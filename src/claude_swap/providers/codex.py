"""Codex (OpenAI Codex CLI) provider for Patch Goblins: ``cswap codex <cmd> --json``.

Store (``<backup root>/providers/codex/``):
  accounts.json        roster, no secrets: number, email, accountId, alias
  creds/<n>.enc        the account's auth.json, sealed with ``dpapi.seal``
  cache.json           last usage reading per identity (no secrets)
  homes/<n>/           transient CODEX_HOME for ``run`` (plaintext deleted on exit)

Codex caches auth in memory and never watches auth.json, so ``switch`` is an
atomic rewrite of ``$CODEX_HOME/auth.json``: running sessions keep their
account until restarted. Patch Goblins never refreshes tokens (Codex refresh
tokens are single-use; refreshing behind a running codex logs it out); an
expired dormant token is reported and Codex renews it after a switch.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from claude_swap import dpapi, poll_policy
from claude_swap.fsutil import read_text_with_retry, replace_with_retry
from claude_swap.json_output import SCHEMA_VERSION, error_envelope, usage_to_json
from claude_swap.paths import get_backup_root
from claude_swap.process_detection import is_pid_alive
from claude_swap.providers import _credentials

USAGE_URL = "https://chatgpt.com/backend-api/wham/usage"
FETCH_TTL_S = 180
FORCED_MIN_S = 60
FIVE_HOUR_S = 18000
WEEK_S = 604800
RESTART_NOTE = (
    "running codex sessions (and the app-server daemon) keep their account "
    "until restarted; new codex sessions use the new account"
)


class CodexError(Exception):
    pass


def store_dir() -> Path:
    return get_backup_root() / "providers" / "codex"


def codex_home() -> Path:
    env = os.environ.get("CODEX_HOME")
    return Path(env) if env else Path.home() / ".codex"


# -- files -------------------------------------------------------------------

def _atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(text)
        replace_with_retry(tmp, path)
    except BaseException:
        Path(tmp).unlink(missing_ok=True)
        raise


def _read_json(path: Path, default):
    try:
        return json.loads(read_text_with_retry(path))
    except FileNotFoundError:
        return default


def _roster() -> list[dict]:
    return _read_json(store_dir() / "accounts.json", {"accounts": []})["accounts"]


def _save_roster(accounts: list[dict]) -> None:
    _atomic_write(store_dir() / "accounts.json", json.dumps({"accounts": accounts}, indent=2))


def _cred_path(n: int) -> Path:
    return store_dir() / "creds" / f"{n}.enc"


def _seal_auth(n: int, auth: dict) -> None:
    _credentials.write(_cred_path(n), json.dumps(auth), _atomic_write)


def _unseal_auth(n: int) -> dict:
    try:
        return json.loads(_credentials.read(_cred_path(n), _atomic_write))
    except FileNotFoundError:
        raise CodexError(f"account {n} has no stored credentials") from None


# -- auth parsing ------------------------------------------------------------

def _jwt_claims(token: str | None) -> dict:
    """Unverified JWT payload (identity only; never trusted for auth)."""
    try:
        part = token.split(".")[1]
        return json.loads(base64.urlsafe_b64decode(part + "=" * (-len(part) % 4)))
    except Exception:
        return {}


def identity(auth: dict) -> dict:
    """``{email, accountId, plan}`` from a ChatGPT-login auth.json."""
    tokens = auth.get("tokens") if isinstance(auth, dict) else None
    if not isinstance(tokens, dict) or not tokens.get("access_token"):
        raise CodexError("no ChatGPT login found (API-key logins have no quota and are not supported)")
    claims = _jwt_claims(tokens.get("id_token"))
    oa = claims.get("https://api.openai.com/auth") or {}
    email = claims.get("email")
    if not isinstance(email, str) or not email:
        raise CodexError("codex login carries no email")
    return {
        "email": email,
        "accountId": tokens.get("account_id") or oa.get("chatgpt_account_id") or "",
        "plan": oa.get("chatgpt_plan_type"),
    }


def _key(ident: dict) -> str:
    return f"{ident['email'].lower()}|{ident['accountId']}"


def _live_auth() -> dict | None:
    path = codex_home() / "auth.json"
    try:
        return json.loads(read_text_with_retry(path))
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def _live_key() -> str | None:
    auth = _live_auth()
    try:
        return _key(identity(auth)) if auth else None
    except CodexError:
        return None


def _find(n: int) -> dict:
    for acc in _roster():
        if acc["number"] == n:
            return acc
    raise CodexError(f"no codex account {n}")


# -- usage -------------------------------------------------------------------

def _http_get(url: str, headers: dict) -> tuple[int, dict | None]:
    """GET returning ``(status, json)``; raises ``OSError`` on network failure."""
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as e:
        return e.code, None


def _iso(ts) -> str | None:
    if not isinstance(ts, (int, float)):
        return None
    return datetime.fromtimestamp(ts, tz=timezone.utc).isoformat(timespec="seconds")


def parse_usage(payload: dict) -> dict:
    """wham/usage payload -> internal usage dict (``five_hour``/``seven_day``).

    Windows are picked by ``limit_window_seconds``; order (primary=5h,
    secondary=weekly) is the fallback when the length is missing.
    """
    rl = payload.get("rate_limit") or {}
    out: dict = {}
    for slot, fallback in (("primary_window", "five_hour"), ("secondary_window", "seven_day")):
        w = rl.get(slot)
        if not isinstance(w, dict) or not isinstance(w.get("used_percent"), (int, float)):
            continue
        name = {FIVE_HOUR_S: "five_hour", WEEK_S: "seven_day"}.get(w.get("limit_window_seconds"), fallback)
        entry: dict = {"pct": float(w["used_percent"])}
        reset = _iso(w.get("reset_at"))
        if reset is None and isinstance(w.get("reset_after_seconds"), (int, float)):
            reset = _iso(time.time() + w["reset_after_seconds"])
        if reset:
            entry["resets_at"] = reset
        out[name] = entry
    return out


def _fetch(auth: dict, pace) -> tuple[str, dict | None, str | None]:
    """``(status, usage, error)`` for one auth; never raises. ``pace`` runs
    right before the request (``poll_policy.pacer``)."""
    tokens = auth["tokens"]
    headers = {
        "Authorization": f"Bearer {tokens['access_token']}",
        "ChatGPT-Account-Id": tokens.get("account_id") or "",
        "User-Agent": "codex-cli",
        "Accept": "application/json",
    }
    pace()
    try:
        code, body = _http_get(USAGE_URL, headers)
    except (OSError, ValueError) as e:
        return "unavailable", None, "timeout" if "timed out" in str(e) else "network"
    if code == 401:
        return "token_expired", None, None
    if code != 200 or not isinstance(body, dict):
        return "unavailable", None, f"http-{code}"
    usage = parse_usage(body)
    return ("ok", usage, None) if usage else ("unavailable", None, "no-windows")


def _row(acc: dict, active: bool, auth: dict | None, refresh: bool, cache: dict, now: float,
         pace) -> dict:
    key = _key(acc)
    entry = cache.get(key, {})
    last_attempt = entry.get("attemptAt", 0)
    due = now - last_attempt >= (FORCED_MIN_S if refresh else FETCH_TTL_S)
    if auth is not None and due:
        status, usage, err = _fetch(auth, pace)
        entry = dict(entry, attemptAt=now, status=status, error=err)
        if usage:
            entry.update(usage=usage, fetchedAt=now)
        cache[key] = entry
    row: dict = {"number": acc["number"], "email": acc["email"], "active": active}
    if acc.get("alias"):
        row["alias"] = acc["alias"]
    if acc.get("plan"):
        row["plan"] = acc["plan"]
    status = entry.get("status") if auth is not None else "no_credentials"
    usage, fetched = entry.get("usage"), entry.get("fetchedAt")
    age = round(now - fetched, 1) if fetched else None
    if status == "ok" and usage:
        row.update(usageStatus="ok", usage=usage_to_json(usage), usageAgeSeconds=age)
        return row
    row.update(usageStatus=status or "unavailable", usage=None)
    if usage:  # stale reading, shown as last-known
        row.update(lastGoodUsage=usage_to_json(usage), lastGoodAgeSeconds=age)
    if entry.get("error"):
        row["usageError"] = entry["error"]
    if status == "token_expired":
        row["usageNote"] = "login expired; codex renews it after a switch, else re-enroll"
    elif not usage:
        row["usageNote"] = "usage not available"
    return row


# -- commands ----------------------------------------------------------------

def _sweep_homes() -> None:
    """Delete plaintext auth.json left in homes/<n> by a ``run`` that died
    (console closed) after sealing back a same-identity refreshed login."""
    for home in (store_dir() / "homes").glob("*"):
        path = home / "auth.json"
        if not path.exists():
            continue
        try:
            if is_pid_alive(int(read_text_with_retry(home / ".pid"))):
                continue
        except (OSError, ValueError):
            pass
        try:
            auth = json.loads(read_text_with_retry(path))
            acc = _find(int(home.name))
            if _key(identity(auth)) == _key(acc):
                _seal_auth(acc["number"], auth)
        except (CodexError, OSError, ValueError, KeyError, AttributeError):
            pass
        path.unlink(missing_ok=True)


def cmd_list(refresh: bool) -> dict:
    _sweep_homes()
    live, live_key = _live_auth(), _live_key()
    cache_path = store_dir() / "cache.json"
    cache = _read_json(cache_path, {})
    now = time.time()
    pace = poll_policy.pacer()
    rows, active_num = [], None
    for acc in _roster():
        active = _key(acc) == live_key
        if active:
            active_num = acc["number"]
            auth = live  # live tokens are the freshest; read-only use
        else:
            try:
                auth = _unseal_auth(acc["number"])
            except (CodexError, ValueError, OSError):
                auth = None
        rows.append(_row(acc, active, auth, refresh, cache, now, pace))
    _atomic_write(cache_path, json.dumps(cache))
    return {"schemaVersion": SCHEMA_VERSION, "activeAccountNumber": active_num, "accounts": rows}


def _import(auth: dict) -> dict:
    """Add or overwrite (same identity) an account; returns its roster entry."""
    ident = identity(auth)
    accounts = _roster()
    acc = next((a for a in accounts if _key(a) == _key(ident)), None)
    created = acc is None
    if created:
        acc = {"number": max((a["number"] for a in accounts), default=0) + 1}
        accounts.append(acc)
    acc.update(email=ident["email"], accountId=ident["accountId"], plan=ident["plan"])
    _seal_auth(acc["number"], auth)
    _save_roster(accounts)
    return {"number": acc["number"], "email": acc["email"], "created": created}


def cmd_add_current() -> dict:
    auth = _live_auth()
    if auth is None:
        raise CodexError(f"no codex login in {codex_home()} (run `codex login`)")
    return {"schemaVersion": SCHEMA_VERSION, "added": _import(auth)}


def cmd_enroll(config_dir: str) -> dict:
    path = Path(config_dir).expanduser() / "auth.json"
    if not path.is_file():
        raise CodexError(f"no login found in {config_dir} (did `codex login` finish?)")
    auth = json.loads(read_text_with_retry(path))
    return {"schemaVersion": SCHEMA_VERSION, "added": _import(auth)}


def cmd_switch(n: int) -> dict:
    target = _find(n)
    live, live_key = _live_auth(), _live_key()
    from_ref = None
    if live_key == _key(target):
        return {"schemaVersion": SCHEMA_VERSION, "switched": False, "from": None,
                "to": {"number": n, "email": target["email"]}, "reason": "already active"}
    if live is not None and live_key is None:
        raise CodexError("the current codex login is not a ChatGPT login; it would be lost (log out first)")
    if live_key:
        # Keep the live login's (possibly refreshed) tokens, enrolling it if new.
        kept = _import(live)
        from_ref = {"number": kept["number"], "email": kept["email"]}
    _atomic_write(codex_home() / "auth.json", json.dumps(_unseal_auth(n), indent=2))
    return {"schemaVersion": SCHEMA_VERSION, "switched": True, "from": from_ref,
            "to": {"number": n, "email": target["email"]}, "reason": None, "note": RESTART_NOTE}


def cmd_remove(n: int) -> dict:
    acc = _find(n)
    if sys.platform == "darwin":
        _credentials.delete(_cred_path(n))
    _save_roster([a for a in _roster() if a["number"] != n])
    if sys.platform != "darwin":
        _cred_path(n).unlink(missing_ok=True)
    return {"schemaVersion": SCHEMA_VERSION, "removed": {"number": n, "email": acc["email"]}}


def cmd_run(n: int) -> dict:
    acc = _find(n)
    if _key(acc) == _live_key():
        # Two processes sharing one single-use refresh token log each other out.
        raise CodexError(f"account {n} is the live login; run plain `codex` instead")
    exe = shutil.which("codex")
    if not exe:
        raise CodexError("codex CLI not found on PATH")
    home = store_dir() / "homes" / str(n)
    auth_path = home / "auth.json"
    original = json.dumps(_unseal_auth(n), indent=2)
    _atomic_write(home / ".pid", str(os.getpid()))
    _atomic_write(auth_path, original)
    try:
        code = subprocess.call([exe], env={**os.environ, "CODEX_HOME": str(home)})
        try:
            updated = read_text_with_retry(auth_path)
            if updated != original:
                _seal_auth(n, json.loads(updated))
        except (FileNotFoundError, json.JSONDecodeError):
            pass
    finally:
        auth_path.unlink(missing_ok=True)
    return {"schemaVersion": SCHEMA_VERSION, "ran": {"number": n, "email": acc["email"]}, "exitCode": code}


def main(argv: list[str]) -> int:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--json", action="store_true")
    p = argparse.ArgumentParser(prog="cswap codex", parents=[common])
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", parents=[common]).add_argument("--refresh", action="store_true")
    sub.add_parser("add-current", parents=[common])
    sub.add_parser("enroll", parents=[common]).add_argument("config_dir")
    for name in ("switch", "remove", "run"):
        sub.add_parser(name, parents=[common]).add_argument("n", type=int)
    try:
        args = p.parse_args(argv)
    except SystemExit:
        print(json.dumps(error_envelope(CodexError("invalid arguments"))))
        return 1
    try:
        result = {
            "list": lambda: cmd_list(args.refresh),
            "add-current": cmd_add_current,
            "enroll": lambda: cmd_enroll(args.config_dir),
            "switch": lambda: cmd_switch(args.n),
            "remove": lambda: cmd_remove(args.n),
            "run": lambda: cmd_run(args.n),
        }[args.cmd]()
    except (CodexError, OSError, ValueError) as e:
        print(json.dumps(error_envelope(e)))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
