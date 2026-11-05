using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using PluginSdk.Clustering;
using PluginSdk.Logging;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;

namespace ServerPlugin;

// One owned grid's removal, a file of its own in <backup root>/.losses
public sealed class LossEntry
{
    public long GridId { get; set; }
    public string Name { get; set; }
    public List<long> OwnerIdentityIds { get; set; } = new List<long>();
    public List<string> OwnerSteamIds { get; set; } = new List<string>();
    public DateTime TimeUtc { get; set; }
    public string Cause { get; set; }

    // CLUSTER_NODE_ID of the node it happened on, none on a plain server
    public string Node { get; set; }
}

// The grid loss log: records why each player-owned grid that Grid Backups
// backs up left the world, in the backup storage, so on a cluster every node
// sees every node's losses.
//
// What tells the causes apart, all on the game thread:
// - merged: the merge sets IsBlockTrasferInProgress on the grid it empties
//   before closing it (MyCubeGrid.MoveBlocksAndClose)
// - cleanup: the trash removal closes grids in MySessionComponentTrash.RemoveGrid
//   and abandoned respawn ships in CloseAbandonedRespawnShip
// - deleted: an admin's or owner's delete runs MyCubeGrid.OnGridClosedRequest,
//   MyEntities.OnEntityCloseRequest or the admin screen's
//   MyEntityList.ProceedEntityAction
// - destroyed, ground: the game closes a grid in the update after its last
//   block went; the damage system's destroy event names that block, with the
//   Grind damage type when a grinder took it
// - hangar: the Hangar plugin closes the grids it stored from its own code, so
//   one of its frames is on the stack
// Patches set the removal scope around those paths; the close itself is seen
// through the grid's OnMarkForClose, which Close raises synchronously.
public sealed class GridLossLog : IPluginGridHandover
{
    private const string HangarPluginId = "17C928CE-4645-4E1B-9365-C3C46BFE061D";

    [ThreadStatic]
    private static string removalScope;

    private readonly Plugin plugin;
    private readonly Dictionary<long, Tracked> tracked = new Dictionary<long, Tracked>();
    private readonly Dictionary<long, (string Cause, DateTime Utc)> lastBlockRemoval =
        new Dictionary<long, (string, DateTime)>();

    // Grids moving to another node, until a while after the move ended
    private readonly Dictionary<long, DateTime> handingOver = new Dictionary<long, DateTime>();
    private readonly HashSet<long> suppressed = new HashSet<long>();
    private MyDamageSystem damageSystem;
    private bool handoverRegistered;
    private bool hangarSearched;
    private Assembly hangar;

