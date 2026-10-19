"""The test rig: isolated Magnetar servers running Grid Backups from this
working copy, and two headless game clients that join server 0 over
DirectTransport and are driven through the Remote plugin.

Every port, client id and folder comes from a test slot of the workspace
(testing/parallel-test-work/README.md in the se workspace), so a run doesn't
collide with other tests on the machine. GB_TASK names the slot; it is claimed
on first use and kept until released with slot.sh. GB_SLOT skips the claim.

Server i of the slot has its Magnetar config folder in
~/.se-test/<task>-ds<i>/magnetar and its DS data folder in
~/.se-test/<task>-ds<i>/data. It compiles Grid Backups, the test probe
(tests/probe) and DirectTransport as dev folders, and Grid Backups keeps its
config and backups in the Quasar data folder ~/.se-test/<task>-ds<i>/quasar.
Server 0 is the one the clients join; nodes.py starts more of them as the
nodes of a cluster. The clients are clients 1 and 2 of the slot: Pulsar folder
~/.se-test/<task>-c<i>, made on first use with new-pulsar-instance.sh, user
data ~/.se-test/<task>-c<i>-data. Each compiles Remote and DirectTransport as
dev folders. Every process starts under run-test.sh, which waits for the RAM
floor (SE_RAM_FLOOR_GIB) and holds the shared test lock.

Paths, all overridable by environment variables:

    GB_WORKSPACE             the se workspace, for its testing/ (default: the
                             folder that holds se1/plugins/grid-backups)
    GB_MAGNETAR              Magnetar install (~/.config/Magnetar)
    GB_PULSAR                Pulsar install the client folders are copied from
    GB_DS64                  DedicatedServer64 of the Steam install
    GB_BIN64                 Bin64 of the game client
    GB_GAME_CFG              SpaceEngineers.cfg the clients start from
    GB_REMOTE_REPO           Remote plugin working copy
    GB_DIRECT_TRANSPORT_REPO DirectTransport working copy

The server and the clients download hub plugins from GitHub. On a machine
GitHub's API rate limits, export PULSAR_GITHUB_TOKEN: Pulsar and Magnetar read
it from the environment the rig passes on. Or put the plugins Magnetar can't
download into GB_SERVER_DEV_FOLDERS as dev folders: name|folder|file entries
separated by ";".
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

HOME = Path.home()
REPO = Path(__file__).resolve().parent.parent
TESTS = REPO / "tests"
PROBE = TESTS / "probe"
WORKSPACE = Path(os.environ.get("GB_WORKSPACE", REPO.parents[2]))
PARALLEL = WORKSPACE / "testing" / "parallel-test-work"
NEW_PULSAR = WORKSPACE / "testing" / "pulsar-dev-instances" / "new-pulsar-instance.sh"
MAGNETAR = Path(os.environ.get("GB_MAGNETAR", HOME / ".config/Magnetar"))
PULSAR = Path(os.environ.get("GB_PULSAR", HOME / ".config/Pulsar"))
STEAM = HOME / ".steam/steam/steamapps/common"
if not STEAM.exists():
    STEAM = HOME / ".steam/debian-installation/steamapps/common"
DS64 = Path(
    os.environ.get("GB_DS64", STEAM / "SpaceEngineersDedicatedServer/DedicatedServer64")
)
BIN64 = Path(os.environ.get("GB_BIN64", STEAM / "SpaceEngineers/Bin64"))
EMPTY_WORLD = DS64.parent / "Content" / "CustomWorlds" / "Empty World"
GAME_CFG = Path(
    os.environ.get("GB_GAME_CFG", HOME / ".config/SpaceEngineers/SpaceEngineers.cfg")
)
REMOTE_REPO = Path(os.environ.get("GB_REMOTE_REPO", REPO.parent / "remote"))
DIRECT_TRANSPORT_REPO = Path(
    os.environ.get("GB_DIRECT_TRANSPORT_REPO", WORKSPACE / "se1" / "direct-transport")
)

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))

from se_remote import RemoteAPI  # noqa: E402

TASK = os.environ.get("GB_TASK", "gb")
TEST_ROOT = HOME / ".se-test"

GRID_BACKUPS_ID = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C"
PROBE_ID = "33BB4953-9BFE-49C4-A476-CF0E2BEFCC4C"

# Measured RSS (testing/parallel-test-work/README.md); the server is a guess
CLIENT_GIB = 3.7
SERVER_GIB = 4.0


def _claim_slot() -> dict[str, int]:
    slot = os.environ.get("GB_SLOT") or _run(PARALLEL / "slot.sh", "claim", TASK)
    text = _run(PARALLEL / "slot.sh", "env", slot)
    values = {k: int(v) for k, v in re.findall(r"(\w+_BASE)=(\d+)", text)}
    values["SLOT"] = int(slot)
    return values


def _run(*command) -> str:
    return subprocess.run(
        [str(c) for c in command], check=True, capture_output=True, text=True
    ).stdout.strip()


SLOT = _claim_slot()


# ---------------------------------------------------------------------------
# Processes. Each runs in a session of its own under run-test.sh, so the whole
# process group is ours to stop.
# ---------------------------------------------------------------------------


def _alive(pid_file: Path, marker: str) -> int | None:
    try:
        pid = int(pid_file.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if marker.encode() in cmdline else None


def start_process(
    command, need_gib: float, cwd: Path, log: Path, pid_file: Path, env=None
):
    with open(log, "w") as out:
        process = subprocess.Popen(
            [str(PARALLEL / "run-test.sh"), str(need_gib), "--", *map(str, command)],
            cwd=cwd,
            env={**os.environ, **(env or {})},
            stdout=out,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            start_new_session=True,
        )
    # Until the child has exec'd, its command line is ours, and _alive would
    # take it for dead
    own = Path("/proc/self/cmdline").read_bytes()
    while process.poll() is None:
        try:
            if Path(f"/proc/{process.pid}/cmdline").read_bytes() != own:
                break
        except OSError:
            pass
        time.sleep(0.05)
    pid_file.write_text(str(process.pid))


def _group_alive(pgid: int) -> bool:
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False


def stop_process(pid_file: Path, marker: str, timeout: float = 60.0) -> None:
    """SIGTERM to the process group we started, SIGKILL after the timeout"""
    pid = _alive(pid_file, marker)
    if pid is None:
        pid_file.unlink(missing_ok=True)
        return
    os.killpg(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while _group_alive(pid):
        if time.monotonic() > deadline:
            os.killpg(pid, signal.SIGKILL)
            break
        time.sleep(1)
    pid_file.unlink(missing_ok=True)


def profile_xml(dev_folders) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>false</DebugBuild></LocalFolderConfig>"
        for i in dev_folders
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>"
        "<Local /><Mods /></Profile>\n"
    )


def write_sources(sources: Path, plugins: dict[str, tuple[Path, str]]) -> None:
    """The loader's dev folder sources: exactly these, name -> (folder, file)"""
    tree = ET.parse(sources)
    root = tree.getroot()
    local = root.find("LocalPluginSources")
    if local is not None:
        root.remove(local)
    local = ET.SubElement(root, "LocalPluginSources")
    for name, (folder, file) in plugins.items():
        plugin = ET.SubElement(local, "LocalPlugin")
        for tag, value in (
            ("Name", name),
            ("Folder", str(folder)),
            ("File", file),
            ("Enabled", "true"),
        ):
            ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


