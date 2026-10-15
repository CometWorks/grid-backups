"""The test world: the game's Empty World with the test players, a faction and
the grids of grids.py.

The players exist before the clients join, so their identity ids are known and
the tests can name them.
"""

from __future__ import annotations

import re
import shutil
from pathlib import Path

# The test players. A and B are the clients, C never joins: blocks built by C
# are in the world from the start.
PLAYER_A = 144115188075870001
PLAYER_B = 144115188075870002
PLAYER_C = 144115188075870003
# The Space Pirates of the Empty World, an NPC
NPC = 144115188075855896
FACTION = 268000000000000001
FACTION_TAG = "GBT"

GRID_SIZE_M = {"Large": 2.5, "Small": 0.5}
ARMOR = {"Large": "LargeBlockArmorBlock", "Small": "SmallBlockArmorBlock"}


def vec(tag: str, v) -> str:
    return f'<{tag} x="{v[0]}" y="{v[1]}" z="{v[2]}" />'


def block(
    xsi_type: str,
    subtype: str,
    cell,
    *,
    built_by: int = 0,
    entity_id: int = 0,
    forward: str = "Forward",
    up: str = "Up",
    extra: str = "",
) -> str:
    """A cube block. Functional blocks get an entity id and their builder as
    owner."""
    entity = f"<EntityId>{entity_id}</EntityId>" if entity_id else ""
    owner = f"<Owner>{built_by}</Owner>" if entity_id and built_by else ""
    builder = f"<BuiltBy>{built_by}</BuiltBy>" if built_by else ""
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="MyObjectBuilder_{xsi_type}">'
        f"<SubtypeName>{subtype}</SubtypeName>{entity}{vec('Min', cell)}"
        f'<BlockOrientation Forward="{forward}" Up="{up}" />'
        f'<ColorMaskHSV x="0" y="-0.8" z="0.55" />{owner}{builder}{extra}'
        "</MyObjectBuilder_CubeBlock>"
    )


def grid(
    name: str,
    entity_id: int,
    blocks: str,
    position,
    static: bool,
    size: str = "Large",
) -> str:
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<SubtypeName /><EntityId>{entity_id}</EntityId>"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        "<PositionAndOrientation>"
        + vec("Position", position)
        + vec("Forward", (0, 0, -1))
        + vec("Up", (0, 1, 0))
        + "</PositionAndOrientation>"
        f"<GridSizeEnum>{size}</GridSizeEnum>"
        f"<CubeBlocks>{blocks}</CubeBlocks>"
        f"<IsStatic>{'true' if static else 'false'}</IsStatic>"
        f"<IsUnsupportedStation>{'true' if static else 'false'}</IsUnsupportedStation>"
        f"<DisplayName>{name}</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks>"
        "</MyObjectBuilder_EntityBase>"
    )


# ---------------------------------------------------------------------------
# Session data: the players and the factions
# ---------------------------------------------------------------------------


def _identity(identity_id: int, name: str) -> str:
    return (
        "<MyObjectBuilder_Identity>"
        f"<IdentityId>{identity_id}</IdentityId><DisplayName>{name}</DisplayName>"
        "<CharacterEntityId>0</CharacterEntityId>"
        "<BlockLimitModifier>0</BlockLimitModifier>"
        "</MyObjectBuilder_Identity>"
    )


def _player(steam_id: int, identity_id: int, name: str) -> str:
    return (
        '<item><Key xsi:type="PlayerId">'
        f"<ClientId>{steam_id}</ClientId><SerialId>0</SerialId></Key>"
        '<Value xsi:type="MyObjectBuilder_Player">'
        f"<DisplayName>{name}</DisplayName><IdentityId>{identity_id}</IdentityId>"
        "<Connected>false</Connected></Value></item>"
    )


