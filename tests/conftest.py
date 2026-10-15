"""One session for the whole run: server 0 with the test world, player A on
client 1 (an administrator) and player B on client 2 (no rights).

Every test starts from the default plugin config (session.DEFAULT_CONFIG), no
backups on disk, no backup state in the plugin and only the grids of the
world; a test changes the config it needs through the probe (`configure`), so
the server keeps running between tests. Only test_restart.py restarts it, and
test_cluster.py starts servers of its own.

GB_ATTACH=1 reuses a running server and clients (session.py start), GB_KEEP=1
leaves them running after the run.
"""

from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import grids  # noqa: E402
import session  # noqa: E402
from session import CLIENTS, SERVER  # noqa: E402

ATTACH = os.environ.get("GB_ATTACH") == "1"
KEEP = os.environ.get("GB_KEEP") == "1"

NO_PERMISSION = "You do not have permission to use that command."


def parse(reply: str) -> dict[str, str]:
    """`Probe x: a=1 b=2` as {"a": "1", "b": "2"}"""
    body = reply.split(": ", 1)[1]
    return dict(p.split("=", 1) for p in body.split() if "=" in p)


def vector(text: str) -> tuple[float, float, float]:
    x, y, z = (float(v) for v in text.split(","))
    return x, y, z


def distance(p, q) -> float:
    return sum((a - b) ** 2 for a, b in zip(p, q)) ** 0.5


def wait_until(predicate, timeout: float, what: str, interval: float = 0.5):
    deadline = time.monotonic() + timeout
    while True:
        if predicate():
            return
        if time.monotonic() > deadline:
            raise AssertionError(f"Timed out waiting for {what}")
        time.sleep(interval)


def wait_idle(server, timeout: float = 30.0) -> None:
    """Until the server's plugin has no backup running or queued"""
    wait_until(
        lambda: server.probe("idle") == "Probe idle: running=0 queued=0",
        timeout,
        f"server {server.index} to finish its backups",
    )


class Game:
    def __init__(self):
        self.a, self.b = session.players()
        self.server = SERVER

    def probe(self, *args) -> str:
        return SERVER.probe(*args)

    def console(self, command: str) -> str:
        return SERVER.console(command)

    # --- config and state --------------------------------------------------

    def configure(self, **options) -> None:
        """Puts the default config with these options into the running plugin"""
        SERVER.write_config(**{**session.DEFAULT_CONFIG, **options})
        assert self.probe("reload") == "Probe: config reloaded"

    def reset(self) -> None:
        """The state every test starts from. Brings the server and the
        clients back if a failed restart test left them down."""
        if not SERVER.pid():
            SERVER.start()
        if not all(c.pid() for c in CLIENTS):
            for client in CLIENTS:
                client.stop()
            self.rejoin()
        self.configure()
        self.probe("scan", 864000)
        wait_idle(SERVER)
        assert self.probe("forget") == "Probe forget: done"
        SERVER.clear_backups()
        self.probe("prune", ",".join(str(g.entity_id) for g in grids.ALL))

    def rejoin(self) -> None:
        session.join_all()
        self.a, self.b = session.players()
        self.a.go_home()

    # --- backups -----------------------------------------------------------

    def scan(self) -> None:
        """One backup scan, now, and its backups written"""
        self.probe("scan", 0)
        time.sleep(1.0)
        wait_idle(SERVER)

    def run(self) -> None:
        """`!gridbackup run` from the console, and its backups written"""
        assert self.console("!gridbackup run") == "Backup creation started."
        time.sleep(0.5)
        wait_idle(SERVER)

    def files(self) -> list[Path]:
        """Every backup file, relative to the backup folder"""
        return [p.relative_to(SERVER.backups) for p in SERVER.backup_files()]

    def grid_files(self, g: grids.Grid, daily=False):
        """The backups of a grid in its default folder, newest first"""
        folder = SERVER.backups / str(g.owner) / g.folder
        if not folder.exists():
            return []
        files = [
            p for p in folder.glob("*.sbc") if p.name.startswith("daily_") == daily
        ]
        return sorted(files, key=lambda p: p.stat().st_mtime, reverse=True)

    def reason(self, path: Path) -> str:
        """Why the plugin wrote this backup, from its log line"""
        full = str(SERVER.backups / path) if not path.is_absolute() else str(path)
        pattern = re.escape(f'"filePath":{json.dumps(full)},"reason":"') + r'(\w+)"'
        self.probe("flush")
        found = re.findall(pattern, SERVER.game_log())
        return found[-1] if found else ""

    def backed_up(self) -> set[int]:
        """Entity ids of the grids with a backup folder"""
        return {int(p.parent.name.rsplit("_", 1)[1]) for p in SERVER.backup_files()}

    # --- grids -------------------------------------------------------------

    def grid(self, entity_id: int) -> dict:
        """The probe's view of a grid, empty when the server has none"""
        reply = self.probe("grid", entity_id)
        if reply == "Probe grid: none":
            return {}
        state = parse(reply)
        state["pos"] = vector(state["pos"])
        state["blocks"] = int(state["blocks"])
        state["owner"] = int(state["owner"])
        state["group"] = [int(i) for i in state["group"].split(",")]
        return state

    def named(self, name: str) -> list[int]:
        reply = self.probe("named", name.replace(" ", "_"))
        return [int(i) for i in reply.split()[2:]]


@pytest.fixture(scope="session")
def session_game():
    attach = ATTACH and SERVER.pid() and all(c.pid() for c in CLIENTS)
    try:
        if not attach:
            session.stop_all()
            session.prepare()
            SERVER.start()
            session.join_all()
        game = Game()
        game.a.go_home()
        yield game
    finally:
        if not KEEP:
            session.stop_all()


@pytest.fixture
def game(session_game):
    session_game.reset()
    return session_game


def pytest_collection_modifyitems(items):
    """The restart tests go last: a failed restart would take the session
    down with it"""
    items.sort(key=lambda item: item.module.__name__ == "test_restart")
