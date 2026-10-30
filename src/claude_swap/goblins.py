"""Patch Goblins' macOS shell; all account and orchestration policy stays upstream.

The menu bar owns the .NET supervisor. Terminal clients use its existing
per-user pipe, so a terminal never starts an independent scheduler. Credentials
are passed only through the existing claude-swap engine, never through this UI.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import queue
import runpy
import shlex
import shutil
import subprocess
import sys
import threading
import time

from claude_swap.exceptions import ClaudeSwitchError


def resources() -> Path:
    if os.environ.get("RESOURCEPATH"):
        return Path(os.environ["RESOURCEPATH"])
    if getattr(sys, "frozen", False):
        return Path(sys.executable).parent.parent / "Resources"
    return Path(__file__).resolve().parents[2]


def backend_command() -> list[str]:
    override = os.environ.get("PATCH_GOBLINS_BACKEND")
    if override:
        return [override]
    root = resources()
    names = [root / "backend" / "patch-goblins-backend",
             root / "build" / "macos" / "backend" / "patch-goblins-backend",
             root / "dock" / "PatchGoblins.Backend" / "bin" / "Release" / "net8.0" / "patch-goblins-backend",
             root / "dock" / "PatchGoblins.Backend" / "bin" / "Debug" / "net8.0" / "patch-goblins-backend"]
    for candidate in names:
        if candidate.is_file():
            return [str(candidate)]
    found = shutil.which("patch-goblins-backend")
    if found:
        return [found]
    raise ClaudeSwitchError("Patch Goblins backend is missing. Install the complete .app or run macos/build-macos.sh.")


def child_environment() -> dict[str, str]:
    env = dict(os.environ)
    # Finder/launchd omits Homebrew and the official Claude CLI install path.
    dirs = [str(Path.home() / ".local" / "bin"), "/opt/homebrew/bin", "/usr/local/bin"]
    env["PATH"] = os.pathsep.join(dict.fromkeys([*dirs, *env.get("PATH", "").split(os.pathsep)]))
    if getattr(sys, "frozen", False):
        env.setdefault("PATCH_GOBLINS_PYTHON", sys.executable)
        env.setdefault("PATCH_GOBLINS_PYTHON_ARGS", json.dumps(["--python-dispatch"]))
        engine = Path(sys.executable).parent / "cswap"
        if not engine.is_file():
            engine = resources() / "engine" / "cswap"
        if engine.is_file():
            env.setdefault("PATCH_GOBLINS_ENGINE", str(engine))
        else:
            env.setdefault("PATCH_GOBLINS_ENGINE", sys.executable)
            env.setdefault("PATCH_GOBLINS_ENGINE_ARGS", json.dumps(["--engine"]))
    return env


class BackendClient:
    """Concurrent JSONL client: approvals must arrive while a call is waiting."""
    def __init__(self, command=None, *, env=None):
        self.command = command or [*backend_command(), "--serve"]
        self.env = env or child_environment()
        self.events: queue.Queue[dict] = queue.Queue()
        self._pending: dict[int, queue.Queue] = {}
        self._lock = threading.Lock()
        self._serial = 0
        self.process = None
        self._closed = False

    def start(self):
        self.process = subprocess.Popen(self.command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                                        bufsize=1, env=self.env)
        threading.Thread(target=self._read, daemon=True, name="goblins-backend-reader").start()

    def _read(self):
        try:
            for line in self.process.stdout:
                try:
                    message = json.loads(line)
                except (ValueError, TypeError):
                    continue  # backend diagnostics are never displayed as protocol
                if not isinstance(message, dict):
                    continue
                if "event" in message:
                    self.events.put(message)
                else:
                    with self._lock:
                        pending = self._pending.get(message.get("id"))
                    if pending is not None:
                        pending.put(message)
        finally:
            with self._lock:
                for pending in self._pending.values():
                    pending.put({"error": {"message": "The Patch Goblins backend stopped."}})
            if not self._closed:
                self.events.put({"event": "stopped"})

    def request(self, method: str, params=None, *, timeout=90):
        result_queue: queue.Queue = queue.Queue(maxsize=2)
        with self._lock:
            if self.process is None or self.process.poll() is not None:
                raise ClaudeSwitchError("The backend is not running. Relaunch Patch Goblins.")
            self._serial += 1
            serial = self._serial
            self._pending[serial] = result_queue
            try:
                self.process.stdin.write(json.dumps({"id": serial, "method": method, "params": params or {}}) + "\n")
                self.process.stdin.flush()
            except (OSError, ValueError):
                self._pending.pop(serial, None)
                raise ClaudeSwitchError("The backend connection closed.") from None
        try:
            response = result_queue.get(timeout=timeout)
            if "error" in response:
                detail = response["error"]
                raise ClaudeSwitchError(detail.get("message", "Backend request failed") if isinstance(detail, dict) else str(detail))
            return response.get("result")
        except queue.Empty:
            raise ClaudeSwitchError(f"Backend request timed out ({method}).") from None
        finally:
            with self._lock:
                self._pending.pop(serial, None)

    def close(self):
        self._closed = True
        process = self.process
        if process is None:
            return
        if process.poll() is None:
            try:
                self.request("quit", timeout=10)
            except ClaudeSwitchError:
                pass
            try:
                process.stdin.close()
                process.wait(timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                process.terminate()
        for stream in (process.stdin, process.stdout):
            if stream:
                stream.close()


def run_terminal(argv: list[str], *, repo: str | None = None) -> Path:
    """Open an argv-safe private command file in the user's macOS Terminal."""
    if sys.platform != "darwin":
        raise ClaudeSwitchError("Opening Terminal is available on macOS only.")
    root = Path.home() / "Library" / "Application Support" / "Patch Goblins" / "terminal"
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    import uuid
    path = root / ("session-" + uuid.uuid4().hex + ".command")
    # Export only launcher configuration, never credentials. Delete the command
    # after the shell reads it; the session itself remains a normal terminal.
    lines = ["#!/bin/bash", "set -e", 'rm -- "$0"']
    if repo:
        lines.append("cd -- " + shlex.quote(str(Path(repo).expanduser().resolve())))
    env = child_environment()
    for key in ("PATH", "PATCH_GOBLINS_BACKEND", "PATCH_GOBLINS_ENGINE", "PATCH_GOBLINS_ENGINE_ARGS", "PATCH_GOBLINS_PYTHON", "PATCH_GOBLINS_PYTHON_ARGS", "PATCH_GOBLINS_DATA"):
        if key in env:
            lines.append("export " + key + "=" + shlex.quote(env[key]))
    lines.append("exec " + shlex.join(argv))
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o700)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines) + "\n")
    subprocess.run(["/usr/bin/open", "-a", "Terminal", str(path)], check=True)
    return path