def _faction(faction_id: int, tag: str, name: str, members) -> str:
    rows = "".join(
        "<MyObjectBuilder_FactionMember>"
        f"<PlayerId>{m}</PlayerId><IsLeader>{str(i == 0).lower()}</IsLeader>"
        f"<IsFounder>{str(i == 0).lower()}</IsFounder>"
        "</MyObjectBuilder_FactionMember>"
        for i, m in enumerate(members)
    )
    return (
        "<MyObjectBuilder_Faction>"
        f"<FactionId>{faction_id}</FactionId><Tag>{tag}</Tag><Name>{name}</Name>"
        "<Description /><PrivateInfo />"
        f"<Members>{rows}</Members><JoinRequests />"
        "<AutoAcceptMember>true</AutoAcceptMember><AutoAcceptPeace>false</AutoAcceptPeace>"
        "<AcceptHumans>true</AcceptHumans><EnableFriendlyFire>false</EnableFriendlyFire>"
        "</MyObjectBuilder_Faction>"
    )


def _insert(text: str, tag: str, content: str) -> str:
    """Puts content at the start of the first <tag> element, opening an empty
    <tag /> if need be"""
    if f"<{tag}>" in text:
        return text.replace(f"<{tag}>", f"<{tag}>{content}", 1)
    new, count = re.subn(rf"<{tag}\s*/>", f"<{tag}>{content}</{tag}>", text, count=1)
    assert count == 1, tag
    return new


def _set(text: str, name: str, value: str) -> str:
    new, count = re.subn(
        rf"<{name}>[^<]*</{name}>|<{name}\s*/>", f"<{name}>{value}</{name}>", text
    )
    if count:
        return new
    # Settings the template leaves at their default
    return text.replace("</Settings>", f"<{name}>{value}</{name}></Settings>", 1)


BASE_SETTINGS = {
    "GameMode": "Creative",
    "OnlineMode": "PUBLIC",
    "MaxPlayers": "8",
    "EnableRespawnShips": "false",
    "EnableSpaceSuitRespawn": "true",
    "TrashRemovalEnabled": "false",
    "EnableCopyPaste": "true",
    "BlockLimitsEnabled": "NONE",
    "EnableResearch": "false",
    "AutoSaveInMinutes": "0",
    "ExperimentalMode": "true",
    "EnableSunRotation": "false",
}


def prepare(
    template: Path,
    world: Path,
    players: dict[int, tuple[int, str]],
    grids_xml: str,
    settings: dict | None = None,
) -> None:
    """A fresh copy of the template world (the game's Empty World) with the
    players (steam id -> (identity id, name)), the faction and the grids, as
    sector object XML"""
    if world.exists():
        shutil.rmtree(world)
    shutil.copytree(template, world)
    (world / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    (world / "Sandbox.sbcB5").unlink(missing_ok=True)

    sector = world / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text = _insert(text, "SectorObjects", grids_xml)
    sector.write_text(text, encoding="utf-8")

    values = {**BASE_SETTINGS, **(settings or {})}
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        text = path.read_text(encoding="utf-8")
        for key, value in values.items():
            text = _set(text, key, value)
        if name == "Sandbox.sbc":
            identities = "".join(_identity(i, n) for i, n in players.values())
            identities += _identity(PLAYER_C, "Charlie")
            text = _insert(text, "Identities", identities)
            text, count = re.subn(
                r"<AllPlayersData>(\s*)<dictionary>",
                lambda m: "<AllPlayersData><dictionary>"
                + "".join(_player(s, i, n) for s, (i, n) in players.items()),
                text,
                count=1,
            )
            assert count == 1, "AllPlayersData"
            faction = _faction(
                FACTION, FACTION_TAG, "Grid Backup Testers", [PLAYER_C, PLAYER_A]
            )
            text, count = re.subn(
                r"<Factions>(\s*)<Factions>",
                lambda m: "<Factions><Factions>" + faction,
                text,
                count=1,
            )
            assert count == 1, "Factions"
            members = "".join(
                f'<item><Key xsi:type="xsd:long">{m}</Key>'
                f'<Value xsi:type="xsd:long">{FACTION}</Value></item>'
                for m in (PLAYER_C, PLAYER_A)
            )
            text, count = re.subn(
                r"<Players>(\s*)<dictionary>",
                lambda m: "<Players><dictionary>" + members,
                text,
                count=1,
            )
            assert count == 1, "Faction players"
        path.write_text(text, encoding="utf-8")
