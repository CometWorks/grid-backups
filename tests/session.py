"""A running test session: server 0 with its world, and the players on the
clients. Also usable from the command line while iterating::

    uv run python tests/session.py start     # server and both clients
    uv run python tests/session.py stop
"""

from __future__ import annotations

import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import grids  # noqa: E402
import rig  # noqa: E402
import world  # noqa: E402

SERVER = rig.Server(0)
CLIENT_A = rig.Client(1, "Alpha")
CLIENT_B = rig.Client(2, "Bravo")
CLIENTS = (CLIENT_A, CLIENT_B)

REPLY_TIMEOUT_S = 20.0
# A reply is complete once no more of it came for this long
QUIET_S = 1.0
# Replies come from the command's root, errors from its title
ROOT_TITLES = {"gridbackup": "Grid Backups", "gbprobe": "GridBackupsProbe"}

# The plugin config every test starts from: every trigger off and no minimum
# delay, so a scan backs up only grids it has no backup state for. Scans run
# only when a test asks for one (`Game.scan`).
DEFAULT_CONFIG = {
    "Enabled": True,
    "UseQuasarConfigFolder": True,
    "BackupFolderName": "GridBackups",
    "PlayerNameOnFolders": False,
    "UseSteamId": False,
    "BackupNobodyGrids": False,
    "BackupNpcGrids": False,
    "ScanIntervalSeconds": 86400,
    "MinMinutesSinceLastSave": 0,
    "MaxMinutesSinceLastSave": 0,
    "BackupBeforeConcealment": True,
    "BackupIfSteady": False,
    "SteadySeconds": 0,
    "MaxBlockCountChange": 0,
    "BackupConnectedGrids": True,
    "KeepOriginalOwner": True,
    "MinBlocksForBackup": 5,
    "NumberOfBackupSaves": 5,
    "NumberOfDailyBackupSaves": 0,
    "DeleteBackupsOlderThanDays": 0,
}

# No NPC ships: the game spawns and removes encounters on its own, which would
# change the set of grids under the tests
WORLD_SETTINGS = {
    "EnableEncounters": "false",
    "GlobalEncounterCap": "0",
    "CargoShipsEnabled": "false",
    "EnableContainerDrops": "false",
    "EnableDrones": "false",
    "EnablePlanetaryEncounters": "false",
}

PLAYERS = {
    CLIENT_A.steam_id: (world.PLAYER_A, CLIENT_A.name),
    CLIENT_B.steam_id: (world.PLAYER_B, CLIENT_B.name),
}


class Player:
    """A client's player, typing chat commands through Remote"""

    def __init__(self, client: rig.Client, identity_id: int):
        self.client = client
        self.identity_id = identity_id
        self.steam_id = client.steam_id
        self.name = client.name
        self.api = client.api()

    def _chat(self) -> list[tuple[str, str, str]]:
        hud = self.api.get_chat_messages().get("hud") or []
        return [(m.get("sender"), m.get("text"), m.get("timestamp")) for m in hud]

    def command(self, text: str, timeout: float = REPLY_TIMEOUT_S) -> str:
        """Sends a chat command and returns its reply. A reply of several
        messages comes back as their lines."""
        prefix = text.split()[0][1:]
        senders = {prefix, ROOT_TITLES.get(prefix)}
        before = set(self._chat())
        self.api.send_chat(text)
        deadline = time.monotonic() + timeout
        replies, last = [], None
        while time.monotonic() < deadline:
            new = [
                t
                for s, t, ts in self._chat()
                if (s, t, ts) not in before and s in senders
            ]
            if new != replies:
                replies, last = new, time.monotonic()
            elif replies and time.monotonic() - last > QUIET_S:
                return "\n".join(replies)
            time.sleep(0.3)
        if replies:
            return "\n".join(replies)
        raise TimeoutError(f"No reply to {text!r}")

    def go_home(self) -> None:
        """Player A only: the admin teleport"""
        self.client.api().character_teleport(*grids.HOME)
        time.sleep(2)


# ---------------------------------------------------------------------------
# Start and stop
# ---------------------------------------------------------------------------


def prepare_world(server: rig.Server) -> None:
    world.prepare(rig.EMPTY_WORLD, server.world, PLAYERS, grids.xml(), WORLD_SETTINGS)


def prepare() -> None:
    """A fresh world with every grid, and the server's configs"""
    prepare_world(SERVER)
    SERVER.prepare([CLIENT_A.steam_id])
    SERVER.write_config(**DEFAULT_CONFIG)


def join(client: rig.Client) -> rig.RemoteAPI:
    """Launches a client, which connects on its own, and waits for its
    character"""
    client.launch(SERVER.port)
    api = client.wait_api()
    rig.wait_world(api)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    return api


def join_all(clients=CLIENTS) -> None:
    """Joins the clients in parallel"""
    errors = []

    def run(client):
        try:
            join(client)
        except Exception as error:  # noqa: BLE001 -- reported below
            errors.append((client.name, error))

    threads = [threading.Thread(target=run, args=(c,)) for c in clients]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    if errors:
        raise RuntimeError(f"Joining failed: {errors}")


def stop_all() -> None:
    for client in CLIENTS:
        client.stop()
    SERVER.stop()


def players() -> tuple[Player, Player]:
    return Player(CLIENT_A, world.PLAYER_A), Player(CLIENT_B, world.PLAYER_B)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_all()
        prepare()
        SERVER.start()
        join_all()
        players()[0].go_home()
        print(
            f"Joined. Remote ports {CLIENT_A.port}, {CLIENT_B.port}; server log {SERVER.log}"
        )
    elif command == "stop":
        stop_all()
    else:
        sys.exit(__doc__)
