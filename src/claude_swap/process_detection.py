"""Detect running Claude Code instances.

Reads session PID files (~/.claude/sessions/{pid}.json) and IDE lockfiles
(~/.claude/ide/{port}.lock) to determine which Claude Code instances are
currently running. Uses the same mechanism Claude Code itself uses internally.
"""

from __future__ import annotations

import calendar
import json
import logging
import os
import subprocess
import sys
import stat
from dataclasses import dataclass, field
from pathlib import Path

from claude_swap.paths import get_claude_config_home

logger = logging.getLogger(__name__)

# A session record names a pid, and the OS recycles pids: once the claude
# that wrote the record is gone, the number can belong to anything. The
# record also carries claude's reading of its own start (``procStart``): on
# Linux the ``/proc/<pid>/stat`` start time in clock ticks since boot, fixed
# for the process's lifetime, and elsewhere ``ps -o lstart``, a wall-clock
# time. Only the latter needs slack, for the small clock steps that move a
# ``ps`` start time on some platforms.
PID_REUSE_SLACK_S = 120

_MONTHS = ("Jan", "Feb", "Mar", "Apr", "May", "Jun",
           "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")


@dataclass
class ClaudeSession:
    """A running Claude Code session from ~/.claude/sessions/{pid}.json."""

    pid: int
    session_id: str
    cwd: str
    started_at: int  # epoch milliseconds
    kind: str  # "interactive", "bg", "daemon", "daemon-worker"
    entrypoint: str  # "cli", "claude-vscode", "claude-desktop", "sdk-cli", "mcp"
    status: str | None = None  # "busy", "idle", "waiting"


@dataclass
class IdeInstance:
    """A running IDE instance from ~/.claude/ide/{port}.lock."""

    port: int  # from filename
    pid: int
    ide_name: str  # "Visual Studio Code", "Cursor", "Windsurf"
    workspace_folders: list[str] = field(default_factory=list)


def get_claude_dir() -> Path:
    """Return the Claude config directory, respecting CLAUDE_CONFIG_DIR."""
    return get_claude_config_home()


def is_pid_alive(pid: int) -> bool:
    """Check whether a process with the given PID is running.

    Cross-platform:
    - macOS/Linux/WSL: os.kill(pid, 0)
    - Windows: ctypes OpenProcess
    """
    if pid <= 1:
        return False

    if sys.platform == "win32":
        return _is_pid_alive_windows(pid)

    try:
        os.kill(pid, 0)
        return True
    except PermissionError:
        # EPERM means the process exists but we lack permission
        return True
    except OSError:
        return False


def _is_pid_alive_windows(pid: int) -> bool:
    """Windows-specific PID liveness check using ctypes."""
    try:
        import ctypes

        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)  # type: ignore[attr-defined]
        handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if handle:
            kernel32.CloseHandle(handle)
            return True
        # ERROR_ACCESS_DENIED: it exists but we may not open it, like EPERM.
        return ctypes.get_last_error() == 5  # type: ignore[attr-defined]
    except Exception:
        return False


