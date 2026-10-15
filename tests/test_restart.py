"""A server restart: the backups and the config stay, the backups can be listed
and restored afterwards, and the first scan after the start backs up every
grid again, since the plugin keeps its backup state in memory only.

This test restarts server 0 and joins the clients again; conftest.py runs it
last.
"""

from __future__ import annotations

import time

import grids
from conftest import wait_idle
from grids import ALPHA_STATION
from session import CLIENTS


def test_backups_and_config_survive_a_restart(game):
    game.configure(NumberOfBackupSaves=4)
    game.run()
    before = set(game.files())
    for client in CLIENTS:
        client.stop()
    game.server.stop()

    game.server.start()
    game.rejoin()
    time.sleep(2)
    wait_idle(game.server)
    assert (
        "<NumberOfBackupSaves>4</NumberOfBackupSaves>"
        in (game.server.plugin_dir / "GridBackups.cfg").read_text()
    )
    after = set(game.files())
    assert before < after
    for g in grids.SCANNED:
        assert len([p for p in after if p.parent.name == g.folder]) == 2, g.name
    reply = game.a.command(f"!gridbackup list Alpha {ALPHA_STATION.entity_id}")
    assert len(reply.splitlines()) == 3
