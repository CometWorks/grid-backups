using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PluginSdk.Commands;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Common.ObjectBuilders;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ServerPlugin;

[CommandRoot("gridbackup", "Grid Backups", "grid backup tools")]
public sealed class GridBackupsCommands : CommandModule
{
    private const int MaxListLines = 30;

    [Command("list", "Lists backed up grids for a player")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public IEnumerable<string> List(string playerNameOrSteamId, string gridNameOrEntityId = null)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Backups == null)
            return Single("GridBackups is not loaded.");

        if (!TryResolvePlayer(playerNameOrSteamId, out var identityId, out var playerName))
            return Single("Player not found.");

        var ownerIds = new[] { identityId };
        return string.IsNullOrWhiteSpace(gridNameOrEntityId)
            ? FormatGridList(plugin.Backups.ListBackedUpGrids(ownerIds), $"Backed up grids for {playerName}")
            : FormatFileList(plugin.Backups.ListBackupFiles(ownerIds, gridNameOrEntityId), gridNameOrEntityId);
    }

    [Command("list faction", "Lists backed up grids for a faction")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public IEnumerable<string> ListFaction(string factionTag, string gridNameOrEntityId = null)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Backups == null)
            return Single("GridBackups is not loaded.");

        var faction = MySession.Static?.Factions?.TryGetFactionByTag(factionTag);
        if (faction == null)
            return Single("Faction not found.");

        var ownerIds = faction.Members.Keys.ToArray();
        var title = $"Backed up grids for [{faction.Tag}] {faction.Name}";
        return string.IsNullOrWhiteSpace(gridNameOrEntityId)
            ? FormatGridList(plugin.Backups.ListBackedUpGrids(ownerIds), title)
            : FormatFileList(plugin.Backups.ListBackupFiles(ownerIds, gridNameOrEntityId), gridNameOrEntityId);
    }

    [Command("find", "Finds backed up grids matching a name or entity id")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public IEnumerable<string> Find(string gridNameOrEntityId)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Backups == null)
            return Single("GridBackups is not loaded.");

        return FormatGridList(plugin.Backups.FindBackedUpGrids(gridNameOrEntityId), $"Find results for {gridNameOrEntityId}");
    }

    [Command("restore", "Restores a backed up grid")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string Restore(string playerNameOrSteamId, string gridNameOrEntityId, int backupNumber = 1, bool keepOriginalPosition = false, bool force = false)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Backups == null)
            return "GridBackups is not loaded.";

        if (!TryResolvePlayer(playerNameOrSteamId, out var identityId, out _))
            return "Player not found.";

        return Restore(plugin, new[] { identityId }, gridNameOrEntityId, backupNumber, keepOriginalPosition, force);
    }

    [Command("restore faction", "Restores a backed up grid from a faction member")]
    [Permission(MyPromoteLevel.SpaceMaster)]
    public string RestoreFaction(string factionTag, string gridNameOrEntityId, int backupNumber = 1, bool keepOriginalPosition = false, bool force = false)
    {
        var plugin = Plugin.Instance;
        if (plugin?.Backups == null)
            return "GridBackups is not loaded.";

        var faction = MySession.Static?.Factions?.TryGetFactionByTag(factionTag);
        if (faction == null)
            return "Faction not found.";

        return Restore(plugin, faction.Members.Keys, gridNameOrEntityId, backupNumber, keepOriginalPosition, force);
    }

    [Command("run", "Starts a full backup scan now")]
    [Permission(MyPromoteLevel.Admin)]
    public string Run()
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return "GridBackups is not loaded.";

        return plugin.Backups.StartManualBackup()
            ? "Backup creation started."
            : "Backup could not start.";
    }

    [Command("status", "Shows backup service state")]
    [Permission(MyPromoteLevel.Admin)]
    public string Status()
    {
        var plugin = Plugin.Instance;
        return plugin?.Backups.GetStatus() ?? "GridBackups is not loaded.";
    }

    private string Restore(Plugin plugin, IEnumerable<long> ownerIds, string gridNameOrEntityId, int backupNumber, bool keepOriginalPosition, bool force)
    {
        Vector3D? spawnPosition = null;
        if (!keepOriginalPosition)
        {
            if (Context.Caller.IsConsole)
                return "Console restore must use keepOriginalPosition=true.";

            var identity = MySession.Static?.Players?.TryGetIdentity(Context.Caller.IdentityId);
            var character = identity?.Character;
            if (character == null)
                return "Caller has no character to restore near.";

            spawnPosition = character.PositionComp.GetPosition() + character.WorldMatrix.Forward * 100.0;
        }

        var result = plugin.Backups.RestoreBackup(ownerIds, gridNameOrEntityId, backupNumber, keepOriginalPosition, spawnPosition, force);
        if (result.Success)
        {
            plugin.Log.Info("Grid backup restored", new { result.FilePath, caller = Context.Caller.Name });
            return $"Restored {Path.GetFileName(result.FilePath)} successfully.";
        }

        return result.Message;
    }

    private static IEnumerable<string> FormatGridList(IReadOnlyList<BackupGridEntry> grids, string title)
    {
        if (grids.Count == 0)
            return Single("No matching backup grids found.");

        var lines = new List<string> { title };
        foreach (var grid in grids.Take(MaxListLines))
            lines.Add($"{grid.Index}. {grid.OwnerName}: {grid.GridFolderName} - latest {grid.LatestBackupLocal:yyyy-MM-dd HH:mm:ss}");

        if (grids.Count > MaxListLines)
            lines.Add($"Showing {MaxListLines} of {grids.Count} matches. Narrow the grid name or entity id.");

        return lines;
    }

    private static IEnumerable<string> FormatFileList(IReadOnlyList<BackupFileEntry> files, string gridNameOrEntityId)
    {
        if (files.Count == 0)
            return Single("Grid not found.");

        var lines = new List<string> { $"Backups for {files[0].GridFolderName} matching {gridNameOrEntityId}" };
        foreach (var file in files.Take(MaxListLines))
            lines.Add($"{file.Index}. {file.FileName} {file.SizeKb:#,##0.00} kb");

        if (files.Count > MaxListLines)
            lines.Add($"Showing {MaxListLines} of {files.Count} backups.");

        return lines;
    }

    private static IEnumerable<string> Single(string message)
        => new[] { message };

    private static bool TryResolvePlayer(string playerNameOrSteamId, out long identityId, out string playerName)
    {
        identityId = 0;
        playerName = "Nobody";

        if (string.Equals(playerNameOrSteamId, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(playerNameOrSteamId, "nobody", StringComparison.OrdinalIgnoreCase))
            return true;

        var players = MySession.Static?.Players;
        if (players == null)
            return false;

        if (long.TryParse(playerNameOrSteamId, out var parsed))
        {
            var identity = players.TryGetIdentity(parsed);
            if (identity != null)
            {
                identityId = identity.IdentityId;
                playerName = identity.DisplayName;
                return true;
            }

            foreach (var candidate in players.GetAllIdentities())
            {
                if ((long)players.TryGetSteamId(candidate.IdentityId) != parsed)
                    continue;

                identityId = candidate.IdentityId;
                playerName = candidate.DisplayName;
                return true;
            }
        }

        var exact = players.GetAllIdentities()
            .FirstOrDefault(identity => string.Equals(identity.DisplayName, playerNameOrSteamId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            identityId = exact.IdentityId;
            playerName = exact.DisplayName;
            return true;
        }

        var partialMatches = players.GetAllIdentities()
            .Where(identity => identity.DisplayName.IndexOf(playerNameOrSteamId, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(2)
            .ToList();
        if (partialMatches.Count != 1)
            return false;

        identityId = partialMatches[0].IdentityId;
        playerName = partialMatches[0].DisplayName;
        return true;
    }
}

public sealed class BackupGridEntry
{
    public int Index { get; set; }
    public long OwnerIdentityId { get; set; }
    public string OwnerName { get; set; }
    public string GridFolderName { get; set; }
    public DateTime LatestBackupLocal { get; set; }
    public DirectoryInfo Directory { get; set; }
}

public sealed class BackupFileEntry
{
    public int Index { get; set; }
    public string GridFolderName { get; set; }
    public string FileName { get; set; }
    public double SizeKb { get; set; }
    public FileInfo File { get; set; }
}

public sealed class RestoreBackupResult
{
    private RestoreBackupResult(bool success, string message, string filePath)
    {
        Success = success;
        Message = message;
        FilePath = filePath;
    }

    public bool Success { get; }
    public string Message { get; }
    public string FilePath { get; }

    public static RestoreBackupResult Ok(string filePath)
        => new RestoreBackupResult(true, null, filePath);

    public static RestoreBackupResult Fail(string message)
        => new RestoreBackupResult(false, message, null);
}

public partial class GridBackupService
{
    public IReadOnlyList<BackupGridEntry> ListBackedUpGrids(IEnumerable<long> ownerIds)
    {
        var entries = EnumerateGridDirectories(ownerIds)
            .OrderBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.GridFolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
            entries[i].Index = i + 1;

        return entries;
    }

    public IReadOnlyList<BackupGridEntry> FindBackedUpGrids(string gridNameOrEntityId)
    {
        var entries = EnumerateGridDirectories(EnumerateKnownOwnerIds())
            .Where(entry => MatchesGrid(entry.Directory, gridNameOrEntityId))
            .OrderByDescending(entry => entry.LatestBackupLocal)
            .ToList();

        for (var i = 0; i < entries.Count; i++)
            entries[i].Index = i + 1;

        return entries;
    }

    public IReadOnlyList<BackupFileEntry> ListBackupFiles(IEnumerable<long> ownerIds, string gridNameOrEntityId)
    {
        var grid = FindGridDirectory(ownerIds, gridNameOrEntityId);
        if (grid == null)
            return Array.Empty<BackupFileEntry>();

        var files = grid.GetFiles("*.sbc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.CreationTimeUtc)
            .Select((file, index) => new BackupFileEntry
            {
                Index = index + 1,
                GridFolderName = grid.Name,
                FileName = file.Name,
                SizeKb = file.Length / 1024.0,
                File = file,
            })
            .ToList();

        return files;
    }

    public RestoreBackupResult RestoreBackup(IEnumerable<long> ownerIds, string gridNameOrEntityId, int backupNumber, bool keepOriginalPosition, Vector3D? spawnPosition, bool force)
    {
        if (backupNumber < 1)
            return RestoreBackupResult.Fail("Backup number must be at least 1.");

        if (!keepOriginalPosition && !spawnPosition.HasValue)
            return RestoreBackupResult.Fail("No restore spawn position is available.");

        var grid = FindGridDirectory(ownerIds, gridNameOrEntityId);
        if (grid == null)
            return RestoreBackupResult.Fail("Grid not found.");

        var file = grid.GetFiles("*.sbc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(candidate => candidate.CreationTimeUtc)
            .Skip(backupNumber - 1)
            .FirstOrDefault();
        if (file == null)
            return RestoreBackupResult.Fail("Backup not found. Check if the number is in range.");

        if (!MyObjectBuilderSerializerKeen.DeserializeXML<MyObjectBuilder_Definitions>(file.FullName, out var definitions))
            return RestoreBackupResult.Fail("Backup file could not be read.");

        var blueprint = definitions.ShipBlueprints?.FirstOrDefault();
        var builders = blueprint?.CubeGrids?.Where(builder => builder != null).ToList();
        if (builders == null || builders.Count == 0)
            return RestoreBackupResult.Fail("Backup file contains no grids.");

        if (!keepOriginalPosition)
            MoveBuildersNearPosition(builders, spawnPosition.Value);

        MyEntities.RemapObjectBuilderCollection(builders.Cast<MyObjectBuilder_EntityBase>());
        foreach (var builder in builders)
            builder.PersistentFlags |= MyPersistentEntityFlags2.InScene;

        if (!force && IsRestorePositionOccupied(builders))
            return RestoreBackupResult.Fail("Restore area appears occupied. Repeat with force=true to ignore the occupation check.");

        foreach (var builder in builders)
            MyEntities.CreateFromObjectBuilderAndAdd(builder, fadeIn: false);

        return RestoreBackupResult.Ok(file.FullName);
    }

    private IEnumerable<BackupGridEntry> EnumerateGridDirectories(IEnumerable<long> ownerIds)
    {
        foreach (var ownerId in ownerIds.Distinct())
        {
            var ownerPath = CreatePathForPlayer(BackupRoot, ownerId);
            if (!Directory.Exists(ownerPath))
                continue;

            foreach (var dir in new DirectoryInfo(ownerPath).GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                var latest = dir.GetFiles("*.sbc", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.CreationTimeUtc)
                    .FirstOrDefault();
                if (latest == null)
                    continue;

                yield return new BackupGridEntry
                {
                    OwnerIdentityId = ownerId,
                    OwnerName = GetOwnerDisplayName(ownerId),
                    GridFolderName = dir.Name,
                    LatestBackupLocal = latest.CreationTime,
                    Directory = dir,
                };
            }
        }
    }

    private IEnumerable<long> EnumerateKnownOwnerIds()
    {
        if (Config.BackupNobodyGrids)
            yield return 0;

        var identities = MySession.Static?.Players?.GetAllIdentities();
        if (identities == null)
            yield break;

        foreach (var identity in identities)
        {
            if (!Config.BackupNpcGrids && MySession.Static.Players.IdentityIsNpc(identity.IdentityId))
                continue;

            yield return identity.IdentityId;
        }
    }

    private DirectoryInfo FindGridDirectory(IEnumerable<long> ownerIds, string gridNameOrEntityId)
    {
        var dirs = ListBackedUpGrids(ownerIds).Select(entry => entry.Directory).ToList();
        if (int.TryParse(gridNameOrEntityId, out var index) && index >= 1 && index <= dirs.Count)
            return dirs[index - 1];

        return dirs.FirstOrDefault(dir => MatchesGrid(dir, gridNameOrEntityId));
    }

    private string GetOwnerDisplayName(long ownerId)
    {
        if (ownerId == 0)
            return "Nobody";

        var identity = MySession.Static?.Players?.TryGetIdentity(ownerId);
        return identity == null ? ownerId.ToString() : $"{identity.DisplayName} #{ownerId}";
    }

    private static bool MatchesGrid(DirectoryInfo dir, string gridNameOrEntityId)
    {
        if (dir == null || string.IsNullOrWhiteSpace(gridNameOrEntityId))
            return false;

        var name = dir.Name;
        var lastUnderscore = name.LastIndexOf('_');
        var gridName = lastUnderscore < 0 ? name : name.Substring(0, lastUnderscore);
        var entityId = lastUnderscore < 0 ? string.Empty : name.Substring(lastUnderscore + 1);
        var regex = WildcardRegex(gridNameOrEntityId);
        var sanitized = SanitizeSegment(gridNameOrEntityId, gridNameOrEntityId);

        return Matches(regex) ||
               (!string.Equals(sanitized, gridNameOrEntityId, StringComparison.Ordinal) && Matches(WildcardRegex(sanitized)));

        bool Matches(string pattern)
            => Regex.IsMatch(entityId, pattern, RegexOptions.IgnoreCase) ||
               Regex.IsMatch(gridName, pattern, RegexOptions.IgnoreCase) ||
               Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase);

        static string WildcardRegex(string value)
            => "^" + Regex.Escape(value).Replace("\\?", ".").Replace("\\*", ".*") + "$";
    }

    private static void MoveBuildersNearPosition(IReadOnlyList<MyObjectBuilder_CubeGrid> builders, Vector3D spawnPosition)
    {
        var primary = builders
            .OrderByDescending(builder => builder.CubeBlocks?.Count ?? 0)
            .First();
        var primaryPosition = GetBuilderPosition(primary);
        var offset = spawnPosition - primaryPosition;

        foreach (var builder in builders)
        {
            var orientation = builder.PositionAndOrientation ?? MyPositionAndOrientation.Default;
            builder.PositionAndOrientation = new MyPositionAndOrientation(
                (Vector3D)orientation.Position + offset,
                orientation.Forward,
                orientation.Up);
        }
    }

    private static Vector3D GetBuilderPosition(MyObjectBuilder_CubeGrid builder)
    {
        if (builder.PositionAndOrientation.HasValue)
            return builder.PositionAndOrientation.Value.Position;

        return Vector3D.Zero;
    }

    private static bool IsRestorePositionOccupied(IEnumerable<MyObjectBuilder_CubeGrid> builders)
    {
        foreach (var builder in builders)
        {
            var sphere = builder.CalculateBoundingSphere();
            var matrix = builder.PositionAndOrientation.HasValue
                ? builder.PositionAndOrientation.Value.GetMatrix()
                : MatrixD.Identity;
            var sphereD = new BoundingSphereD(Vector3D.Transform((Vector3D)sphere.Center, matrix), sphere.Radius);
            if (MyEntities.IsSpherePenetrating(ref sphereD))
                return true;
        }

        return false;
    }
}
