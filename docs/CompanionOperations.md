# Companion operations

Quasar reaches Grid Backups through its companion channel: a Quasar UI plugin
sends `ServerCommandType.PluginRequest` to Quasar.Agent, which hands it to the
Magnetar plugin that implements `IQuasarCompanionRequestHandler` with the
requested plugin id. The Clangy assistant in Quasar (QSR-4) uses these
operations for the player tools `my_grids`, `my_grid_backups` and
`my_grid_restore`. Limits, quotas, which loss causes may be restored without
an admin, and the audit are Quasar's side.

Plugin id: `9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C`, the id in `GridBackups.xml`.

## How the plugin answers

Magnetar compiles hub plugins from source against the game, PluginSdk and a
few libraries, not against `Magnetar.Protocol`, and Grid Backups also runs
where Quasar.Agent doesn't. So the plugin doesn't implement the handler
interface itself. Once Quasar.Agent has loaded `Magnetar.Protocol`,
`QuasarCompanionBridge` emits a small adapter type that implements `IPlugin`
and `IQuasarCompanionRequestHandler`, forwards each request to the plugin,
and adds it to the game's plugin list, where Quasar.Agent looks for handlers.
Without Quasar.Agent nothing happens.

Other plugins and tests can call the same operations directly:

```csharp
Task<CompanionReply> HandleCompanionRequestAsync(string operation, string payloadJson, CancellationToken cancellationToken);
```

Don't wait for it on the game thread: the operations read game state there.

## Conventions

- JSON names are camelCase. Times are UTC, ISO 8601.
- Requests name the player by `steamId`, a decimal string. The plugin finds
  the player's identities itself.
- Entity and identity ids are JSON numbers (64-bit). Steam ids in answers are
  decimal strings, like the request's.
- An error is `Success = false` with a short `Error`: unknown operation,
  missing argument, unknown player. A refused restore is a normal answer with
  `status` and `reason`.
- On a cluster the backups, the loss log and the restore records are in the
  shared storage, so every node answers for the whole cluster, except
  `present` and the gone check, which only see the node asked.

## Ownership

A player owns a grid when they are among its big owners (`BigOwners`): the
owners of the most blocks, several on a tie. Every backup records the big
owners of its biggest grid in a side file, and the plugin keeps a marker per
big owner (`.owners/<identity id>/<grid id>` in the backup folder), so a grid
is found for any of them. Backups taken before version 1.2.0 have no side
file; their only owner is the one their owner folder is named after.

## Operations

### `list`

```json
{ "steamId": "76561198000000000" }
```

Every grid the player is a big owner of in at least one backup, newest backup
first:

```json
{
  "grids": [
    {
      "gridId": 144115188075855873,
      "name": "Miner",
      "blocks": 512,
      "pcu": 4100,
      "ownerSteamIds": ["76561198000000000"],
      "present": false,
      "lastBackupUtc": "2026-11-05T10:00:00Z",
      "backupCount": 3,
      "loss": { "timeUtc": "2026-11-05T11:00:00Z", "cause": "destroyed" }
    }
  ]
}
```

`blocks`, `pcu` and `ownerSteamIds` come from the newest of the player's
backups, `backupCount` counts the player's backups. `present` means a grid
with that id exists on the node asked; on a cluster the caller merges the
nodes. `loss` is the latest loss entry of the grid, left out while the grid
is present or when none was logged.

### `describe`

```json
{ "steamId": "76561198000000000", "gridId": 144115188075855873 }
```

The grid's backups that have the player among their big owners, newest first.
An error when there are none.

```json
{
  "gridId": 144115188075855873,
  "name": "Miner",
  "ownerSteamIds": ["76561198000000000"],
  "backups": [
    {
      "backupId": "144115188075855872/Miner_144115188075855873/2026_11_05_10_00_00.sbc",
      "takenUtc": "2026-11-05T10:00:00Z",
      "blocks": 512,
      "pcu": 4100,
      "gridCount": 2,
      "position": { "x": 1000.0, "y": 0.0, "z": -500.0 }
    }
  ],
  "loss": { "timeUtc": "2026-11-05T11:00:00Z", "cause": "destroyed" }
}
```

`backupId` is the backup's path below the backup folder. `position` is that
of the backup's biggest grid. `gridCount` counts the grids joined by rotors,
pistons and connectors that the backup holds.

### `losses`

```json
{ "steamId": "76561198000000000", "sinceUtc": "2026-11-01T00:00:00Z" }
```