# ---------------------------------------------------------------------------
# Plugin config
# ---------------------------------------------------------------------------


def grid_backups_config(**options) -> str:
    """GridBackups.cfg with these options, by option name; the rest keep their
    defaults"""
    values = "".join(
        f"<{k}>{str(v).lower() if isinstance(v, bool) else v}</{k}>"
        for k, v in options.items()
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<GridBackupsConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"{values}</GridBackupsConfig>\n"
    )


# ---------------------------------------------------------------------------
# Servers
# ---------------------------------------------------------------------------


class Server:
    """Server i of the slot. env adds to its environment, a None value takes a
    variable out. Grid Backups finds its Quasar data folder in QUASAR_DATA_DIR,
    which is `quasar` unless env says otherwise; pass the folder the plugin
    will use as `quasar` then, for the config and the backup files."""

    def __init__(self, index: int = 0, env=None, quasar: Path | None = None):
        self.index = index
        self.root = TEST_ROOT / f"{TASK}-ds{index}"
        self.config = self.root / "magnetar"
        self.data = self.root / "data"
        self.quasar = quasar or self.root / "quasar"
        self.pid_file = self.root / "server.pid"
        self.log = self.root / "server.log"
        self.port = SLOT["DS_SERVER_PORT_BASE"] + index
        self.world = self.data / "Saves" / "GridBackupsTest"
        self.env = {"QUASAR_DATA_DIR": str(self.quasar), **(env or {})}
        # Where the plugin keeps its config, and its backups unless the config
        # or cluster storage moves them
        self.plugin_dir = self.quasar / "GridBackups"
        self.backups = self.plugin_dir

    def prepare(self, admins) -> None:
        """Magnetar's config and the dedicated config, and an empty Quasar
        data folder. The world is written separately, see world.prepare."""
        if not (self.config / "Sources").exists():
            # The install's plugin sources, hub cache and compiled hub plugins
            self.config.mkdir(parents=True, exist_ok=True)
            template = MAGNETAR / "Magnetar"
            shutil.copytree(template / "Sources", self.config / "Sources")
            shutil.copy(template / "config.xml", self.config / "config.xml")
            if (template / "GitHub").exists():
                shutil.copytree(template / "GitHub", self.config / "GitHub")
        sources = {
            "grid-backups": (REPO, "GridBackups.xml"),
            "grid-backups-probe": (PROBE, "GridBackupsProbe.xml"),
            "direct-transport": (DIRECT_TRANSPORT_REPO, "DirectTransportServer.xml"),
        }
        for entry in filter(
            None, os.environ.get("GB_SERVER_DEV_FOLDERS", "").split(";")
        ):
            name, folder, file = entry.split("|")
            sources[name] = (Path(folder), file)
        write_sources(self.config / "Sources" / "sources.xml", sources)
        (self.config / "Profiles").mkdir(exist_ok=True)
        (self.config / "Profiles" / "Current.xml").write_text(
            profile_xml(["direct-transport", GRID_BACKUPS_ID, PROBE_ID]),
            encoding="utf-8",
        )
        admins_xml = "".join(f"<unsignedLong>{a}</unsignedLong>" for a in admins)
        self.data.mkdir(parents=True, exist_ok=True)
        (self.data / "SpaceEngineers-Dedicated.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            '<MyConfigDedicated xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
            'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
            f"<IP>127.0.0.1</IP><SteamPort>{SLOT['DS_STEAM_PORT_BASE'] + self.index}</SteamPort>"
            f"<ServerPort>{self.port}</ServerPort>"
            f"<Administrators>{admins_xml}</Administrators><Banned />"
            f"<ServerName>Grid Backups Test {TASK} {self.index}</ServerName>"
            "<WorldName>GridBackupsTest</WorldName>"
            "<PauseGameWhenEmpty>false</PauseGameWhenEmpty>"
            "<AutoRestartEnabled>false</AutoRestartEnabled>"
            "<IgnoreLastSession>true</IgnoreLastSession>"
            "<RemoteApiEnabled>false</RemoteApiEnabled>"
            f"<LoadWorld>{self.world}</LoadWorld>"
            "</MyConfigDedicated>\n",
            encoding="utf-8",
        )
        if self.quasar.exists():
            shutil.rmtree(self.quasar)
        self.plugin_dir.mkdir(parents=True)
        for name in ("probe-cmd.txt", "probe-cmd.tmp", "probe-out.txt"):
            (self.root / name).unlink(missing_ok=True)

    def write_config(self, **options) -> None:
        """GridBackups.cfg, read when the plugin loads or on `!gbprobe reload`"""
        (self.plugin_dir / "GridBackups.cfg").write_text(
            grid_backups_config(**options), encoding="utf-8"
        )

    def backup_files(self) -> list[Path]:
        """Every grid backup Grid Backups wrote, daily ones included"""
        if not self.backups.exists():
            return []
        return sorted(
            p for p in self.backups.rglob("*.sbc") if p.parent.name != ".pending"
        )

    def clear_backups(self) -> None:
        """Deletes every backup, keeping the config"""
        if self.backups.exists():
            for path in self.backups.iterdir():
                if path.is_dir():
                    shutil.rmtree(path)

    def console(self, command: str, timeout: float = 60.0) -> str:
        """Runs a chat command as the server console, through the probe's
        command file, and returns its reply lines"""
        command_id = str(time.time_ns())
        cmd, tmp = self.root / "probe-cmd.txt", self.root / "probe-cmd.tmp"
        out = self.root / "probe-out.txt"
        deadline = time.monotonic() + timeout
        while cmd.exists():
            if time.monotonic() > deadline:
                raise TimeoutError(f"The server did not take {cmd}")
            time.sleep(0.2)
        tmp.write_text(f"{command_id}\t{command}\n", encoding="utf-8")
        tmp.replace(cmd)
        while time.monotonic() < deadline:
            if self.pid() is None:
                raise RuntimeError(f"The server exited, see {self.log}")
            lines = out.read_text(encoding="utf-8").splitlines() if out.exists() else []
            mine = [
                l.split("\t", 1)[1] for l in lines if l.startswith(command_id + "\t")
            ]
            if mine and mine[-1] == ".":
                return "\n".join(mine[:-1])
            time.sleep(0.2)
        raise TimeoutError(f"No reply to {command!r} from server {self.index}")

    def probe(self, *args) -> str:
        return self.console("!gbprobe " + " ".join(str(a) for a in args))

    def pid(self) -> int | None:
        return _alive(self.pid_file, str(self.config))

    def start(self, timeout: float = 2400.0) -> None:
        """Starts the server and waits for its world. The RAM guard may hold
        the start for a while."""
        if self.pid():
            raise RuntimeError(
                f"Server {self.index} is already running, pid {self.pid()}"
            )
        # The game log of the last start, kept for looking into a failure
        old_logs = self.root / "old-logs"
        old_logs.mkdir(exist_ok=True)
        for old in self.data.glob("SpaceEngineersDedicated*.log"):
            old.replace(old_logs / f"{time.strftime('%Y%m%d-%H%M%S')}-{old.name}")
        # Through env, so that run-test.sh itself keeps this environment: its
        # locks live in the real HOME
        variables = {
            "SE_DIRECT_TRANSPORT": "1",
            "GB_PROBE_DIR": str(self.root),
            **self.env,
        }
        start_process(
            [
                "env",
                *(f"-u{k}" for k, v in variables.items() if v is None),
                *(f"{k}={v}" for k, v in variables.items() if v is not None),
                MAGNETAR / "MagnetarInterim.bin",
                "-multiInstance",
                "-stableLogs",
                "-noimplicitmod",
                "-consent",
                "deny",
                "-config",
                self.config,
                "-ds64",
                DS64,
                "-path",
                self.data,
            ],
            SERVER_GIB,
            MAGNETAR,
            self.log,
            self.pid_file,
        )
        deadline = time.monotonic() + timeout
        while "Game ready" not in self.log.read_text(errors="replace"):
            if self.pid() is None:
                raise RuntimeError(f"Server {self.index} exited, see {self.log}")
            if time.monotonic() > deadline:
                raise TimeoutError(
                    f"Server {self.index} did not get ready, see {self.log}"
                )
            time.sleep(2)

    def stop(self) -> None:
        """SIGTERM makes Magnetar save the world and quit"""
        stop_process(self.pid_file, str(self.config), timeout=180)

    def game_log(self) -> str:
        """The dedicated server's log of the current start"""
        logs = sorted(self.data.glob("SpaceEngineersDedicated*.log"))
        return logs[-1].read_text(errors="replace") if logs else ""


