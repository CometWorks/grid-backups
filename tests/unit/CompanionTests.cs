using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ServerPlugin;
using VRageMath;
using Xunit;

namespace GridBackups.Tests;

// The companion operations' decisions that need no game (RestoreLogic) and the
// index files on disk
public sealed class RestoreLogicTests
{
    [Fact]
    public void BigOwnersHoldTheMostBlocks() =>
        Assert.Equal(new long[] { 2 }, RestoreLogic.BigOwners(new long[] { 1, 2, 2, 0, 0, 0 }));

    [Fact]
    public void BigOwnersTie() =>
        Assert.Equal(new long[] { 1, 2 }, RestoreLogic.BigOwners(new long[] { 2, 1, 0, 2, 1 }));

    [Fact]
    public void NoBigOwnersWithoutOwnedBlocks() =>
        Assert.Empty(RestoreLogic.BigOwners(new long[] { 0, 0 }));

    private sealed class Backup
    {
        public DateTime Taken;
    }

    private static readonly DateTime T0 = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Backup[] Backups =
    {
        new Backup { Taken = T0.AddHours(-2) },
        new Backup { Taken = T0.AddHours(-1) },
        new Backup { Taken = T0.AddHours(1) },
    };

    [Fact]
    public void NewestBackupBeforeTheLoss() =>
        Assert.Same(Backups[1], RestoreLogic.ChooseBackup(Backups, b => b.Taken, T0));

    [Fact]
    public void NewestBackupWithoutALoss() =>
        Assert.Same(Backups[2], RestoreLogic.ChooseBackup(Backups, b => b.Taken, null));

    [Fact]
    public void NoBackupBeforeTheLoss() =>
        Assert.Null(RestoreLogic.ChooseBackup(Backups, b => b.Taken, T0.AddHours(-3)));

    [Theory]
    [InlineData(true, "deleted", true, "destroyed", 1, "merged")]
    [InlineData(false, "cleanup", true, "destroyed", 1, "cleanup")]
    [InlineData(false, null, true, "destroyed", 1, "hangar")]
    [InlineData(false, null, false, "destroyed", 1, "destroyed")]
    [InlineData(false, null, false, "ground", 4, "ground")]
    [InlineData(false, null, false, "ground", 10, "unknown")]
    [InlineData(false, null, false, null, 0, "unknown")]
    public void LossCauses(
        bool moved,
        string scope,
        bool hangar,
        string removal,
        int secondsAgo,
        string cause
    ) =>
        Assert.Equal(
            cause,
            RestoreLogic.Classify(
                moved,
                scope,
                hangar,
                removal,
                removal == null ? null : T0.AddSeconds(-secondsAgo),
                T0
            )
        );

    [Fact]
    public void SpotAtTheStartWhenFree() =>
        Assert.Equal(
            new Vector3D(1, 2, 3),
            RestoreLogic.FindSpot(new Vector3D(1, 2, 3), 50, 1000, new Vector3D[0], 2000, _ => true)
        );

    [Fact]
    public void SpotOutwardWhenTheStartIsTaken()
    {
        var spot = RestoreLogic.FindSpot(
            Vector3D.Zero,
            50,
            1000,
            new Vector3D[0],
            2000,
            p => p.Length() > 1
        );
        Assert.Equal(50, spot.Value.Length(), 3);
    }

    [Fact]
    public void SpotSkipsExclusionZones()
    {
        // A foreign grid 500 m away: the first spot 2 km from it is 1.5 km
        // from the start on the far side
        var foreign = new[] { new Vector3D(500, 0, 0) };
        var spot = RestoreLogic.FindSpot(Vector3D.Zero, 100, 10000, foreign, 2000, _ => true);
        Assert.NotNull(spot);
        Assert.True(Vector3D.Distance(spot.Value, foreign[0]) >= 2000);
        Assert.Equal(1500, spot.Value.Length(), 3);
    }

    [Fact]
    public void NoSpotWithinTheSearch() =>
        Assert.Null(
            RestoreLogic.FindSpot(
                Vector3D.Zero,
                100,
                1000,
                new[] { new Vector3D(500, 0, 0) },
                2000,
                _ => true
            )
        );

    [Theory]
    [InlineData("Alpha station_123", 123)]
    [InlineData("76561198000000000", 76561198000000000)]
    [InlineData("Some_Player_144115188075855873", 144115188075855873)]
    [InlineData("Nobody", 0)]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    public void TrailingNumbers(string name, long number) =>
        Assert.Equal(number, GridBackupService.TrailingNumber(name));
}

public sealed class IndexTests : IDisposable
{
    private readonly Rig rig = new();