def app_command(*args: str) -> list[str]:
    if getattr(sys, "frozen", False):
        return [sys.executable, *args]
    return [sys.executable, "-m", "claude_swap.goblins", *args]


def add_account() -> str:
    """Reuse official isolated login + Keychain capture without swapping a live login."""
    import tempfile
    from claude_swap.enroll import enroll
    from claude_swap.session import AUTH_OVERRIDE_ENV_VARS, delete_macos_keychain_entry
    from claude_swap.switcher import ClaudeAccountSwitcher
    env = child_environment()
    claude = shutil.which("claude", path=env["PATH"])
    if not claude:
        raise ClaudeSwitchError("Install Claude Code before adding an account.")
    with tempfile.TemporaryDirectory(prefix="patch-goblins-login-") as temp:
        profile = Path(temp)
        for name in AUTH_OVERRIDE_ENV_VARS:
            env.pop(name, None)
        env["CLAUDE_CONFIG_DIR"] = temp
        env["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = temp
        try:
            outcome = subprocess.run([claude, "auth", "login"], env=env, check=False)
            if outcome.returncode != 0:
                raise ClaudeSwitchError("Claude Code login did not finish. No account was added.")
            return enroll(ClaudeAccountSwitcher(), temp)
        finally:
            if sys.platform == "darwin":
                delete_macos_keychain_entry(profile)


def add_provider(provider: str) -> str:
    """Reuse the Windows tray's official temporary-home login and provider engine."""
    import importlib
    import tempfile
    if provider not in ("codex", "grok"):
        raise ClaudeSwitchError("Choose Codex or Grok.")
    env = child_environment()
    binary = shutil.which(provider, path=env["PATH"])
    if not binary:
        raise ClaudeSwitchError(f"Install the official {provider} CLI first.")
    with tempfile.TemporaryDirectory(prefix=f"patch-goblins-{provider}-login-") as temporary:
        env["CODEX_HOME" if provider == "codex" else "GROK_HOME"] = temporary
        for name in ("GROK_AUTH", "GROK_AUTH_PATH", "OPENAI_API_KEY", "CODEX_API_KEY", "XAI_API_KEY"):
            env.pop(name, None)
        if provider == "codex":
            (Path(temporary) / "config.toml").write_text('cli_auth_credentials_store = "file"\n', encoding="utf-8")
        if subprocess.run([binary, "login"], env=env, check=False).returncode != 0:
            raise ClaudeSwitchError(f"{provider} login did not finish. No account was added.")
        module = importlib.import_module("claude_swap.providers." + provider)
        provider_error = module.CodexError if provider == "codex" else module.GrokError
        # Preserve the existing default login on first enrollment, as Windows
        # does. These reads never refresh or rotate its single-use token.
        if not module._roster():
            try:
                module.cmd_add_current()
            except provider_error:
                pass  # a machine with no prior login simply enrolls the new one
        try:
            return module.cmd_enroll(temporary)["added"]["email"]
        except (provider_error, OSError, ValueError) as exc:
            raise ClaudeSwitchError(str(exc)) from None


def provider_terminal(provider: str, account: int | None) -> int:
    if provider not in ("codex", "grok"):
        raise ClaudeSwitchError("Choose Codex or Grok.")
    if account is not None:
        import importlib
        module = importlib.import_module("claude_swap.providers." + provider)
        provider_error = module.CodexError if provider == "codex" else module.GrokError
        try:
            result = module.cmd_run(account)
        except (provider_error, OSError, ValueError) as exc:
            raise ClaudeSwitchError(str(exc)) from None
        return int(result.get("exitCode", 0))
    env = child_environment()
    binary = shutil.which(provider, path=env["PATH"])
    if not binary:
        raise ClaudeSwitchError(f"Install the official {provider} CLI first.")
    return subprocess.run([binary], env=env, check=False).returncode


def provider_account_label(account: dict) -> str:
    from claude_swap.menubar import format_account_label
    def window(value):
        return {**value, "resets_at": value.get("resetsAt", value.get("resets_at"))} if isinstance(value, dict) else value
    usage = account.get("usage") or account.get("lastGoodUsage")
    if isinstance(usage, dict):
        usage = {"five_hour": window(usage.get("fiveHour")), "seven_day": window(usage.get("sevenDay")),
                 "scoped": [window(value) for value in usage.get("scoped") or []]}
    else:
        usage = account.get("usageNote") or account.get("usageStatus") or "usage unavailable"
    text = format_account_label(account.get("number"), account.get("email", ""), usage, alias=account.get("alias"))
    return text + (" (stale)" if account.get("usage") is None and account.get("lastGoodUsage") else "")


class MenuExtension:
    hosts_auto_switch = True

    def __init__(self, *, native_smoke=False):
        self.client = BackendClient()
        self.snapshot: dict = {}
        self._refreshing = False
        self._last_refresh = 0.0
        self._stopped = False
        self._restart_at = 0.0
        self._restart_count = 0
        self.native_smoke = native_smoke
        self.smoke_passed = False
        self._smoke_deadline = time.monotonic() + 20

    def start(self, app):
        self.switcher = app.switcher
        self.client.start()
        self._refresh()

    def _background(self, fn):
        def invoke():
            try:
                fn()
            except Exception:
                self.client.events.put({"event": "ui_error", "message": "The action failed. Check the backend status and try again."})
        threading.Thread(target=invoke, daemon=True).start()

    def _refresh(self):
        if self._refreshing or self._stopped:
            return
        self._refreshing = True
        def take():
            try:
                self.snapshot = self.client.request("status") or {}
                self._last_refresh = time.monotonic()
                self.client.events.put({"event": "ui_changed"})
            finally:
                self._refreshing = False
        self._background(take)

    def tick(self, app):
        import rumps
        if self.native_smoke and time.monotonic() > self._smoke_deadline:
            self._finish_smoke(app, False)
            return
        while True:
            try:
                message = self.client.events.get_nowait()
            except queue.Empty:
                break
            event = message.get("event")
            if event == "approval":
                import AppKit
                AppKit.NSApplication.sharedApplication().activateIgnoringOtherApps_(True)
                approved = rumps.alert(title=message.get("title", "Patch Goblins approval"),
                                       message=message.get("body", ""), ok="Approve", cancel="Cancel") == 1
                self._background(lambda m=message, yes=approved: self.client.request("approval", {"id": m["id"], "approved": yes}))
            elif event == "notify":
                if not self.native_smoke:
                    rumps.notification("Patch Goblins", message.get("title", ""), message.get("body", ""))
            elif event == "ui_error":
                if self.native_smoke:
                    self._finish_smoke(app, False)
                    return
                rumps.alert(title="Patch Goblins", message=message["message"])
            elif event == "ui_copy":
                import AppKit
                board = AppKit.NSPasteboard.generalPasteboard()
                board.clearContents()
                board.setString_forType_(message["text"], AppKit.NSPasteboardTypeString)
                rumps.notification("Patch Goblins", "Hand-off ready", "Paste the copied prompt into Grok to continue with the notes.")
            elif event == "stopped":
                self._stopped = True
                self._restart_at = time.monotonic() + 3
                rumps.notification("Patch Goblins", "Supervisor stopped", "Work is held while the supervisor recovers its saved state.")
                app.rebuild_menu()
            elif event in ("changed", "ready"):
                self._refresh()
            elif event == "ui_changed":
                self._restart_count = 0
                config = self.snapshot.get("config", {})
                enabled = config.get("autoSwitch") if isinstance(config, dict) else None
                if isinstance(enabled, bool) and app.settings.auto_switch_enabled != enabled:
                    app.settings.auto_switch_enabled = enabled
                    app.settings.save(app.switcher.backup_dir / "menubar_settings.json")
                app.rebuild_menu()
                if self.native_smoke and self.snapshot.get("schemaVersion") == 1 and isinstance(self.snapshot.get("accounts"), dict):
                    self._finish_smoke(app, True)
                    return
        if self._stopped and self._restart_count < 3 and time.monotonic() >= self._restart_at:
            self._restart_count += 1
            self._guard(self.restart)
        if time.monotonic() - self._last_refresh > (0.5 if self.native_smoke else 15):
            self._refresh()

    def _finish_smoke(self, app, passed):
        self.smoke_passed = passed
        if passed:
            print("Native menu bar, supervisor bridge and bundled credential engine: passed", flush=True)
            app.on_quit(None)
        else:
            print("Native menu/backend smoke failed to obtain an account snapshot", file=sys.stderr, flush=True)
            self.stop()
            os._exit(1)  # testing only, isolated HOME verified before any native startup

    def restart(self):
        self.client.close()
        self.client = BackendClient()
        self.client.start()
        self._stopped = False
        self._refreshing = False
        self._refresh()

    def auto_switch_changed(self, enabled):
        self._background(lambda: self.client.request("settings", {"values": {"autoSwitch": bool(enabled)}}))

    def _choose_repo(self):
        import AppKit
        AppKit.NSApplication.sharedApplication().activateIgnoringOtherApps_(True)
        panel = AppKit.NSOpenPanel.openPanel()
        panel.setCanChooseFiles_(False)
        panel.setCanChooseDirectories_(True)
        panel.setAllowsMultipleSelection_(False)
        panel.setMessage_("Choose the root of a Git repository")
        initial = self.snapshot.get("config", {}).get("terminalFolder")
        if initial and Path(initial).is_dir():
            panel.setDirectoryURL_(AppKit.NSURL.fileURLWithPath_(initial))
        return panel.URL().path() if panel.runModal() == AppKit.NSModalResponseOK else None

    def menu(self, app, rumps):
        work = rumps.MenuItem("Patch Goblins · work" + (" (stopped)" if self._stopped else ""))
        if self._stopped:
            work.add(rumps.MenuItem("Restart supervisor", callback=lambda _: self._guard(self.restart)))
        work.add(rumps.MenuItem("Open agents terminal…", callback=lambda _: self.open_agents()))
        work.add(rumps.MenuItem("Open work dashboard…", callback=lambda _: self.open_dashboard()))
        work.add(rumps.MenuItem("Open account dashboard", callback=lambda _: self._guard(lambda: run_terminal(app_command("--accounts")))))
        work.add(rumps.MenuItem("Sign in another account…", callback=lambda _: self._guard(lambda: run_terminal(app_command("--add-account")))))
        work.add(rumps.MenuItem("New general agent", callback=lambda _: self._background(lambda: self.client.request("new_agent"))))
        work.add(rumps.MenuItem("Prime fresh 5-hour windows", callback=lambda _: self._background(self._prime)))
        prime_status = rumps.MenuItem("Auto-Prime status")
        for line in self.snapshot.get("primeStatus", []) or ["No fresh 5-hour windows right now"]:
            prime_status.add(rumps.MenuItem(str(line)))
        work.add(prime_status)
        work.add(self._account_tools(app, rumps))
        work.add(self._desktop_settings(rumps))
        handoffs = rumps.MenuItem("Continue interrupted work")
        for source in ("claude", "codex"):
            for target in ("claude", "codex", "grok"):
                if source != target:
                    handoffs.add(rumps.MenuItem(f"{source.title()} → {target.title()}…", callback=lambda _, s=source, t=target: self._handoff(s, t)))
        work.add(handoffs)
        repos = self.snapshot.get("repos", self.snapshot.get("Repos", []))
        for repo in repos if isinstance(repos, list) else []:
            path = repo.get("path", repo.get("Path", ""))
            if not path:
                continue
            submenu = rumps.MenuItem(Path(path).name)
            for action in ("pause", "resume", "stop"):
                submenu.add(rumps.MenuItem(action.title(), callback=lambda _, p=path, a=action: self._background(lambda: self.client.request("call", {"tool": "agents_control", "args": {"action": a}, "repo": p}))))
            work.add(submenu)
        login = rumps.MenuItem("Launch at login", callback=lambda _: self._guard(self.toggle_login))
        from claude_swap.launch_agent import plist_path
        login.state = int(plist_path("com.patch-goblins.menubar").exists())
        work.add(login)
        return [work, *self._provider_menus(rumps), None]

    def _provider_menus(self, rumps):
        providers = self.snapshot.get("providers", {})
        menus = []
        for provider in ("codex", "grok"):
            section = rumps.MenuItem(provider.title())
            data = providers.get(provider, {}) if isinstance(providers, dict) else {}
            for account in data.get("accounts", []) if isinstance(data, dict) else []:
                number, active = account["number"], account.get("active", False)
                item = rumps.MenuItem(provider_account_label(account))
                item.state = int(active)
                item.add(rumps.MenuItem("Open terminal…", callback=lambda _, p=provider, n=number, a=active: self._provider_open(p, None if a else n)))
                item.add(rumps.MenuItem("Switch account", callback=lambda _, p=provider, n=number: self._provider_switch(p, n)))
                item.add(rumps.MenuItem("Remove account", callback=lambda _, p=provider, n=number: self._provider_remove(p, n)))
                section.add(item)
            if not isinstance(data, dict) or not data.get("accounts"):
                section.add(rumps.MenuItem("No managed accounts"))
            section.add(rumps.MenuItem("Sign in another account…", callback=lambda _, p=provider: self._guard(lambda: run_terminal(app_command("--add-provider", p)))))
            section.add(rumps.MenuItem("Open current login…", callback=lambda _, p=provider: self._provider_open(p, None)))
            menus.append(section)
        return menus

    def _provider_open(self, provider, number):
        def opened():
            folder = self._choose_repo()
            if folder:
                args = ["--provider-terminal", provider]
                if number is not None:
                    args += ["--account", str(number)]
                run_terminal(app_command(*args), repo=folder)
        self._guard(opened)

    def _provider_switch(self, provider, number):
        def switched():
            self.client.request("provider_switch", {"provider": provider, "number": number})
            self.client.events.put({"event": "notify", "title": f"{provider.title()} account selected", "body": "New sessions use this account. Existing sessions keep their account until restarted."})
            self._refresh()
        self._background(switched)

    def _provider_remove(self, provider, number):
        import rumps
        if rumps.alert(title="Remove account", message=f"Remove {provider.title()} account {number} from Patch Goblins?", ok="Remove", cancel="Cancel") == 1:
            def removed():
                self.client.request("provider_command", {"provider": provider, "args": ["remove", str(number)]})
                self._refresh()
            self._background(removed)

    def _desktop_settings(self, rumps):
        config = self.snapshot.get("config", {})
        menu = rumps.MenuItem("Agent settings")
        for key, title in (("autoPrime", "Auto-Prime"), ("autoLine", "Adaptive switch threshold"),
                           ("quotaNotifications", "Quota notifications"), ("agentDraftPrs", "Draft pull requests"),
                           ("agentUltracode", "Allow ultracode"), ("askApproval", "Ask for approvals"),
                           ("preserveRemoteControl", "Restore Remote Control"), ("autoProjectAccount", "Prefer folder account")):
            value = bool(config.get(key, key in ("autoLine", "askApproval", "preserveRemoteControl")))
            item = rumps.MenuItem(title, callback=lambda _, k=key, v=value: self._toggle_setting(k, not v, config))
            item.state = int(value)
            menu.add(item)
        for key, title in (("agentIssues", "Issues at once"), ("agentOpenPrs", "Open pull request limit")):
            menu.add(rumps.MenuItem(title + "…", callback=lambda _, k=key, t=title: self._set_number(k, t)))
        menu.add(rumps.MenuItem("Choose terminal start folder…", callback=lambda _: self._set_terminal_folder()))
        return menu

    def _prime(self):
        status = self.client.request("prime", timeout=300)
        self.client.events.put({"event": "notify", "title": "Auto-Prime", "body": "\n".join(status) if isinstance(status, list) and status else "No fresh 5-hour windows right now."})
        self._refresh()

    def _set_terminal_folder(self):
        def selected():
            folder = self._choose_repo()
            if folder:
                self._background(lambda: self._save_setting("terminalFolder", folder))
        self._guard(selected)

    def _set_number(self, key, title):
        response = self._input(title, "Enter a whole number (0 issues means automatic).")
        if response.clicked == 1:
            def saved():
                self.client.request("settings", {"values": {key: int(response.text.strip())}})
                self._refresh()
            self._background(saved)

    def _toggle_setting(self, key, value, config):
        """Save a menu toggle."""
        self._background(lambda: self._save_setting(key, value))

    def _save_setting(self, key, value):
        self.client.request("settings", {"values": {key: value}})
        self._refresh()

    @staticmethod
    def _input(title, message):
        import AppKit
        import rumps
        AppKit.NSApplication.sharedApplication().activateIgnoringOtherApps_(True)
        return rumps.Window(title=title, message=message, ok="Save", cancel="Cancel", dimensions=(320, 24)).run()

    def _account_tools(self, app, rumps):
        menu = rumps.MenuItem("Account tools")
        for number, email, _active, _display, _usage, alias, _disabled, _at in app.snapshot["accounts"]:
            account = rumps.MenuItem(f"{number} · {alias or email}")
            account.add(rumps.MenuItem("Open terminal…", callback=lambda _, n=number: self._claude_terminal(n)))
            account.add(rumps.MenuItem("Set alias…", callback=lambda _, n=number: self._alias(app, n)))
            account.add(rumps.MenuItem("Assign folder…", callback=lambda _, n=number: self._map(app, n)))
            menu.add(account)
        menu.add(rumps.MenuItem("Remove folder mapping…", callback=lambda _: self._map(app, None)))
        menu.add(rumps.MenuItem("View folder mappings", callback=lambda _: self._guard(lambda: run_terminal(app_command("--engine", "map")))))
        return menu

    def _claude_terminal(self, number):
        def opened():
            folder = self._choose_repo()
            if folder:
                config = self.snapshot.get("config", {})
                args = ["--engine", "run"]
                mapped = False
                if config.get("autoProjectAccount") and hasattr(self, "switcher"):
                    from claude_swap.mappings import MappingStore
                    mapped = MappingStore(self.switcher.backup_dir).resolve(folder) is not None
                if not mapped:
                    args.append(str(number))
                if config.get("preserveRemoteControl", True):
                    args.append("--restore-rc")
                run_terminal(app_command(*args), repo=folder)
        self._guard(opened)

    def _alias(self, app, number):
        response = self._input("Account alias", "Letters, digits, dot, dash or underscore. Leave blank to remove.")
        if response.clicked == 1:
            def saved():
                if response.text.strip():
                    app.switcher.set_alias(str(number), response.text.strip())
                else:
                    app.switcher.unset_alias(str(number))
                app.refresh_async()
            self._guard(saved)

    def _map(self, app, number):
        def mapped():
            folder = self._choose_repo()
            if not folder:
                return
            from claude_swap.mappings import MappingStore
            store = MappingStore(app.switcher.backup_dir)
            if number is None:
                store.remove(folder)
            else:
                _, email, org = app.switcher.resolve_account(str(number))
                store.set(folder, email, org)
            app.refresh_async()
            self._refresh()
        self._guard(mapped)

    def _handoff(self, source, target):
        def chosen():
            folder = self._choose_repo()
            if folder:
                def continued():
                    result = self.client.request("handoff", {"source": source, "target": target, "folder": folder}, timeout=180)
                    if target == "grok":
                        prompt = result.get("prompt", "") if isinstance(result, dict) else str(result or "")
                        if prompt:
                            self.client.events.put({"event": "ui_copy", "text": prompt})
                self._background(continued)
        self._guard(chosen)

    @staticmethod
    def _guard(fn):
        import rumps
        try:
            fn()
        except (ClaudeSwitchError, OSError, subprocess.SubprocessError) as exc:
            rumps.alert(title="Patch Goblins", message=str(exc))

    def open_agents(self):
        def opened():
            repo = self._choose_repo()
            if repo:
                subprocess.Popen([*backend_command(), "agents-terminal", "--repo", repo], env=child_environment(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self._guard(opened)

    def open_dashboard(self):
        def opened():
            repo = self._choose_repo()
            if repo:
                run_terminal(app_command("--tui", "--repo", repo), repo=repo)
        self._guard(opened)

    @staticmethod
    def toggle_login():
        from claude_swap import launch_agent
        label = "com.patch-goblins.menubar"
        if launch_agent.plist_path(label).exists():
            # Do not boot out our own current process from a menu callback.
            launch_agent.plist_path(label).unlink()
            subprocess.run(["launchctl", "disable", launch_agent.service_target(label)], capture_output=True, check=False)
        else:
            subprocess.run(["launchctl", "enable", launch_agent.service_target(label)], capture_output=True, check=False)
            # Write for the next login. Bootstrapping now would create another UI.
            target = launch_agent.plist_path(label)
            target.parent.mkdir(parents=True, exist_ok=True)
            launch_agent.log_paths(label)[0].parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(launch_agent.build_plist(app_command(), label))

    def stop(self):
        self.client.close()


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    if args and args[0] in ("--engine", "--python-dispatch", "--worker-host"):
        mode = args.pop(0)
        if mode == "--worker-host" or (mode == "--python-dispatch" and args and args[0].endswith("worker_host.py")):
            if not args or Path(args[0]).name != "worker_host.py" or not Path(args[0]).is_file():
                raise SystemExit("Worker helper is missing")
            sys.argv = args
            runpy.run_path(args[0], run_name="__main__")
        else:
            from claude_swap.cli import main as engine_main
            sys.argv = ["cswap", *args]
            engine_main()
        return
    parser = argparse.ArgumentParser(prog="patch-goblins")
    parser.add_argument("mode", nargs="?", choices=["menubar"], default="menubar")
    parser.add_argument("--tui", action="store_true", help="Open the work dashboard (menu bar must be running)")
    parser.add_argument("--accounts", action="store_true", help="Open the existing account dashboard")
    parser.add_argument("--add-account", action="store_true", help="Sign in through the official Claude CLI in an isolated profile")
    parser.add_argument("--native-smoke", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--add-provider", choices=["codex", "grok"])
    parser.add_argument("--provider-terminal", choices=["codex", "grok"])
    parser.add_argument("--account", type=int)
    parser.add_argument("--repo", default=os.getcwd())
    options = parser.parse_args(args)
    try:
        if options.add_provider:
            print("Added account: " + add_provider(options.add_provider))
        elif options.provider_terminal:
            raise SystemExit(provider_terminal(options.provider_terminal, options.account))
        elif options.add_account:
            print("Added account: " + add_account())
        elif options.tui:
            from claude_swap.goblins_ui import GoblinsApp
            GoblinsApp(str(Path(options.repo).expanduser().resolve())).run()
        elif options.accounts:
            from claude_swap.switcher import ClaudeAccountSwitcher
            from claude_swap.tui.app import CswapApp
            app = CswapApp(ClaudeAccountSwitcher())
            app.TITLE = "Patch Goblins · accounts"
            app.run()
        else:
            if sys.platform != "darwin":
                raise ClaudeSwitchError("The native menu bar is available on macOS only. Use --tui for the work dashboard.")
            from claude_swap import menubar
            from claude_swap.switcher import ClaudeAccountSwitcher
            icon = resources() / "goblin.png"
            if options.native_smoke:
                smoke_home = os.environ.get("PATCH_GOBLINS_SMOKE_HOME")
                smoke_data = os.environ.get("PATCH_GOBLINS_DATA")
                if not smoke_home or not smoke_data or Path(smoke_home).resolve() != Path.home().resolve() or not Path(smoke_data).resolve().is_relative_to(Path(smoke_home).resolve()):
                    raise ClaudeSwitchError("Native smoke requires an explicitly isolated HOME and PATCH_GOBLINS_DATA.")
                profile = Path(smoke_home) / "claude-smoke"
                profile.mkdir(mode=0o700, exist_ok=True)
                os.environ["CLAUDE_CONFIG_DIR"] = str(profile)
                os.environ["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = str(profile)
            extension = MenuExtension(native_smoke=options.native_smoke)
            menubar.run(ClaudeAccountSwitcher(), extension=extension, app_name="Patch Goblins", icon=icon if icon.is_file() else None)
            if options.native_smoke:
                if not extension.smoke_passed:
                    raise ClaudeSwitchError("Native menu/backend smoke did not obtain an account snapshot.")
                print("Native menu bar, supervisor bridge and bundled credential engine: passed")
    except (ClaudeSwitchError, OSError) as exc:
        print(f"Patch Goblins: {exc}", file=sys.stderr)
        raise SystemExit(1) from None


if __name__ == "__main__":
    main()
