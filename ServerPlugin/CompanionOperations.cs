using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.World;

namespace ServerPlugin;

// The answer to a companion request: payload JSON on success, a short error
// otherwise. A refused restore is a success with a status in its payload.
public sealed class CompanionReply
{
    public bool Success { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

// An error the requester is told as it is
public sealed class CompanionError : Exception
{
    public CompanionError(string message)
        : base(message) { }
}

// The operations Quasar calls through the companion channel (QSR-4), see
// docs/CompanionOperations.md. Files are read on the calling thread, game
// state only on the game thread.
public sealed partial class CompanionOperations
{
    private readonly Plugin plugin;

    public CompanionOperations(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private GridBackupService Backups => plugin.Backups;
    private GridLossLog Losses => plugin.Losses;

    public async Task<CompanionReply> HandleAsync(
        string operation,
        string payloadJson,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (Backups == null || Losses == null)
                throw new CompanionError(
                    plugin.LoadError is { } error
                        ? $"GridBackups failed to load: {error}"
                        : "GridBackups is not loaded."
                );

            var payload = string.IsNullOrWhiteSpace(payloadJson)
                ? new JObject()
                : JObject.Parse(payloadJson);
            cancellationToken.ThrowIfCancellationRequested();
            object result = operation switch
            {
                "list" => await List(payload),
                "describe" => await Describe(payload),
                "losses" => await ListLosses(payload),
                "restore" => await Restore(payload),
                "remove-restored" => await RemoveRestored(payload),
                _ => throw new CompanionError($"Unsupported operation '{operation}'."),
            };
            return new CompanionReply { Success = true, PayloadJson = Json.Write(result) };
        }
        catch (CompanionError ex)
        {
            return new CompanionReply { Error = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            plugin.Log.Error("Companion request failed", ex, new { operation });
            return new CompanionReply { Error = ex.Message };
        }
    }

    // --- list, describe, losses -------------------------------------------------

    private async Task<object> List(JObject payload)
    {
        var player = await ResolvePlayer(payload);
        var dirs = Backups.GridDirectories();
        var losses = Losses.ReadAll();
        var grids = new List<ListedGrid>();
        foreach (var gridId in Backups.GridIdsOf(player.IdentityIds))
        {
            var mine = OwnBackups(gridId, dirs, player);
            var newest = mine.FirstOrDefault();
            var info = newest == null ? null : GridBackupService.Details(newest);
            if (info == null)
                continue;

            grids.Add(
                new ListedGrid
                {
                    GridId = gridId,
                    Name = info.Name,
                    Blocks = info.Blocks,
                    Pcu = info.Pcu,
                    OwnerSteamIds = info.OwnerSteamIds,
                    LastBackupUtc = newest.TakenUtc,
                    BackupCount = mine.Count,
                    Loss = LossOf(gridId, losses),
                }
            );
        }

        var present = await OnGameThread(() =>
            grids.Where(g => GridExists(g.GridId)).Select(g => g.GridId).ToList()
        );
        foreach (var grid in grids)
        {
            grid.Present = present.Contains(grid.GridId);
            if (grid.Present)
                grid.Loss = null;
        }

        return new { Grids = grids.OrderByDescending(g => g.LastBackupUtc).ToList() };
    }

    private async Task<object> Describe(JObject payload)
    {
        var player = await ResolvePlayer(payload);
        var gridId = GridIdOf(payload);
        var mine = OwnBackups(gridId, Backups.GridDirectories(), player);
        if (mine.Count == 0)
            throw new CompanionError("The player is a big owner in no backup of this grid.");

        var backups = mine.Select(b => (Backup: b, Info: GridBackupService.Details(b)))
            .Where(b => b.Info != null)
            .ToList();
        var newest = backups.First().Info;
        var present = await OnGameThread(() => GridExists(gridId));
        return new
        {
            GridId = gridId,
            newest.Name,
            newest.OwnerSteamIds,
            Backups = backups
                .Select(b => new
                {
                    b.Backup.BackupId,
                    TakenUtc = b.Backup.TakenUtc,
                    b.Info.Blocks,
                    b.Info.Pcu,
                    b.Info.GridCount,
                    b.Info.Position,
                })
                .ToList(),
            Loss = present ? null : LossOf(gridId, Losses.ReadAll()),
        };
    }

    private async Task<object> ListLosses(JObject payload)
    {
        var player = await ResolvePlayer(payload);
        var since = payload.Value<DateTime?>("sinceUtc")?.ToUniversalTime();
        var steamId = player.SteamId.ToString();
        return new
        {
            Losses = Losses
                .ReadAll()
                .Where(e =>
                    (since == null || e.TimeUtc >= since)
                    && (
                        e.OwnerIdentityIds.Any(player.IdentityIds.Contains)
                        || e.OwnerSteamIds.Contains(steamId)
                    )
                )
                .ToList(),
        };
    }

    // The backups of a grid the player is a big owner in, newest first
    private List<StoredBackup> OwnBackups(
        long gridId,
        Dictionary<long, List<DirectoryInfo>> dirs,
        Player player
    ) =>
        Backups
            .BackupsOf(gridId, dirs)
            .Where(b => b.Owners.Any(player.IdentityIds.Contains))
            .ToList();

    private static LossRef LossOf(long gridId, List<LossEntry> losses) =>
        losses.FirstOrDefault(e => e.GridId == gridId) is { } entry
            ? new LossRef { TimeUtc = entry.TimeUtc, Cause = entry.Cause }
            : null;

    // --- helpers ------------------------------------------------------------------

    private static bool GridExists(long gridId) =>
        MyEntities.TryGetEntityById(gridId, out var entity)
        && entity is MyCubeGrid
        && !entity.MarkedForClose;

    private static long GridIdOf(JObject payload) =>
        long.TryParse(payload.Value<string>("gridId"), out var id) && id != 0
            ? id
            : throw new CompanionError("gridId is missing.");

    private static Task<Player> ResolvePlayer(JObject payload)
    {
        if (!ulong.TryParse(payload.Value<string>("steamId"), out var steamId) || steamId == 0)
            throw new CompanionError("steamId is missing.");

        return OnGameThread(() =>
        {
            var players =
                MySession.Static?.Players ?? throw new CompanionError("No world is loaded.");
            var ids = new HashSet<long>(
                players
                    .GetAllIdentities()
                    .Where(i => players.TryGetSteamId(i.IdentityId) == steamId)
                    .Select(i => i.IdentityId)
            );
            var main = players.TryGetIdentityId(steamId);
            if (main != 0)
                ids.Add(main);
            if (ids.Count == 0)
                throw new CompanionError("The player has no identity on this server.");

            return new Player
            {
                SteamId = steamId,
                MainIdentityId = main != 0 ? main : ids.First(),
                IdentityIds = ids,
            };
        });
    }

    internal static Task<T> OnGameThread<T>(Func<T> func)
    {
        var game = MySandboxGame.Static ?? throw new CompanionError("The game is not running.");
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        game.Invoke(
            () =>
            {
                try
                {
                    done.SetResult(func());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            },
            "GridBackups companion"
        );
        return done.Task;
    }

    private sealed class Player
    {
        public ulong SteamId { get; set; }
        public long MainIdentityId { get; set; }
        public HashSet<long> IdentityIds { get; set; }
    }

    private sealed class ListedGrid
    {
        public long GridId { get; set; }
        public string Name { get; set; }
        public int Blocks { get; set; }
        public int Pcu { get; set; }
        public List<string> OwnerSteamIds { get; set; }
        public bool Present { get; set; }
        public DateTime LastBackupUtc { get; set; }
        public int BackupCount { get; set; }
        public LossRef Loss { get; set; }
    }

    private sealed class LossRef
    {
        public DateTime TimeUtc { get; set; }
        public string Cause { get; set; }
    }
}