    public IndexTests()
    {
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("quasar"));
    }

    public void Dispose() => rig.Dispose();

    private string Root => rig.Service.BackupRoot;

    private string Touch(params string[] parts)
    {
        var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void GridsOfEveryBigOwner()
    {
        // A backup in owner 1's folder that owner 2 co-owns, and an older
        // backup of owner 2's own, without a side file
        Touch("1", "Shared ship_500", "2026_11_05_10_00_00.sbc");
        Touch(".owners", "2", "500");
        Touch("2", "Old base_600", "2026_10_01_10_00_00.sbc");

        Assert.Equal(
            new long[] { 500, 600 },
            rig.Service.GridIdsOf(new long[] { 2 }).OrderBy(i => i)
        );
        Assert.Empty(rig.Service.GridIdsOf(new long[] { 3 }));
    }

    [Fact]
    public void BackupsOfAGridAcrossFolders()
    {
        var newer = Touch("1", "Renamed_500", "2026_11_05_10_00_00.sbc");
        var older = Touch("2", "Original_500", "2026_11_04_10_00_00.sbc");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        Json.WriteFile(
            GridBackupService.SidecarPath(newer),
            new BackupInfo
            {
                GridId = 500,
                OwnerIdentityIds = new List<long> { 1, 2 },
                TakenUtc = DateTime.UtcNow,
            }
        );

        var backups = rig.Service.BackupsOf(500, rig.Service.GridDirectories());

        Assert.Equal(
            new[]
            {
                "1/Renamed_500/2026_11_05_10_00_00.sbc",
                "2/Original_500/2026_11_04_10_00_00.sbc",
            },
            backups.Select(b => b.BackupId)
        );
        Assert.Equal(new long[] { 1, 2 }, backups[0].Owners);
        // No side file: the owner folder's identity, which no session resolves here
        Assert.Empty(backups[1].Owners);
    }

    [Fact]
    public void RetentionTakesTheSideFiles()
    {
        rig.Config.NumberOfBackupSaves = 1;
        var keep = Touch("1", "Ship_500", "2026_11_05_10_00_00.sbc");
        var drop = Touch("1", "Ship_500", "2026_11_04_10_00_00.sbc");
        File.SetLastWriteTimeUtc(drop, DateTime.UtcNow.AddDays(-1));
        Touch("1", "Ship_500", "2026_11_05_10_00_00.json");
        Touch("1", "Ship_500", "2026_11_04_10_00_00.json");

        rig.Call("CleanUpDirectory", Path.GetDirectoryName(keep));

        Assert.Equal(
            new[] { "2026_11_05_10_00_00.json", "2026_11_05_10_00_00.sbc" },
            Directory.GetFiles(Path.GetDirectoryName(keep)).Select(Path.GetFileName).OrderBy(n => n)
        );
    }

    [Fact]
    public void LossEntryJson() =>
        Assert.Equal(
            "{\"gridId\":5,\"name\":\"Ship\",\"ownerIdentityIds\":[1],\"ownerSteamIds\":[\"7\"],"
                + "\"timeUtc\":\"2026-11-05T12:00:00Z\",\"cause\":\"destroyed\"}",
            Json.Write(
                new LossEntry
                {
                    GridId = 5,
                    Name = "Ship",
                    OwnerIdentityIds = new List<long> { 1 },
                    OwnerSteamIds = new List<string> { "7" },
                    TimeUtc = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc),
                    Cause = LossCause.Destroyed,
                }
            )
        );
}

// The two members Quasar.Agent finds on the plugin by reflection, and the
// errors that come back through them. The answers that need a game are in
// the in-game suite.
public sealed class CompanionMemberTests : IDisposable
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly Rig rig = new();

    public CompanionMemberTests()
    {
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("quasar"));
        Set("Backups", rig.Service);
        Set("Losses", new GridLossLog(rig.Plugin));
        Set("Companion", new CompanionOperations(rig.Plugin));
    }

    public void Dispose() => rig.Dispose();

    private void Set(string property, object value) =>
        typeof(Plugin).GetProperty(property, Any).SetValue(rig.Plugin, value);

    private Task<string> Call(string operation, string payload) =>
        rig.Plugin.HandleQuasarCompanionRequestAsync(operation, payload, CancellationToken.None);

    [Fact]
    public void TheManifestIdNamesTheHandler()
    {
        var manifest = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "../../../../../GridBackups.xml")
        );
        Assert.Equal(manifest.Root.Element("Id").Value, rig.Plugin.QuasarCompanionPluginId);
    }

    [Fact]
    public void TheSignatureQuasarAgentLooksFor()
    {
        var method = typeof(Plugin).GetMethod(
            "HandleQuasarCompanionRequestAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(string), typeof(CancellationToken) },
            null
        );
        Assert.Equal(typeof(Task<string>), method?.ReturnType);
        Assert.Equal(
            typeof(string),
            typeof(Plugin)
                .GetProperty("QuasarCompanionPluginId", BindingFlags.Public | BindingFlags.Instance)
                ?.PropertyType
        );
    }

    [Theory]
    [InlineData("nope", "{}", "Unsupported operation 'nope'.")]
    [InlineData("list", "{}", "steamId is missing.")]
    [InlineData("list", "", "steamId is missing.")]
    [InlineData("remove-restored", "{}", "entityIds is missing.")]
    [InlineData("remove-restored", "{\"entityIds\":[5]}", "Not restored by Grid Backups: 5.")]
    public async Task ErrorsAreExceptions(string operation, string payload, string error)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Call(operation, payload)
        );
        Assert.Equal(error, ex.Message);
    }

    [Fact]
    public async Task NotLoaded()
    {
        Set("Companion", null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Call("list", "{}"));
        Assert.Equal("GridBackups is not loaded.", ex.Message);
    }
}
