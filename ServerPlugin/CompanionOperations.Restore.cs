using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

// Restores and the undo of a restore
public sealed partial class CompanionOperations
{
    private const string Original = "original";
    private const string NearPlayer = "near-player";
    private const string NearPlayerStation = "near-player-station";

    // What a restore spawned, a file of its own in <backup root>/.restored,
    // so remove-restored takes back only grids a restore made
    public sealed class RestoreRecord
    {
        public DateTime TimeUtc { get; set; }
        public string SteamId { get; set; }
        public long GridId { get; set; }
        public string BackupId { get; set; }
        public List<long> EntityIds { get; set; } = new List<long>();
    }

    private sealed class RestoreResult
    {
        public string Status { get; set; }
        public string Reason { get; set; }
        public string BackupId { get; set; }
        public List<long> EntityIds { get; set; }
        public Position Position { get; set; }
    }

    private static RestoreResult Refused(string reason, string backupId = null) =>
        new RestoreResult
        {
            Status = "refused",
            Reason = reason,
            BackupId = backupId,
        };

    private async Task<object> Restore(JObject payload)
    {
        var player = await ResolvePlayer(payload);
        var gridId = GridIdOf(payload);
        var placement = payload.Value<string>("placement") ?? Original;
        if (placement is not (Original or NearPlayer or NearPlayerStation))
            throw new CompanionError($"Unknown placement '{placement}'.");
        var emptyInventories = payload.Value<bool?>("emptyInventories") ?? false;
        var exclusion = payload.Value<double?>("foreignExclusionMeters") ?? 2000;
        var maxSearch = payload.Value<double?>("maxSearchMeters") ?? 10000;

        // 1. The grid is gone: not here, and not taken into Hangar. Hangar
        // keeps no grid ids, so the loss log's cause is what tells: a grid
        // stored there is in Hangar, or back in the world under a new id.
        if (await OnGameThread(() => GridExists(gridId)))
            return Refused("The grid still exists on this server.");
        var loss = Losses.ReadAll().FirstOrDefault(e => e.GridId == gridId);
        if (loss?.Cause == LossCause.Hangar)
            return Refused("The grid was stored in Hangar.");

        // R8: the newest backup before the removal, never a later one
        var backup = RestoreLogic.ChooseBackup(
            Backups.BackupsOf(gridId, Backups.GridDirectories()),
            b => b.TakenUtc,
            loss?.TimeUtc
        );
        if (backup == null)
            return Refused(
                loss == null
                    ? "The grid has no backup."
                    : "The grid has no backup taken before it was removed."
            );

        // 2. Only a big owner restores
        if (!backup.Owners.Any(player.IdentityIds.Contains))
            return Refused("The player is not a big owner in the backup.", backup.BackupId);

        var builders =
            GridBackupService.LoadBuilders(backup.File.FullName)
            ?? throw new CompanionError("The backup file could not be read.");
        Prepare(builders, emptyInventories, placement == NearPlayerStation);

        var result = await OnGameThread(() =>
            Spawn(builders, player, placement, exclusion, maxSearch)
        );
        result.BackupId = backup.BackupId;
        if (result.Status != "restored")
            return result;

        WriteRecord(
            new RestoreRecord
            {
                TimeUtc = DateTime.UtcNow,
                SteamId = player.SteamId.ToString(),
                GridId = gridId,
                BackupId = backup.BackupId,
                EntityIds = result.EntityIds,
            }
        );
        plugin.Log.Info(
            "Grid restored",
            new
            {
                gridId,
                backup.BackupId,
                steamId = player.SteamId,
                placement,
            }
        );
        return result;
    }

