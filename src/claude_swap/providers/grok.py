"""Grok (xAI Grok Build CLI) provider for Patch Goblins: ``cswap grok <cmd> --json``.

Store (``<backup root>/providers/grok/``):
  accounts.json        roster, no secrets: number, email, userId, scope, alias
  creds/<n>.enc        ``{"scope", "record"}`` (one auth.json entry), sealed with ``dpapi.seal``
  cache.json           last usage reading per identity (no secrets)
  homes/<n>/           transient GROK_HOME for ``run`` (plaintext deleted on exit)

``$GROK_HOME/auth.json`` (or ``$GROK_AUTH_PATH``) maps a scope key
(``<issuer>::<client_id>``) to one plaintext login record. ``switch`` swaps that
scope's record atomically while holding grok's own ``auth.json.lock``.
Running grok sessions keep their in-memory token and adopt the file only on
their next refresh or rejection (and may decline an older-minted token), so
they keep their account until restarted. Patch Goblins never refreshes tokens:
grok refresh tokens rotate and are single-use, so a refresh behind grok's
back can revoke its live login. An expired dormant token is reported as
``token_expired``; grok renews it after a switch.
"""

from __future__ import annotations

import argparse
import contextlib
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

ISSUER = "https://auth.x.ai"
DEFAULT_SCOPE = f"{ISSUER}::b1a00492-073a-47ea-816f-4c329264a828"
LEGACY_SCOPE = "https://accounts.x.ai/sign-in"
BILLING_URL = "https://cli-chat-proxy.grok.com/v1/billing?format=credits"
CLIENT_VERSION = "1.0.45"
FETCH_TTL_S = 180
FORCED_MIN_S = 60
TOKEN_TTL_S = 30 * 86400  # grok's TOKEN_TTL when expires_at is absent
LOCK_WAIT_S = 5.0
RESTART_NOTE = (
    "running grok sessions keep their account until restarted (they may later "
    "write their own refreshed login back); new grok sessions use the new account"
)


class GrokError(Exception):
    pass


def store_dir() -> Path:
    return get_backup_root() / "providers" / "grok"


def grok_home() -> Path:
    env = os.environ.get("GROK_HOME")
    return Path(env) if env else Path.home() / ".grok"


def auth_path() -> Path:
    env = os.environ.get("GROK_AUTH_PATH")
    return Path(env) if env else grok_home() / "auth.json"


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


