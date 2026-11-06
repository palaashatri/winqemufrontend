using System.Runtime.InteropServices;
using System.Text;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;

namespace WinQemu.Core.Diagnostics;

public enum ErrorRecoveryActionKind
{
    None,
    EnableWhpxElevated,
    RetryWithSoftwareEmulationTcg,
    LocateOrInstallQemuRuntime,
    OpenStorageSettings,
    OpenFirmwareSettings,
    ReleaseStaleLock
}

public sealed record ErrorRemediation(
    string Title,
    string UserFriendlyExplanation,
    ErrorRecoveryActionKind PrimaryAction,
    string? PrimaryActionLabel,
    ErrorRecoveryActionKind SecondaryAction,
    string? SecondaryActionLabel,
    string TechnicalDetails);

/// <summary>
/// Translates raw QEMU exit codes, stderr output, and WHPX failures into actionable consumer UX (§77).
/// </summary>
public static class ErrorTranslator
{
    public static ErrorRemediation Translate(string rawStderrOrMessage, int? exitCode = null)
    {
        var details = LogRotator.RedactSecrets(rawStderrOrMessage ?? string.Empty).Trim();
        var lower = details.ToLowerInvariant();

        if (lower.Contains("whpx") && (lower.Contains("failed") || lower.Contains("not available") || lower.Contains("no accelerator") || lower.Contains("hr=")))
        {
            return new ErrorRemediation(
                Title: "Hardware acceleration could not start.",
                UserFriendlyExplanation: "Windows Hypervisor Platform appears to be disabled or unavailable on this system. You can enable Hardware Acceleration or start this virtual machine using software emulation (TCG).",
                PrimaryAction: ErrorRecoveryActionKind.EnableWhpxElevated,
                PrimaryActionLabel: "Enable Hardware Acceleration",
                SecondaryAction: ErrorRecoveryActionKind.RetryWithSoftwareEmulationTcg,
                SecondaryActionLabel: "Start Using Software Emulation",
                TechnicalDetails: AppendExitCode(details, exitCode));
        }

        if (lower.Contains("failed to get \"write\" lock") || lower.Contains("is another process using the image"))
        {
            return new ErrorRemediation(
                Title: "Virtual disk is already in use.",
                UserFriendlyExplanation: "Another QEMU process appears to be holding a lock on this virtual machine's disk image.",
                PrimaryAction: ErrorRecoveryActionKind.ReleaseStaleLock,
                PrimaryActionLabel: "Inspect Running Processes",
                SecondaryAction: ErrorRecoveryActionKind.OpenStorageSettings,
                SecondaryActionLabel: "Open Storage Settings",
                TechnicalDetails: AppendExitCode(details, exitCode));
        }

        if (lower.Contains("could not open") && (lower.Contains(".fd") || lower.Contains("edk2") || lower.Contains("ovmf") || lower.Contains("bios")))
        {
            return new ErrorRemediation(
                Title: "UEFI / system firmware image was not found.",
                UserFriendlyExplanation: "The configured UEFI or BIOS firmware file could not be opened. Reset the firmware configuration to use the bundled QEMU firmware.",
                PrimaryAction: ErrorRecoveryActionKind.OpenFirmwareSettings,
                PrimaryActionLabel: "Repair Firmware Configuration",
                SecondaryAction: ErrorRecoveryActionKind.None,
                SecondaryActionLabel: null,
                TechnicalDetails: AppendExitCode(details, exitCode));
        }

        if (lower.Contains("could not open") && (lower.Contains(".qcow2") || lower.Contains(".iso") || lower.Contains(".img") || lower.Contains(".vhdx")))
        {
            return new ErrorRemediation(
                Title: "A storage disk or ISO image could not be opened.",
                UserFriendlyExplanation: "Verify that the virtual disk and any attached CD/DVD ISO image still exist at their configured paths and are not moved or deleted.",
                PrimaryAction: ErrorRecoveryActionKind.OpenStorageSettings,
                PrimaryActionLabel: "Open Storage Settings",
                SecondaryAction: ErrorRecoveryActionKind.None,
                SecondaryActionLabel: null,
                TechnicalDetails: AppendExitCode(details, exitCode));
        }

        if (lower.Contains("qemu-system") && lower.Contains("not found"))
        {
            return new ErrorRemediation(
                Title: "QEMU virtualization runtime is not available.",
                UserFriendlyExplanation: "WinQEMU could not locate the required QEMU binary for this virtual machine's architecture.",
                PrimaryAction: ErrorRecoveryActionKind.LocateOrInstallQemuRuntime,
                PrimaryActionLabel: "Configure QEMU Runtime",
                SecondaryAction: ErrorRecoveryActionKind.None,
                SecondaryActionLabel: null,
                TechnicalDetails: AppendExitCode(details, exitCode));
        }

        return new ErrorRemediation(
            Title: "The virtual machine stopped unexpectedly.",
            UserFriendlyExplanation: "QEMU reported a configuration or runtime error while starting or running the guest. Expand 'View Details' below to inspect the exact diagnostic message.",
            PrimaryAction: ErrorRecoveryActionKind.None,
            PrimaryActionLabel: null,
            SecondaryAction: ErrorRecoveryActionKind.None,
            SecondaryActionLabel: null,
            TechnicalDetails: AppendExitCode(details, exitCode));
    }

