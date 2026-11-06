using System.Text.Json.Serialization;

namespace WinQemu.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppThemePreference
{
    System,
    Light,
    Dark
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunningVmClosePolicy
{
    AskEveryTime,
    KeepRunning,
    GracefulShutdown,
    ForceStop
}

public sealed class AppSettings
{
    public const int CurrentSettingsVersion = 1;

    public int Version { get; set; } = CurrentSettingsVersion;

    // General (§48)
    public string VmStorageDirectory { get; set; } = GetDefaultVmStorageDirectory();
    public string IsoDownloadCacheDirectory { get; set; } = GetDefaultIsoCacheDirectory();
    public bool ReattachRunningVmsOnLaunch { get; set; } = true;
    public RunningVmClosePolicy CloseRunningVmPolicy { get; set; } = RunningVmClosePolicy.AskEveryTime;
    public bool RememberClosePolicy { get; set; }
    public bool CheckForUpdatesAutomatically { get; set; } = true;

    // Appearance (§48)
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;
    public bool PreferGridView { get; set; } = true;

    // Virtualization (§48)
    public string? CustomQemuDirectory { get; set; }
    public string ManagedQemuDirectory { get; set; } = GetDefaultManagedQemuDirectory();
    public bool PreferWhpxWhenAvailable { get; set; } = true;
    public bool WarnWhenFallingBackToTcg { get; set; } = true;

    // Display (§48)
    public VmDisplayScalingMode DefaultScalingMode { get; set; } = VmDisplayScalingMode.FitAspectRatio;
    public bool AutoRevealToolbarInFullscreen { get; set; } = true;
    public string FullscreenShortcut { get; set; } = "Ctrl+Alt+F";

    // Input (§48)
    public string HostKeyShortcut { get; set; } = "Ctrl+Alt";
    public bool AutoCapturePointerOnClick { get; set; } = true;

    // Integration (§48)
    public bool EnableClipboardSyncByDefault { get; set; } = true;
    public bool EnableGuestAgentByDefault { get; set; } = true;

    // Advanced (§48)
    public bool EnableVerboseLogging { get; set; }
    public int MaxLogFileSizeMiB { get; set; } = 10;
    public int MaxRotatedLogFiles { get; set; } = 5;
    public bool EnableDeveloperToolsGlobally { get; set; }

    public static string GetDefaultVmStorageDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Virtual Machines");
    }

    public static string GetDefaultIsoCacheDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "WinQemu", "Downloads");
    }

    public static string GetDefaultManagedQemuDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "WinQemu", "Runtime", "qemu");
    }

    public static string GetDefaultSettingsFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "WinQemu", "settings.json");
    }
}
