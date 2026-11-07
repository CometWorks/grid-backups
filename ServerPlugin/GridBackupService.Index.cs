using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders.Private;

namespace ServerPlugin;

// What a backup holds, written next to it as <backup>.json when it is taken
public sealed class BackupInfo
{
    public long GridId { get; set; }
    public string Name { get; set; }

    // Big owners of the backup's biggest grid, from its blocks
    public List<long> OwnerIdentityIds { get; set; } = new List<long>();
    public List<string> OwnerSteamIds { get; set; } = new List<string>();
    public DateTime TakenUtc { get; set; }
    public int Blocks { get; set; }
    public int Pcu { get; set; }
    public int GridCount { get; set; }
    public Position Position { get; set; }
}

// One backup file on disk and what is known about it
public sealed class StoredBackup
{
    public FileInfo File { get; set; }

    // Its path below the backup root, '/' separated: the backupId of the
    // companion operations
    public string BackupId { get; set; }

    // From the side file; null for backups taken before the index existed
    public BackupInfo Info { get; set; }

    // The identity the owner folder is named after, the single owner older
    // backups have
    public long FolderOwner { get; set; }

    public DateTime TakenUtc => Info?.TakenUtc ?? File.LastWriteTimeUtc;

    public IReadOnlyList<long> Owners =>
        Info?.OwnerIdentityIds
        ?? (FolderOwner != 0 ? new List<long> { FolderOwner } : new List<long>());
}

// The backup index: a side file per backup with its big owners and size, and
// an owner marker <root>/.owners/<identityId>/<gridId> for every big owner,
// so a grid is found for any of its big owners, not only the one whose
// folder holds it. Backups taken before keep their owner folder as their
// only owner.
public sealed partial class GridBackupService
{
    private const string OwnersFolder = ".owners";
    internal const string LossesFolder = ".losses";
    internal const string RestoredFolder = ".restored";

    public static string SidecarPath(string backupPath) =>
        Path.ChangeExtension(backupPath, ".json");

    // Needs no game thread: definitions and identities are only read
    public static BackupInfo DescribeBuilders(
        IReadOnlyList<MyObjectBuilder_CubeGrid> builders,
        DateTime takenUtc
    )
    {
        var primary = builders.OrderByDescending(g => g.CubeBlocks?.Count ?? 0).First();
        var owners = RestoreLogic.BigOwners(
            primary.CubeBlocks?.Select(b => b.Owner) ?? Enumerable.Empty<long>()
        );
        var position = primary.PositionAndOrientation?.Position ?? default;
        return new BackupInfo
        {
            GridId = primary.EntityId,
            Name = primary.DisplayName,
            OwnerIdentityIds = owners,
            OwnerSteamIds = SteamIdsOf(owners),
            TakenUtc = takenUtc,
            Blocks = builders.Sum(g => g.CubeBlocks?.Count ?? 0),
            Pcu = builders.Sum(Pcu),
            GridCount = builders.Count,
            Position = new Position
            {
                X = position.X,
                Y = position.Y,
                Z = position.Z,
            },
        };
    }

    public static List<string> SteamIdsOf(IEnumerable<long> identityIds)
    {
        var players = MySession.Static?.Players;
        return identityIds
            .Select(id => players?.TryGetSteamId(id) ?? 0)
            .Where(steamId => steamId != 0)
            .Select(steamId => steamId.ToString())
            .ToList();
    }