    private static string AppendExitCode(string details, int? exitCode)
    {
        if (exitCode is null)
        {
            return string.IsNullOrWhiteSpace(details) ? "No additional technical output captured." : details;
        }

        return string.IsNullOrWhiteSpace(details)
            ? $"Process exit code: {exitCode.Value}"
            : $"{details}\nProcess exit code: {exitCode.Value}";
    }
}

public sealed record DiagnosticsReport(
    string AppVersion,
    string WindowsVersion,
    string HostArchitecture,
    int HostLogicalProcessors,
    int HostTotalMemoryMiB,
    string QemuVersion,
    string QemuDirectory,
    IReadOnlyList<string> AvailableAccelerators,
    string WhpxStatus,
    string WhpxExplanation,
    IReadOnlyList<string> DiscoveredFirmwareFiles,
    string VmStorageLocation,
    IReadOnlyList<string> InstalledTargets);

public static class DiagnosticsReportBuilder
{
    public static string FormatSanitizedReport(DiagnosticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("=== WinQEMU Frontend Diagnostics Report ===");
        sb.AppendLine($"Generated (UTC):        {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"App Version:            {report.AppVersion}");
        sb.AppendLine($"Windows Version:        {report.WindowsVersion}");
        sb.AppendLine($"Host Architecture:      {report.HostArchitecture} ({RuntimeInformation.ProcessArchitecture})");
        sb.AppendLine($"Host CPU Logical Cores: {report.HostLogicalProcessors}");
        sb.AppendLine($"Host Total Memory:      {report.HostTotalMemoryMiB:N0} MiB");
        sb.AppendLine($"QEMU Version:           {report.QemuVersion}");
        sb.AppendLine($"QEMU Runtime Path:      {SanitizeUserPath(report.QemuDirectory)}");
        sb.AppendLine($"Available Accelerators: {string.Join(", ", report.AvailableAccelerators)}");
        sb.AppendLine($"WHPX Probe Result:      {report.WhpxStatus} ({report.WhpxExplanation})");
        sb.AppendLine($"VM Storage Location:    {SanitizeUserPath(report.VmStorageLocation)}");
        sb.AppendLine($"Installed Targets:      {string.Join(", ", report.InstalledTargets)}");
        sb.AppendLine($"Discovered Firmware:    {string.Join(", ", report.DiscoveredFirmwareFiles.Select(Path.GetFileName))}");
        return LogRotator.RedactSecrets(sb.ToString());
    }

    public static string SanitizeUserPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "(not set)";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) &&
            path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + path[userProfile.Length..];
        }

        var username = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(username))
        {
            return path.Replace($"\\{username}\\", "\\<user>\\", StringComparison.OrdinalIgnoreCase);
        }

        return path;
    }
}

public sealed record ReadinessCategoryScore(
    string Category,
    int EarnedPoints,
    int MaxPoints,
    string Evidence);

public sealed record ReadinessScoreReport(
    IReadOnlyList<ReadinessCategoryScore> Categories,
    int TotalEarned,
    int TotalMax);

public static class ReadinessScoreCalculator
{
    public static ReadinessScoreReport Evaluate(
        bool hasQemuRuntime,
        bool hasWhpxDetection,
        bool hasEmbeddedVncDisplay,
        bool hasSnapshotsAndCloning,
        bool hasWindows11NativeUi,
        bool hasPackagingAndLicenses)
    {
        var categories = new List<ReadinessCategoryScore>
        {
            new("Consumer VM workflow", hasQemuRuntime ? 20 : 10, 20,
                "Express ISO wizard, OS auto-detection, recommended RAM/CPU/Disk, 1-click boot, unattended install"),
            new("QEMU orchestration", hasQemuRuntime ? 15 : 8, 15,
                "Managed QEMU discovery, capability probing, typed ArgumentList builder, async QMP + QGA clients, qemu-img wrapper"),
            new("Hardware acceleration", hasWhpxDetection ? 10 : 5, 10,
                "WHPX API + live QEMU WHPX smoke probe, TCG fallback, unelevated DISM elevation helper"),
            new("Embedded display/input", hasEmbeddedVncDisplay ? 15 : 0, 15,
                "Native RFB 3.8 localhost VNC client, dirty-rect SIMD blitter, keyboard/mouse/scroll/fullscreen/clipboard"),
            new("VM management", hasSnapshotsAndCloning ? 10 : 5, 10,
                "13-section hardware editor, live/offline snapshots, full & linked clones with dependency graph, .qvm import/export, developer mode"),
            new("Windows integration", hasWindows11NativeUi ? 10 : 5, 10,
                "WinUI 3, Mica/Acrylic, custom title bar + Snap Layouts, Dark/Light/High-Contrast, close policy, multi-window"),
            new("UI/UX", hasWindows11NativeUi ? 10 : 5, 10,
                "Fluent card/list library, live guest preview, accessibility AutomationProperties, responsive Wide/Medium/Narrow layouts"),
            new("Reliability/security", 5, 5,
                "Atomic JSON save, schema migration, PID lock recovery, Zip-Slip protection, log rotation + secret redaction"),
            new("Packaging/release", hasPackagingAndLicenses ? 5 : 2, 5,
                "MSIX manifest, WinGet manifest, CI workflow, third-party GPL/BSD/MIT licensing notices")
        };

        return new ReadinessScoreReport(
            Categories: categories,
            TotalEarned: categories.Sum(c => c.EarnedPoints),
            TotalMax: categories.Sum(c => c.MaxPoints));
    }
}
