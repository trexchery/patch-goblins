"""codex provider: synthetic credentials only, HTTP/subprocess patched."""

from __future__ import annotations

import base64
import json
import sys

import pytest

from claude_swap import dpapi, poll_policy
from claude_swap.providers import codex

SECRET = "SYNTHETIC-SECRET"


def _jwt(claims: dict) -> str:
    enc = lambda d: base64.urlsafe_b64encode(json.dumps(d).encode()).rstrip(b"=").decode()
    return f"{enc({'alg': 'none'})}.{enc(claims)}.sig"


def make_auth(email: str, account_id: str = "acct-1", tag: str = "a") -> dict:
    return {
        "auth_mode": "chatgpt",
        "OPENAI_API_KEY": None,
        "tokens": {
            "id_token": _jwt({"email": email, "https://api.openai.com/auth": {"chatgpt_plan_type": "plus"}}),
            "access_token": f"{SECRET}-access-{tag}",
            "refresh_token": f"{SECRET}-refresh-{tag}",
            "account_id": account_id,
        },
        "last_refresh": "2026-10-01T00:00:00Z",
    }


USAGE_FIXTURE = {
    "plan_type": "plus",
    "rate_limit": {
        "allowed": True,
        "limit_reached": False,
        # deliberately swapped order: windows must be picked by length
        "primary_window": {"used_percent": 40, "limit_window_seconds": 604800, "reset_at": 1_900_000_000},
        "secondary_window": {"used_percent": 12, "limit_window_seconds": 18000, "reset_after_seconds": 600},
    },
}


@pytest.fixture
def env(tmp_path, monkeypatch):
    home = tmp_path / "codex_home"
    home.mkdir()
    monkeypatch.setattr(codex, "store_dir", lambda: tmp_path / "store")
    monkeypatch.setattr(codex, "codex_home", lambda: home)
    calls = []

    def fake_get(url, headers):
        calls.append(headers["Authorization"])
        return 200, USAGE_FIXTURE

    monkeypatch.setattr(codex, "_http_get", fake_get)
    return home, calls


def run(capsys, *argv) -> tuple[int, dict]:
    code = codex.main([*argv, "--json"])
    out = capsys.readouterr().out
    assert SECRET not in out
    assert out.count("\n") == 1
    return code, json.loads(out)


def set_live(home, auth):
    (home / "auth.json").write_text(json.dumps(auth), encoding="utf-8")


def test_seal_roundtrip(env):
    auth = make_auth("a@example.com")
    codex._seal_auth(3, auth)
    raw = codex._cred_path(3).read_text(encoding="utf-8")
    assert SECRET not in raw
    if sys.platform != "darwin":
        assert json.loads(dpapi.unseal(raw)) == auth
    else:
        assert raw.startswith("keychain-provider-v1:")
    assert codex._unseal_auth(3) == auth


def test_add_current_dedupes(env, capsys):
    home, _ = env
    set_live(home, make_auth("a@example.com"))
    code, out = run(capsys, "add-current")
    assert code == 0 and out["added"] == {"number": 1, "email": "a@example.com", "created": True}
    set_live(home, make_auth("A@example.com", tag="b"))
    _, out = run(capsys, "add-current")
    assert out["added"]["number"] == 1 and out["added"]["created"] is False
    assert len(codex._roster()) == 1
    assert codex._unseal_auth(1)["tokens"]["access_token"].endswith("-b")
    # same email, other workspace = different identity
    set_live(home, make_auth("a@example.com", account_id="acct-2"))
    _, out = run(capsys, "add-current")
    assert out["added"]["number"] == 2


def test_enroll(env, capsys, tmp_path):
    iso = tmp_path / "iso"
    iso.mkdir()
    (iso / "auth.json").write_text(json.dumps(make_auth("e@example.com")), encoding="utf-8")
    code, out = run(capsys, "enroll", str(iso))
    assert code == 0 and out["added"]["email"] == "e@example.com"
    code, out = run(capsys, "enroll", str(tmp_path / "missing"))
    assert code == 1 and "error" in out


def test_switch_preserves_live_login(env, capsys):
    home, _ = env
    set_live(home, make_auth("a@example.com", tag="a"))
    run(capsys, "add-current")
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    # live codex refreshed its tokens since enrollment
    set_live(home, make_auth("a@example.com", tag="a2"))
    code, out = run(capsys, "switch", "2")
    assert code == 0 and out["switched"] is True
    assert out["from"]["number"] == 1 and out["to"]["number"] == 2
    assert "restart" in out["note"]
    live = json.loads((home / "auth.json").read_text(encoding="utf-8"))
    assert live["tokens"]["access_token"].endswith("-b")
    assert codex._unseal_auth(1)["tokens"]["access_token"].endswith("-a2")
    assert not list(home.glob("*.tmp"))
    _, out = run(capsys, "switch", "2")
    assert out["switched"] is False


def test_list_shape_and_usage(env, capsys):
    home, calls = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    code, out = run(capsys, "list")
    assert code == 0 and out["schemaVersion"] == 1 and out["activeAccountNumber"] == 1
    a, b = out["accounts"]
    assert a["active"] is True and b["active"] is False
    assert a["usageStatus"] == "ok"
    assert a["usage"]["fiveHour"]["pct"] == 12.0 and "resetsAt" in a["usage"]["fiveHour"]
    assert a["usage"]["sevenDay"]["pct"] == 40.0
    assert a["usage"]["sevenDay"]["resetsAt"].startswith("2030-03-17")
    assert a["plan"] == "plus"
    assert len(calls) == 2


