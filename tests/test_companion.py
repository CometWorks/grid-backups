"""The companion operations Quasar calls (docs/CompanionOperations.md), run
through the probe: a grid loss and its cause in the loss log, a restore that
keeps the grid's entity ids, the checks that refuse one, the undo of a
restore, placement away from other players' grids, every big owner seeing a
grid, and the same operations through Quasar's handler interface.

Each test that loses Loss ship brings it back in place, with its ids, so the
tests after it find the world as it was."""

from __future__ import annotations

import base64
import json
import os
import time
from datetime import datetime, timedelta, timezone

import grids
import pytest
import world
from conftest import distance, parse, vector, wait_until
from grids import ALPHA_STATION, BRAVO_STATION, LOSS_SHIP, SHARED_BASE

LOSS_ID = LOSS_SHIP.entity_id


class CompanionError(Exception):
    pass


def companion(game, operation: str, via: str = "companion", **payload) -> dict:
    """Runs a companion operation and returns its payload; raises
    CompanionError with the error of a failed one"""
    request = str(time.time_ns())
    encoded = base64.b64encode(json.dumps(payload).encode()).decode()
    assert game.probe(via, request, operation, encoded) == "Probe companion: started"
    reply = ""
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        reply = game.probe("result", request)
        if reply != "Probe result: pending":
            break
        time.sleep(0.2)
    body = reply.split(": ", 1)[1]
    if body.startswith("ok "):
        return json.loads(body[3:])
    raise CompanionError(body.removeprefix("error "))


def steam(player) -> str:
    return str(player.steam_id)


def losses(game, player=None, **payload) -> list[dict]:
    player = player or game.a
    return companion(game, "losses", steamId=steam(player), **payload)["losses"]


def lose(game, how: str) -> dict:
    """Backs Loss ship up, loses it the way named and returns its loss entry"""
    game.scan()
    assert game.probe(how, LOSS_ID) == f"Probe {how}: done"
    wait_until(lambda: not game.grid(LOSS_ID), 15, "Loss ship to go")
    found: list[dict] = []

    def logged() -> bool:
        found[:] = [e for e in losses(game) if e["gridId"] == LOSS_ID]
        return bool(found)

    wait_until(logged, 15, "the loss entry")
    return found[0]


def restore(game, player=None, **options) -> dict:
    payload = {
        "steamId": steam(player or game.a),
        "gridId": LOSS_ID,
        "placement": "original",
        "emptyInventories": False,
        "foreignExclusionMeters": 0,
        "maxSearchMeters": 10000,
        **options,
    }
    return companion(game, "restore", **payload)


def bring_back(game) -> None:
    """Loss ship in place again, whatever the test left"""
    state = game.grid(LOSS_ID)
    if state and distance(state["pos"], LOSS_SHIP.position) < 0.1:
        return
    if state:
        game.probe(
            "prune", ",".join(str(g.entity_id) for g in grids.ALL if g != LOSS_SHIP)
        )
        wait_until(lambda: not game.grid(LOSS_ID), 15, "Loss ship to go")
    result = restore(game)
    assert result["status"] == "restored", result
    game.probe("clear", grids.LOSS_CARGO_ID)


@pytest.mark.parametrize(
    "how,cause",
    [
        ("damage", "destroyed"),
        ("delete", "deleted"),
        ("grind", "ground"),
        ("trash", "cleanup"),
    ],
)
def test_a_loss_is_logged_with_its_cause(game, how, cause):
    try:
        entry = lose(game, how)
        assert entry["cause"] == cause
        assert entry["name"] == LOSS_SHIP.name
        assert entry["ownerIdentityIds"] == [world.PLAYER_A]
        assert entry["ownerSteamIds"] == [steam(game.a)]

        listed = {
            g["gridId"]: g
            for g in companion(game, "list", steamId=steam(game.a))["grids"]
        }
        assert listed[LOSS_ID]["present"] is False
        assert listed[LOSS_ID]["loss"]["cause"] == cause
        assert listed[ALPHA_STATION.entity_id]["present"] is True
        assert "loss" not in listed[ALPHA_STATION.entity_id]
        assert losses(game, game.b) == []
    finally:
        bring_back(game)


def test_restore_keeps_the_entity_ids(game):
    try:
        lose(game, "damage")
        before = losses(game)
        result = restore(game)
        assert result["status"] == "restored", result
        assert result["entityIds"] == [LOSS_ID]
        state = game.grid(LOSS_ID)
        assert state["blocks"] == LOSS_SHIP.blocks
        assert distance(state["pos"], LOSS_SHIP.position) < 0.1
        # The cargo container's block id came back too
        assert (
            game.probe("give", grids.LOSS_CARGO_ID, "Ingot", "Iron", 1)
            == "Probe give: done"
        )

        again = restore(game)
        assert again["status"] == "refused"
        assert again["reason"] == "The grid still exists on this server."

        undo = companion(game, "remove-restored", entityIds=[LOSS_ID])
        assert undo == {"removed": [LOSS_ID], "missing": []}
        wait_until(lambda: not game.grid(LOSS_ID), 15, "the restored grid to go")
        assert companion(game, "remove-restored", entityIds=[LOSS_ID]) == {
            "removed": [],
            "missing": [LOSS_ID],
        }
        # An undo is not a loss
        time.sleep(1)
        assert losses(game) == before

        with pytest.raises(CompanionError, match="Not restored by Grid Backups"):
            companion(game, "remove-restored", entityIds=[ALPHA_STATION.entity_id])
    finally:
        bring_back(game)