    // Grids come back at rest, without the characters seated in them, and
    // optionally empty: inventories (cargo, tools, ammo, ice) and production queues
    private static void Prepare(
        List<MyObjectBuilder_CubeGrid> builders,
        bool emptyInventories,
        bool asStation
    )
    {
        foreach (var grid in builders)
        {
            grid.LinearVelocity = default;
            grid.AngularVelocity = default;
            grid.PersistentFlags |= MyPersistentEntityFlags2.InScene;
            foreach (var block in grid.CubeBlocks ?? new List<MyObjectBuilder_CubeBlock>())
            {
                if (block is MyObjectBuilder_Cockpit cockpit)
                    cockpit.Pilot = null;
                if (!emptyInventories)
                    continue;
                if (block is MyObjectBuilder_ProductionBlock production)
                {
                    production.Queue = null;
                    production.InputInventory?.Items.Clear();
                    production.OutputInventory?.Items.Clear();
                }
                foreach (var component in block.ComponentContainer?.Components ?? new())
                    Empty(component.Component);
            }
        }

        if (asStation)
            builders.OrderByDescending(g => g.CubeBlocks?.Count ?? 0).First().IsStatic = true;
    }

    private static void Empty(MyObjectBuilder_ComponentBase component)
    {
        if (component is MyObjectBuilder_Inventory inventory)
            inventory.Items.Clear();
        else if (component is MyObjectBuilder_InventoryAggregate aggregate)
            foreach (var inner in aggregate.Inventories ?? new())
                Empty(inner);
    }

    // On the game thread: 4. the backup's entity ids are free, 5. a spot, then the spawn
    private RestoreResult Spawn(
        List<MyObjectBuilder_CubeGrid> builders,
        Player player,
        string placement,
        double exclusion,
        double maxSearch
    )
    {
        // R14: the grids keep the entity ids of the backup, so a restored grid
        // is the same grid to the next gone check. No remapping.
        var ids = builders
            .SelectMany(g =>
                new[] { g.EntityId }.Concat(
                    g.CubeBlocks?.Select(b => b.EntityId) ?? Enumerable.Empty<long>()
                )
            )
            .Where(id => id != 0)
            .ToList();
        var inUse = ids.Where(MyEntityIdentifier.ExistsById).ToList();
        if (inUse.Count > 0)
            return Refused(
                $"{inUse.Count} of the backup's entity ids are in use, {inUse[0]} among them."
            );

        var primary = builders.OrderByDescending(g => g.CubeBlocks?.Count ?? 0).First();
        var primaryPosition = (Vector3D)(primary.PositionAndOrientation?.Position ?? Vector3D.Zero);
        var bounds = Bounds(builders);
        var offset = bounds.Center - primaryPosition;

        Vector3D start;
        var nearPlayer = placement != Original;
        if (nearPlayer)
        {
            var character = MySession
                .Static.Players.TryGetIdentity(player.MainIdentityId)
                ?.Character;
            if (character == null || character.IsDead || character.MarkedForClose)
                return Refused("The player has no character on this server.");
            start =
                character.PositionComp.GetPosition()
                + character.WorldMatrix.Forward * (bounds.Radius + 50)
                - offset;
        }
        else
        {
            start = primaryPosition;
        }

        var foreign = ForeignGrids(player);
        var spot = RestoreLogic.FindSpot(
            start,
            Math.Max(2 * bounds.Radius + 10, 50),
            maxSearch,
            foreign,
            exclusion,
            candidate => IsFree(new BoundingSphereD(candidate + offset, bounds.Radius), nearPlayer)
        );
        if (spot == null)
            return new RestoreResult
            {
                Status = "no-spot",
                Reason =
                    $"No free spot within {maxSearch:0} m of the start that is {exclusion:0} m "
                    + "or more from grids of players outside the player's faction.",
            };

        var shift = spot.Value - primaryPosition;
        foreach (var grid in builders)
        {
            var at = grid.PositionAndOrientation ?? MyPositionAndOrientation.Default;
            grid.PositionAndOrientation = new MyPositionAndOrientation(
                (Vector3D)at.Position + shift,
                at.Forward,
                at.Up
            );
        }

        foreach (var grid in builders)
            if (
                MyEntities.CreateFromObjectBuilderAndAdd(grid, fadeIn: false) is MyCubeGrid spawned
                && grid == primary
            )
                Losses.Track(spawned);

        return new RestoreResult
        {
            Status = "restored",
            EntityIds = builders.Select(g => g.EntityId).ToList(),
            Position = new Position
            {
                X = spot.Value.X,
                Y = spot.Value.Y,
                Z = spot.Value.Z,
            },
        };
    }