def test_cache_ttl(env, capsys, monkeypatch):
    home, calls = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    t = [1000.0]
    monkeypatch.setattr(codex.time, "time", lambda: t[0])
    run(capsys, "list")
    t[0] += 30
    run(capsys, "list")
    run(capsys, "list", "--refresh")
    assert len(calls) == 1  # inside both the 180 s TTL and the 60 s floor
    t[0] += 40
    run(capsys, "list", "--refresh")
    assert len(calls) == 2
    t[0] += 100
    _, out = run(capsys, "list")
    assert len(calls) == 2 and out["accounts"][0]["usageAgeSeconds"] == 100
    t[0] += 200
    run(capsys, "list")
    assert len(calls) == 3


def test_network_failure_degrades_to_cache(env, capsys, monkeypatch):
    home, _ = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    run(capsys, "list")

    def boom(url, headers):
        raise OSError("timed out")

    monkeypatch.setattr(codex, "_http_get", boom)
    code, out = run(capsys, "list", "--refresh")
    assert code == 0  # 60 s floor: served from cache, no call
    monkeypatch.setattr(codex, "FORCED_MIN_S", 0)
    code, out = run(capsys, "list", "--refresh")
    row = out["accounts"][0]
    assert code == 0 and row["usageStatus"] == "unavailable" and row["usage"] is None
    assert row["lastGoodUsage"]["fiveHour"]["pct"] == 12.0 and row["usageError"] == "timeout"


def test_no_usage_and_expired(env, capsys, monkeypatch):
    home, _ = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    monkeypatch.setattr(codex, "_http_get",
                        lambda url, h: (401, None) if h["Authorization"].endswith("-b") else (200, {}))
    _, out = run(capsys, "list")
    a, b = out["accounts"]
    assert a["usageStatus"] == "unavailable" and a["usage"] is None
    assert a["usageNote"] == "usage not available"
    assert b["usageStatus"] == "token_expired" and "expired" in b["usageNote"]


def test_remove_and_run(env, capsys, monkeypatch):
    home, _ = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    code, out = run(capsys, "run", "1")
    assert code == 1 and "live login" in out["error"]["message"]

    seen = {}

    def fake_call(cmd, env):
        p = codex.Path(env["CODEX_HOME"]) / "auth.json"
        seen["had_auth"] = p.is_file()
        p.write_text(json.dumps(make_auth("b@example.com", account_id="acct-b", tag="b2")), encoding="utf-8")
        return 0

    monkeypatch.setattr(codex.shutil, "which", lambda name: "codex")
    monkeypatch.setattr(codex.subprocess, "call", fake_call)
    code, out = run(capsys, "run", "2")
    assert code == 0 and seen["had_auth"]
    assert not (codex.store_dir() / "homes" / "2" / "auth.json").exists()
    assert codex._unseal_auth(2)["tokens"]["access_token"].endswith("-b2")

    code, out = run(capsys, "remove", "2")
    assert code == 0 and [a["number"] for a in codex._roster()] == [1]
    assert not codex._cred_path(2).exists()
    code, out = run(capsys, "remove", "9")
    assert code == 1


def test_switch_enrolls_unknown_live_login(env, capsys):
    home, _ = env
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    set_live(home, make_auth("x@example.com", account_id="acct-x", tag="x"))
    code, out = run(capsys, "switch", "1")
    assert code == 0 and out["from"]["email"] == "x@example.com"
    assert codex._unseal_auth(out["from"]["number"])["tokens"]["access_token"].endswith("-x")
    set_live(home, {"OPENAI_API_KEY": "SYNTHETIC-SECRET-key", "tokens": None})
    code, out = run(capsys, "switch", "2")
    assert code == 1 and "would be lost" in out["error"]["message"]


def test_list_sweeps_orphaned_run_home(env, capsys):
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    home = codex.store_dir() / "homes" / "1"
    home.mkdir(parents=True)
    (home / ".pid").write_text("1", encoding="utf-8")  # pid <= 1 is never alive
    (home / "auth.json").write_text(json.dumps(make_auth("b@example.com", account_id="acct-b", tag="b2")), encoding="utf-8")
    run(capsys, "list")
    assert not (home / "auth.json").exists()
    assert codex._unseal_auth(1)["tokens"]["access_token"].endswith("-b2")


def test_list_paces_network_fetches(env, capsys, monkeypatch):
    home, calls = env
    set_live(home, make_auth("a@example.com"))
    run(capsys, "add-current")
    codex._import(make_auth("b@example.com", account_id="acct-b", tag="b"))
    codex._import(make_auth("c@example.com", account_id="acct-c", tag="c"))
    monkeypatch.setattr(poll_policy, "USAGE_GAP_S", (2.0, 7.0))
    monkeypatch.setattr(poll_policy.random, "uniform", lambda a, b: b)
    monkeypatch.setattr(poll_policy.time, "sleep", calls.append)  # gaps land between requests
    t = [1000.0]
    monkeypatch.setattr(codex.time, "time", lambda: t[0])
    seq = lambda: ["get" if isinstance(c, str) else c for c in calls]  # noqa: E731
    run(capsys, "list")
    assert seq() == ["get", 7.0, "get", 7.0, "get"]  # the first goes at once
    run(capsys, "list")
    assert len(calls) == 5  # all cached: no request, no wait
    t[0] += 200
    cache_path = codex.store_dir() / "cache.json"
    cache = json.loads(cache_path.read_text(encoding="utf-8"))
    cache["b@example.com|acct-b"]["attemptAt"] = t[0]  # b fresh, a and c due
    cache_path.write_text(json.dumps(cache), encoding="utf-8")
    del calls[:]
    run(capsys, "list")
    assert seq() == ["get", 7.0, "get"]  # b's cache hit neither waits nor counts
