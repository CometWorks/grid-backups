using PluginSdk.Config;

namespace ServerPlugin;

[Tab("general", caption: "General")]
[Tab("policy", caption: "Backup Policy")]
[Tab("retention", caption: "Retention")]
[Section("storage", parent: "general", caption: "Storage")]
[Section("ownership", parent: "general", caption: "Ownership")]
[Section("timing", parent: "policy", caption: "Timing")]
[Section("triggers", parent: "policy", caption: "Triggers")]
[Section("export", parent: "policy", caption: "Export")]
public class GridBackupsConfig : PluginConfig
{
    [BoolOption("Enable automatic grid backups", Parent = "storage")]
    public bool Enabled { get; set => SetField(ref field, value); } = true;

    [BoolOption("For standalone servers, store backups under the Quasar config folder. Clusters always use PluginSdk shared storage.", Parent = "storage")]
    public bool UseQuasarConfigFolder { get; set => SetField(ref field, value); } = true;

    [StringOption(maxLength: 128, pattern: @"^[A-Za-z0-9_. -]+$", description: "Folder name below the selected storage root", Parent = "storage")]
    public string BackupFolderName { get; set => SetField(ref field, value); } = "GridBackups";

    [BoolOption("Prefix owner folder names with player names", Parent = "ownership")]
    public bool PlayerNameOnFolders { get; set => SetField(ref field, value); }

    [BoolOption("Use Steam id instead of identity id in owner folder names when known", Parent = "ownership")]
    public bool UseSteamId { get; set => SetField(ref field, value); }

    [BoolOption("Backup grids without a player owner", Parent = "ownership")]
    public bool BackupNobodyGrids { get; set => SetField(ref field, value); }

    [BoolOption("Backup NPC-owned grids", Parent = "ownership")]
    public bool BackupNpcGrids { get; set => SetField(ref field, value); }

    [IntOption(1, 86400, "How often to scan grids for backup conditions", Parent = "timing")]
    public int ScanIntervalSeconds { get; set => SetField(ref field, value); } = 60;

    [IntOption(0, 10080, "Minimum minutes since the last backup before another automatic backup is allowed. 0 disables the minimum.", Parent = "timing")]
    public int MinMinutesSinceLastSave { get; set => SetField(ref field, value); } = 15;

    [IntOption(0, 43200, "Maximum minutes since the last backup before a backup is forced. 0 disables the maximum.", Parent = "timing")]
    public int MaxMinutesSinceLastSave { get; set => SetField(ref field, value); } = 1440;

    [BoolOption("Allow external concealment plugins to call the public before-concealment backup entrypoint", Parent = "triggers")]
    public bool BackupBeforeConcealment { get; set => SetField(ref field, value); } = true;

    [BoolOption("Backup grids after they stay unchanged for the configured steady time", Parent = "triggers")]
    public bool BackupIfSteady { get; set => SetField(ref field, value); } = true;

    [IntOption(0, 86400, "Seconds a grid must remain at the same block count before steady backup triggers. 0 means immediate.", Parent = "triggers")]
    public int SteadySeconds { get; set => SetField(ref field, value); } = 300;

    [IntOption(0, 1000000, "Backup after block count changes by at least this many blocks since last backup. 0 disables this trigger.", Parent = "triggers")]
    public int MaxBlockCountChange { get; set => SetField(ref field, value); } = 250;

    [BoolOption("Include mechanically connected subgrids in one backup", Parent = "export")]
    public bool BackupConnectedGrids { get; set => SetField(ref field, value); } = true;

    [BoolOption("Keep original owners in exported object builders", Parent = "export")]
    public bool KeepOriginalOwner { get; set => SetField(ref field, value); } = true;

    [IntOption(0, 1000000, "Minimum total block count required for a backup", Parent = "export")]
    public int MinBlocksForBackup { get; set => SetField(ref field, value); } = 20;

    [IntOption(1, 1000, "Normal backup files to keep per grid", Parent = "retention")]
    public int NumberOfBackupSaves { get; set => SetField(ref field, value); } = 5;

    [IntOption(0, 1000, "Daily backup files to keep per grid. 0 disables daily backups.", Parent = "retention")]
    public int NumberOfDailyBackupSaves { get; set => SetField(ref field, value); }

    [IntOption(0, 3650, "Delete backup files older than this many days after a backup cycle. 0 disables age cleanup.", Parent = "retention")]
    public int DeleteBackupsOlderThanDays { get; set => SetField(ref field, value); } = 10;
}
