"""Several Magnetar servers as the nodes of one cluster that share one cluster
storage root, for testing what a plugin keeps in PluginSdk's shared storage
(PluginStorage.GetSharedDirectory).

Each node is a server of the slot (rig.Server) started with CLUSTER_NODE_ID
and CLUSTER_SHARED_ROOT. That makes it a cluster process for the PluginSdk:
PluginCluster.IsClusterProcess is true, and PluginStorage hands every node the
same <shared root>/plugins/<plugin folder>. Nothing else of a cluster runs: no
Gateway, no World Authority, no partitions, no grid handover. Every node loads
its own copy of the same world, so all of them see every grid at the same
time, the worst case for plugins that coordinate through shared files. Nodes
take no clients; drive them with Server.console, which needs the probe
(tests/probe) loaded, as rig.Server does.

    def setup(node):
        session.prepare_world(node)
        node.write_config(...)

    with Cluster([1, 2], setup) as cluster:
        cluster.nodes[0].console("!gridbackup run")

Usable without Grid Backups too: copy rig.py, nodes.py and tests/probe, and
add the plugin under test to Server.prepare's dev folders.
"""

from __future__ import annotations

import hashlib
import shutil
import threading
from pathlib import Path

import rig


def folder_name(plugin_id: str) -> str:
    """PluginStorage.FolderName: a file-safe prefix of the id and 16 hex
    digits of its SHA-256"""
    safe = ""
    for c in plugin_id:
        dot = c == "." and (not safe or safe[-1] != ".")
        ok = (c.isalnum() and ord(c) < 128) or dot or c in "_-"
        safe += c if ok else "_"
    prefix = safe.strip(".")[:48].rstrip(".")
    digest = hashlib.sha256(plugin_id.encode()).hexdigest()[:16]
    return f"{prefix or 'plugin'}-{digest}"


class Cluster:
    """Servers `indices` of the slot as nodes node-1, node-2, ... sharing the
    storage root `shared` (~/.se-test/<task>-shared by default, emptied on
    start). setup(node) writes a node's world and configs after its folders
    are prepared."""

    def __init__(self, indices, setup, shared: Path | None = None):
        self.shared = shared or rig.TEST_ROOT / f"{rig.TASK}-shared"
        self.setup = setup
        self.nodes = [
            rig.Server(
                index,
                env={
                    "CLUSTER_NODE_ID": f"node-{n}",
                    "CLUSTER_SHARED_ROOT": str(self.shared),
                },
            )
            for n, index in enumerate(indices, 1)
        ]

    def shared_dir(self, plugin_id: str) -> Path:
        return self.shared / "plugins" / folder_name(plugin_id)

    def start(self) -> None:
        self.stop()
        if self.shared.exists():
            shutil.rmtree(self.shared)
        self.shared.mkdir(parents=True)
        for node in self.nodes:
            node.prepare([])
            self.setup(node)
        errors = []

        def run(node):
            try:
                node.start()
            except Exception as error:  # noqa: BLE001 -- reported below
                errors.append((node.index, error))

        threads = [threading.Thread(target=run, args=(n,)) for n in self.nodes]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        if errors:
            raise RuntimeError(f"Starting the nodes failed: {errors}")

    def stop(self) -> None:
        for node in self.nodes:
            node.stop()

    def __enter__(self) -> Cluster:
        try:
            self.start()
        except BaseException:
            self.stop()
            raise
        return self

    def __exit__(self, *exc) -> None:
        self.stop()
