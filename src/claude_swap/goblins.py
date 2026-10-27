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
    raise ClaudeSwitchError("Patch Goblins backend is missing. Install the complete .app or run macos/build.sh.")


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


class MenuExtension:
    def __init__(self):
        self.client = BackendClient()
        self.snapshot: dict = {}
        self._refreshing = False
        self._last_refresh = 0.0
        self._stopped = False
        self._restart_at = 0.0
        self._restart_count = 0

    def start(self, app):
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
                rumps.notification("Patch Goblins", message.get("title", ""), message.get("body", ""))
            elif event == "ui_error":
                rumps.alert(title="Patch Goblins", message=message["message"])
            elif event == "stopped":
                self._stopped = True
                self._restart_at = time.monotonic() + 3
                rumps.notification("Patch Goblins", "Supervisor stopped", "Work is held while the supervisor recovers its saved state.")
                app.rebuild_menu()
            elif event in ("changed", "ready"):
                self._refresh()
            elif event == "ui_changed":
                self._restart_count = 0
                app.rebuild_menu()
        if self._stopped and self._restart_count < 3 and time.monotonic() >= self._restart_at:
            self._restart_count += 1
            self._guard(self.restart)
        if time.monotonic() - self._last_refresh > 15:
            self._refresh()

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
        return panel.URL().path() if panel.runModal() == AppKit.NSModalResponseOK else None

    def menu(self, app, rumps):
        work = rumps.MenuItem("Patch Goblins · work" + (" (stopped)" if self._stopped else ""))
        if self._stopped:
            work.add(rumps.MenuItem("Restart supervisor", callback=lambda _: self._guard(self.restart)))
        work.add(rumps.MenuItem("Open agents terminal…", callback=lambda _: self.open_agents()))
        work.add(rumps.MenuItem("Open work dashboard…", callback=lambda _: self.open_dashboard()))
        work.add(rumps.MenuItem("Open account dashboard", callback=lambda _: self._guard(lambda: run_terminal(app_command("--accounts")))))
        work.add(rumps.MenuItem("Sign in another account…", callback=lambda _: self._guard(lambda: run_terminal(app_command("--add-account")))))
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
        return [work, None]

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
    parser.add_argument("--repo", default=os.getcwd())
    options = parser.parse_args(args)
    try:
        if options.add_account:
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
            menubar.run(ClaudeAccountSwitcher(), extension=MenuExtension(), app_name="Patch Goblins", icon=icon if icon.is_file() else None)
    except (ClaudeSwitchError, OSError) as exc:
        print(f"Patch Goblins: {exc}", file=sys.stderr)
        raise SystemExit(1) from None


if __name__ == "__main__":
    main()
