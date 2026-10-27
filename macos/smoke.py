"""Verify the actual frozen launchers, engine and detached worker, without a login."""

import json
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import time


def main(app: Path) -> None:
    app = app.resolve()
    executable = app / "Contents" / "MacOS" / "Patch Goblins"
    engine = executable.with_name("cswap")
    backend = app / "Contents" / "Resources" / "backend" / "patch-goblins-backend"
    helper = backend.with_name("worker_host.py")
    with tempfile.TemporaryDirectory(prefix="patch-goblins-smoke-") as directory:
        root = Path(directory)
        env = {k: v for k, v in os.environ.items() if k not in (
            "GH_TOKEN", "GITHUB_TOKEN", "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CONFIG_DIR", "RESOURCEPATH", "PYTHONPATH", "PYTHONHOME",
        )}
        env.update(HOME=str(root), USERPROFILE=str(root), XDG_DATA_HOME=str(root / "xdg"),
                   CLAUDE_CONFIG_DIR=str(root / "claude"), PATCH_GOBLINS_DATA=str(root / "data"))
        for command in ([engine, "--version"], [executable, "--engine", "--version"],
                        [backend, "--help"]):
            result = subprocess.run(command, env=env, text=True, capture_output=True, timeout=30)
            if result.returncode:
                raise RuntimeError(f"Frozen executable failed: {command[0].name}, exit {result.returncode}: {result.stderr}")
            print(result.stdout.strip())
        source, output, errors, record = [root / name for name in ("stdin", "stdout", "stderr", "record.json")]
        source.write_text("synthetic worker input\n")
        result = subprocess.run([
            executable, "--worker-host", helper, "worker", record, source, output, errors,
            root, "/bin/sh", "-c", "cat; exit 7",
        ], env=env, capture_output=True, timeout=30)
        assert result.returncode == 7, (result.returncode, result.stderr.decode(errors="replace"))
        assert output.read_text() == "synthetic worker input\n"
        assert json.loads(record.read_text())["exitCode"] == 7
        assert record.stat().st_mode & 0o077 == 0, "Worker metadata must be private"
        print("Frozen worker dispatch, stdin/stdout, durable exit status and permissions: passed")
        env["PATCH_GOBLINS_SMOKE_HOME"] = str(root)
        result = subprocess.run([executable, "--native-smoke"], env=env, text=True,
                                capture_output=True, timeout=40)
        marker = "Native menu bar, supervisor bridge and bundled credential engine: passed"
        assert result.returncode == 0 and marker in result.stdout, (
            result.returncode, result.stdout, result.stderr,
        )
        print(marker)
        # Exercise the real open/Terminal/.command path without invoking Claude.
        repo = root / "synthetic repo 'quoted'"
        subprocess.run(["git", "init", "--quiet", "-b", "main", str(repo)],
                       env=env, check=True, capture_output=True, timeout=15)
        argv_file = root / "terminal-argv"
        fake_engine = root / "synthetic engine.sh"
        argv_temporary = argv_file.with_suffix(".tmp")
        fake_engine.write_text("#!/bin/sh\numask 077\nprintf '%s\\n' \"$@\" > " +
                               shlex.quote(str(argv_temporary)) + "\nmv -- " +
                               shlex.quote(str(argv_temporary)) + " " + shlex.quote(str(argv_file)) + "\n")
        fake_engine.chmod(0o700)
        terminal_env = dict(env, PATCH_GOBLINS_ENGINE=str(fake_engine), PATCH_GOBLINS_ENGINE_ARGS="[]")
        opened = subprocess.run([backend, "agents-terminal", "--repo", repo], env=terminal_env,
                                text=True, capture_output=True, timeout=20)
        assert opened.returncode == 0, (opened.returncode, opened.stdout, opened.stderr)
        deadline = time.monotonic() + 15
        while not argv_file.exists() and time.monotonic() < deadline:
            time.sleep(0.1)
        assert argv_file.exists(), "Terminal did not execute the private command file"
        arguments = argv_file.read_text().splitlines()
        assert arguments[0] == "run", arguments
        paths = {flag: Path(arguments[arguments.index(flag) + 1])
                 for flag in ("--settings", "--mcp-config", "--plugin-dir")}
        settings = json.loads(paths["--settings"].read_text())
        assert settings["theme"] == "custom:goblin:patch-goblins"
        mcp = json.loads(paths["--mcp-config"].read_text())
        assert mcp["mcpServers"]["patch-goblins"]["args"] == ["agents-mcp", "--repo", str(repo)]
        assert (paths["--settings"].parent / "statusline.sh").is_file()
        assert (paths["--plugin-dir"] / "themes" / "patch-goblins.json").is_file()
        assert (paths["--plugin-dir"] / "commands" / "goal.md").is_file()
        print("Native Terminal launch, quoted paths, MCP bridge and goblin theme: passed")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
