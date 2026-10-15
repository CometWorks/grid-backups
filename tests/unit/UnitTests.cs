using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using PluginSdk.Clustering;
using PluginSdk.Storage;
using ServerPlugin;
using Xunit;

// The tests change process environment variables
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GridBackups.Tests;

internal static class GameAssemblies
{
    // The plugin's types implement game interfaces, so loading them needs the
    // game assemblies, which the build leaves in DedicatedServer64
    [ModuleInitializer]
    internal static void Init()
    {
        string folder = typeof(GameAssemblies)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "Dedicated64")
            .Value;
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string path = Path.Combine(folder, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}

// A plugin with a config of its own in a temporary folder, its backup service,
// and the process environment the plugin reads, restored afterwards
public sealed class Rig : IDisposable
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string[] Variables =
    {
        "QUASAR_DATA_DIR",
        "QUASAR_DS_CONFIG_PATH",
        "HOME",
        "CLUSTER_NODE_ID",
        "CLUSTER_GATEWAY_REGISTRY",
        PluginStorage.SharedRootVariable,
    };

    private readonly Dictionary<string, string> saved = Variables.ToDictionary(
        v => v,
        Environment.GetEnvironmentVariable
    );

    private readonly SdkConfigFile<GridBackupsConfig> configFile;

    public string Folder { get; } =
        Path.Combine(Path.GetTempPath(), "grid-backups-tests-" + Guid.NewGuid().ToString("N"));

    public Plugin Plugin { get; } = new Plugin();
    public GridBackupService Service { get; }
    public GridBackupsConfig Config => Plugin.Config;

    public Rig()
    {
        foreach (string variable in Variables)
            Environment.SetEnvironmentVariable(variable, null);
        Directory.CreateDirectory(Folder);
        configFile = SdkConfigFile<GridBackupsConfig>.Load(
            Plugin.Log,
            Path.Combine(Folder, "GridBackups.cfg")
        );
        typeof(Plugin).GetField("configFile", Any).SetValue(Plugin, configFile);
        typeof(Plugin).GetProperty("PluginDataPath").SetValue(Plugin, Path.Combine(Folder, "data"));
        Service = new GridBackupService(Plugin);
    }

    public void Dispose()
    {
        configFile.Dispose();
        foreach (var pair in saved)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        Directory.Delete(Folder, true);
    }

    public static object Call(Type type, string method, params object[] args) =>
        type.GetMethod(method, Any).Invoke(null, args);

    public object Call(string method, params object[] args) =>
        typeof(GridBackupService).GetMethod(method, Any).Invoke(Service, args);

    public string Sub(params string[] parts) =>
        Path.Combine(new[] { Folder }.Concat(parts).ToArray());
}

public sealed class StorageTests : IDisposable
{
    private readonly Rig rig = new();

    public void Dispose() => rig.Dispose();

    private string QuasarRoot() => (string)Rig.Call(typeof(Plugin), "ResolveQuasarRoot");

    private string ServiceQuasarRoot() => (string)rig.Call("ResolveQuasarConfigRoot");