    private static int Pcu(MyObjectBuilder_CubeGrid grid)
    {
        var pcu = 0;
        foreach (var block in grid.CubeBlocks ?? new List<MyObjectBuilder_CubeBlock>())
            if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(block.GetId(), out var def))
                pcu += def.PCU;
        return pcu;
    }

    private void WriteIndex(string backupPath, BackupInfo info)
    {
        Json.WriteFile(SidecarPath(backupPath), info);

        foreach (var owner in info.OwnerIdentityIds)
        {
            var folder = Path.Combine(BackupRoot, OwnersFolder, owner.ToString());
            var marker = Path.Combine(folder, info.GridId.ToString());
            if (File.Exists(marker))
                continue;
            Directory.CreateDirectory(folder);
            File.WriteAllText(marker, string.Empty);
        }
    }

    private static void DeleteBackupFile(FileInfo file)
    {
        file.Delete();
        var sidecar = SidecarPath(file.FullName);
        if (File.Exists(sidecar))
            File.Delete(sidecar);
    }

    // Every grid folder of every owner folder, by grid id: a grid has more
    // than one when it was renamed or its first big owner changed
    public Dictionary<long, List<DirectoryInfo>> GridDirectories()
    {
        var result = new Dictionary<long, List<DirectoryInfo>>();
        foreach (var owner in new DirectoryInfo(BackupRoot).GetDirectories())
        {
            if (owner.Name.StartsWith("."))
                continue;
            foreach (var grid in owner.GetDirectories())
            {
                var id = TrailingNumber(grid.Name);
                if (id == 0)
                    continue;
                if (!result.TryGetValue(id, out var list))
                    result[id] = list = new List<DirectoryInfo>();
                list.Add(grid);
            }
        }

        return result;
    }

    // Grid ids any of these identities owns or owned in a backup: their
    // owner markers and their owner folders
    public HashSet<long> GridIdsOf(IEnumerable<long> identityIds)
    {
        var result = new HashSet<long>();
        foreach (var identity in identityIds.Distinct())
        {
            var markers = new DirectoryInfo(
                Path.Combine(BackupRoot, OwnersFolder, identity.ToString())
            );
            if (markers.Exists)
                foreach (var marker in markers.GetFiles())
                    if (long.TryParse(marker.Name, out var id))
                        result.Add(id);

            var folder = new DirectoryInfo(CreatePathForPlayer(BackupRoot, identity));
            if (folder.Exists)
                foreach (var grid in folder.GetDirectories())
                    if (TrailingNumber(grid.Name) is var id and not 0)
                        result.Add(id);
        }

        return result;
    }

    public List<StoredBackup> BackupsOf(long gridId, Dictionary<long, List<DirectoryInfo>> dirs)
    {
        var root = BackupRoot;
        var result = new List<StoredBackup>();
        if (!dirs.TryGetValue(gridId, out var gridDirs))
            return result;

        foreach (var dir in gridDirs)
        {
            var folderOwner = OwnerOfFolder(dir.Parent?.Name);
            foreach (var file in dir.GetFiles("*.sbc", SearchOption.TopDirectoryOnly))
            {
                var sidecar = SidecarPath(file.FullName);
                result.Add(
                    new StoredBackup
                    {
                        File = file,
                        BackupId = file
                            .FullName.Substring(root.Length)
                            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Replace(Path.DirectorySeparatorChar, '/'),
                        Info = File.Exists(sidecar) ? Json.ReadFile<BackupInfo>(sidecar) : null,
                        FolderOwner = folderOwner,
                    }
                );
            }
        }

        return result.OrderByDescending(b => b.TakenUtc).ToList();
    }

    // The side file, or for an older backup what its file holds, with its
    // owner folder as its single owner. Null when the file can't be read.
    public static BackupInfo Details(StoredBackup backup)
    {
        if (backup.Info != null)
            return backup.Info;

        var builders = LoadBuilders(backup.File.FullName);
        if (builders == null)
            return null;

        var info = DescribeBuilders(builders, backup.File.LastWriteTimeUtc);
        info.OwnerIdentityIds = backup.Owners.ToList();
        info.OwnerSteamIds = SteamIdsOf(info.OwnerIdentityIds);
        return info;
    }

    public static List<MyObjectBuilder_CubeGrid> LoadBuilders(string path)
    {
        if (
            !MyObjectBuilderSerializerKeen.DeserializeXML<MyObjectBuilder_Definitions>(
                path,
                out var definitions
            )
        )
            return null;

        var builders = definitions
            ?.ShipBlueprints?.FirstOrDefault()
            ?.CubeGrids?.Where(builder => builder != null)
            .ToList();
        return builders == null || builders.Count == 0 ? null : builders;
    }

    // Owner folders are named <identity id>, <Steam id> or <name>_<either>
    private static long OwnerOfFolder(string folderName)
    {
        var number = TrailingNumber(folderName);
        var players = MySession.Static?.Players;
        if (number == 0 || players == null)
            return 0;
        if (players.TryGetIdentity(number) != null)
            return number;
        return players.TryGetIdentityId((ulong)number);
    }

    // The number after the last '_', or the whole name, 0 for none
    public static long TrailingNumber(string name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;
        var tail = name.Substring(name.LastIndexOf('_') + 1);
        return long.TryParse(tail, out var number) && number > 0 ? number : 0;
    }
}
