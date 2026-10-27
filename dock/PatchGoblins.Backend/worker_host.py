"""Private POSIX worker host: detached process group, file stdio, durable exit status.

Uses the Python runtime already bundled with Patch Goblins. No credentials or
environment values are serialized. Only its owning Supervisor starts this host.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import threading
import time


def save(path: Path, value: dict) -> None:
    temporary = path.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        os.chmod(temporary, 0o600)
        json.dump(value, stream)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def main() -> int:
    mode, record, stdin, stdout, stderr, cwd, *command = sys.argv[1:]
    if not command:
        raise SystemExit("worker executable is required")
    os.umask(0o077)
    os.setsid()
    parent_pid = os.getppid()
    state = {"pid": os.getpid(), "group": os.getpgrp(), "started": time.time(), "child": None, "exitCode": None}
    record_path = Path(record)
    # Open regular files before starting: workers hold no pipe to the UI/backend.
    with open(stdin, "rb") as source, open(stdout, "ab", buffering=0) as output:
        errors = output if stderr == stdout else open(stderr, "ab", buffering=0)
        try:
            os.dup2(source.fileno(), 0)
            os.dup2(output.fileno(), 1)
            os.dup2(errors.fileno(), 2)
            if mode == "verification":
                def watch_parent() -> None:
                    while os.getppid() == parent_pid:
                        time.sleep(0.2)
                    os.killpg(os.getpgrp(), signal.SIGKILL)
                threading.Thread(target=watch_parent, daemon=True).start()
            child = subprocess.Popen(command, cwd=cwd, stdin=source, stdout=output, stderr=errors, close_fds=True)
            state["child"] = child.pid
            save(record_path, state)
            # A terminal/backend restart does not interrupt the worker. Supervisor
            # explicitly stops the descendants first, allowing cswap to seal them.
            signal.signal(signal.SIGHUP, signal.SIG_IGN)
            state["exitCode"] = child.wait()
            save(record_path, state)
            return child.returncode if child.returncode >= 0 else 128 - child.returncode
        except BaseException:
            state["exitCode"] = 1
            save(record_path, state)
            raise
        finally:
            if errors is not output:
                errors.close()


if __name__ == "__main__":
    raise SystemExit(main())