@contextlib.contextmanager
def _auth_lock(path: Path):
    """Hold grok's advisory ``auth.json.lock`` so a swap never races a refresh.

    ponytail: best-effort byte-0 lock (msvcrt) / flock (POSIX); matches grok's
    whole-file lock as far as we can tell without grok installed to test.
    """
    lock = path.with_name(path.name + ".lock")
    lock.parent.mkdir(parents=True, exist_ok=True)
    with open(lock, "a+b") as f:
        deadline = time.monotonic() + LOCK_WAIT_S
        while True:
            try:
                if sys.platform == "win32":
                    import msvcrt
                    f.seek(0)
                    msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(f.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError:
                if time.monotonic() > deadline:
                    raise GrokError("grok is updating its login (auth.json.lock held); retry") from None
                time.sleep(0.1)
        try:
            yield
        finally:
            if sys.platform == "win32":
                import msvcrt
                f.seek(0)
                with contextlib.suppress(OSError):
                    msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
            # flock is released when the file closes


def _roster() -> list[dict]:
    return _read_json(store_dir() / "accounts.json", {"accounts": []})["accounts"]


def _save_roster(accounts: list[dict]) -> None:
    _atomic_write(store_dir() / "accounts.json", json.dumps({"accounts": accounts}, indent=2))


def _cred_path(n: int) -> Path:
    return store_dir() / "creds" / f"{n}.enc"


def _seal(n: int, scope: str, record: dict) -> None:
    _credentials.write(_cred_path(n), json.dumps({"scope": scope, "record": record}), _atomic_write)


def _unseal(n: int) -> tuple[str, dict]:
    try:
        data = json.loads(_credentials.read(_cred_path(n), _atomic_write))
    except FileNotFoundError:
        raise GrokError(f"account {n} has no stored credentials") from None
    return data["scope"], data["record"]


# -- auth parsing ------------------------------------------------------------

def pick_login(store: object) -> tuple[str, dict]:
    """``(scope, record)`` of the usable login in an auth.json store.

    Prefers the default xAI OAuth scope, then any other OAuth scope, then an
    API-key login. Legacy ``accounts.x.ai`` records are ignored (grok does too).
    """
    if not isinstance(store, dict):
        raise GrokError("grok auth.json is not a JSON object")
    candidates = [
        (s, r) for s, r in store.items()
        if s != LEGACY_SCOPE and isinstance(r, dict) and r.get("key") and r.get("user_id")
    ]
    candidates.sort(key=lambda sr: (sr[0] != DEFAULT_SCOPE, sr[1].get("auth_mode") == "api_key"))
    if not candidates:
        raise GrokError("no grok login found (run `grok login`)")
    return candidates[0]


def _key(user_id: str) -> str:
    return str(user_id)


def _live_store() -> dict | None:
    try:
        data = json.loads(read_text_with_retry(auth_path()))
    except (FileNotFoundError, json.JSONDecodeError):
        return None
    return data if isinstance(data, dict) else None


def _live_login() -> tuple[str, dict] | None:
    store = _live_store()
    try:
        return pick_login(store) if store is not None else None
    except GrokError:
        return None


def _find(n: int) -> dict:
    for acc in _roster():
        if acc["number"] == n:
            return acc
    raise GrokError(f"no grok account {n}")


# -- usage -------------------------------------------------------------------

def _http_get(url: str, headers: dict) -> tuple[int, dict | None]:
    """GET returning ``(status, json)``; raises ``OSError`` on network failure."""
    req = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as e:
        return e.code, None


def _parse_time(value) -> datetime | None:
    try:
        return datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None


def _expired(record: dict, now: float) -> bool:
    exp = _parse_time(record.get("expires_at"))
    if exp is None:
        created = _parse_time(record.get("create_time"))
        if created is None:
            return False  # unknown; let the server decide
        return created.timestamp() + TOKEN_TTL_S <= now
    return exp.timestamp() <= now


def parse_usage(payload: dict) -> dict:
    """billing?format=credits payload -> internal usage dict.

    proto3 JSON omits zero values, so a missing ``creditUsagePercent`` is 0.
    A weekly period maps to ``seven_day``; anything else (monthly) to a named
    ``scoped`` window so the tray labels it instead of mislabeling it weekly.
    """
    cfg = payload.get("config")
    if not isinstance(cfg, dict):
        return {}
    pct = cfg.get("creditUsagePercent", 0)
    if not isinstance(pct, (int, float)):
        return {}
    period = cfg.get("currentPeriod") or {}
    ptype = str(period.get("type", ""))
    entry: dict = {"pct": float(pct)}
    end = period.get("end") or cfg.get("billingPeriodEnd")
    if end and _parse_time(end):
        entry["resets_at"] = end
    if "WEEKLY" in ptype:
        return {"seven_day": entry}
    return {"scoped": [{"name": "Monthly limit", **entry}]}


def _fetch(scope: str, record: dict, now: float, pace) -> tuple[str, dict | None, str | None]:
    """``(status, usage, error)`` for one login; never raises, never refreshes.
    ``pace`` runs right before the request (``poll_policy.pacer``)."""
    mode = record.get("auth_mode")
    first_party = mode in ("oidc", "external", "grok", "web_login") and (
        record.get("oidc_issuer", ISSUER) == ISSUER or scope.startswith(ISSUER)
    )
    if not first_party:
        return "api_key", None, None
    if _expired(record, now):
        return "token_expired", None, None
    headers = {
        "Authorization": f"Bearer {record['key']}",
        "X-XAI-Token-Auth": "xai-grok-cli",
        "x-userid": str(record.get("user_id", "")),
        "x-grok-client-version": CLIENT_VERSION,
        "x-grok-client-mode": "headless",
        "Accept": "application/json",
    }
    pace()
    try:
        code, body = _http_get(BILLING_URL, headers)
    except (OSError, ValueError) as e:
        return "unavailable", None, "timeout" if "timed out" in str(e) else "network"
    if code in (401, 403):
        return "token_expired", None, None
    if code != 200 or not isinstance(body, dict):
        return "unavailable", None, f"http-{code}"
    usage = parse_usage(body)
    return ("ok", usage, None) if usage else ("unavailable", None, "no-windows")


def _row(acc: dict, active: bool, login: tuple[str, dict] | None, refresh: bool,
         cache: dict, now: float, pace) -> dict:
    key = _key(acc["userId"])
    entry = cache.get(key, {})
    due = now - entry.get("attemptAt", 0) >= (FORCED_MIN_S if refresh else FETCH_TTL_S)
    if login is not None and due:
        status, usage, err = _fetch(*login, now, pace)
        entry = dict(entry, attemptAt=now, status=status, error=err)
        if usage:
            entry.update(usage=usage, fetchedAt=now)
        cache[key] = entry
    row: dict = {"number": acc["number"], "email": acc["email"], "active": active}
    if acc.get("alias"):
        row["alias"] = acc["alias"]
    status = entry.get("status") if login is not None else "no_credentials"
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
        row["usageNote"] = "login expired; grok renews it after a switch, else re-enroll"
    elif not usage:
        row["usageNote"] = "usage not available"
    return row


# -- commands ----------------------------------------------------------------

def _sweep_homes() -> None:
    """Delete plaintext auth.json left in homes/<n> by a ``run`` that died
    (console closed) after sealing back a same-user refreshed login."""
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
            scope, record = pick_login(json.loads(read_text_with_retry(path)))
            acc = _find(int(home.name))
            if _key(record["user_id"]) == _key(acc["userId"]):
                _seal(acc["number"], scope, record)
        except (GrokError, OSError, ValueError, KeyError):
            pass
        path.unlink(missing_ok=True)


def cmd_list(refresh: bool) -> dict:
    _sweep_homes()
    live = _live_login()
    live_key = _key(live[1]["user_id"]) if live else None
    cache_path = store_dir() / "cache.json"
    cache = _read_json(cache_path, {})
    now = time.time()
    pace = poll_policy.pacer()
    rows, active_num = [], None
    for acc in _roster():
        active = _key(acc["userId"]) == live_key
        if active:
            active_num = acc["number"]
            login = live  # live token is the freshest; read-only use
        else:
            try:
                login = _unseal(acc["number"])
            except (GrokError, ValueError, OSError, KeyError):
                login = None
        rows.append(_row(acc, active, login, refresh, cache, now, pace))
    _atomic_write(cache_path, json.dumps(cache))
    return {"schemaVersion": SCHEMA_VERSION, "activeAccountNumber": active_num, "accounts": rows}


def _import(scope: str, record: dict) -> dict:
    """Add or overwrite (same user_id) an account; returns its roster entry."""
    email = record.get("email") or ""
    accounts = _roster()
    acc = next((a for a in accounts if _key(a["userId"]) == _key(record["user_id"])), None)
    created = acc is None
    if created:
        acc = {"number": max((a["number"] for a in accounts), default=0) + 1}
        accounts.append(acc)
    acc.update(email=email or acc.get("email") or str(record["user_id"]),
               userId=str(record["user_id"]), scope=scope)
    _seal(acc["number"], scope, record)
    _save_roster(accounts)
    return {"number": acc["number"], "email": acc["email"], "created": created}


def cmd_add_current() -> dict:
    live = _live_login()
    if live is None:
        raise GrokError(f"no grok login in {auth_path()} (run `grok login`)")
    return {"schemaVersion": SCHEMA_VERSION, "added": _import(*live)}


def cmd_enroll(config_dir: str) -> dict:
    path = Path(config_dir).expanduser() / "auth.json"
    if not path.is_file():
        raise GrokError(f"no login found in {config_dir} (did `grok login` finish?)")
    return {"schemaVersion": SCHEMA_VERSION,
            "added": _import(*pick_login(json.loads(read_text_with_retry(path))))}


def cmd_switch(n: int) -> dict:
    target = _find(n)
    to_ref = {"number": n, "email": target["email"]}
    scope, record = _unseal(n)
    path = auth_path()
    with _auth_lock(path):
        store = _live_store() or {}
        live = None
        with contextlib.suppress(GrokError):
            live = pick_login(store)
        if live and _key(live[1]["user_id"]) == _key(target["userId"]):
            return {"schemaVersion": SCHEMA_VERSION, "switched": False, "from": None,
                    "to": to_ref, "reason": "already active"}
        from_ref = None
        if live:
            # Keep the live login's (possibly refreshed) tokens, enrolling it if new.
            kept = _import(*live)
            from_ref = {"number": kept["number"], "email": kept["email"]}
            store.pop(live[0], None)
        store[scope] = record
        _atomic_write(path, json.dumps(store, indent=2))
    result = {"schemaVersion": SCHEMA_VERSION, "switched": True, "from": from_ref,
              "to": to_ref, "reason": None, "note": RESTART_NOTE}
    if os.environ.get("GROK_AUTH"):
        result["warning"] = "GROK_AUTH is set; grok processes using it ignore auth.json"
    return result


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
    live = _live_login()
    if live and _key(live[1]["user_id"]) == _key(acc["userId"]):
        # Two processes sharing one single-use refresh token revoke each other.
        raise GrokError(f"account {n} is the live login; run plain `grok` instead")
    exe = shutil.which("grok")
    if not exe:
        raise GrokError("grok CLI not found on PATH")
    scope, record = _unseal(n)
    home = store_dir() / "homes" / str(n)
    path = home / "auth.json"
    original = json.dumps({scope: record}, indent=2)
    _atomic_write(home / ".pid", str(os.getpid()))
    _atomic_write(path, original)
    env = {k: v for k, v in os.environ.items() if k not in ("GROK_AUTH", "GROK_AUTH_PATH")}
    try:
        code = subprocess.call([exe], env={**env, "GROK_HOME": str(home)})
        try:
            updated = read_text_with_retry(path)
            if updated != original:
                new_scope, new_record = pick_login(json.loads(updated))
                if _key(new_record["user_id"]) == _key(acc["userId"]):
                    _seal(n, new_scope, new_record)
        except (FileNotFoundError, json.JSONDecodeError, GrokError):
            pass
    finally:
        path.unlink(missing_ok=True)
    return {"schemaVersion": SCHEMA_VERSION, "ran": {"number": n, "email": acc["email"]}, "exitCode": code}


def main(argv: list[str]) -> int:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--json", action="store_true")
    p = argparse.ArgumentParser(prog="cswap grok", parents=[common])
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", parents=[common]).add_argument("--refresh", action="store_true")
    sub.add_parser("add-current", parents=[common])
    sub.add_parser("enroll", parents=[common]).add_argument("config_dir")
    for name in ("switch", "remove", "run"):
        sub.add_parser(name, parents=[common]).add_argument("n", type=int)
    try:
        args = p.parse_args(argv)
    except SystemExit:
        print(json.dumps(error_envelope(GrokError("invalid arguments"))))
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
    except (GrokError, OSError, ValueError, KeyError) as e:
        # KeyError text is a field name, never a value.
        print(json.dumps(error_envelope(e)))
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
