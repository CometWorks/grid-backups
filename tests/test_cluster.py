"""Cluster storage: two nodes keep their backups in the cluster's shared
storage (PluginSdk PluginStorage.GetSharedDirectory) and coordinate through
it: the minimum time counts the other node's backups, file names don't clash,
retention and daily backups hold across nodes, and a node waits for another
node's lock on a grid folder. A cluster process without shared storage gets no
backups at all.

The nodes are Magnetar servers of their own (nodes.py), both running the
whole test world, so both see every grid. No clients join them.
"""

from __future__ import annotations

import fcntl
import re
import time
from datetime import date

import grids
import nodes
import pytest
import rig
import session
from conftest import wait_idle, wait_until
from grids import ALPHA_STATION

CONFIG = {
    **session.DEFAULT_CONFIG,
    "ScanIntervalSeconds": 2,
    "MinMinutesSinceLastSave": 10,
}
NAME = re.compile(r"\d{4}(_\d\d){5}_[0-9a-f]{32}\.sbc")


def setup(node: rig.Server) -> None:
    session.prepare_world(node)
    node.write_config(**CONFIG)


@pytest.fixture(scope="module")
def cluster():
    with nodes.Cluster([1, 2], setup) as cluster:
        root = cluster.shared_dir(rig.GRID_BACKUPS_ID) / "GridBackups"
        for node in cluster.nodes:
            node.backups = root
        yield cluster


@pytest.fixture
def node1(cluster):
    """The nodes back to CONFIG, no backups being written"""
    for node in cluster.nodes:
        node.write_config(**CONFIG)
        assert node.probe("reload") == "Probe: config reloaded"
        wait_idle(node)
    return cluster.nodes[0]


def files(node, g: grids.Grid = ALPHA_STATION, daily=False) -> list:
    folder = node.backups / str(g.owner) / g.folder
    found = folder.glob("*.sbc") if folder.exists() else []
    return sorted(p.name for p in found if p.name.startswith("daily_") == daily)


def run(node) -> None:
    assert node.console("!gridbackup run") == "Backup creation started."
    time.sleep(0.5)
    wait_idle(node, timeout=60)


def test_backups_in_the_shared_storage(cluster, node1):
    for node in cluster.nodes:
        reply = node.console("!gridbackup status")
        assert reply.startswith(f"root={node.backups},"), reply


def test_one_backup_per_grid_from_both_nodes(cluster, node1):
    # Both nodes scan every 2 s from the start; whichever comes second finds
    # the other's backup younger than the minimum time
    for node in cluster.nodes:
        wait_until(
            lambda: node.probe("state", ALPHA_STATION.entity_id) != "Probe state: none",
            60,
            "a scan",
        )
        wait_idle(node)
    for g in grids.SCANNED:
        assert len(files(node1, g)) == 1, g.name


def test_forced_backups_from_both_nodes(cluster, node1):
    before = files(node1)
    for node in cluster.nodes:
        run(node)
    new = sorted(set(files(node1)) - set(before))
    assert len(new) == 2
    assert all(NAME.fullmatch(n) for n in new), new


def test_retention_across_nodes(cluster, node1):
    for node in cluster.nodes:
        node.write_config(**{**CONFIG, "NumberOfBackupSaves": 3})
        node.probe("reload")
    for _ in range(2):
        for node in cluster.nodes:
            run(node)
    assert len(files(node1)) == 3


def test_one_daily_backup_across_nodes(cluster, node1):
    for node in cluster.nodes:
        node.write_config(**{**CONFIG, "NumberOfDailyBackupSaves": 2})
        node.probe("reload")
        run(node)
    assert files(node1, daily=True) == [f"daily_{date.today():%Y_%m_%d}.sbc"]


def test_waits_for_a_grid_lock_of_another_node(cluster, node1):
    folder = node1.backups / str(ALPHA_STATION.owner) / ALPHA_STATION.folder
    before = files(node1)
    other_before = files(node1, grids.TRIGGER)
    # The same lock the nodes take: a POSIX record lock on the first byte
    with open(folder / ".backup.lock", "a+b") as lock:
        fcntl.lockf(lock, fcntl.LOCK_EX | fcntl.LOCK_NB, 1, 0)
        run(node1)  # Alpha's backup gives up after 5 s
        assert files(node1) == before
        assert set(files(node1, grids.TRIGGER)) - set(other_before)
    node1.probe("flush")
    assert "Grid backup failed" in node1.game_log()
    time.sleep(1.1)
    run(node1)
    assert set(files(node1)) - set(before)


def test_no_backups_without_shared_storage(cluster):
    node = rig.Server(3, env={"CLUSTER_NODE_ID": "node-3"})
    node.prepare([])
    setup(node)
    try:
        node.start()
        reply = node.console("!gridbackup status")
        assert "CLUSTER_SHARED_ROOT" in reply, reply
        node.probe("flush")
        assert re.search(
            r"\[GridBackups\] .*Failed to load(?! config)", node.game_log()
        )
        assert not list(node.quasar.rglob("*.sbc"))
    finally:
        node.stop()
