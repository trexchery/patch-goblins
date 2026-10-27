"""grok provider: synthetic credentials only, HTTP/subprocess patched."""

from __future__ import annotations

import json
import sys

import pytest

from claude_swap import dpapi, poll_policy
from claude_swap.providers import grok

SECRET = "SYNTHETIC-SECRET"


def make_record(email: str, user_id: str, tag: str = "a", **extra) -> dict:
    rec = {
        "key": f"{SECRET}-access-{tag}",
        "refresh_token": f"{SECRET}-refresh-{tag}",
        "auth_mode": "oidc",
        "create_time": "2026-10-01T00:00:00Z",
        "expires_at": "2099-01-01T00:00:00Z",
        "user_id": user_id,
        "email": email,
        "coding_data_retention_opt_out": False,
        "oidc_issuer": grok.ISSUER,
    }
    rec.update(extra)
    return rec


def make_store(email: str, user_id: str, tag: str = "a", **extra) -> dict:
    return {grok.DEFAULT_SCOPE: make_record(email, user_id, tag, **extra)}


USAGE_FIXTURE = {
    "config": {
        "creditUsagePercent": 37.5,
        "currentPeriod": {"type": "USAGE_PERIOD_TYPE_WEEKLY",
                          "start": "2026-10-13T00:00:00Z", "end": "2026-10-20T00:00:00Z"},
        "isUnifiedBillingUser": True,
    },
    "subscriptionTier": "SuperGrok",
}


@pytest.fixture
def env(tmp_path, monkeypatch):
    home = tmp_path / "grok_home"
    home.mkdir()
    monkeypatch.delenv("GROK_AUTH_PATH", raising=False)
    monkeypatch.delenv("GROK_AUTH", raising=False)
    monkeypatch.setenv("GROK_HOME", str(home))
    monkeypatch.setattr(grok, "store_dir", lambda: tmp_path / "store")
    calls = []

    def fake_get(url, headers):
        calls.append(headers["Authorization"])
        return 200, USAGE_FIXTURE

    monkeypatch.setattr(grok, "_http_get", fake_get)
    return home, calls


def write_live(home, store):
    (home / "auth.json").write_text(json.dumps(store), encoding="utf-8")


def run(capsys, *argv):
    code = grok.main([*argv, "--json"])
    out = capsys.readouterr().out
    assert SECRET not in out
    return code, json.loads(out)


def test_seal_store_roundtrip(env):
    rec = make_record("a@example.com", "u1")
    grok._seal(7, grok.DEFAULT_SCOPE, rec)
    raw = grok._cred_path(7).read_text(encoding="utf-8")
    assert SECRET not in raw
    if sys.platform != "darwin":
        assert json.loads(dpapi.unseal(raw)) == {"scope": grok.DEFAULT_SCOPE, "record": rec}
    else:
        assert raw.startswith("keychain-provider-v1:")
    assert grok._unseal(7) == (grok.DEFAULT_SCOPE, rec)


def test_add_current_dedupes(env, capsys):
    home, _ = env
    write_live(home, make_store("a@example.com", "u1"))
    code, out = run(capsys, "add-current")
    assert code == 0 and out["added"] == {"number": 1, "email": "a@example.com", "created": True}
    write_live(home, make_store("a@example.com", "u1", tag="refreshed"))
    code, out = run(capsys, "add-current")
    assert out["added"]["created"] is False and len(grok._roster()) == 1
    assert grok._unseal(1)[1]["key"].endswith("refreshed")


def test_add_current_ignores_legacy_scope(env, capsys):
    home, _ = env
    write_live(home, {grok.LEGACY_SCOPE: make_record("old@example.com", "u0")})
    code, out = run(capsys, "add-current")
    assert code == 1 and "error" in out


