"""What a backup holds and where it goes: grids joined by a rotor in one
backup or apart, owner folder names, NPC and nobody-owned grids, the original
owners, and the retention limits pruning old files."""

from __future__ import annotations

import os
import re
import time
import xml.etree.ElementTree as ET
from datetime import date

import grids
import world
from conftest import parse
from grids import (
    ALPHA_STATION,
    BRAVO_STATION,
    CHARLIE_BASE,
    ODD_NAME,
    PIRATE_BASE,
    ROTOR_BASE,
    WRECK,
)
from session import CLIENT_A

DAY_S = 86400


def blueprint_grids(path) -> list[ET.Element]:
    """The CubeGrid elements of a backup file"""
    return (
        ET.parse(path)
        .getroot()
        .findall("./ShipBlueprints/ShipBlueprint/CubeGrids/CubeGrid")
    )


def attach_rotor_head(game) -> int:
    top = int(parse(game.probe("attach", grids.STATOR_ID))["top"])
    assert top
    return top


def folders(game) -> set[str]:
    """owner folder/grid folder of every backup"""
    return {str(p.parent) for p in game.files()}


def age_file(path, days: float) -> None:
    then = time.time() - days * DAY_S
    os.utime(path, (then, then))


# --- grouping -----------------------------------------------------------------


def test_connected_grids_in_one_backup(game):
    top = attach_rotor_head(game)
    game.scan()
    [backup] = game.grid_files(ROTOR_BASE)
    names = sorted(g.findtext("DisplayName") for g in blueprint_grids(backup))
    assert len(names) == 2 and ROTOR_BASE.name in names
    assert top not in game.backed_up()


def test_connected_grids_apart(game):
    top = attach_rotor_head(game)
    game.configure(BackupConnectedGrids=False, MinBlocksForBackup=1)
    game.scan()
    [backup] = game.grid_files(ROTOR_BASE)
    assert len(blueprint_grids(backup)) == 1
    assert top in game.backed_up()


# --- owner folders ------------------------------------------------------------


def test_owner_folders_by_identity_id(game):
    game.scan()
    found = folders(game)
    for g, owner in (
        (ALPHA_STATION, world.PLAYER_A),
        (BRAVO_STATION, world.PLAYER_B),
        (CHARLIE_BASE, world.PLAYER_C),
    ):
        assert f"{owner}/{g.folder}" in found


def test_owner_folders_with_player_names(game):
    game.configure(PlayerNameOnFolders=True)
    game.scan()
    found = folders(game)
    assert f"Alpha_{world.PLAYER_A}/{ALPHA_STATION.folder}" in found
    assert f"Bravo_{world.PLAYER_B}/{BRAVO_STATION.folder}" in found
    assert f"Charlie_{world.PLAYER_C}/{CHARLIE_BASE.folder}" in found


def test_owner_folders_by_steam_id(game):
    game.configure(UseSteamId=True)
    game.scan()
    found = folders(game)
    assert f"{CLIENT_A.steam_id}/{ALPHA_STATION.folder}" in found
    # Charlie never joined, so the server knows no Steam id for him
    assert f"{world.PLAYER_C}/{CHARLIE_BASE.folder}" in found


def test_grid_names_made_safe_for_folders(game):
    game.scan()
    assert f"{world.PLAYER_A}/Odd_name_x__{ODD_NAME.entity_id}" in folders(game)


# --- NPC and nobody grids -----------------------------------------------------


def test_npc_and_nobody_grids_left_out(game):
    game.scan()
    assert not {PIRATE_BASE.entity_id, WRECK.entity_id} & game.backed_up()


def test_npc_and_nobody_grids_included(game):
    game.configure(BackupNpcGrids=True, BackupNobodyGrids=True)
    game.scan()
    found = folders(game)
    assert f"{world.NPC}/{PIRATE_BASE.folder}" in found
    assert f"0/{WRECK.folder}" in found


def test_nobody_folder_with_player_names(game):
    game.configure(BackupNobodyGrids=True, PlayerNameOnFolders=True)
    game.scan()
    assert f"Nobody_0/{WRECK.folder}" in folders(game)


# --- owners in the backup -----------------------------------------------------


def owners(path) -> set[int]:
    text = path.read_text(encoding="utf-8")
    return {int(i) for i in re.findall(r"<(?:Owner|BuiltBy)>(-?\d+)</", text)}


def test_backups_keep_the_owners(game):
    game.run()
    [backup] = game.grid_files(ALPHA_STATION)
    assert world.PLAYER_A in owners(backup)


def test_backups_without_the_owners(game):
    game.configure(KeepOriginalOwner=False)
    game.run()
    [backup] = game.grid_files(ALPHA_STATION)
    assert owners(backup) <= {0}


# --- retention ----------------------------------------------------------------


def test_keeps_the_newest_backups(game):
    game.configure(NumberOfBackupSaves=2)
    names = []
    for _ in range(3):
        game.run()
        names.append(game.grid_files(ALPHA_STATION)[0].name)
        time.sleep(1.1)
    assert [p.name for p in game.grid_files(ALPHA_STATION)] == names[:0:-1]


def test_one_daily_backup_a_day(game):
    game.configure(NumberOfDailyBackupSaves=2)
    game.run()
    time.sleep(1.1)
    game.run()
    [daily] = game.grid_files(ALPHA_STATION, daily=True)
    assert daily.name == f"daily_{date.today():%Y_%m_%d}.sbc"
    assert len(game.grid_files(ALPHA_STATION)) == 2


def test_keeps_the_newest_daily_backups(game):
    game.configure(NumberOfDailyBackupSaves=2)
    game.run()
    [today] = game.grid_files(ALPHA_STATION, daily=True)
    for days in (2, 3):
        old = today.with_name(f"daily_2020_01_0{days}.sbc")
        old.write_bytes(today.read_bytes())
        age_file(old, days)
    time.sleep(1.1)
    game.run()
    assert [p.name for p in game.grid_files(ALPHA_STATION, daily=True)] == [
        today.name,
        "daily_2020_01_02.sbc",
    ]


def test_deletes_backups_older_than_the_limit(game):
    game.configure(DeleteBackupsOlderThanDays=2)
    game.run()
    alpha = game.grid_files(ALPHA_STATION)[0]
    bravo = game.grid_files(BRAVO_STATION)[0]
    old = alpha.with_name("2020_01_01_00_00_00.sbc")
    recent = alpha.with_name("2020_01_02_00_00_00.sbc")
    other = bravo.with_name("2020_01_01_00_00_00.sbc")
    for path, days in ((old, 3), (recent, 1), (other, 3)):
        path.write_bytes(alpha.read_bytes())
        age_file(path, days)
    time.sleep(1.1)
    game.probe("forget")  # the age cleanup runs at most once an hour
    game.run()
    assert not old.exists() and not other.exists()
    assert recent.exists()


def test_age_cleanup_at_most_once_an_hour(game):
    game.configure(DeleteBackupsOlderThanDays=2)
    game.run()
    alpha = game.grid_files(ALPHA_STATION)[0]
    old = alpha.with_name("2020_01_01_00_00_00.sbc")
    old.write_bytes(alpha.read_bytes())
    age_file(old, 3)
    time.sleep(1.1)
    game.run()
    assert old.exists()