# ---------------------------------------------------------------------------
# Clients
# ---------------------------------------------------------------------------


class Client:
    """Client i of the slot"""

    def __init__(self, index: int, name: str):
        self.index = index
        self.name = name
        self.pulsar = TEST_ROOT / f"{TASK}-c{index}"
        self.launcher = self.pulsar / "Interim.bin"
        self.appdata = TEST_ROOT / f"{TASK}-c{index}-data"
        self.port = SLOT["REMOTE_PORT_BASE"] + index
        self.steam_id = SLOT["CLIENT_ID_BASE"] + index
        self.pid_file = self.appdata / "game.pid"
        self.launch_log = self.appdata / "launch.log"
        self.log = self.appdata / "SpaceEngineers.log"

    def args(self, server_port: int) -> list:
        return [
            "-multiInstance",
            "-bin64",
            BIN64,
            "-lazySteam",
            "-noprompt",
            "-noupdate",
            "-nosplash",
            "-stablelogs",
            "-sources",
            "--no-steam",
            "--client-id",
            self.steam_id,
            "--client-name",
            self.name,
            "-appdata",
            self.appdata,
            "--quality",
            "minimal",
            "--resolution",
            "640x480",
            "--no-audio",
            "--headless",
            "--no-render",
            "--connect",
            f"127.0.0.1:{server_port}",
        ]

    def _ensure_pulsar(self) -> None:
        if not self.launcher.exists():
            subprocess.run(
                [str(NEW_PULSAR), str(self.pulsar), str(PULSAR)],
                check=True,
                stdout=subprocess.DEVNULL,
            )

    def _write_configs(self) -> None:
        legacy = self.pulsar / "Legacy"
        (legacy / "Profiles" / "Current.xml").write_text(
            profile_xml(["remote", "direct-transport"]), encoding="utf-8"
        )
        write_sources(
            legacy / "Sources" / "sources.xml",
            {
                "remote": (REMOTE_REPO, "Remote.xml"),
                "direct-transport": (
                    DIRECT_TRANSPORT_REPO,
                    "DirectTransportClient.xml",
                ),
            },
        )
        self.appdata.mkdir(parents=True, exist_ok=True)
        (self.appdata / "Remote.cfg").write_text(
            '<?xml version="1.0" encoding="utf-8"?>\n'
            "<PluginConfig><Enabled>true</Enabled><ListenIP>127.0.0.1</ListenIP>"
            f"<ListenPort>{self.port}</ListenPort>"
            "<AdminPassword>SpaceEngineers</AdminPassword>"
            "<GridGetRateLimit>1000</GridGetRateLimit><GridGetBurstSize>2000</GridGetBurstSize>"
            "<GridSetRateLimit>200</GridSetRateLimit><GridSetBurstSize>400</GridSetBurstSize>"
            "</PluginConfig>\n",
            encoding="utf-8",
        )
        game_cfg = self.appdata / "SpaceEngineers.cfg"
        if not game_cfg.exists():
            shutil.copy(GAME_CFG, game_cfg)

    def pid(self) -> int | None:
        return _alive(self.pid_file, str(self.launcher))

    def launch(self, server_port: int) -> None:
        if self.pid():
            raise RuntimeError(f"Client {self.index} is already running")
        self._ensure_pulsar()
        self._write_configs()
        self.log.unlink(missing_ok=True)
        start_process(
            [self.launcher, *self.args(server_port)],
            CLIENT_GIB,
            self.pulsar,
            self.launch_log,
            self.pid_file,
            {"PULSAR_NO_RENDER": "true"},
        )

    def stop(self) -> None:
        stop_process(self.pid_file, str(self.launcher))

    def api(self) -> RemoteAPI:
        return RemoteAPI(
            f"http://127.0.0.1:{self.port}", username="admin", password="SpaceEngineers"
        )

    def wait_api(self, timeout: float = 2400.0) -> RemoteAPI:
        """The RAM guard may hold the launch for a while, so this waits as long
        as the process lives"""
        api = self.api()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if not self.pid():
                raise RuntimeError(f"Client {self.index} exited, see {self.launch_log}")
            try:
                api.ping()
                return api
            except Exception:  # noqa: BLE001 -- not listening yet
                time.sleep(3)
        raise TimeoutError(f"Client {self.index} has no Remote API")