    [Fact]
    public void QuasarDataDirFirst()
    {
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("q"));
        Environment.SetEnvironmentVariable(
            "QUASAR_DS_CONFIG_PATH",
            rig.Sub("x", "Magnetars", "1", "ds.cfg")
        );
        Assert.Equal(rig.Sub("q"), QuasarRoot());
        Assert.Equal(rig.Sub("q"), ServiceQuasarRoot());
    }

    [Theory]
    [InlineData("q/Magnetars/1/SpaceEngineers-Dedicated.cfg", "q")]
    [InlineData("q/magnetars/1/ds.cfg", "q")]
    [InlineData("Magnetars/q/Magnetars/1/ds.cfg", "Magnetars/q")]
    public void QuasarRootAboveTheMagnetarsFolder(string dsConfig, string root)
    {
        Environment.SetEnvironmentVariable("QUASAR_DS_CONFIG_PATH", rig.Sub(dsConfig));
        Assert.Equal(rig.Sub(root), QuasarRoot());
        Assert.Equal(rig.Sub(root), ServiceQuasarRoot());
    }

    [Fact]
    public void PlatformDefault()
    {
        // GetFolderPath returns "" for a home folder that doesn't exist
        Directory.CreateDirectory(rig.Sub("home"));
        Environment.SetEnvironmentVariable("HOME", rig.Sub("home"));
        // A DS config path without a Magnetars folder doesn't count
        Environment.SetEnvironmentVariable("QUASAR_DS_CONFIG_PATH", rig.Sub("ds", "ds.cfg"));
        string expected = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Quasar"
            )
            : rig.Sub("home", ".config", "Quasar");
        Assert.Equal(expected, QuasarRoot());
        Assert.Equal(expected, ServiceQuasarRoot());
    }

    [Fact]
    public void StandaloneBackupRoot()
    {
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("q"));
        Assert.Equal(rig.Sub("q", "GridBackups"), rig.Service.BackupRoot);
        rig.Config.BackupFolderName = "Other";
        Assert.Equal(rig.Sub("q", "Other"), rig.Service.BackupRoot);
        rig.Config.UseQuasarConfigFolder = false;
        Assert.Equal(rig.Sub("data", "Other"), rig.Service.BackupRoot);
    }

    [Theory]
    [InlineData("..", "GridBackups")]
    [InlineData("a/b", "a_b")]
    [InlineData("  ", "GridBackups")]
    public void BackupFolderNameStaysInTheRoot(string name, string folder)
    {
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("q"));
        rig.Config.BackupFolderName = name;
        Assert.Equal(rig.Sub("q", folder), rig.Service.BackupRoot);
    }

    private const string PluginId = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";

    [Fact]
    public void ClusterBackupRootIsTheSharedStorage()
    {
        PluginCluster.BindOwner(PluginId, typeof(Plugin).Assembly);
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("q"));
        Environment.SetEnvironmentVariable("CLUSTER_NODE_ID", "node-1");
        Environment.SetEnvironmentVariable(PluginStorage.SharedRootVariable, rig.Sub("shared"));
        string expected = rig.Sub(
            "shared",
            "plugins",
            PluginStorage.FolderName(PluginId),
            "GridBackups"
        );
        Assert.Equal(expected, rig.Service.BackupRoot);
        // Whatever the standalone setting
        rig.Config.UseQuasarConfigFolder = false;
        Assert.Equal(expected, rig.Service.BackupRoot);
    }

    [Fact]
    public void ClusterWithoutSharedStorageHasNoBackupRoot()
    {
        PluginCluster.BindOwner(PluginId, typeof(Plugin).Assembly);
        Environment.SetEnvironmentVariable("QUASAR_DATA_DIR", rig.Sub("q"));
        Environment.SetEnvironmentVariable("CLUSTER_NODE_ID", "node-1");
        Assert.Throws<InvalidOperationException>(() => rig.Service.BackupRoot);
        Assert.False(Directory.Exists(rig.Sub("q", "GridBackups")));
    }
}

public sealed class TriggerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Type StateType = typeof(GridBackupService).GetNestedType(
        "GridState",
        BindingFlags.NonPublic
    );

    private readonly Rig rig = new();

    public TriggerTests()
    {
        rig.Config.MinMinutesSinceLastSave = 15;
        rig.Config.MaxMinutesSinceLastSave = 60;
        rig.Config.BackupIfSteady = true;
        rig.Config.SteadySeconds = 300;
        rig.Config.MaxBlockCountChange = 10;
    }

    public void Dispose() => rig.Dispose();

    // A grid backed up `backupMinutesAgo` with 100 blocks (null: never), last
    // changed `changedSecondsAgo`, now at `blocks` blocks
    private string Reason(double? backupMinutesAgo, double changedSecondsAgo, int blocks = 100)
    {
        object state = Activator.CreateInstance(StateType, Now.AddSeconds(-changedSecondsAgo), 100);
        if (backupMinutesAgo.HasValue)
            StateType
                .GetProperty("LastBackupUtc")
                .SetValue(state, Now.AddMinutes(-backupMinutesAgo.Value));
        return rig.Call("GetBackupReason", state, blocks, Now).ToString();
    }

    [Fact]
    public void FirstBackup()
    {
        // With a max time set, a grid never backed up is past it: MaxTime
        rig.Config.MaxMinutesSinceLastSave = 0;
        Assert.Equal("Initial", Reason(null, 0));
    }

    [Fact]
    public void NothingWithinTheMinTime()
    {
        Assert.Equal("None", Reason(10, 3600, blocks: 500));
    }

    [Fact]
    public void MaxTimeFirst()
    {
        rig.Config.MinMinutesSinceLastSave = 120;
        Assert.Equal("MaxTime", Reason(60, 0));
    }

    [Fact]
    public void NoMaxTimeWhenZero()
    {
        rig.Config.MaxMinutesSinceLastSave = 0;
        rig.Config.BackupIfSteady = false;
        Assert.Equal("None", Reason(100000, 0));
    }

    [Fact]
    public void NoMinTimeWhenZero()
    {
        rig.Config.MinMinutesSinceLastSave = 0;
        Assert.Equal("BlockCountChange", Reason(0, 0, blocks: 110));
    }

    [Theory]
    [InlineData(110, "BlockCountChange")]
    [InlineData(90, "BlockCountChange")]
    [InlineData(109, "None")]
    public void BlockCountChange(int blocks, string reason) =>
        Assert.Equal(reason, Reason(20, 0, blocks));

    [Fact]
    public void NoBlockCountTriggerWhenZero()
    {
        rig.Config.MaxBlockCountChange = 0;
        Assert.Equal("None", Reason(20, 0, blocks: 1000));
    }

    [Theory]
    [InlineData(300, "Steady")]
    [InlineData(299, "None")]
    public void Steady(double changedSecondsAgo, string reason) =>
        Assert.Equal(reason, Reason(20, changedSecondsAgo));

    [Fact]
    public void NoSteadyTriggerWhenOff()
    {
        rig.Config.BackupIfSteady = false;
        Assert.Equal("None", Reason(20, 3600));
    }
}