The loss entries that name the player among the grid's big owners, newest
first; `sinceUtc` is optional.

```json
{
  "losses": [
    {
      "gridId": 144115188075855873,
      "name": "Miner",
      "ownerIdentityIds": [144115188075855872],
      "ownerSteamIds": ["76561198000000000"],
      "timeUtc": "2026-11-05T11:00:00Z",
      "cause": "destroyed",
      "node": "node-2"
    }
  ]
}
```

`node` is the `CLUSTER_NODE_ID` of the node the grid was lost on, left out on
a plain server.

### `restore`

```json
{
  "steamId": "76561198000000000",
  "gridId": 144115188075855873,
  "placement": "original",
  "emptyInventories": false,
  "foreignExclusionMeters": 2000,
  "maxSearchMeters": 10000
}
```

`placement` is `original`, `near-player` or `near-player-station`; the values
above are the defaults. The answer:

```json
{
  "status": "restored",
  "backupId": "144115188075855872/Miner_144115188075855873/2026_11_05_10_00_00.sbc",
  "entityIds": [144115188075855873, 144115188075855900],
  "position": { "x": 1000.0, "y": 0.0, "z": -500.0 }
}
```

`status` is `restored`, `no-spot` or `refused`, the last two with a `reason`.
`entityIds` are the restored grids, the biggest first, and `position` is
where the biggest went. The checks, in order:

1. The grid is gone: no grid with its id on this node, and its latest loss
   was not into Hangar. Hangar stores no grid ids, so the loss log's `hangar`
   cause is what tells; a grid loaded back from Hangar gets a new id, so its
   old one stays refused too.
2. The backup is the newest one taken before the latest loss of the grid, or
   the newest of all when no loss was logged. A backup taken after it is
   never used.
3. The player is a big owner in that backup.
4. None of the backup's grid and block entity ids is in use. The grids keep
   these ids, nothing is remapped, so the restored grid is the same grid to
   the next gone check, and `remove-restored` can take it back.
5. A spot: `original` starts at the backup's position, the near-player modes
   in front of the player's character on this node (refused when the player
   has none here). The spot must be free of grids and characters (near the
   player also of terrain) and at least `foreignExclusionMeters` from grids
   whose big owners include a player outside the player's faction. When the
   start isn't such a spot, the search goes outward in shells, skipping the
   exclusion zones, up to `maxSearchMeters`. Nothing found is `no-spot`.
   `near-player-station` makes the biggest grid static.

Restored grids are at rest and without the characters seated in their
cockpits and cryo chambers. `emptyInventories` empties every inventory
(cargo, tools, ammo, ice) and the production queues.

The PluginSdk has no way to tell which partition a position belongs to, so
the plugin can't refuse a spawn position of another node; Quasar picks the
node.

### `remove-restored`

```json
{ "entityIds": [144115188075855873, 144115188075855900] }
```

The undo of a restore: closes the listed grids that still exist.

```json
{ "removed": [144115188075855873], "missing": [144115188075855900] }
```

Only ids a restore spawned are accepted, any other id makes it an error.
Each restore is recorded in `.restored` in the backup folder. Removing a grid
this way is not a loss, so its latest loss stays the one it was restored from.

## Loss log

Each removal of a player-owned grid that Grid Backups backs up (seen by a
backup scan with at least `MinBlocksForBackup` blocks) is a file in `.losses`
in the backup folder. Grids handed over to another cluster node, and every
grid when a world unloads, are not losses. How the causes are told apart:

| Cause | How |
|---|---|
| `destroyed` | The grid closed within 5 seconds of a block of it being destroyed by damage (the damage system's destroy event). |
| `ground` | The same, with the `Grind` damage type: hand and ship grinders raise it before razing the block. |
| `deleted` | Closed inside `MyCubeGrid.OnGridClosedRequest`, `MyEntities.OnEntityCloseRequest` or the admin screen's `MyEntityList.ProceedEntityAction`. |
| `cleanup` | Closed inside the trash removal: `MySessionComponentTrash.RemoveGrid` and `CloseAbandonedRespawnShip`. |
| `merged` | A merge block moved its blocks into another grid (`IsBlockTrasferInProgress` while it closes). |
| `hangar` | A frame of the Hangar plugin (id `17C928CE-4645-4E1B-9365-C3C46BFE061D`) was on the stack when it closed. |
| `unknown` | None of these, for example a grid deleted by another plugin's command. |

The loss log and the restore records follow the backups' retention:
`DeleteBackupsOlderThanDays` deletes their files too.
