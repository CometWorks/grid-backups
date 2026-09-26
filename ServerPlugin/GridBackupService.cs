using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using PluginSdk.Storage;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Groups;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;

namespace ServerPlugin;

public sealed partial class GridBackupService
{
    private const string DailyPrefix = "daily";
    private const string PluginId = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";
    private readonly Plugin plugin;
    private readonly object stateLock = new object();
    private readonly ConcurrentDictionary<string, object> gridLocks = new ConcurrentDictionary<string, object>();
    private readonly ConcurrentDictionary<long, byte> queuedGrids = new ConcurrentDictionary<long, byte>();
    private readonly Dictionary<long, GridState> states = new Dictionary<long, GridState>();
    private DateTime nextScanUtc = DateTime.MinValue;
    private int runningBackups;
    private DateTime lastCleanupUtc = DateTime.MinValue;

    public GridBackupService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private GridBackupsConfig Config => plugin.Config;

    public string BackupRoot
    {
        get
        {
            var root = PluginCluster.IsClusterProcess
                ? PluginStorage.GetSharedDirectory(PluginId)
                : Config.UseQuasarConfigFolder
                ? ResolveQuasarConfigRoot()
                : plugin.PluginDataPath;

            var folder = SanitizeSegment(Config.BackupFolderName, "GridBackups");
            var path = Path.Combine(root, folder);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public void Update()
    {
        if (!Config.Enabled || MySession.Static == null)
            return;

        var now = DateTime.UtcNow;
        if (now < nextScanUtc)
            return;

        nextScanUtc = now.AddSeconds(Math.Max(1, Config.ScanIntervalSeconds));
        ScanAndQueue(now);
    }

    public bool StartManualBackup()
    {
        if (MySession.Static == null)
            return false;

        foreach (var group in EnumerateBackupGroups())
            QueueBackup(group, BackupReason.Manual, force: true);

        return true;
    }

    public bool BackupBeforeConcealment(IReadOnlyList<MyObjectBuilder_CubeGrid> grids, long ownerIdentity)
    {
        if (!Config.Enabled || !Config.BackupBeforeConcealment || grids == null || grids.Count == 0)
            return false;

        return BackupBuilders(ownerIdentity, grids, BackupReason.BeforeConcealment, respectMinDelay: true);
    }

    public bool BackupExternalBuilders(IReadOnlyList<MyObjectBuilder_CubeGrid> grids, long ownerIdentity, bool respectMinDelay)
    {
        if (!Config.Enabled || grids == null || grids.Count == 0)
            return false;

        return BackupBuilders(ownerIdentity, grids, BackupReason.Manual, respectMinDelay);
    }

    public string GetStatus()
    {
        lock (stateLock)
            return $"root={BackupRoot}, tracked={states.Count}, running={runningBackups}, nextScanUtc={nextScanUtc:O}";
    }

    private void ScanAndQueue(DateTime now)
    {
        var seen = new HashSet<long>();

        foreach (var group in EnumerateBackupGroups())
        {
            if (group.Grids.Count == 0)
                continue;

            var key = group.PrimaryEntityId;
            seen.Add(key);
            var blockCount = group.BlockCount;

            BackupReason reason;
            lock (stateLock)
            {
                if (!states.TryGetValue(key, out var state))
                {
                    state = new GridState(now, blockCount);
                    states[key] = state;
                }

                if (state.LastObservedBlockCount != blockCount)
                {
                    state.LastObservedBlockCount = blockCount;
                    state.LastChangedUtc = now;
                }

                reason = GetBackupReason(state, blockCount, now);
            }

            if (reason == BackupReason.None)
                continue;

            QueueBackup(group, reason, force: reason == BackupReason.MaxTime);
        }

        lock (stateLock)
        {
            foreach (var key in states.Keys.ToArray())
                if (!seen.Contains(key))
                    states.Remove(key);
        }
    }

    private BackupReason GetBackupReason(GridState state, int blockCount, DateTime now)
    {
        var sinceLast = now - state.LastBackupUtc;

        if (Config.MaxMinutesSinceLastSave > 0 && sinceLast.TotalMinutes >= Config.MaxMinutesSinceLastSave)
            return BackupReason.MaxTime;

        if (Config.MinMinutesSinceLastSave > 0 && sinceLast.TotalMinutes < Config.MinMinutesSinceLastSave)
            return BackupReason.None;

        if (state.LastBackupUtc == DateTime.MinValue)
            return BackupReason.Initial;

        if (Config.MaxBlockCountChange > 0 &&
            Math.Abs(blockCount - state.LastBackupBlockCount) >= Config.MaxBlockCountChange)
            return BackupReason.BlockCountChange;

        if (Config.BackupIfSteady && (now - state.LastChangedUtc).TotalSeconds >= Config.SteadySeconds)
            return BackupReason.Steady;

        return BackupReason.None;
    }

    private bool QueueBackup(BackupGroup group, BackupReason reason, bool force)
    {
        if (group.BlockCount < Config.MinBlocksForBackup)
            return false;

        if (!force && !MinDelayElapsed(group.PrimaryEntityId))
            return false;

        if (!queuedGrids.TryAdd(group.PrimaryEntityId, 0))
            return false;

        List<MyObjectBuilder_CubeGrid> builders;
        try
        {
            builders = group.Grids.Select(GetBuilder).ToList();
        }
        catch (Exception ex)
        {
            queuedGrids.TryRemove(group.PrimaryEntityId, out _);
            plugin.Log.Error("Failed to collect grid object builders", ex, new { group.PrimaryEntityId });
            return false;
        }

        var owner = group.OwnerIdentityId;
        Interlocked.Increment(ref runningBackups);
        Task.Run(() =>
        {
            try
            {
                BackupBuilders(owner, builders, reason, respectMinDelay: !force);
            }
            finally
            {
                queuedGrids.TryRemove(group.PrimaryEntityId, out _);
                Interlocked.Decrement(ref runningBackups);
            }
        });

        return true;
    }

    private bool BackupBuilders(long ownerIdentity, IReadOnlyList<MyObjectBuilder_CubeGrid> builders, BackupReason reason, bool respectMinDelay)
    {
        if (builders.Count == 0)
            return false;

        var biggest = builders
            .OrderByDescending(g => g.CubeBlocks?.Count ?? 0)
            .First();
        var primaryEntityId = biggest.EntityId;

        if (respectMinDelay && !MinDelayElapsed(primaryEntityId))
            return false;

        try
        {
            var gridName = string.IsNullOrWhiteSpace(biggest.DisplayName) ? "Grid" : biggest.DisplayName;
            var pathForPlayer = CreatePathForPlayer(BackupRoot, ownerIdentity);
            var pathForGrid = CreatePathForGrid(pathForPlayer, gridName, primaryEntityId);
            Directory.CreateDirectory(pathForGrid);

            string filePath;
            lock (gridLocks.GetOrAdd(pathForGrid, _ => new object()))
            using (AcquireGridLock(pathForGrid))
            {
                if (PluginCluster.IsClusterProcess)
                {
                    var latest = new DirectoryInfo(pathForGrid).GetFiles("*.sbc", SearchOption.TopDirectoryOnly)
                        .Where(file => !file.Name.StartsWith(DailyPrefix + "_", StringComparison.OrdinalIgnoreCase))
                        .Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                    var elapsed = DateTime.UtcNow - latest;
                    if (reason == BackupReason.MaxTime
                        ? Config.MaxMinutesSinceLastSave > 0 && elapsed.TotalMinutes < Config.MaxMinutesSinceLastSave
                        : respectMinDelay && Config.MinMinutesSinceLastSave > 0
                          && elapsed.TotalMinutes < Config.MinMinutesSinceLastSave)
                        return false;
                }

                var now = DateTime.Now;
                if (Config.NumberOfDailyBackupSaves > 0)
                {
                    var dailyPath = Path.Combine(pathForGrid, $"{DailyPrefix}_{now:yyyy_MM_dd}.sbc");
                    if (!File.Exists(dailyPath))
                        SaveGrid(dailyPath, gridName, builders);
                }

                filePath = Path.Combine(pathForGrid, PluginCluster.IsClusterProcess
                    ? $"{now:yyyy_MM_dd_HH_mm_ss}_{Guid.NewGuid():N}.sbc"
                    : $"{now:yyyy_MM_dd_HH_mm_ss}.sbc");
                if (!SaveGrid(filePath, gridName, builders))
                    return false;

                CleanUpDirectory(pathForGrid);
            }
            CleanupOldBackupsIfDue();

            var backupUtc = DateTime.UtcNow;
            var blockCount = builders.Sum(g => g.CubeBlocks?.Count ?? 0);
            lock (stateLock)
            {
                states[primaryEntityId] = new GridState(backupUtc, blockCount)
                {
                    LastBackupUtc = backupUtc,
                    LastBackupBlockCount = blockCount,
                };
            }

            plugin.Log.Info("Grid backup saved", new { filePath, reason = reason.ToString() });
            return true;
        }
        catch (Exception ex)
        {
            plugin.Log.Error("Grid backup failed", ex, new { ownerIdentity, primaryEntityId, reason = reason.ToString() });
            return false;
        }
    }

    private bool MinDelayElapsed(long entityId)
    {
        if (Config.MinMinutesSinceLastSave <= 0)
            return true;

        lock (stateLock)
        {
            return !states.TryGetValue(entityId, out var state) ||
                   state.LastBackupUtc == DateTime.MinValue ||
                   (DateTime.UtcNow - state.LastBackupUtc).TotalMinutes >= Config.MinMinutesSinceLastSave;
        }
    }

    private IEnumerable<BackupGroup> EnumerateBackupGroups()
    {
        var exported = new HashSet<long>();

        if (Config.BackupConnectedGrids)
        {
            foreach (var group in MyCubeGridGroups.Static.Mechanical.Groups)
            {
                var grids = group.Nodes
                    .Select(n => n.NodeData)
                    .Where(IsBackupCandidate)
                    .ToList();

                if (grids.Count == 0)
                    continue;

                var backupGroup = CreateGroup(grids);
                if (backupGroup == null)
                    continue;

                foreach (var grid in grids)
                    exported.Add(grid.EntityId);

                yield return backupGroup;
            }
        }

        foreach (var entity in MyEntities.GetEntities())
        {
            if (entity is not MyCubeGrid grid || exported.Contains(grid.EntityId) || !IsBackupCandidate(grid))
                continue;

            var backupGroup = CreateGroup(new List<MyCubeGrid> { grid });
            if (backupGroup != null)
                yield return backupGroup;
        }
    }

    private bool IsBackupCandidate(MyCubeGrid grid)
    {
        if (grid == null || grid.MarkedForClose || grid.Closed)
            return false;

        if (grid.Physics == null && !grid.IsStatic)
            return false;

        var owner = GetOwnerIdentityId(grid);
        if (owner == 0)
            return Config.BackupNobodyGrids;

        return Config.BackupNpcGrids || !MySession.Static.Players.IdentityIsNpc(owner);
    }

    private BackupGroup CreateGroup(List<MyCubeGrid> grids)
    {
        var biggest = grids.OrderByDescending(g => g.BlocksCount).FirstOrDefault();
        if (biggest == null)
            return null;

        return new BackupGroup(
            biggest.EntityId,
            GetOwnerIdentityId(biggest),
            grids.Sum(g => g.BlocksCount),
            grids);
    }

    private static MyObjectBuilder_CubeGrid GetBuilder(MyCubeGrid grid)
    {
        if (grid.GetObjectBuilder() is MyObjectBuilder_CubeGrid builder)
            return builder;

        throw new InvalidOperationException($"{grid.DisplayName} object builder is not a cube grid");
    }

    private bool SaveGrid(string filePath, string gridName, IReadOnlyList<MyObjectBuilder_CubeGrid> builders)
    {
        var definition = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_ShipBlueprintDefinition>();
        definition.Id = new MyDefinitionId(
            new MyObjectBuilderType(typeof(MyObjectBuilder_ShipBlueprintDefinition)),
            gridName);
        definition.CubeGrids = builders.ToArray();

        if (!Config.KeepOriginalOwner)
        {
            foreach (var grid in definition.CubeGrids)
            {
                if (grid.CubeBlocks == null)
                    continue;

                foreach (var block in grid.CubeBlocks)
                {
                    block.Owner = 0;
                    block.BuiltBy = 0;
                }
            }
        }

        var definitions = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Definitions>();
        definitions.ShipBlueprints = new[] { definition };

        var directory = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, ".pending");
        Directory.CreateDirectory(pending);
        var temporary = Path.Combine(pending, Guid.NewGuid().ToString("N") + ".sbc");
        try
        {
            if (!MyObjectBuilderSerializerKeen.SerializeXML(temporary, false, definitions))
                return false;
            try { File.Move(temporary, filePath); }
            catch (IOException) when (File.Exists(filePath)) { return false; }
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream AcquireGridLock(string directory)
    {
        var stream = new FileStream(Path.Combine(directory, ".backup.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        var wait = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try { stream.Lock(0, 1); return stream; }
                catch (IOException) when (wait.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(25); }
            }
        }
        catch { stream.Dispose(); throw; }
    }

    private string CreatePathForPlayer(string root, long playerId)
    {
        string folderName;
        if (Config.UseSteamId && playerId != 0 && MySession.Static?.Players != null)
        {
            var steamId = MySession.Static.Players.TryGetSteamId(playerId);
            folderName = steamId == 0 ? playerId.ToString() : steamId.ToString();
        }
        else
        {
            folderName = playerId.ToString();
        }

        if (Config.PlayerNameOnFolders)
        {
            var identity = MySession.Static?.Players.TryGetIdentity(playerId);
            var name = identity == null ? "Nobody" : SanitizeSegment(identity.DisplayName, "Player");
            folderName = $"{name}_{folderName}";
        }

        return Path.Combine(root, folderName);
    }

    private static string CreatePathForGrid(string playerPath, string gridName, long entityId)
        => Path.Combine(playerPath, $"{SanitizeSegment(gridName, "Grid")}_{entityId}");

    private static long GetOwnerIdentityId(MyCubeGrid grid)
        => grid.BigOwners != null && grid.BigOwners.Count > 0 ? grid.BigOwners[0] : 0;

    private void CleanUpDirectory(string pathForGrid)
    {
        var files = new DirectoryInfo(pathForGrid)
            .GetFiles("*.sbc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var normalCount = 0;
        var dailyCount = 0;

        foreach (var file in files)
        {
            var isDaily = file.Name.StartsWith(DailyPrefix, StringComparison.OrdinalIgnoreCase);
            if (isDaily)
            {
                if (dailyCount++ >= Config.NumberOfDailyBackupSaves)
                    file.Delete();
            }
            else if (normalCount++ >= Config.NumberOfBackupSaves)
            {
                file.Delete();
            }
        }
    }

    private void CleanupOldBackupsIfDue()
    {
        if (Config.DeleteBackupsOlderThanDays <= 0)
            return;

        var now = DateTime.UtcNow;
        if ((now - lastCleanupUtc).TotalHours < 1)
            return;

        lastCleanupUtc = now;
        var cutoff = now.AddDays(-Config.DeleteBackupsOlderThanDays);

        foreach (var file in new DirectoryInfo(BackupRoot).GetFiles("*.sbc", SearchOption.AllDirectories))
        {
            if (file.Directory?.Name == ".pending") continue;
            try
            {
                var directory = file.DirectoryName;
                lock (gridLocks.GetOrAdd(directory, _ => new object()))
                using (AcquireGridLock(directory))
                    if (file.Exists && file.LastWriteTimeUtc < cutoff)
                        file.Delete();
            }
            catch (Exception ex)
            {
                plugin.Log.Warning("Failed deleting old backup", new { file = file.FullName, error = ex.Message });
            }
        }
    }

    private string ResolveQuasarConfigRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("QUASAR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return explicitRoot;

        var dsConfig = Environment.GetEnvironmentVariable("QUASAR_DS_CONFIG_PATH");
        var derived = TryGetQuasarRootFromPath(dsConfig);
        if (derived != null)
            return derived;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quasar");

        return Path.Combine(home, ".config", "Quasar");
    }

    private static string TryGetQuasarRootFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var full = Path.GetFullPath(path);
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = parts.Length - 1; i >= 0; i--)
            if (string.Equals(parts[i], "Magnetars", StringComparison.OrdinalIgnoreCase))
                return string.Join(Path.DirectorySeparatorChar.ToString(), parts.Take(i));

        return null;
    }

    private static string SanitizeSegment(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        const int maxSegmentLength = 120;
        const string windowsInvalid = "<>:\"/\\|?*";

        var platformInvalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Trim()
            .Select(c => char.IsControl(c) || windowsInvalid.IndexOf(c) >= 0 || platformInvalid.Contains(c) ? '_' : c)
            .ToArray();
        var sanitized = new string(chars).TrimEnd(' ', '.');
        if (IsWindowsReservedName(sanitized))
            sanitized = "_" + sanitized;
        if (sanitized.Length > maxSegmentLength)
            sanitized = sanitized.Substring(0, maxSegmentLength).TrimEnd(' ', '.');

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static bool IsWindowsReservedName(string value)
    {
        var name = value.Split('.')[0];
        return string.Equals(name, "CON", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "PRN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "AUX", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "NUL", StringComparison.OrdinalIgnoreCase) ||
               IsReservedDeviceName(name, "COM") ||
               IsReservedDeviceName(name, "LPT");
    }

    private static bool IsReservedDeviceName(string value, string prefix)
        => value.Length == 4 &&
           value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
           value[3] >= '1' &&
           value[3] <= '9';

    private sealed class GridState
    {
        public GridState(DateTime now, int blockCount)
        {
            LastObservedBlockCount = blockCount;
            LastBackupBlockCount = blockCount;
            LastChangedUtc = now;
            LastBackupUtc = DateTime.MinValue;
        }

        public int LastObservedBlockCount { get; set; }
        public int LastBackupBlockCount { get; set; }
        public DateTime LastChangedUtc { get; set; }
        public DateTime LastBackupUtc { get; set; }
    }

    private sealed class BackupGroup
    {
        public BackupGroup(long primaryEntityId, long ownerIdentityId, int blockCount, List<MyCubeGrid> grids)
        {
            PrimaryEntityId = primaryEntityId;
            OwnerIdentityId = ownerIdentityId;
            BlockCount = blockCount;
            Grids = grids;
        }

        public long PrimaryEntityId { get; }
        public long OwnerIdentityId { get; }
        public int BlockCount { get; }
        public List<MyCubeGrid> Grids { get; }
    }

    private enum BackupReason
    {
        None,
        Initial,
        Manual,
        BeforeConcealment,
        Steady,
        MaxTime,
        BlockCountChange,
    }
}
