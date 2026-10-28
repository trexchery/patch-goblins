"""Real POSIX parent-crash behavior of the detached orchestration host."""
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

import pytest


pytestmark = pytest.mark.skipif(sys.platform == "win32", reason="POSIX worker adapter")
HELPER = Path(__file__).resolve().parents[1] / "dock" / "PatchGoblins.Backend" / "worker_host.py"


def wait_for(condition, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = condition()
        if result:
            return result
        time.sleep(0.05)
    raise AssertionError("Timed out waiting for worker lifecycle condition")


def live(pid):
    result = subprocess.run(["/bin/ps", "-p", str(pid), "-o", "stat="], capture_output=True, text=True, check=False)
    return result.returncode == 0 and bool(result.stdout.strip()) and not result.stdout.lstrip().startswith("Z")


@pytest.mark.parametrize("mode,survives", [("worker", True), ("verification", False)])
def test_backend_crash_preserves_workers_but_terminates_verification(tmp_path, mode, survives):
    record = tmp_path / "record.json"
    source = tmp_path / "in"
    output = tmp_path / "out"
    source.write_text("")
    command = [sys.executable, str(HELPER), mode, str(record), str(source), str(output), str(output), str(tmp_path), "/bin/sh", "-c", "sleep 60 & wait"]
    # The controller represents the .NET backend. The worker's standard streams
    # are ordinary files, so killing it must not close or interrupt a worker.
    controller = subprocess.Popen([sys.executable, "-c",
        "import json,subprocess,sys,time; subprocess.Popen(json.loads(sys.argv[1])); time.sleep(60)", json.dumps(command)],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    state = None
    try:
        def ready():
            try:
                value = json.loads(record.read_text())
                return value if value.get("child") else None
            except (OSError, ValueError):
                return None
        state = wait_for(ready)
        assert live(state["pid"])
        assert live(state["child"])
        controller.kill()
        controller.wait(timeout=5)
        if survives:
            time.sleep(0.5)
            assert live(state["pid"])
            assert live(state["child"])
        else:
            wait_for(lambda: not live(state["pid"]))
            wait_for(lambda: not live(state["child"]))
    finally:
        if controller.poll() is None:
            controller.kill()
        controller.wait(timeout=5)
        if state:
            try:
                os.killpg(state["group"], signal.SIGKILL)
            except ProcessLookupError:
                pass
