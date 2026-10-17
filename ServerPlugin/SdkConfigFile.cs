using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using PluginSdk.Config;
using PluginSdk.Logging;

namespace ServerPlugin;

public sealed class SdkConfigFile<T> : IDisposable
    where T : PluginConfig, new()
{
    private const int SaveDelayMs = 500;
    private readonly Logger log;
    private readonly string path;
    private Timer saveTimer;

    public T Data { get; }

    private SdkConfigFile(Logger log, string path, T data)
    {
        this.log = log;
        this.path = path;
        Data = data;
        Data.PropertyChanged += OnPropertyChanged;
    }

    public static SdkConfigFile<T> Load(Logger log, string path)
    {
        try
        {
            var data = ConfigStorage.LoadXml<T>(path);
            var file = new SdkConfigFile<T>(log, path, data);
            file.Save();
            return file;
        }
        catch (Exception ex)
        {
            log.Error("Failed to load config, using defaults", ex, new { path });
            KeepBrokenFile(log, path);
            var file = new SdkConfigFile<T>(log, path, new T());
            file.Save();
            return file;
        }
    }

    // Moved aside, so writing the defaults doesn't lose the admin's settings
    private static void KeepBrokenFile(Logger log, string path)
    {
        if (!File.Exists(path))
            return;

        var keptPath = $"{path}.corrupted.{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        try
        {
            File.Move(path, keptPath);
            log.Warning("Moved the broken config aside", new { keptPath });
        }
        catch (Exception ex)
        {
            log.Error("Failed to move the broken config aside", ex, new { path, keptPath });
        }
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e) => SaveLater();

    private void SaveLater()
    {
        if (saveTimer == null)
            saveTimer = new Timer(_ => Save());

        saveTimer.Change(SaveDelayMs, Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            ConfigStorage.SaveXml(Data, path);
        }
        catch (Exception ex)
        {
            log.Error("Failed to save config", ex, new { path });
        }
    }

    public void Dispose()
    {
        Data.PropertyChanged -= OnPropertyChanged;
        saveTimer?.Dispose();
        Save();
    }
}
