"""Executable desktop protocol contract, with synthetic engine and local Git only.

Run after dotnet build with PATCH_GOBLINS_TEST_BACKEND set to the apphost.
The same tests exercise Linux DDC and the Apple Silicon executable in CI.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

import pytest


@pytest.fixture
def host(tmp_path):
    executable = os.environ.get("PATCH_GOBLINS_TEST_BACKEND")
    if not executable:
        pytest.skip("Set PATCH_GOBLINS_TEST_BACKEND to the compiled backend apphost")
    engine = tmp_path / "synthetic_engine.py"
    engine.write_text('''import json, sys
if sys.argv[1] == "config":
    print(json.dumps({"schemaVersion":1,"settings":[{"key":"autoswitch.threshold","value":"90"}]}))
elif sys.argv[1] == "list":
    print(json.dumps({"schemaVersion":1,"activeAccountNumber":None,"accounts":[]}))
else:
    raise SystemExit(2)
''')
    env = dict(os.environ)
    for key in list(env):
        if key.startswith(("GH_", "GITHUB_", "ANTHROPIC_", "CLAUDE_", "HANDOFF_", "PATCH_GOBLINS_")):
            env.pop(key)
    env.update({"HOME": str(tmp_path), "USERPROFILE": str(tmp_path),
                "PATCH_GOBLINS_DATA": str(tmp_path / "data"),
                "PATCH_GOBLINS_ENGINE": sys.executable,
                "PATCH_GOBLINS_ENGINE_ARGS": json.dumps([str(engine)])})
    process = subprocess.Popen([executable, "--serve"], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               text=True, encoding="utf-8", env=env)
    incoming = queue.Queue()
    def read():
        for line in process.stdout:
            incoming.put(json.loads(line))
    thread = threading.Thread(target=read, daemon=True)
    thread.start()
    def send(id, method, params=None):
        process.stdin.write(json.dumps({"id": id, "method": method, "params": params or {}}) + "\n")
        process.stdin.flush()
    def receive(predicate):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            value = incoming.get(timeout=max(0.1, deadline - time.monotonic()))
            if predicate(value):
                return value
        raise AssertionError("Backend did not send the expected response")
    receive(lambda value: value.get("event") == "ready")
    yield process, send, receive, env
    if process.poll() is None:
        try:
            send(9999, "quit")
            process.wait(timeout=15)
        except (OSError, subprocess.TimeoutExpired):
            process.kill()
            process.wait(timeout=5)
    process.stdin.close()
    thread.join(timeout=5)
    process.stdout.close()
    process.stderr.close()


def test_executable_status_settings_and_private_pipe(host, tmp_path):
    process, send, receive, env = host
    send(1, "status")
    status = receive(lambda value: value.get("id") == 1)
    assert status["result"]["repos"] == []
    send(2, "settings", {"values": {"agentIssues": 3, "agentDraftPrs": True}})
    settings = receive(lambda value: value.get("id") == 2)["result"]
    assert settings["agentIssues"] == 3
    assert settings["agentDraftPrs"] is True
    peer = subprocess.run([process.args[0], "status"], env=env, capture_output=True, text=True, timeout=15)
    assert peer.returncode == 0, peer.stderr
    assert json.loads(peer.stdout)["config"]["agentIssues"] == 3
    assert (tmp_path / "data" / "settings.json").is_file()


def test_start_approval_does_not_block_reader_and_rejection_never_authorizes(host, tmp_path):
    process, send, receive, env = host
    repo = tmp_path / "repository"
    repo.mkdir()
    for args in (["init", "-b", "main"], ["config", "user.name", "Synthetic"],
                 ["config", "user.email", "synthetic@example.test"]):
        subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True)
    (repo / "README.md").write_text("synthetic test\n")
    subprocess.run(["git", "-C", str(repo), "add", "."], check=True)
    subprocess.run(["git", "-C", str(repo), "commit", "-m", "fixture"], check=True, capture_output=True)
    send(3, "call", {"tool": "agents_control", "args": {"action": "start", "verify": "exit 0"}, "repo": str(repo)})
    approval = receive(lambda value: value.get("event") == "approval")
    assert str(repo) in approval["body"]
    assert "exit 0" in approval["body"]
    send(4, "status")
    receive(lambda value: value.get("id") == 4)
    send(5, "approval", {"id": approval["id"], "approved": False})
    response = receive(lambda value: value.get("id") == 3)
    assert "did not confirm" in response["result"]
    send(6, "status")
    assert receive(lambda value: value.get("id") == 6)["result"]["repos"] == []


def test_second_supervisor_cannot_open_same_state_and_unknown_approval_is_rejected(host):
    process, send, receive, env = host
    duplicate = subprocess.run([process.args[0], "--serve"], env=env, input="", capture_output=True, text=True, timeout=15)
    assert duplicate.returncode != 0
    send(8, "approval", {"id": "invented-approval", "approved": True})
    assert receive(lambda value: value.get("id") == 8)["result"]["answered"] is False


def test_provider_menu_rejects_credential_export_and_empty_prime_sends_nothing(host):
    process, send, receive, env = host
    send(10, "provider_command", {"provider": "codex", "args": ["export"]})
    assert "menu can" in receive(lambda value: value.get("id") == 10)["error"]["message"]
    send(11, "provider_switch", {"provider": "unknown", "number": 1})
    assert "codex or grok" in receive(lambda value: value.get("id") == 11)["error"]["message"]
    send(12, "prime")
    assert receive(lambda value: value.get("id") == 12)["result"] == []
    send(13, "settings", {"values": {"autoProjectAccount": True, "terminalFolder": None}})
    result = receive(lambda value: value.get("id") == 13)["result"]
    assert result["autoProjectAccount"] is True and result["terminalFolder"] is None
