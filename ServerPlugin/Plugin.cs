using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PluginSdk.Commands;
using PluginSdk.Config;
using PluginSdk.Logging;
using VRage.Game;
using VRage.Plugins;

// Define assembly version when compiled by Magnetar
#if !LOCAL_BUILD
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ServerPlugin;

// ReSharper disable once UnusedType.Global
public sealed class Plugin : IPlugin
{
    public const string Name = "GridBackups";
    private const string ConfigFileName = Name + ".cfg";
    private static bool failed;

    public static Plugin Instance { get; private set; }

    public Logger Log { get; } = Logger.Create(Name);
    public GridBackupsConfig Config => configFile?.Data;
    public GridBackupService Backups { get; private set; }
    public string PluginDataPath { get; private set; }

    private SdkConfigFile<GridBackupsConfig> configFile;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
#if DEBUG
        Thread.Sleep(100);
#endif

        Instance = this;
        Log.Info("Loading");

        try
        {
            PluginDataPath = ResolvePluginDataPath();
            configFile = SdkConfigFile<GridBackupsConfig>.Load(Log, Path.Combine(PluginDataPath, ConfigFileName));
            Backups = new GridBackupService(this);
            ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(GridBackupsCommands));

            var schemaJson = ConfigStorage.SaveJson(Config);
            File.WriteAllText(Path.Combine(PluginDataPath, Name + ".schema.json"), schemaJson);

            Log.Info("Loaded", new { PluginDataPath, Backups.BackupRoot });
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Critical("Failed to load", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            configFile?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Critical("Dispose failed", ex);
        }
        finally
        {
            Instance = null;
        }
    }

    public void Update()
    {
        if (failed)
            return;

        try
        {
            Backups?.Update();
        }
        catch (Exception ex)
        {
            failed = true;
            Log.Critical("Update failed", ex);
        }
    }

    public bool BackupBeforeConcealment(List<MyObjectBuilder_CubeGrid> grids, long ownerIdentity)
        => Backups?.BackupBeforeConcealment(grids, ownerIdentity) ?? false;

    public bool BackupGridsManuallyWithBuilders(List<MyObjectBuilder_CubeGrid> grids, long ownerIdentity)
        => Backups?.BackupExternalBuilders(grids, ownerIdentity, respectMinDelay: false) ?? false;

    private static string ResolvePluginDataPath()
    {
        var root = ResolveQuasarRoot();
        return Create(Path.Combine(root, Name));
    }

    private static string ResolveQuasarRoot()
    {
        var quasarRoot = Environment.GetEnvironmentVariable("QUASAR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(quasarRoot))
            return quasarRoot;

        var dsConfig = Environment.GetEnvironmentVariable("QUASAR_DS_CONFIG_PATH");
        var derived = TryGetQuasarRootFromPath(dsConfig);
        if (derived != null)
            return derived;

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quasar");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Quasar");
    }

    private static string Create(string path)
    {
        Directory.CreateDirectory(path);
        return path;
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
}
