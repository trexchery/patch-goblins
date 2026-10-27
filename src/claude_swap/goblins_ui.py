"""A terminal control surface for the existing Patch Goblins supervisor tools."""
from __future__ import annotations

import asyncio
import json
from pathlib import Path

from textual.app import App, ComposeResult
from textual.binding import Binding
from textual.containers import Horizontal
from textual.widgets import Button, Footer, Header, Input, RichLog, Static

from claude_swap.exceptions import ClaudeSwitchError
from claude_swap.goblins import backend_command, child_environment


HELP = """Commands (JSON arguments use the same tools as the agents terminal):
  status                         current work, questions and PRs
  goals                          Director goals and their evidence
  start {\"verify\":\"npm test\",\"base\":\"main\"}   asks for approval in the menu bar
  pause / resume / stop          control this repository
  goal: the owner's objective   ask the Director to plan a goal
  queue {\"action\":\"add\",\"title\":\"Fix\",\"text\":\"Acceptance criteria\"}
  goal {\"action\":\"answer\",\"goal\":\"g1\",\"text\":\"Your answer\"}
  details {\"item\":\"w1\"}       run, routing, verification and PR evidence
  memory {\"action\":\"list\"}     shared and repository notes
  doctor                         read-only repository readiness
  settings {\"agentIssues\":2}     concurrency, PR limits, drafts, ultracode, approvals

Tools: agents_status, agents_control, agents_queue, agents_details,
agents_goal, agents_memory, agents_doctor. You may type any tool with JSON.
Sensitive pushes and exact check changes still require owner approval.
Close this dashboard to release its repository activity lease.
"""


def parse_command(text: str) -> tuple[str, dict]:
    text = text.strip()
    if text.lower().startswith("goal:"):
        objective = text[5:].strip()
        if not objective:
            raise ValueError("State the goal after 'goal:'.")
        return "agents_goal", {"action": "set", "title": " ".join(objective.split()[:8]), "text": objective}
    command, _, raw = text.partition(" ")
    command = command.lower()
    args = json.loads(raw) if raw.strip() else {}
    if not isinstance(args, dict):
        raise ValueError("Arguments must be a JSON object.")
    if command in ("start", "pause", "resume", "stop"):
        return "agents_control", {**args, "action": command}
    if command == "goals":
        return "agents_goal", {**args, "action": "list"}
    aliases = {"status": "agents_status", "queue": "agents_queue", "goal": "agents_goal",
               "details": "agents_details", "memory": "agents_memory", "doctor": "agents_doctor",
               "settings": "desktop_settings"}
    tool = aliases.get(command, command)
    if tool not in {*aliases.values(), "agents_control"}:
        raise ValueError("Unknown command. Type 'help' for the tool commands.")
    return tool, args


class GoblinsApp(App):
    TITLE = "Patch Goblins · work"
    BINDINGS = [Binding("ctrl+q", "quit", "Quit"), Binding("ctrl+r", "refresh", "Refresh")]
    CSS = """
    Screen { background: #101710; color: #dce8d8; }
    Header { background: #294b28; }
    #repo { height: 2; padding: 0 1; color: #a6eba6; }
    #output { height: 1fr; border: solid #3d6837; }
    #buttons { height: 3; }
    Button { min-width: 10; margin-right: 1; }
    Input { border: solid #56a83e; }
    """

    def __init__(self, repo: str):
        super().__init__()
        self.repo = repo
        self._lease = None
        self._busy = False

    def compose(self) -> ComposeResult:
        yield Header()
        yield Static(self.repo, id="repo")
        yield RichLog(id="output", wrap=True, markup=False)
        with Horizontal(id="buttons"):
            for title in ("Status", "Goals", "Pause", "Resume", "Doctor", "Help"):
                yield Button(title, id=title.lower())
        yield Input(placeholder="start {\"verify\":\"npm test\"} · goal: ... · help", id="command")
        yield Footer()

    async def on_mount(self):
        self.query_one("#command", Input).focus()
        self.query_one("#output", RichLog).write("🟢 Patch Goblins · Director, agents, worktrees and pull requests\n" + HELP)
        # An MCP connection holds the repository lease in the same way as a
        # Claude agents terminal. Closing stdin releases it on the server.
        try:
            self._lease = await asyncio.create_subprocess_exec(
                *backend_command(), "agents-mcp", "--repo", self.repo,
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL, env=child_environment())
        except (ClaudeSwitchError, OSError) as exc:
            self.query_one("#output", RichLog).write(str(exc))
        self.run_worker(self.execute("status"), exclusive=False, exit_on_error=False)

    async def on_unmount(self):
        if self._lease and self._lease.returncode is None:
            self._lease.stdin.close()
            try:
                await asyncio.wait_for(self._lease.wait(), 5)
            except asyncio.TimeoutError:
                self._lease.terminate()
                await self._lease.wait()

    async def execute(self, text: str):
        output = self.query_one("#output", RichLog)
        if text.strip().lower() == "help":
            output.write(HELP)
            return
        if self._busy:
            output.write("Waiting for the previous command. Approval requests appear in the menu bar.")
            return
        self._busy = True
        try:
            tool, args = parse_command(text)
            process = await asyncio.create_subprocess_exec(
                *backend_command(), "call", tool, json.dumps(args), "--repo", self.repo,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE, env=child_environment())
            try:
                stdout, stderr = await asyncio.wait_for(process.communicate(), 300)
            except asyncio.TimeoutError:
                process.terminate()
                await process.wait()
                raise ClaudeSwitchError("The request timed out. Open Patch Goblins from Applications and check pending approvals.") from None
            output.write("> " + tool)
            result = stdout.decode("utf-8", errors="replace").strip()
            error = stderr.decode("utf-8", errors="replace").strip()
            output.write(result or error or ("Done." if process.returncode == 0 else "Start Patch Goblins from Applications first."))
        except (ClaudeSwitchError, OSError, ValueError) as exc:
            output.write(str(exc))
        finally:
            self._busy = False

    async def on_input_submitted(self, event: Input.Submitted):
        text = event.value
        event.input.value = ""
        self.run_worker(self.execute(text), exclusive=False, exit_on_error=False)

    async def on_button_pressed(self, event: Button.Pressed):
        self.run_worker(self.execute(event.button.id), exclusive=False, exit_on_error=False)

    def action_refresh(self):
        self.run_worker(self.execute("status"), exclusive=False, exit_on_error=False)
