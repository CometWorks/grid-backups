"""The !gridbackup chat commands: what they answer, who may use them, and bad
input. Player A is an administrator, player B has no rights until a test
makes him a space master. list, find and restore need space master, run and
status need admin. Restores themselves are in test_restore.py."""

from __future__ import annotations

import re
import shutil
import time

import grids
import pytest
import world
from conftest import NO_PERMISSION, wait_idle
from grids import ALPHA_SHIP, ALPHA_STATION, BRAVO_STATION, CHARLIE_BASE
from session import CLIENT_A

LIST_LINE = re.compile(r"(\d+)\. (.+): (.+) - latest \d{4}-\d\d-\d\d \d\d:\d\d:\d\d")


def grid_lines(reply: str) -> list[tuple[str, str]]:
    """(owner, grid folder) of each line of a grid list, the title left out"""
    return [LIST_LINE.fullmatch(line).groups()[1:] for line in reply.splitlines()[1:]]


def alpha(g: grids.Grid) -> tuple[str, str]:
    return (f"Alpha #{world.PLAYER_A}", g.folder)


ALPHA_GRIDS = sorted(
    alpha(g) for g in grids.SCANNED if g not in (grids.BRAVO_STATION, CHARLIE_BASE)
)


@pytest.mark.parametrize(
    "command",
    [
        "list Alpha",
        "list faction GBT",
        f"find {ALPHA_STATION.entity_id}",
        f"restore Alpha {ALPHA_STATION.entity_id}",
        "restore faction GBT 1",
        "run",
        "status",
    ],
)
def test_commands_need_rights(game, command):
    game.run()
    before = game.files()
    assert game.b.command(f"!gridbackup {command}") == NO_PERMISSION
    assert game.files() == before


def test_space_master_lists_but_does_not_run(game):
    game.run()
    try:
        assert game.probe("promote", game.b.steam_id, "SpaceMaster") == (
            "Probe promote: SpaceMaster"
        )
        assert game.b.command("!gridbackup list Bravo").startswith(
            "Backed up grids for Bravo"
        )
        assert game.b.command("!gridbackup run") == NO_PERMISSION
        assert game.b.command("!gridbackup status") == NO_PERMISSION
    finally:
        game.probe("promote", game.b.steam_id, "None")


def test_overview(game):
    lines = game.a.command("!gridbackup").splitlines()
    for command in (
        "find",
        "list",
        "list faction",
        "restore",
        "restore faction",
        "run",
        "status",
    ):
        assert any(
            line.startswith(f"!gridbackup {command} ") for line in lines
        ), command
    assert "!gridbackup run" not in game.b.command("!gridbackup")


def test_unknown_command(game):
    assert game.a.command("!gridbackup frob") == "Unknown command. Try !gridbackup help"


# --- run and status -----------------------------------------------------------


def test_run_backs_up_every_grid(game):
    assert game.a.command("!gridbackup run") == "Backup creation started."
    time.sleep(0.5)
    wait_idle(game.server)
    assert game.backed_up() == {g.entity_id for g in grids.SCANNED}


def test_status(game):
    reply = game.a.command("!gridbackup status")
    match = re.fullmatch(
        r"root=(.+), tracked=(\d+), running=(\d+), nextScanUtc=\S+", reply
    )
    assert match, reply
    assert match.group(1) == str(game.server.backups)


def test_backup_folder_name(game):
    game.configure(BackupFolderName="Other backups")
    root = game.server.quasar / "Other backups"
    try:
        assert game.console("!gridbackup status").startswith(f"root={root},")
        game.run()
        assert (root / str(world.PLAYER_A) / ALPHA_STATION.folder).is_dir()
    finally:
        game.configure()
        shutil.rmtree(root, ignore_errors=True)


@pytest.mark.xfail(
    strict=True,
    reason="SE1-0230: UseQuasarConfigFolder=false still keeps the backups in the Quasar folder",
)
def test_storage_outside_the_quasar_folder(game):
    game.configure(UseQuasarConfigFolder=False)
    try:
        root = game.console("!gridbackup status").split(",")[0].removeprefix("root=")
        # Today it is the plugin's data folder, the GridBackups folder in the
        # Quasar folder, so the backups land one folder deeper
        assert not root.startswith(str(game.server.quasar))
    finally:
        game.configure()
        shutil.rmtree(game.server.plugin_dir / "GridBackups", ignore_errors=True)


# --- list ---------------------------------------------------------------------


@pytest.mark.parametrize(
    "who", ["Alpha", "alpha", "alp", str(CLIENT_A.steam_id), str(world.PLAYER_A)]
)
def test_list_a_player(game, who):
    game.run()
    reply = game.a.command(f"!gridbackup list {who}")
    assert reply.splitlines()[0] == "Backed up grids for Alpha"
    assert grid_lines(reply) == ALPHA_GRIDS


@pytest.mark.parametrize("who", ["Zulu", "a", "12345"])
def test_list_an_unknown_or_ambiguous_player(game, who):
    # "a" is in Alpha, Bravo and Charlie
    game.run()
    assert game.a.command(f"!gridbackup list {who}") == "Player not found."


