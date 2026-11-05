using System.Text;
using System.Text.Json;
using WinQemu.Core.Models;

namespace WinQemu.Core.Configuration;

public sealed class AppSettingsStore
{
    private readonly string _settingsFilePath;

    public AppSettingsStore(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? AppSettings.GetDefaultSettingsFilePath();
    }

    public string SettingsFilePath => _settingsFilePath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            var defaults = new AppSettings();
            EnsureDirectoriesExist(defaults);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, VmConfigurationStore.JsonOptions) ?? new AppSettings();
            EnsureDirectoriesExist(settings);
            return settings;
        }
        catch
        {
            var fallback = new AppSettings();
            EnsureDirectoriesExist(fallback);
            return fallback;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureDirectoriesExist(settings);

        var dir = Path.GetDirectoryName(Path.GetFullPath(_settingsFilePath))!;
        Directory.CreateDirectory(dir);

        var tempPath = $"{_settingsFilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(settings, VmConfigurationStore.JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, _settingsFilePath, overwrite: true);
    }

    private static void EnsureDirectoriesExist(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.VmStorageDirectory))
        {
            try { Directory.CreateDirectory(settings.VmStorageDirectory); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(settings.IsoDownloadCacheDirectory))
        {
            try { Directory.CreateDirectory(settings.IsoDownloadCacheDirectory); } catch { }
        }
    }
}
