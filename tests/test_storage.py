"""Where a standalone server keeps the plugin's config and backups: the Quasar
data folder from QUASAR_DATA_DIR, or derived from QUASAR_DS_CONFIG_PATH (the
folder above its Magnetars folder), or the platform default in the home
folder; and what a broken config file does.

Each test starts server 3 of the slot with the environment it needs, no
clients. Server 0 covers QUASAR_DATA_DIR alone.
"""

from __future__ import annotations

import pytest
import rig
import session


def start(servers, env: dict, quasar, config: str | None = None) -> rig.Server:
    """Server 3 with this environment, stopped after the test"""
    server = rig.Server(3, env=env, quasar=quasar)
    servers.append(server)
    server.prepare([])
    session.prepare_world(server)
    if config is None:
        server.write_config(**session.DEFAULT_CONFIG)
    else:
        (server.plugin_dir / "GridBackups.cfg").write_text(config, encoding="utf-8")
    server.start()
    return server


@pytest.fixture
def servers():
    """Stops what the test started"""
    started = []
    yield started
    for server in started:
        server.stop()


def status_root(server: rig.Server) -> str:
    return server.console("!gridbackup status").split(",")[0].removeprefix("root=")


def test_quasar_folder_from_the_ds_config_path(servers):
    root = rig.TEST_ROOT / f"{rig.TASK}-ds3" / "quasar-derived"
    cfg = root / "Magnetars" / "test" / "SpaceEngineers-Dedicated.cfg"
    server = start(
        servers, {"QUASAR_DATA_DIR": None, "QUASAR_DS_CONFIG_PATH": str(cfg)}, root
    )
    assert status_root(server) == str(root / "GridBackups")
    assert (root / "GridBackups" / "GridBackups.schema.json").exists()
    # The server's first scan backed up every grid there
    assert list((root / "GridBackups").rglob("*.sbc"))


def test_quasar_data_dir_before_the_ds_config_path(servers):
    root = rig.TEST_ROOT / f"{rig.TASK}-ds3" / "quasar"
    cfg = rig.TEST_ROOT / f"{rig.TASK}-ds3" / "other" / "Magnetars" / "x" / "ds.cfg"
    server = start(servers, {"QUASAR_DS_CONFIG_PATH": str(cfg)}, root)
    assert status_root(server) == str(root / "GridBackups")


def test_quasar_folder_in_the_home_folder(servers):
    home = rig.TEST_ROOT / f"{rig.TASK}-ds3" / "home"
    root = home / ".config" / "Quasar"
    server = start(servers, {"QUASAR_DATA_DIR": None, "HOME": str(home)}, root)
    assert status_root(server) == str(root / "GridBackups")


def test_broken_config(servers):
    broken = "<GridBackupsConfig><Enabled>maybe</Enabled>"
    root = rig.TEST_ROOT / f"{rig.TASK}-ds3" / "quasar"
    server = start(servers, {}, root, config=broken)
    # The plugin runs on the defaults
    assert status_root(server) == str(root / "GridBackups")
    server.probe("flush")
    log = server.game_log()
    assert "Failed to load config, using defaults" in log
    assert "Moved the broken config aside" in log
    # Kept next to the new config of defaults
    (kept,) = server.plugin_dir.glob("GridBackups.cfg.corrupted.*.txt")
    assert kept.read_text() == broken
    assert (
        "<Enabled>true</Enabled>" in (server.plugin_dir / "GridBackups.cfg").read_text()
    )
