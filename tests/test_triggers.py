"""What starts a backup: the first scan of a grid, the minimum and maximum time
since its last backup, a block count that settled, a large block count change,
and the entry points other plugins call.

The tests scan on demand (`Game.scan`) and move a grid's last backup back in
time with the probe instead of waiting for minutes to pass.

Concealment 1.1.0 and later call BackupBeforeConcealment before they conceal a
grid group, and the Concealment suite tests that. Here the probe calls the entry
points the way Concealment does, so this suite runs without Concealment.
Concealment asks WouldBackupBeforeConcealment first, so it builds object
builders only for a backup that gets written; its answer must match what
BackupBeforeConcealment then does.
"""

from __future__ import annotations

import time

import grids
from grids import ALPHA_SHIP, ALPHA_STATION, PIRATE_BASE, TINY, TRIGGER, WRECK


def test_first_scan_backs_up_every_grid(game):
    game.scan()
    assert game.backed_up() == {g.entity_id for g in grids.SCANNED}
    assert len(game.files()) == len(grids.SCANNED)
    assert {game.reason(p) for p in game.files()} == {"Initial"}


def test_no_backup_while_disabled(game):
    game.configure(Enabled=False)
    game.scan()
    assert game.files() == []


def test_no_second_backup_without_a_trigger(game):
    game.scan()
    time.sleep(1.1)
    game.scan()
    assert len(game.files()) == len(grids.SCANNED)


def test_grids_below_the_minimum_block_count(game):
    game.scan()
    assert TINY.entity_id not in game.backed_up()

    game.configure(MinBlocksForBackup=1)
    game.probe("forget")
    time.sleep(1.1)
    game.scan()
    assert TINY.entity_id in game.backed_up()


def test_max_time_forces_a_backup(game):
    game.configure(MaxMinutesSinceLastSave=1)
    game.scan()
    time.sleep(1.1)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 1

    game.probe("age", TRIGGER.entity_id, 61)
    game.scan()
    first, latest = reversed(game.grid_files(TRIGGER))
    # A first backup is Initial even with a max time set
    assert game.reason(first) == "Initial"
    assert game.reason(latest) == "MaxTime"
    assert len(game.grid_files(ALPHA_STATION)) == 1


def test_max_time_overrides_the_min_time(game):
    game.configure(MaxMinutesSinceLastSave=2, MinMinutesSinceLastSave=3)
    game.scan()
    game.probe("age", TRIGGER.entity_id, 150)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 2
    assert game.reason(game.grid_files(TRIGGER)[0]) == "MaxTime"


def test_block_count_change(game):
    game.configure(MaxBlockCountChange=3)
    game.scan()
    game.probe("raze", TRIGGER.entity_id, 2)
    time.sleep(1.1)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 1

    game.probe("raze", TRIGGER.entity_id, 1)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 2
    assert game.reason(game.grid_files(TRIGGER)[0]) == "BlockCountChange"
    assert len(game.grid_files(ALPHA_STATION)) == 1


def test_min_time_holds_back_a_block_count_backup(game):
    game.configure(MaxBlockCountChange=1, MinMinutesSinceLastSave=1)
    game.scan()
    game.probe("raze", TRIGGER.entity_id, 1)
    time.sleep(1.1)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 1

    game.probe("age", TRIGGER.entity_id, 61)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 2


def test_steady_backup_once_the_block_count_settles(game):
    game.configure(BackupIfSteady=True, SteadySeconds=5, MinMinutesSinceLastSave=1)
    game.scan()
    # A block razed and the minimum time over: the scan sees the change, so
    # the grid isn't steady yet
    game.probe("raze", TRIGGER.entity_id, 1)
    game.probe("age", TRIGGER.entity_id, 61)
    time.sleep(1.1)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 1

    time.sleep(6)
    game.scan()
    assert len(game.grid_files(TRIGGER)) == 2
    assert game.reason(game.grid_files(TRIGGER)[0]) == "Steady"


# --- the entry points of other plugins ----------------------------------------


def test_backup_before_concealment(game):
    assert game.probe("would", ALPHA_SHIP.entity_id) == "Probe would: True"
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: True"
    assert len(game.grid_files(ALPHA_SHIP)) == 1
    assert game.reason(game.grid_files(ALPHA_SHIP)[0]) == "BeforeConcealment"
    assert game.backed_up() == {ALPHA_SHIP.entity_id}


def test_backup_before_concealment_switched_off(game):
    game.configure(BackupBeforeConcealment=False)
    assert game.probe("would", ALPHA_SHIP.entity_id) == "Probe would: False"
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: False"
    game.configure(BackupBeforeConcealment=True, Enabled=False)
    assert game.probe("would", ALPHA_SHIP.entity_id) == "Probe would: False"
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: False"
    assert game.files() == []


def test_backup_before_concealment_applies_the_scan_filters(game):
    # Tiny is below the minimum block count, the pirate base is an NPC's and the
    # wreck is nobody's: the scans skip them, and so does a backup before concealment
    skipped = (TINY, PIRATE_BASE, WRECK)
    for grid in skipped:
        assert game.probe("would", grid.entity_id) == "Probe would: False", grid.name
        assert (
            game.probe("conceal", grid.entity_id) == "Probe conceal: False"
        ), grid.name
    assert game.files() == []

    game.configure(MinBlocksForBackup=1, BackupNpcGrids=True, BackupNobodyGrids=True)
    for grid in skipped:
        assert game.probe("would", grid.entity_id) == "Probe would: True", grid.name
        assert game.probe("conceal", grid.entity_id) == "Probe conceal: True", grid.name
    assert game.backed_up() == {g.entity_id for g in skipped}


def test_backup_before_concealment_waits_for_the_min_time(game):
    game.configure(MinMinutesSinceLastSave=1)
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: True"
    time.sleep(1.1)
    assert game.probe("would", ALPHA_SHIP.entity_id) == "Probe would: False"
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: False"
    game.probe("age", ALPHA_SHIP.entity_id, 61)
    assert game.probe("would", ALPHA_SHIP.entity_id) == "Probe would: True"
    assert game.probe("conceal", ALPHA_SHIP.entity_id) == "Probe conceal: True"
    assert len(game.grid_files(ALPHA_SHIP)) == 2


def test_manual_backup_by_another_plugin_ignores_the_min_time(game):
    game.configure(MinMinutesSinceLastSave=10)
    assert game.probe("external", ALPHA_SHIP.entity_id) == "Probe external: True"
    time.sleep(1.1)
    assert game.probe("external", ALPHA_SHIP.entity_id) == "Probe external: True"
    assert len(game.grid_files(ALPHA_SHIP)) == 2
    assert game.reason(game.grid_files(ALPHA_SHIP)[0]) == "Manual"


def test_backups_within_the_same_second(game):
    # Three backups in well under a second: two of them share the second
    reply = game.probe("external", ALPHA_SHIP.entity_id, 3)
    assert reply == "Probe external: True True True"
    names = [p.name for p in game.grid_files(ALPHA_SHIP)]
    assert len(names) == 3
    assert any(n.endswith("_2.sbc") for n in names), names