def test_restore_takes_the_newest_backup_and_empties_inventories(game):
    try:
        game.probe("give", grids.LOSS_CARGO_ID, "Ingot", "Iron", 100)
        game.scan()
        time.sleep(1.1)
        game.probe("give", grids.LOSS_CARGO_ID, "Ingot", "Iron", 100)
        game.run()
        assert game.grid(LOSS_ID)["items"] == "Ingot/Iron:200"
        lose(game, "damage")

        described = companion(game, "describe", steamId=steam(game.a), gridId=LOSS_ID)
        assert len(described["backups"]) == 2
        assert described["loss"]["cause"] == "destroyed"
        newest = described["backups"][0]
        assert newest["gridCount"] == 1
        assert newest["blocks"] == LOSS_SHIP.blocks
        assert (
            distance(
                vector(",".join(str(newest["position"][k]) for k in "xyz")),
                LOSS_SHIP.position,
            )
            < 0.1
        )

        result = restore(game, emptyInventories=True)
        assert result["status"] == "restored", result
        assert result["backupId"] == newest["backupId"]
        assert game.grid(LOSS_ID)["items"] == "-"
    finally:
        game.probe("clear", grids.LOSS_CARGO_ID)
        bring_back(game)


def test_only_a_big_owner_restores(game):
    try:
        lose(game, "damage")
        result = restore(game, game.b)
        assert result["status"] == "refused"
        assert result["reason"] == "The player is not a big owner in the backup."
        assert not game.grid(LOSS_ID)
        with pytest.raises(CompanionError, match="big owner in no backup"):
            companion(game, "describe", steamId=steam(game.b), gridId=LOSS_ID)
    finally:
        bring_back(game)


def test_every_big_owner_sees_a_shared_grid(game):
    game.scan()
    for player in (game.a, game.b):
        listed = {
            g["gridId"]: g
            for g in companion(game, "list", steamId=steam(player))["grids"]
        }
        shared = listed[SHARED_BASE.entity_id]
        assert shared["present"] is True
        assert sorted(shared["ownerSteamIds"]) == sorted([steam(game.a), steam(game.b)])
        assert shared["backupCount"] == 1
    described = companion(
        game, "describe", steamId=steam(game.b), gridId=SHARED_BASE.entity_id
    )
    assert described["name"] == SHARED_BASE.name
    # The admin command answers for every big owner too
    assert SHARED_BASE.folder in game.a.command("!gridbackup list Bravo")


def test_restore_keeps_away_from_other_players_grids(game):
    try:
        lose(game, "damage")
        result = restore(game, foreignExclusionMeters=2000)
        assert result["status"] == "restored", result
        spot = tuple(result["position"][k] for k in "xyz")
        # The base Bravo co-owns is within 2 km of the original place
        assert distance(LOSS_SHIP.position, SHARED_BASE.position) < 2000
        for foreign in (BRAVO_STATION, SHARED_BASE):
            assert distance(spot, foreign.position) >= 2000
        assert distance(game.grid(LOSS_ID)["pos"], spot) < 0.1
        companion(game, "remove-restored", entityIds=result["entityIds"])
        wait_until(lambda: not game.grid(LOSS_ID), 15, "the restored grid to go")

        nowhere = restore(game, foreignExclusionMeters=2000, maxSearchMeters=500)
        assert nowhere["status"] == "no-spot"
        assert not game.grid(LOSS_ID)
    finally:
        bring_back(game)


def test_restore_near_the_player_as_a_station(game):
    try:
        lose(game, "damage")
        result = restore(game, placement="near-player-station")
        assert result["status"] == "restored", result
        state = game.grid(LOSS_ID)
        assert state["static"] == "1"
        player = parse(game.probe("player", world.PLAYER_A))
        assert distance(state["pos"], vector(player["pos"])) < 300
        companion(game, "remove-restored", entityIds=result["entityIds"])
        wait_until(lambda: not game.grid(LOSS_ID), 15, "the restored grid to go")
    finally:
        bring_back(game)


def test_losses_since(game):
    try:
        lose(game, "damage")
        later = (datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat()
        assert losses(game, sinceUtc=later) == []
        assert len(losses(game, sinceUtc="2000-01-01T00:00:00Z")) == 1
    finally:
        bring_back(game)


@pytest.mark.skipif(
    not os.environ.get("GB_MAGNETAR_PROTOCOL"),
    reason="GB_MAGNETAR_PROTOCOL names no Magnetar.Protocol.dll",
)
def test_operations_through_quasars_handler_interface(game):
    path = os.environ["GB_MAGNETAR_PROTOCOL"]
    assert game.probe("protocol", path) == "Probe protocol: Magnetar.Protocol"
    game.scan()
    listed: list[dict] = []

    def answered() -> bool:
        try:
            listed[:] = companion(game, "list", via="quasar", steamId=steam(game.a))[
                "grids"
            ]
        except AssertionError:
            # "Probe quasar: no handler" until the bridge registers
            return False
        return True

    wait_until(answered, 30, "the companion handler", interval=2)
    assert ALPHA_STATION.entity_id in {g["gridId"] for g in listed}
    with pytest.raises(CompanionError, match="Unsupported operation"):
        companion(game, "nope", via="quasar")
