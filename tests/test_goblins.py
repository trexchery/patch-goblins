"""Synthetic desktop transport/lifecycle tests, with no account or GitHub access."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import threading
import queue
import time
import types
from unittest.mock import MagicMock, patch

import pytest

from claude_swap import goblins
from claude_swap.exceptions import ClaudeSwitchError, CredentialReadError
from claude_swap.goblins_ui import parse_command


FAKE_HOST = r'''
import json, sys
waiting = None
for line in sys.stdin:
    m = json.loads(line)
    if m['method'] == 'call':
        waiting = m['id']
        print(json.dumps({'event':'approval','id':'native-1','title':'Confirm check','body':'synthetic check'}), flush=True)
    elif m['method'] == 'approval':
        print(json.dumps({'id':m['id'],'result':{'answered':True}}), flush=True)
        print(json.dumps({'id':waiting,'result':'approved' if m['params']['approved'] else 'cancelled'}), flush=True)
    elif m['method'] == 'crash':
        sys.exit(7)
    elif m['method'] == 'quit':
        print(json.dumps({'id':m['id'],'result':True}), flush=True)
        break
    else:
        print(json.dumps({'id':m['id'],'result':{'schemaVersion':1,'repos':[]}}), flush=True)
'''


@pytest.fixture
def client():
    value = goblins.BackendClient([sys.executable, "-u", "-c", FAKE_HOST])
    value.start()
    try:
        yield value
    finally:
        value.close()


@pytest.mark.parametrize("approved,expected", [(True, "approved"), (False, "cancelled")])
def test_approval_roundtrip_does_not_deadlock_pending_call(client, approved, expected):
    answers = []
    worker = threading.Thread(target=lambda: answers.append(client.request("call", {"tool": "synthetic"}, timeout=8)))
    worker.start()
    event = client.events.get(timeout=5)
    assert event["event"] == "approval"
    assert client.request("approval", {"id": event["id"], "approved": approved}) == {"answered": True}
    worker.join(timeout=8)
    assert not worker.is_alive()
    assert answers == [expected]
    assert client.request("status")["schemaVersion"] == 1


def test_backend_crash_unblocks_waiting_request_and_reports_stop(client):
    with pytest.raises(ClaudeSwitchError, match="backend stopped"):
        client.request("crash", timeout=8)
    assert client.events.get(timeout=5)["event"] == "stopped"


def test_backend_close_waits_for_clean_shutdown(client):
    process = client.process
    client.close()
    assert process.poll() == 0


def test_frozen_launcher_uses_bundled_engine_and_worker_dispatch(monkeypatch, tmp_path):
    binary = tmp_path / "Contents" / "MacOS" / "Patch Goblins"
    binary.parent.mkdir(parents=True)
    (binary.parent / "cswap").touch()
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "executable", str(binary))
    for name in ("PATCH_GOBLINS_ENGINE", "PATCH_GOBLINS_ENGINE_ARGS", "PATCH_GOBLINS_PYTHON", "PATCH_GOBLINS_PYTHON_ARGS"):
        monkeypatch.delenv(name, raising=False)
    env = goblins.child_environment()
    assert env["PATCH_GOBLINS_ENGINE"] == str(binary.parent / "cswap")
    assert json.loads(env["PATCH_GOBLINS_PYTHON_ARGS"]) == ["--python-dispatch"]
    assert env["PATCH_GOBLINS_PYTHON"] == str(binary)


def test_engine_dispatch_happens_without_loading_native_ui(monkeypatch):
    monkeypatch.setattr(sys, "argv", ["original"])
    with patch("claude_swap.cli.main") as engine:
        goblins.main(["--engine", "list", "--json"])
    engine.assert_called_once_with()
    assert sys.argv == ["cswap", "list", "--json"]


def test_native_smoke_refuses_unisolated_account_stores(monkeypatch):
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.delenv("PATCH_GOBLINS_SMOKE_HOME", raising=False)
    with patch("claude_swap.menubar.run") as native:
        with pytest.raises(SystemExit) as failure:
            goblins.main(["--native-smoke"])
    assert failure.value.code == 1
    native.assert_not_called()


def test_backend_auto_switch_setting_updates_menu_without_rewriting_backend(monkeypatch, tmp_path):
    from claude_swap.menubar import MenuBarSettings
    monkeypatch.setitem(sys.modules, "rumps", types.SimpleNamespace())
    client = MagicMock()
    client.events = queue.Queue()
    with patch("claude_swap.goblins.BackendClient", return_value=client):
        extension = goblins.MenuExtension()
    extension._last_refresh = time.monotonic()
    extension.snapshot = {"config": {"autoSwitch": True}}
    settings = MenuBarSettings(auto_switch_enabled=False)
    settings.save = MagicMock(wraps=settings.save)
    app = types.SimpleNamespace(settings=settings,
                                switcher=types.SimpleNamespace(backup_dir=tmp_path),
                                rebuild_menu=MagicMock())
    client.events.put({"event": "ui_changed"})
    extension.tick(app)
    assert settings.auto_switch_enabled is True
    assert json.loads((tmp_path / "menubar_settings.json").read_text())["auto_switch_enabled"] is True
    settings.save.assert_called_once()
    client.request.assert_not_called()
    client.events.put({"event": "ui_changed"})
    extension.tick(app)
    settings.save.assert_called_once()  # an unchanged backend snapshot does not churn the file


def test_terminal_command_quotes_paths_and_never_uses_shell(monkeypatch, tmp_path):
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.setattr(Path, "home", lambda: tmp_path)
    repo = tmp_path / "a folder's $repo"
    repo.mkdir()
    with patch("claude_swap.goblins.subprocess.run") as opened:
        script = goblins.run_terminal(["/app/Patch Goblins", "--tui", "--repo", str(repo)], repo=str(repo))
    content = script.read_text()
    assert "exec '/app/Patch Goblins' --tui --repo " in content
    assert 'rm -- "$0"' in content
    assert "'\"'\"'" in content
    opened.assert_called_once_with(["/usr/bin/open", "-a", "Terminal", str(script)], check=True)


@pytest.mark.parametrize("command,tool,args", [
    ('start {"verify":"npm test","base":"main"}', "agents_control", {"action": "start", "verify": "npm test", "base": "main"}),
    ("pause", "agents_control", {"action": "pause"}),
    ("goals", "agents_goal", {"action": "list"}),
    ('queue {"action":"answer","item":"w1","text":"Use green"}', "agents_queue", {"action": "answer", "item": "w1", "text": "Use green"}),
    ('settings {"agentDraftPrs":true}', "desktop_settings", {"agentDraftPrs": True}),
])
def test_terminal_routes_existing_tools_without_reimplementing_policy(command, tool, args):
    assert parse_command(command) == (tool, args)


def test_goal_preserves_owner_words():
    tool, args = parse_command("goal: ship the complete macOS app; checks must pass")
    assert tool == "agents_goal"
    assert args["text"] == "ship the complete macOS app; checks must pass"
    assert args["action"] == "set"


@pytest.mark.parametrize("command", ["goal:", "unknown", "status []", "pause {invalid}"])
def test_terminal_rejects_invalid_tools_and_payloads(command):
    with pytest.raises(ValueError):
        parse_command(command)


def test_macos_enrollment_reads_keychain_profile_without_plaintext_seed(tmp_path):
    from claude_swap.enroll import build_envelope
    (tmp_path / ".claude.json").write_text(json.dumps({"oauthAccount": {"emailAddress": "synthetic@example.com"}}))
    credentials = json.dumps({"claudeAiOauth": {"accessToken": "SYNTHETIC-access", "refreshToken": "SYNTHETIC-refresh"}})
    with patch("claude_swap.enroll.read_config_dir_credentials", return_value=credentials) as read:
        envelope = build_envelope(tmp_path, 1)
    read.assert_called_once_with(str(tmp_path), strict_keychain=True)
    assert envelope["accounts"][0]["email"] == "synthetic@example.com"
    assert not (tmp_path / ".credentials.json").exists()


def test_macos_enrollment_refuses_unreadable_keychain_even_if_seed_exists(tmp_path):
    from claude_swap.enroll import build_envelope
    (tmp_path / ".credentials.json").write_text("stale")
    with patch("claude_swap.enroll.read_config_dir_credentials", side_effect=CredentialReadError("locked")):
        with pytest.raises(CredentialReadError, match="locked"):
            build_envelope(tmp_path, 1)


def test_official_login_is_isolated_and_secret_overrides_removed(monkeypatch, temp_home):
    captured = {}
    monkeypatch.setenv("ANTHROPIC_API_KEY", "SYNTHETIC-api-key")
    def login(argv, **kwargs):
        captured.update(kwargs["env"])
        assert argv == ["/fake/claude", "auth", "login"]
        assert "ANTHROPIC_API_KEY" not in kwargs["env"]
        return subprocess.CompletedProcess(argv, 0)
    with patch("claude_swap.goblins.shutil.which", return_value="/fake/claude"), \
         patch("claude_swap.goblins.subprocess.run", side_effect=login), \
         patch("claude_swap.enroll.enroll", return_value="synthetic@example.com") as enroll:
        assert goblins.add_account() == "synthetic@example.com"
    assert captured["CLAUDE_CONFIG_DIR"] == captured["CLAUDE_SECURESTORAGE_CONFIG_DIR"]
    assert not Path(captured["CLAUDE_CONFIG_DIR"]).exists()
    assert enroll.call_args.args[1] == captured["CLAUDE_CONFIG_DIR"]


@pytest.mark.asyncio
async def test_work_dashboard_runs_tools_and_releases_repository_lease(monkeypatch, tmp_path):
    from claude_swap.goblins_ui import GoblinsApp
    calls = []
    class InputPipe:
        def __init__(self):
            self.closed = False
        def close(self):
            self.closed = True
    class Process:
        def __init__(self, lease=False):
            self.stdin = InputPipe()
            self.returncode = None if lease else 0
        async def communicate(self):
            return b"Synthetic agent status", b""
        async def wait(self):
            self.returncode = 0
            return 0
        def terminate(self):
            self.returncode = -15
    lease = Process(True)
    async def create(*argv, **kwargs):
        calls.append(argv)
        return lease if "agents-mcp" in argv else Process()
    monkeypatch.setattr("claude_swap.goblins_ui.backend_command", lambda: ["synthetic-backend"])
    monkeypatch.setattr("claude_swap.goblins_ui.asyncio.create_subprocess_exec", create)
    app = GoblinsApp(str(tmp_path))
    async with app.run_test() as pilot:
        await pilot.pause()
        await pilot.click("#pause")
        await pilot.pause()
        assert any("agents-mcp" in call for call in calls)
        assert any("agents_control" in call and '{"action": "pause"}' in call for call in calls)
    assert lease.stdin.closed
