"""Every grid of the test world.

Each grid sits at a site of its own, in a row along -z away from the players'
home. Functional blocks belong to player A unless said otherwise; armor blocks
belong to nobody, so a grid of armor only is a nobody grid.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

import world
from world import PLAYER_A, PLAYER_B, PLAYER_C, block, grid

# The players wait here between tests
HOME = (0.0, 0.0, 0.0)
FIRST_SITE_M = 500.0
SITE_SPACING_M = 300.0


def site(i: int):
    return (0.0, 0.0, -(FIRST_SITE_M + SITE_SPACING_M * i))


def offset(position, dx=0.0, dy=0.0, dz=0.0):
    return (position[0] + dx, position[1] + dy, position[2] + dz)


@dataclass
class Grid:
    name: str
    entity_id: int
    position: tuple
    xml: str
    blocks: int
    # The owner of its functional blocks, 0 for nobody
    owner: int

    @property
    def folder(self) -> str:
        """The grid's backup folder name, characters Windows doesn't allow in
        file names replaced"""
        return re.sub(r'[<>:"/\\|?*]', "_", self.name) + f"_{self.entity_id}"


_grids: list[Grid] = []


def _id(k: int) -> int:
    return 900_000_000_000 + 1000 * k


def add(name: str, position, blocks: list[str], static: bool = True) -> Grid:
    entity_id = _id(len(_grids))
    xml = grid(name, entity_id, "".join(blocks), position, static)
    owner = re.search(r"<Owner>(\d+)</Owner>", xml)
    g = Grid(
        name,
        entity_id,
        position,
        xml,
        len(blocks),
        int(owner.group(1)) if owner else 0,
    )
    _grids.append(g)
    return g


def armor(cells) -> list[str]:
    return [block("CubeBlock", world.ARMOR["Large"], c) for c in cells]


def box(x: int, y: int, z: int):
    return [(i, j, k) for i in range(x) for j in range(y) for k in range(z)]


def functional(xsi_type: str, subtype: str, cell, entity_id: int, owner: int) -> str:
    return block(
        xsi_type,
        subtype,
        cell,
        built_by=owner,
        entity_id=entity_id,
        extra="<Enabled>true</Enabled>",
    )


def battery(cell, entity_id: int, owner: int = PLAYER_A) -> str:
    return functional("BatteryBlock", "LargeBlockBatteryBlock", cell, entity_id, owner)


def cargo(cell, entity_id: int, owner: int = PLAYER_A) -> str:
    return functional(
        "CargoContainer", "LargeBlockSmallContainer", cell, entity_id, owner
    )


def _next() -> int:
    """The id of the grid added next, for its block ids"""
    return _id(len(_grids))


# The tests set MinBlocksForBackup to 5, so every grid but Tiny is backed up

ALPHA_STATION = add(
    "Alpha station",
    site(0),
    armor(box(3, 3, 3)) + [battery((1, 3, 1), _next() + 100)],
)
ALPHA_SHIP = add(
    "Alpha ship",
    site(1),
    armor(box(3, 1, 3)) + [battery((1, 1, 1), _next() + 100)],
    static=False,
)
BRAVO_STATION = add(
    "Bravo station",
    site(2),
    armor(box(3, 3, 3)) + [battery((1, 3, 1), _next() + 100, owner=PLAYER_B)],
)
# Charlie is in the faction with Alpha, and never joins
CHARLIE_BASE = add(
    "Charlie base",
    site(3),
    armor(box(3, 3, 3)) + [battery((1, 3, 1), _next() + 100, owner=PLAYER_C)],
)
PIRATE_BASE = add(
    "Pirate base",
    site(4),
    armor(box(3, 3, 3)) + [battery((1, 3, 1), _next() + 100, owner=world.NPC)],
)
WRECK = add("Wreck", site(5), armor(box(3, 3, 3)))
TINY = add("Tiny", site(6), armor(box(3, 1, 1)) + [battery((1, 1, 0), _next() + 100)])
# Odd_name_x__<id> in folder names
ODD_NAME = add(
    "Odd:name/x?",
    site(7),
    armor(box(3, 1, 3)) + [battery((1, 1, 1), _next() + 100)],
)
# A long row of armor that the tests raze from its +x end
TRIGGER = add(
    "Trigger",
    site(8),
    armor(box(12, 1, 3)) + [battery((0, 1, 1), _next() + 100)],
)
# A rotor base; the tests build its head, a grid of its own
ROTOR_BASE_ID = _next()
STATOR_ID = ROTOR_BASE_ID + 101
ROTOR_BASE = add(
    "Rotor base",
    site(9),
    armor(box(3, 1, 3))
    + [
        battery((0, 1, 0), ROTOR_BASE_ID + 100),
        functional("MotorStator", "LargeStator", (1, 1, 1), STATOR_ID, PLAYER_A),
    ],
)
# A station with a cargo container the tests fill, for restores
RESTORE_STATION_ID = _next()
CARGO_ID = RESTORE_STATION_ID + 101
RESTORE_STATION = add(
    "Restore station",
    site(10),
    armor(box(4, 2, 4))
    + [
        battery((0, 2, 0), RESTORE_STATION_ID + 100),
        cargo((2, 2, 2), CARGO_ID),
    ],
)

ALL = list(_grids)
BY_ID = {g.entity_id: g for g in ALL}
# The grids a backup scan backs up with the default test config
SCANNED = [g for g in ALL if g not in (TINY, PIRATE_BASE, WRECK)]


def xml() -> str:
    return "".join(g.xml for g in ALL)