    public GridLossLog(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private string Folder =>
        Path.Combine(plugin.Backups.BackupRoot, GridBackupService.LossesFolder);

    // --- patches --------------------------------------------------------------

    public static void Patch(Harmony harmony, Logger log)
    {
        PatchScope(
            harmony,
            log,
            typeof(MySessionComponentTrash),
            "RemoveGrid",
            nameof(CleanupPrefix)
        );
        PatchScope(
            harmony,
            log,
            typeof(MySessionComponentTrash),
            "CloseAbandonedRespawnShip",
            nameof(CleanupPrefix)
        );
        PatchScope(harmony, log, typeof(MyCubeGrid), "OnGridClosedRequest", nameof(DeletePrefix));
        PatchScope(harmony, log, typeof(MyEntities), "OnEntityCloseRequest", nameof(DeletePrefix));
        PatchScope(harmony, log, typeof(MyEntityList), "ProceedEntityAction", nameof(DeletePrefix));
    }

    private static void PatchScope(
        Harmony harmony,
        Logger log,
        Type type,
        string method,
        string prefix
    )
    {
        try
        {
            harmony.Patch(
                AccessTools.Method(type, method),
                prefix: new HarmonyMethod(typeof(GridLossLog), prefix),
                finalizer: new HarmonyMethod(typeof(GridLossLog), nameof(ScopeFinalizer))
            );
        }
        catch (Exception ex)
        {
            // Its closes are then logged as unknown
            log.Warning(
                "Loss log patch failed",
                new
                {
                    type = type.Name,
                    method,
                    ex.Message,
                }
            );
        }
    }

    // The outer scope wins: a cleanup that goes through a close request is a cleanup
    private static void CleanupPrefix(out string __state)
    {
        __state = removalScope;
        removalScope ??= LossCause.Cleanup;
    }

    private static void DeletePrefix(out string __state)
    {
        __state = removalScope;
        removalScope ??= LossCause.Deleted;
    }

    private static void ScopeFinalizer(string __state) => removalScope = __state;

    // --- game thread ----------------------------------------------------------

    public void Update()
    {
        if (MySession.Static == null)
            return;

        var system = MyDamageSystem.Static;
        if (system != null && system != damageSystem)
        {
            damageSystem = system;
            system.RegisterDestroyHandler(0, OnBlockDestroyed);
        }

        if (!handoverRegistered && MySession.Static.Ready)
        {
            handoverRegistered = true;
            try
            {
                PluginCluster.ForPlugin(GridBackupService.PluginId).RegisterGridHandover(this);
            }
            catch (Exception ex)
            {
                plugin.Log.Warning("Grid handover hooks unavailable", new { ex.Message });
            }
        }
    }

    // A grid the backups cover, with its big owners as last seen: its last
    // block may be gone by the time it closes, and its owners with it
    public void Track(MyCubeGrid grid)
    {
        tracked[grid.EntityId] = new Tracked(grid.DisplayName, grid.BigOwners.ToList());
        grid.OnMarkForClose -= OnGridClosing;
        grid.OnMarkForClose += OnGridClosing;
    }

    // Drops what the next close can't need any more
    public void Prune(DateTime nowUtc)
    {
        foreach (var id in lastBlockRemoval.Keys.ToList())
            if (nowUtc - lastBlockRemoval[id].Utc > RestoreLogic.RemovalWindow)
                lastBlockRemoval.Remove(id);
        foreach (var id in handingOver.Keys.ToList())
            if (handingOver[id] < nowUtc)
                handingOver.Remove(id);
    }

    // The next close of this grid is not a loss: an undo of a restore
    public void Suppress(long gridId)
    {
        if (tracked.ContainsKey(gridId))
            suppressed.Add(gridId);
    }

    private void OnBlockDestroyed(object target, MyDamageInformation info)
    {
        if (target is MySlimBlock { CubeGrid: { } grid })
            lastBlockRemoval[grid.EntityId] = (
                info.Type == MyDamageType.Grind ? LossCause.Ground : LossCause.Destroyed,
                DateTime.UtcNow
            );
    }

    private void OnGridClosing(MyEntity entity)
    {
        entity.OnMarkForClose -= OnGridClosing;
        if (entity is not MyCubeGrid grid || !tracked.TryGetValue(grid.EntityId, out var known))
            return;

        tracked.Remove(grid.EntityId);
        if (suppressed.Remove(grid.EntityId))
            return;
        // A server stopping or a world unloading closes everything
        if (MyEntities.IsClosingAll || MySession.Static == null || !MySession.Static.Ready)
            return;
        if (handingOver.ContainsKey(grid.EntityId))
            return;

        try
        {
            var owners = grid.BigOwners.Count > 0 ? grid.BigOwners.ToList() : known.Owners;
            owners = owners
                .Where(o => o != 0 && !MySession.Static.Players.IdentityIsNpc(o))
                .ToList();
            if (owners.Count == 0)
                return;

            var now = DateTime.UtcNow;
            lastBlockRemoval.TryGetValue(grid.EntityId, out var removal);
            var cause = RestoreLogic.Classify(
                grid.IsBlockTrasferInProgress,
                removalScope,
                HangarOnStack(),
                removal.Cause,
                removal.Cause == null ? null : removal.Utc,
                now
            );

            Write(
                new LossEntry
                {
                    GridId = grid.EntityId,
                    Name = string.IsNullOrWhiteSpace(grid.DisplayName)
                        ? known.Name
                        : grid.DisplayName,
                    OwnerIdentityIds = owners,
                    OwnerSteamIds = GridBackupService.SteamIdsOf(owners),
                    TimeUtc = now,
                    Cause = cause,
                    Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID"),
                }
            );
        }
        catch (Exception ex)
        {
            plugin.Log.Error("Loss log failed", ex, new { grid.EntityId });
        }
    }

    private bool HangarOnStack()
    {
        if (!hangarSearched)
        {
            hangarSearched = true;
            hangar = AppDomain
                .CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    (string)
                        assembly
                            .GetType("ServerPlugin.HangarStorage", false)
                            ?.GetField("PluginId")
                            ?.GetRawConstantValue() == HangarPluginId
                );
        }

        return hangar != null
            && new StackTrace(false)
                .GetFrames()
                .Any(frame => frame?.GetMethod()?.DeclaringType?.Assembly == hangar);
    }

    // --- grid handover ----------------------------------------------------------

    public byte[] Leaving(long gridId)
    {
        handingOver[gridId] = DateTime.MaxValue;
        return null;
    }

    public void Left(long gridId) => handingOver[gridId] = DateTime.UtcNow.AddSeconds(30);

    public void Stayed(long gridId) => handingOver.Remove(gridId);

    public void Arrived(long gridId, byte[] state) { }

    // --- storage ----------------------------------------------------------------

    private void Write(LossEntry entry)
    {
        var folder = Folder;
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Json.WriteFile(
                    Path.Combine(
                        folder,
                        $"{entry.TimeUtc:yyyyMMddHHmmssfff}_{entry.GridId}_{Guid.NewGuid():N}.json"
                    ),
                    entry
                );
                plugin.Log.Info(
                    "Grid loss logged",
                    new
                    {
                        entry.GridId,
                        entry.Name,
                        entry.Cause,
                    }
                );
            }
            catch (Exception ex)
            {
                plugin.Log.Error("Writing a loss entry failed", ex, new { entry.GridId });
            }
        });
    }

    // Every logged loss, newest first
    public List<LossEntry> ReadAll()
    {
        var dir = new DirectoryInfo(Folder);
        if (!dir.Exists)
            return new List<LossEntry>();

        return dir.GetFiles("*.json")
            .Select(file => Json.ReadFile<LossEntry>(file.FullName))
            .Where(entry => entry != null)
            .OrderByDescending(entry => entry.TimeUtc)
            .ToList();
    }

    private sealed class Tracked
    {
        public Tracked(string name, List<long> owners)
        {
            Name = name;
            Owners = owners;
        }

        public string Name { get; }
        public List<long> Owners { get; }
    }
}