def test_switch_writes_target_and_preserves_live(env, capsys, monkeypatch):
    home, _ = env
    write_live(home, make_store("b@example.com", "u2", tag="b"))
    run(capsys, "add-current")
    write_live(home, make_store("a@example.com", "u1", tag="a"))
    run(capsys, "add-current")
    # grok refreshed account 2 (live) since it was enrolled
    live = make_store("a@example.com", "u1", tag="a2")
    live["xai::api_key"] = make_record("a@example.com", "u1", tag="k", auth_mode="api_key")
    write_live(home, live)
    writes = []
    real = grok._atomic_write
    monkeypatch.setattr(grok, "_atomic_write", lambda p, t: (writes.append(p), real(p, t)))
    code, out = run(capsys, "switch", "1")
    assert code == 0 and out["switched"] is True
    assert out["from"] == {"number": 2, "email": "a@example.com"}
    assert "restarted" in out["note"]
    assert home / "auth.json" in writes  # atomic temp+rename helper
    written = json.loads((home / "auth.json").read_text(encoding="utf-8"))
    assert written[grok.DEFAULT_SCOPE]["user_id"] == "u2"
    assert written[grok.DEFAULT_SCOPE]["key"].endswith("-b")
    assert "xai::api_key" in written  # unrelated scopes untouched
    assert grok._unseal(2)[1]["key"].endswith("a2")  # refreshed live login kept
    code, out = run(capsys, "switch", "1")
    assert out["switched"] is False and out["reason"] == "already active"


def test_switch_enrolls_unknown_live_login(env, capsys):
    home, _ = env
    write_live(home, make_store("b@example.com", "u2", tag="b"))
    run(capsys, "add-current")
    write_live(home, make_store("new@example.com", "u9", tag="n"))  # never enrolled
    code, out = run(capsys, "switch", "1")
    assert code == 0 and out["from"] == {"number": 2, "email": "new@example.com"}
    assert grok._unseal(2)[1]["key"].endswith("-n")  # not lost


def test_list_envelope_and_usage(env, capsys):
    home, calls = env
    write_live(home, make_store("b@example.com", "u2", tag="b"))
    run(capsys, "add-current")
    write_live(home, make_store("a@example.com", "u1"))
    run(capsys, "add-current")
    code, out = run(capsys, "list")
    assert code == 0 and out["schemaVersion"] == 1 and out["activeAccountNumber"] == 2
    a1, a2 = out["accounts"]
    assert a2["active"] is True and a1["active"] is False
    assert a1["usageStatus"] == "ok"
    assert a1["usage"]["sevenDay"]["pct"] == 37.5
    assert a1["usage"]["sevenDay"]["resetsAt"] == "2026-10-20T00:00:00Z"
    assert len(calls) == 2


def test_monthly_and_proto3_zero(env):
    usage = grok.parse_usage({"config": {"currentPeriod": {"type": "USAGE_PERIOD_TYPE_MONTHLY",
                                                           "end": "2026-11-01T00:00:00Z"}}})
    assert usage == {"scoped": [{"name": "Monthly limit", "pct": 0.0, "resets_at": "2026-11-01T00:00:00Z"}]}
    assert grok.parse_usage({}) == {}


def test_no_usage_paths(env, capsys, monkeypatch):
    home, calls = env
    write_live(home, {"xai::api_key": make_record("k@example.com", "u9", auth_mode="api_key")})
    run(capsys, "add-current")
    write_live(home, make_store("x@example.com", "u3", expires_at="2000-01-01T00:00:00Z"))
    run(capsys, "add-current")
    write_live(home, {})
    _, out = run(capsys, "list")
    api, expired = out["accounts"]
    assert api["usageStatus"] == "api_key" and api["usage"] is None
    assert api["usageNote"] == "usage not available"
    assert expired["usageStatus"] == "token_expired" and "expired" in expired["usageNote"]
    assert calls == []  # neither path touches the network


def test_401_marks_login_expired(env, capsys, monkeypatch):
    home, _ = env
    write_live(home, make_store("a@example.com", "u1"))
    run(capsys, "add-current")
    monkeypatch.setattr(grok, "_http_get", lambda u, h: (401, None))
    _, out = run(capsys, "list")
    assert out["accounts"][0]["usageStatus"] == "token_expired"