def process_created(pid: int) -> int | None:
    """A Windows process's creation time (a FILETIME, as an int), or None when
    unknowable or not on Windows. Same PID, different value: a recycled PID."""
    if sys.platform != "win32":
        return None
    try:
        import ctypes
        from ctypes import wintypes

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)  # type: ignore[attr-defined]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return None
        try:
            times = [wintypes.FILETIME() for _ in range(4)]
            if not kernel32.GetProcessTimes(wintypes.HANDLE(handle), *(ctypes.byref(t) for t in times)):
                return None
            return (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
        finally:
            kernel32.CloseHandle(handle)
    except Exception:
        return None


def _ps(pid: int, *columns: str) -> str | None:
    """``ps -o`` ``columns`` for ``pid``, or None when unknowable.

    POSIX only, under ``LC_ALL=C TZ=UTC`` like claude's own reading. Windows
    and every failure answer None: not knowing must never be read as "not
    the recorded process".
    """
    if sys.platform == "win32":
        return None
    try:
        proc = subprocess.run(
            ["ps", "-o", ",".join(f"{c}=" for c in columns), "-p", str(pid)],
            capture_output=True,
            text=True,
            timeout=5,
            env={**os.environ, "LC_ALL": "C", "TZ": "UTC"},
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    text = proc.stdout.strip()
    if proc.returncode != 0 or not text:
        return None
    return text


def process_started_at(pid: int) -> int | None:
    """Epoch seconds at which ``pid`` started, or None when unknowable.

    Read the way claude stamps ``procStart`` into its record, ``ps -o
    lstart=`` under ``LC_ALL=C TZ=UTC``, so the two agree to the second for
    the same process.
    """
    text = _ps(pid, "lstart")
    if text is None:
        return None
    try:
        return _lstart_seconds(text)
    except ValueError:
        return None


def _lstart_seconds(text: str) -> int:
    """``Wed Sep  2 20:35:59 2026``, the ``ps -o lstart`` format under
    ``LC_ALL=C TZ=UTC``, as epoch seconds. Parsed by hand because ``strptime``
    reads month names in the process locale."""
    parts = text.split()
    if len(parts) != 5 or parts[1] not in _MONTHS:
        raise ValueError(text)
    _, month, day, clock, year = parts
    hours, minutes, seconds = (int(p) for p in clock.split(":"))
    return calendar.timegm(
        (int(year), _MONTHS.index(month) + 1, int(day), hours, minutes, seconds, 0, 0, 0)
    )


def process_start_ticks(pid: int) -> str | None:
    """``/proc/<pid>/stat``'s start time, in clock ticks since boot, or None
    when unreadable. Linux by construction: the file exists nowhere else."""
    try:
        text = Path(f"/proc/{pid}/stat").read_text(encoding="ascii", errors="replace")
    except OSError:
        return None
    return _stat_start_ticks(text)


def _stat_start_ticks(text: str) -> str | None:
    """Field 22 of a ``/proc/<pid>/stat`` line, as the string claude stamps
    into ``procStart``. The command name sits in parentheses before the
    numeric fields and may itself contain spaces or parentheses, so the
    fields are counted from the last closing one."""
    _, _, rest = text.rpartition(")")
    fields = rest.split()
    if len(fields) <= 19 or not fields[19].isdigit():
        return None
    return fields[19]


def process_is_claude(pid: int) -> bool | None:
    """Does the process at ``pid`` look like a claude, or None when unknowable.

    Judged from ``ps -o comm=,args=``: the native binary and the symlink to
    it are named ``claude``, and an npm install runs ``cli.js`` out of a
    ``claude-code`` package directory.
    """
    text = _ps(pid, "comm", "args")
    if text is None:
        return None
    return "claude" in text.lower()


def pid_matches_record(pid: int, proc_start: str | None) -> bool:
    """Is the live process at ``pid`` the one that wrote a record stamped
    ``proc_start``, claude's reading of its own start?

    On Linux the stamp is the ``/proc/<pid>/stat`` start time in clock ticks
    since boot, which a process keeps for life and no two processes at one
    pid share, so equality is the whole test and no clock domain is
    involved. Elsewhere it is ``ps -o lstart``, a wall-clock time: a
    recycled pid belongs to a process that started after the recorded claude
    did, and only that direction disqualifies, and only a stranger. Some
    ``ps`` builds derive every start time from a boot time that moves with
    each wall-clock step (a WSL2 resume re-syncing the clock steps it by the
    whole sleep), so a live session can read as younger than its own record;
    a claude at the pid is kept either way, and a pid genuinely recycled by
    another claude lingers only for that process's lifetime, which is what
    happened before this check. On Windows the stamp is a FILETIME, checked
    against the process's creation time. Everything unknowable (no ``/proc``
    and no creation time, ``ps`` unavailable, an unstamped or unparseable
    record) passes, because "cannot tell" must
    never turn a live session into "nobody there".
    """
    if not proc_start:
        return True
    if proc_start.isdigit():
        ticks = process_start_ticks(pid)
        if ticks is not None:
            return ticks == proc_start
        # Windows: the stamp is the process's creation FILETIME, compared as
        # the tray and _marker_is_live do (claude's own reading, within 2 s).
        created = process_created(pid)
        return created is None or abs(created - int(proc_start)) < 2 * 10_000_000
    try:
        recorded = _lstart_seconds(proc_start)
    except ValueError:
        return True
    started = process_started_at(pid)
    if started is None or started <= recorded + PID_REUSE_SLACK_S:
        return True
    return process_is_claude(pid) is not False


def scan_sessions(claude_dir: Path | None = None) -> tuple[list[ClaudeSession], int]:
    """Live sessions, and how many records could NOT be read.

    A record counts as live only when its pid is alive AND still belongs to
    the claude that wrote it (see ``pid_matches_record``): a crashed claude
    leaves its record behind, and the OS can hand the number to something
    else.

    Two kinds of caller read this directory and they need opposite things from
    an unparseable record:

    - A SCAN (a listing, a status display) wants it skipped. One bad file must
      not take out the whole listing.
    - A GUARD wants to know. ``0 live`` and ``0 readable`` are the same list,
      and only the first is safe to act on -- the callers gate ``_bootstrap``
      (which deletes a profile's Keychain entry and overwrites
      ``.credentials.json``) and account removal, so reading "could not tell"
      as "nobody there" runs them underneath a live instance.

    So the count is returned rather than swallowed, and ``list_sessions``
    below is the scan-shaped view that drops it.
    """
    sessions_dir = (claude_dir or get_claude_dir()) / "sessions"
    try:
        mode = sessions_dir.stat().st_mode
    except FileNotFoundError:
        return [], 0
    if not stat.S_ISDIR(mode):
        raise OSError(f"Session path is not a directory: {sessions_dir}")

    sessions = []
    unreadable = 0
    # iterdir propagates access errors; glob may silently treat an unreadable
    # directory as empty, which is unsafe for a guard.
    paths = list(sessions_dir.iterdir())
    for path in (p for p in paths if p.name.endswith(".json")):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            pid = data["pid"]
            if not is_pid_alive(pid):
                continue
            if not pid_matches_record(pid, data.get("procStart")):
                logger.debug(
                    "Skipping session file %s: pid %s was recycled", path, pid
                )
                continue
            sessions.append(ClaudeSession(
                pid=pid,
                session_id=data.get("sessionId", ""),
                cwd=data.get("cwd", ""),
                started_at=data.get("startedAt", 0),
                kind=data.get("kind", ""),
                entrypoint=data.get("entrypoint", ""),
                status=data.get("status"),
            ))
        except (
            json.JSONDecodeError,   # malformed JSON
            KeyError,               # required field missing
            TypeError,              # field has the wrong type (e.g. pid not an int)
            AttributeError,         # valid JSON that is not an object: a
                                    # top-level array reaches `.get` as a list.
                                    # Also how a too-deep nesting lands where
                                    # the parser's recursion limit is high
                                    # enough not to raise -- it differs per
                                    # machine, so BOTH outcomes must be inert.
            ValueError,             # includes UnicodeDecodeError from read_text
            OverflowError,          # pid too large for os.kill's C long (is_pid_alive)
            RecursionError,         # pathologically nested JSON in json.loads
            OSError,
        ) as exc:
            unreadable += 1
            logger.debug("Skipping session file %s: %s", path, exc)
    keyed, key_unreadable = _keyed_sessions(paths, {s.pid for s in sessions})
    sessions.extend(keyed)
    unreadable += key_unreadable
    return sessions, unreadable


def _keyed_sessions(paths: list[Path], known: set[int]) -> tuple[list[ClaudeSession], int]:
    """Live claudes with a ``{pid}.<hash>.key`` but no ``{pid}.json``.

    Claude Code writes the key seconds before the record at start, and no
    record at all for a session that is not persisted, an agent-team member,
    a parked spare, or when registering fails. They are still on this login, so a scan
    or a guard must see them. Folder and status are unknown. A process that
    started after the key was written holds a recycled pid and is skipped.
    """
    found = []
    unreadable = 0
    for path in (p for p in paths if p.name.endswith(".key")):
        try:
            pid = int(path.name.split(".", 1)[0])
            if pid in known or not is_pid_alive(pid):
                continue
            started = _started_epoch(pid)
            if started is not None and started > path.stat().st_mtime + 2:
                continue
            known.add(pid)
            found.append(ClaudeSession(pid=pid, session_id="", cwd="", started_at=0,
                                       kind="interactive", entrypoint="cli"))
        except (ValueError, OverflowError, OSError) as exc:
            unreadable += 1
            logger.debug("Skipping session key %s: %s", path, exc)
    return found, unreadable


def _started_epoch(pid: int) -> float | None:
    """Epoch seconds at which ``pid`` started, or None when unknowable."""
    ft = process_created(pid)
    if ft is not None:
        return ft / 1e7 - 11644473600  # FILETIME: 100 ns ticks since 1601
    return process_started_at(pid)


def list_sessions(claude_dir: Path | None = None) -> list[ClaudeSession]:
    """Live sessions. A record that cannot be read is SKIPPED.

    SCAN USE ONLY. The returned list cannot distinguish "no live sessions"
    from "no readable records", so anything gating a destructive step must
    call :func:`scan_sessions` and treat a non-zero count as live.
    """
    return scan_sessions(claude_dir)[0]


def list_ide_instances(claude_dir: Path | None = None) -> list[IdeInstance]:
    """Read IDE lockfiles and return only those with alive processes."""
    ide_dir = (claude_dir or get_claude_dir()) / "ide"
    if not ide_dir.is_dir():
        return []

    instances = []
    for path in ide_dir.glob("*.lock"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            pid = data.get("pid")
            if pid is None or not is_pid_alive(pid):
                continue
            port = int(path.stem)
            instances.append(IdeInstance(
                port=port,
                pid=pid,
                ide_name=data.get("ideName", "Unknown IDE"),
                workspace_folders=data.get("workspaceFolders", []),
            ))
        except (
            json.JSONDecodeError,   # malformed JSON
            KeyError,               # required field missing
            TypeError,              # field has the wrong type (e.g. pid not an int)
            AttributeError,         # valid JSON that is not an object: a
                                    # top-level array reaches `.get` as a list.
                                    # Also how a too-deep nesting lands where
                                    # the parser's recursion limit is high
                                    # enough not to raise -- it differs per
                                    # machine, so BOTH outcomes must be inert.
            ValueError,             # includes UnicodeDecodeError from read_text
            OverflowError,          # pid too large for os.kill's C long (is_pid_alive)
            RecursionError,         # pathologically nested JSON in json.loads
            OSError,
        ) as exc:
            logger.debug("Skipping IDE lockfile %s: %s", path, exc)
    return instances


def get_running_instances(
    claude_dir: Path | None = None,
) -> tuple[list[ClaudeSession], list[IdeInstance]]:
    """Return all running Claude Code sessions and IDE instances."""
    resolved = claude_dir or get_claude_dir()
    return list_sessions(resolved), list_ide_instances(resolved)