public sealed class FileTests : IDisposable
{
    private readonly Rig rig = new();

    public void Dispose() => rig.Dispose();

    private string Grid => rig.Sub("grid");

    private void File(string name, double hoursAgo)
    {
        string path = Path.Combine(Grid, name);
        System.IO.File.WriteAllText(path, "");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursAgo));
    }

    private string[] Files() =>
        new DirectoryInfo(Grid).GetFiles().Select(f => f.Name).OrderBy(n => n).ToArray();

    [Fact]
    public void RetentionKeepsTheNewest()
    {
        Directory.CreateDirectory(Grid);
        rig.Config.NumberOfBackupSaves = 2;
        rig.Config.NumberOfDailyBackupSaves = 1;
        for (int i = 1; i <= 4; i++)
        {
            File($"2026_01_0{i}_00_00_00.sbc", 10 - i);
            File($"daily_2026_01_0{i}.sbc", 10 - i);
        }
        File("notes.txt", 100);

        rig.Call("CleanUpDirectory", Grid);

        Assert.Equal(
            new[]
            {
                "2026_01_03_00_00_00.sbc",
                "2026_01_04_00_00_00.sbc",
                "daily_2026_01_04.sbc",
                "notes.txt",
            },
            Files()
        );
    }

    [Fact]
    public void NoDailyBackupsKept()
    {
        Directory.CreateDirectory(Grid);
        rig.Config.NumberOfDailyBackupSaves = 0;
        File("daily_2026_01_01.sbc", 1);
        File("2026_01_01_00_00_00.sbc", 1);
        rig.Call("CleanUpDirectory", Grid);
        Assert.Equal(new[] { "2026_01_01_00_00_00.sbc" }, Files());
    }

    [Theory]
    [InlineData("Alpha station_123", "Alpha station", true)]
    [InlineData("Alpha station_123", "alpha*", true)]
    [InlineData("Alpha station_123", "Alpha?station", true)]
    [InlineData("Alpha station_123", "123", true)]
    [InlineData("Alpha station_123", "12", false)]
    [InlineData("Alpha station_123", "Alpha", false)]
    [InlineData("Alpha station_123", "Alpha station_123", true)]
    [InlineData("Odd_name_x__7", "Odd:name/x?", true)]
    [InlineData("Odd_name_x__7", "", false)]
    public void GridNameMatching(string folder, string pattern, bool matches) =>
        Assert.Equal(
            matches,
            (bool)
                Rig.Call(
                    typeof(GridBackupService),
                    "MatchesGrid",
                    new DirectoryInfo(folder),
                    pattern
                )
        );

    [Theory]
    [InlineData("Alpha station", "Alpha station")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("name. ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.txt", "_com1.txt")]
    [InlineData("COM0", "COM0")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("", "Grid")]
    [InlineData("...", "Grid")]
    public void SafeFolderNames(string name, string folder) =>
        Assert.Equal(folder, Rig.Call(typeof(GridBackupService), "SanitizeSegment", name, "Grid"));

    [Fact]
    public void LongNamesCut() =>
        Assert.Equal(
            new string('x', 120),
            Rig.Call(typeof(GridBackupService), "SanitizeSegment", new string('x', 200), "Grid")
        );
}
