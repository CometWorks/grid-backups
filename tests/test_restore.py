"""Restoring a backup: the restored grid matches the backed-up one (blocks,
owners, inventories, joined grids, position when asked to keep it), lands near
the caller otherwise, picks the backup by number, and doesn't spawn into an
existing grid unless forced."""

from __future__ import annotations

import re
import time

import grids
import world
from conftest import distance, parse, vector
from grids import ALPHA_STATION, CHARLIE_BASE, RESTORE_STATION, ROTOR_BASE

RESTORED = re.compile(r"Restored (\S+\.sbc) successfully\.")
# Where the tests park a grid while its backup is restored in its place
AWAY = (2000.0, 0.0, 0.0)


def restore(game, args: str) -> str:
    """Restores as player A, and returns the file restored"""
    reply = game.a.command(f"!gridbackup restore {args}")
    match = RESTORED.fullmatch(reply)
    assert match, reply
    return match.group(1)


def restored_copy(game, g: grids.Grid) -> dict:
    """The one grid with g's name that is not g"""
    wait = time.monotonic() + 10
    while True:
        ids = [i for i in game.named(g.name) if i != g.entity_id]
        if ids or time.monotonic() > wait:
            break
        time.sleep(0.5)
    assert len(ids) == 1, ids
    return {**game.grid(ids[0]), "id": ids[0]}


def fill_cargo(game, amount: float) -> None:
    game.probe("give", grids.CARGO_ID, "Ingot", "Iron", amount)


def test_restore_near_the_caller(game):
    game.run()
    file = restore(game, f"Alpha {ALPHA_STATION.entity_id}")
    assert file == game.grid_files(ALPHA_STATION)[0].name
    copy = restored_copy(game, ALPHA_STATION)
    player = parse(game.probe("player", world.PLAYER_A))
    # 100 m in front of the player
    assert abs(distance(copy["pos"], vector(player["pos"])) - 100) < 1
    original = game.grid(ALPHA_STATION.entity_id)
    assert copy["blocks"] == original["blocks"]
    assert copy["owner"] == world.PLAYER_A


def test_restored_grid_matches_the_backup(game):
    fill_cargo(game, 500)
    original = game.grid(RESTORE_STATION.entity_id)
    assert original["items"] == "Ingot/Iron:500"
    game.run()
    game.probe("place", RESTORE_STATION.entity_id, *AWAY)
    try:
        restore(game, f"Alpha {RESTORE_STATION.entity_id} 1 true")
        copy = restored_copy(game, RESTORE_STATION)
        assert distance(copy["pos"], RESTORE_STATION.position) < 0.1
        for key in ("blocks", "owner", "owners", "items", "static", "name"):
            assert copy[key] == original[key], key
        assert copy["id"] != RESTORE_STATION.entity_id
    finally:
        game.probe("place", RESTORE_STATION.entity_id, *RESTORE_STATION.position)
        game.probe("clear", grids.CARGO_ID)


def test_restore_an_older_backup(game):
    fill_cargo(game, 100)
    try:
        game.run()
        time.sleep(1.1)
        fill_cargo(game, 100)
        game.run()
        older = game.grid_files(RESTORE_STATION)[1].name
        assert restore(game, f"Alpha {RESTORE_STATION.entity_id} 2") == older
        assert restored_copy(game, RESTORE_STATION)["items"] == "Ingot/Iron:100"
    finally:
        game.probe("clear", grids.CARGO_ID)


def test_restore_joined_grids(game):
    top = int(parse(game.probe("attach", grids.STATOR_ID))["top"])
    game.run()
    copy = restored_copy_after(game, ROTOR_BASE, f"Alpha {ROTOR_BASE.entity_id}")
    assert len(copy["group"]) == 2
    assert top not in copy["group"]


def restored_copy_after(game, g: grids.Grid, args: str) -> dict:
    restore(game, args)
    return restored_copy(game, g)


def test_restore_refuses_an_occupied_place(game):
    game.run()
    reply = game.a.command(
        f"!gridbackup restore Alpha {ALPHA_STATION.entity_id} 1 true"
    )
    assert reply == (
        "Restore area appears occupied. Repeat with force=true to ignore the "
        "occupation check."
    )
    assert game.named(ALPHA_STATION.name) == [ALPHA_STATION.entity_id]


def test_forced_restore_into_an_occupied_place(game):
    game.run()
    restore(game, f"Alpha {ALPHA_STATION.entity_id} 1 true true")
    copy = restored_copy(game, ALPHA_STATION)
    assert distance(copy["pos"], ALPHA_STATION.position) < 0.1


def test_restore_from_a_faction_member(game):
    game.run()
    copy = restored_copy_after(
        game, CHARLIE_BASE, f"faction GBT {CHARLIE_BASE.entity_id}"
    )
    assert copy["owner"] == world.PLAYER_C


def test_restore_by_list_number(game):
    game.run()
    # Alpha's grids by name: Alpha ship, Alpha station, ...
    copy = restored_copy_after(game, ALPHA_STATION, "Alpha 2")
    assert copy["blocks"] == ALPHA_STATION.blocks


def test_console_restore_in_place(game):
    game.run()
    game.probe("place", ALPHA_STATION.entity_id, *AWAY)
    try:
        reply = game.console(
            f"!gridbackup restore Alpha {ALPHA_STATION.entity_id} 1 true"
        )
        assert RESTORED.fullmatch(reply), reply
        copy = restored_copy(game, ALPHA_STATION)
        assert distance(copy["pos"], ALPHA_STATION.position) < 0.1
    finally:
        game.probe("place", ALPHA_STATION.entity_id, *ALPHA_STATION.position)