def test_list_a_player_without_backups(game):
    game.run()
    game.server.clear_backups()
    assert game.a.command("!gridbackup list Alpha") == "No matching backup grids found."


def test_list_nobody(game):
    game.configure(BackupNobodyGrids=True)
    game.run()
    reply = game.a.command("!gridbackup list nobody")
    assert grid_lines(reply) == [("Nobody", grids.WRECK.folder)]
    assert game.a.command("!gridbackup list 0") == reply


@pytest.mark.parametrize(
    "grid", ['"Alpha station"', str(ALPHA_STATION.entity_id), "Alpha?st*", "1"]
)
def test_list_the_backups_of_a_grid(game, grid):
    game.run()
    newest = game.grid_files(ALPHA_STATION)[0]
    if grid == "1":
        # List numbers count the player's grids by name: Alpha ship comes first
        expected = ALPHA_SHIP
        newest = game.grid_files(ALPHA_SHIP)[0]
    else:
        expected = ALPHA_STATION
    lines = game.a.command(f"!gridbackup list Alpha {grid}").splitlines()
    assert lines[0] == f"Backups for {expected.folder} matching {grid.strip(chr(34))}"
    assert re.fullmatch(rf"1\. {newest.name} [\d.,]+ kb", lines[1]), lines[1]
    assert len(lines) == 2


def test_list_the_backups_of_an_unknown_grid(game):
    game.run()
    assert game.a.command("!gridbackup list Alpha Nothing") == "Grid not found."
    # Bravo's grid is not Alpha's
    reply = game.a.command(f"!gridbackup list Alpha {BRAVO_STATION.entity_id}")
    assert reply == "Grid not found."


def test_list_a_faction(game):
    game.run()
    reply = game.a.command("!gridbackup list faction GBT")
    assert reply.splitlines()[0] == "Backed up grids for [GBT] Grid Backup Testers"
    assert sorted(grid_lines(reply)) == sorted(
        ALPHA_GRIDS + [(f"Charlie #{world.PLAYER_C}", CHARLIE_BASE.folder)]
    )


def test_list_the_backups_of_a_faction_grid(game):
    game.run()
    lines = game.a.command(
        f"!gridbackup list faction GBT {CHARLIE_BASE.entity_id}"
    ).splitlines()
    assert (
        lines[0]
        == f"Backups for {CHARLIE_BASE.folder} matching {CHARLIE_BASE.entity_id}"
    )


def test_list_an_unknown_faction(game):
    assert game.a.command("!gridbackup list faction XYZ") == "Faction not found."


# --- find ---------------------------------------------------------------------


def test_find_by_wildcard(game):
    game.run()
    reply = game.a.command("!gridbackup find Alpha*")
    assert reply.splitlines()[0] == "Find results for Alpha*"
    assert sorted(grid_lines(reply)) == [alpha(ALPHA_SHIP), alpha(ALPHA_STATION)]


def test_find_by_entity_id_across_owners(game):
    game.run()
    reply = game.a.command(f"!gridbackup find {BRAVO_STATION.entity_id}")
    assert grid_lines(reply) == [(f"Bravo #{world.PLAYER_B}", BRAVO_STATION.folder)]


def test_find_by_a_name_with_odd_characters(game):
    game.run()
    reply = game.a.command(f'!gridbackup find "{grids.ODD_NAME.name}"')
    folder = grids.ODD_NAME.folder
    assert grid_lines(reply) == [(f"Alpha #{world.PLAYER_A}", folder)]


def test_find_nothing(game):
    game.run()
    assert game.a.command("!gridbackup find Zulu*") == "No matching backup grids found."


def test_find_leaves_out_npc_grids_once_not_backed_up(game):
    game.configure(BackupNpcGrids=True)
    game.run()
    assert grid_lines(game.a.command("!gridbackup find Pirate*"))
    game.configure()
    assert (
        game.a.command("!gridbackup find Pirate*") == "No matching backup grids found."
    )


# --- restore input ------------------------------------------------------------


@pytest.mark.parametrize(
    "args, reply",
    [
        (f"Alpha {ALPHA_STATION.entity_id} 0", "Backup number must be at least 1."),
        (
            f"Alpha {ALPHA_STATION.entity_id} 9",
            "Backup not found. Check if the number is in range.",
        ),
        ("Alpha Nothing", "Grid not found."),
        ("Zulu Nothing", "Player not found."),
        ("faction XYZ Nothing", "Faction not found."),
    ],
)
def test_restore_bad_input(game, args, reply):
    game.run()
    assert game.a.command(f"!gridbackup restore {args}") == reply
    assert game.named(ALPHA_STATION.name) == [ALPHA_STATION.entity_id]


def test_restore_with_a_bad_number(game):
    reply = game.a.command(f"!gridbackup restore Alpha {ALPHA_STATION.entity_id} two")
    assert "Usage: !gridbackup restore" in reply, reply


def test_console_restore_needs_the_original_position(game):
    game.run()
    reply = game.console(f"!gridbackup restore Alpha {ALPHA_STATION.entity_id}")
    assert reply == "Console restore must use keepOriginalPosition=true."