def test_cache_ttl_and_network_failure(env, capsys, monkeypatch):
    home, calls = env
    write_live(home, make_store("a@example.com", "u1"))
    run(capsys, "add-current")
    now = [1_000_000.0]
    monkeypatch.setattr(grok.time, "time", lambda: now[0])
    run(capsys, "list")
    now[0] += 30
    run(capsys, "list", "--refresh")
    assert len(calls) == 1  # forced refresh still floored at 60 s
    now[0] += 100
    run(capsys, "list")
    assert len(calls) == 1  # 130 s < 180 s TTL
    run(capsys, "list", "--refresh")
    assert len(calls) == 2

    def boom(url, headers):
        raise OSError("timed out")

    monkeypatch.setattr(grok, "_http_get", boom)
    now[0] += 200
    code, out = run(capsys, "list")
    row = out["accounts"][0]
    assert code == 0 and row["usageStatus"] == "unavailable" and row["usageError"] == "timeout"
    assert row["lastGoodUsage"]["sevenDay"]["pct"] == 37.5 and row["lastGoodAgeSeconds"] == 200


def test_enroll_remove_and_run(env, capsys, tmp_path, monkeypatch):
    iso = tmp_path / "iso"
    iso.mkdir()
    (iso / "auth.json").write_text(json.dumps(make_store("c@example.com", "u4", tag="c")), encoding="utf-8")
    code, out = run(capsys, "enroll", str(iso))
    assert code == 0 and out["added"]["email"] == "c@example.com"

    seen = {}

    def fake_call(cmd, env):
        home = env["GROK_HOME"]
        seen["home"] = home
        p = grok.Path(home) / "auth.json"
        seen["had_auth"] = p.is_file()
        p.write_text(json.dumps(make_store("c@example.com", "u4", tag="c2")), encoding="utf-8")
        return 0

    monkeypatch.setattr(grok.shutil, "which", lambda n: "grok")
    monkeypatch.setattr(grok.subprocess, "call", fake_call)
    code, out = run(capsys, "run", "1")
    assert code == 0 and seen["had_auth"]
    assert grok.Path(seen["home"]).is_relative_to(tmp_path / "store")
    assert not (grok.Path(seen["home"]) / "auth.json").exists()
    assert grok._unseal(1)[1]["key"].endswith("c2")

    code, out = run(capsys, "remove", "1")
    assert code == 0 and grok._roster() == [] and not grok._cred_path(1).exists()


def test_list_sweeps_orphaned_run_home(env, capsys):
    grok._import(grok.DEFAULT_SCOPE, make_record("b@example.com", "user-b", "b"))
    home = grok.store_dir() / "homes" / "1"
    home.mkdir(parents=True)
    (home / "auth.json").write_text(json.dumps(make_store("b@example.com", "user-b", "b2")), encoding="utf-8")
    run(capsys, "list")
    assert not (home / "auth.json").exists()
    assert grok._unseal(1)[1]["key"].endswith("-b2")


def test_list_paces_network_fetches(env, capsys, monkeypatch):
    home, calls = env
    write_live(home, make_store("a@example.com", "u1"))
    run(capsys, "add-current")
    write_live(home, {"xai::api_key": make_record("k@example.com", "u9", auth_mode="api_key")})
    run(capsys, "add-current")
    write_live(home, make_store("c@example.com", "u3", tag="c"))
    run(capsys, "add-current")
    monkeypatch.setattr(poll_policy, "USAGE_GAP_S", (2.0, 7.0))
    monkeypatch.setattr(poll_policy.random, "uniform", lambda a, b: b)
    monkeypatch.setattr(poll_policy.time, "sleep", calls.append)  # gaps land between requests
    run(capsys, "list")
    # the first goes at once; the API-key row makes no request, so no gap
    assert ["get" if isinstance(c, str) else c for c in calls] == ["get", 7.0, "get"]
    run(capsys, "list")
    assert len(calls) == 3  # all cached: no request, no wait
