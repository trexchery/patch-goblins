"""Verify the actual frozen launchers, engine and detached worker, without a login."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


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


if __name__ == "__main__":
    main(Path(sys.argv[1]))