def wait_world(api: RemoteAPI, timeout: float = 900.0) -> None:
    """Waits until the session is ready, clicking OK on any message box"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(1)
        try:
            boxes = [
                s
                for s in api.list_screens()
                if s.get("type") == "MyGuiScreenMessageBox"
            ]
            for box in boxes:
                api.control_click(text="OK", screen=box["index"])
            if not boxes and api.get_state().get("ready"):
                return
        except Exception:  # noqa: BLE001 -- the API times out while a world loads
            pass
    raise TimeoutError("The world did not become ready")


def ensure_character(api: RemoteAPI, timeout: float = 300.0) -> dict:
    """Respawns at the first spawn option until there is a live character"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            character = api.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        try:
            screens = api.list_screens()
            medical = next(
                (i for i, s in enumerate(screens) if "Medical" in s.get("type", "")),
                None,
            )
            if medical is not None:
                for button in ("Join", "Respawn"):
                    try:
                        api.control_click(text=button, screen=medical)
                        break
                    except Exception:  # noqa: BLE001 -- the other page of the screen
                        continue
        except Exception:  # noqa: BLE001 -- slow while the world streams in
            pass
        time.sleep(3)
    raise TimeoutError("No live character")


def focus_gameplay(api: RemoteAPI, timeout: float = 30.0) -> None:
    """Closes the welcome screen and anything else above the gameplay screen"""
    keep = ("MyGuiScreenGamePlay", "MyGuiScreenHudSpace")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        screens = [s.get("type") for s in api.list_screens()]
        extra = [i for i, kind in enumerate(screens) if kind not in keep]
        if not extra:
            return
        if "MyGuiScreenLoading" not in screens:
            api.close_screen(extra[-1])
        time.sleep(0.5)
    raise TimeoutError("Could not get back to the gameplay screen")