    // One sphere around all grids of the backup, in world space
    private static BoundingSphereD Bounds(List<MyObjectBuilder_CubeGrid> builders)
    {
        BoundingSphereD? bounds = null;
        foreach (var grid in builders)
        {
            var sphere = grid.CalculateBoundingSphere();
            var matrix = grid.PositionAndOrientation?.GetMatrix() ?? MatrixD.Identity;
            var world = new BoundingSphereD(
                Vector3D.Transform((Vector3D)sphere.Center, matrix),
                sphere.Radius
            );
            bounds = bounds.HasValue ? BoundingSphereD.CreateMerged(bounds.Value, world) : world;
        }

        return bounds ?? new BoundingSphereD(Vector3D.Zero, 1);
    }

    // Grids and characters in the way. Terrain counts only near the player:
    // at the original position the grid stood on it.
    private static bool IsFree(BoundingSphereD sphere, bool checkTerrain)
    {
        var blocked = MyEntities
            .GetTopMostEntitiesInSphere(ref sphere)
            .Any(e => (e is MyCubeGrid || e is MyCharacter) && !e.MarkedForClose);
        return !blocked && !(checkTerrain && MyEntities.IsSpherePenetrating(ref sphere));
    }

    // Grids whose big owners include a player who is not this one and not in
    // the player's faction
    private static List<Vector3D> ForeignGrids(Player player)
    {
        var session = MySession.Static;
        var faction = session.Factions.TryGetPlayerFaction(player.MainIdentityId);
        bool Foreign(long owner) =>
            owner != 0
            && !player.IdentityIds.Contains(owner)
            && !session.Players.IdentityIsNpc(owner)
            && (faction == null || session.Factions.TryGetPlayerFaction(owner) != faction);

        return MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.MarkedForClose && g.BigOwners.Any(Foreign))
            .Select(g => g.PositionComp.WorldAABB.Center)
            .ToList();
    }

    // --- records ----------------------------------------------------------------

    private string RecordsFolder =>
        Path.Combine(Backups.BackupRoot, GridBackupService.RestoredFolder);

    private void WriteRecord(RestoreRecord record)
    {
        try
        {
            Directory.CreateDirectory(RecordsFolder);
            Json.WriteFile(
                Path.Combine(
                    RecordsFolder,
                    $"{record.TimeUtc:yyyyMMddHHmmssfff}_{record.GridId}_{Guid.NewGuid():N}.json"
                ),
                record
            );
        }
        catch (Exception ex)
        {
            plugin.Log.Error("Writing a restore record failed", ex, new { record.GridId });
        }
    }

    private HashSet<long> RestoredIds()
    {
        var dir = new DirectoryInfo(RecordsFolder);
        if (!dir.Exists)
            return new HashSet<long>();
        return new HashSet<long>(
            dir.GetFiles("*.json")
                .Select(f => Json.ReadFile<RestoreRecord>(f.FullName))
                .Where(r => r != null)
                .SelectMany(r => r.EntityIds)
        );
    }

    // The undo of a restore: closes the listed grids that still exist
    private async Task<object> RemoveRestored(JObject payload)
    {
        var ids = (payload["entityIds"] as JArray)?.Select(t => t.Value<long>()).ToList();
        if (ids == null || ids.Count == 0)
            throw new CompanionError("entityIds is missing.");

        var restored = RestoredIds();
        var foreign = ids.Where(id => !restored.Contains(id)).ToList();
        if (foreign.Count > 0)
            throw new CompanionError(
                $"Not restored by Grid Backups: {string.Join(", ", foreign)}."
            );

        return await OnGameThread(() =>
        {
            var removed = new List<long>();
            var missing = new List<long>();
            foreach (var id in ids)
            {
                if (
                    MyEntities.TryGetEntityById(id, out var entity)
                    && entity is MyCubeGrid grid
                    && !grid.MarkedForClose
                )
                {
                    // An undo is not a loss: the grid's last loss stays its latest
                    Losses.Suppress(id);
                    grid.Close();
                    removed.Add(id);
                }
                else
                {
                    missing.Add(id);
                }
            }

            return new { Removed = removed, Missing = missing };
        });
    }
}
