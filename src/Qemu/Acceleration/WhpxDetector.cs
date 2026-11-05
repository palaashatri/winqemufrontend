using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinQemu.Qemu.Acceleration;

/// <summary>
/// Distinguishes acceleration state exactly as required by §8:
/// Supported, Enabled, Available, Working, Unavailable, Misconfigured.
/// </summary>
public enum WhpxAccelerationStatus
{
    Working,
    Available,
    Enabled,
    Supported,
    Unavailable,
    Misconfigured
}

public sealed record WhpxProbeReport(
    WhpxAccelerationStatus Status,
    bool IsHostX64OrArm64,
    bool IsWinHvPlatformDllPresent,
    bool IsHypervisorPresentViaApi,
    bool IsQemuWhpxCompiledIn,
    bool DidQemuWhpxSmokeTestPass,
    string UserFriendlyTitle,
    string Explanation,
    bool CanOfferElevatedEnable);

public static partial class WhpxDetector
{
    private const int WHvCapabilityCodeHypervisorPresent = 0x00000000;

    [LibraryImport("WinHvPlatform.dll", SetLastError = true)]
    private static partial int WHvGetCapability(
        int capabilityCode,
        IntPtr capabilityBuffer,
        uint capabilityBufferSizeInBytes,
        out uint writtenSizeInBytes);

    public static async Task<WhpxProbeReport> ProbeAsync(
        string? qemuSystemX86_64Path,
        IReadOnlyList<string>? probedAccelerators = null,
        CancellationToken cancellationToken = default)
    {
        var arch = RuntimeInformation.OSArchitecture;
        var isSupportedHostArch = arch is Architecture.X64 or Architecture.Arm64;

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var dllPath = Path.Combine(system32, "WinHvPlatform.dll");
        var isDllPresent = File.Exists(dllPath);
        var isHypervisorPresent = CheckWinHvHypervisorPresent();

        var isQemuWhpxCompiled = probedAccelerators?.Contains("whpx", StringComparer.OrdinalIgnoreCase) ?? false;
        if (!isQemuWhpxCompiled && !string.IsNullOrWhiteSpace(qemuSystemX86_64Path) && File.Exists(qemuSystemX86_64Path))
        {
            isQemuWhpxCompiled = await CheckQemuSupportsWhpxFlagAsync(qemuSystemX86_64Path, cancellationToken).ConfigureAwait(false);
        }

        if (!isSupportedHostArch)
        {
            return new WhpxProbeReport(
                Status: WhpxAccelerationStatus.Unavailable,
                IsHostX64OrArm64: false,
                IsWinHvPlatformDllPresent: isDllPresent,
                IsHypervisorPresentViaApi: isHypervisorPresent,
                IsQemuWhpxCompiledIn: isQemuWhpxCompiled,
                DidQemuWhpxSmokeTestPass: false,
                UserFriendlyTitle: "Hardware acceleration unavailable",
                Explanation: "Host architecture does not support Windows Hypervisor Platform.",
                CanOfferElevatedEnable: false);
        }

        if (!isHypervisorPresent)
        {
            return new WhpxProbeReport(
                Status: isDllPresent ? WhpxAccelerationStatus.Supported : WhpxAccelerationStatus.Unavailable,
                IsHostX64OrArm64: true,
                IsWinHvPlatformDllPresent: isDllPresent,
                IsHypervisorPresentViaApi: false,
                IsQemuWhpxCompiledIn: isQemuWhpxCompiled,
                DidQemuWhpxSmokeTestPass: false,
                UserFriendlyTitle: "Hardware acceleration unavailable",
                Explanation: "Windows Hypervisor Platform is not enabled. Enable it to run same-architecture virtual machines at near-native speed.",
                CanOfferElevatedEnable: true);
        }

        if (string.IsNullOrWhiteSpace(qemuSystemX86_64Path) || !File.Exists(qemuSystemX86_64Path))
        {
            return new WhpxProbeReport(
                Status: WhpxAccelerationStatus.Enabled,
                IsHostX64OrArm64: true,
                IsWinHvPlatformDllPresent: isDllPresent,
                IsHypervisorPresentViaApi: true,
                IsQemuWhpxCompiledIn: false,
                DidQemuWhpxSmokeTestPass: false,
                UserFriendlyTitle: "Windows Hypervisor Platform enabled",
                Explanation: "Windows Hypervisor Platform is active on Windows, awaiting QEMU runtime configuration.",
                CanOfferElevatedEnable: false);
        }

        if (!isQemuWhpxCompiled)
        {
            return new WhpxProbeReport(
                Status: WhpxAccelerationStatus.Misconfigured,
                IsHostX64OrArm64: true,
                IsWinHvPlatformDllPresent: isDllPresent,
                IsHypervisorPresentViaApi: true,
                IsQemuWhpxCompiledIn: false,
                DidQemuWhpxSmokeTestPass: false,
                UserFriendlyTitle: "QEMU build lacks WHPX support",
                Explanation: "Windows Hypervisor Platform is enabled, but the selected QEMU binary was compiled without '-accel whpx'.",
                CanOfferElevatedEnable: false);
        }

        // Run a real lightweight QEMU WHPX initialization smoke probe (§8)
        var (smokePassed, smokeDetail) = await RunQemuWhpxSmokeTestAsync(qemuSystemX86_64Path, cancellationToken).ConfigureAwait(false);
        if (smokePassed)
        {
            return new WhpxProbeReport(
                Status: WhpxAccelerationStatus.Working,
                IsHostX64OrArm64: true,
                IsWinHvPlatformDllPresent: isDllPresent,
                IsHypervisorPresentViaApi: true,
                IsQemuWhpxCompiledIn: true,
                DidQemuWhpxSmokeTestPass: true,
                UserFriendlyTitle: "Hardware acceleration: ✓ Enabled (WHPX)",
                Explanation: "Windows Hypervisor Platform is active and verified with a live QEMU WHPX initialization probe.",
                CanOfferElevatedEnable: false);
        }

        return new WhpxProbeReport(
            Status: WhpxAccelerationStatus.Available,
            IsHostX64OrArm64: true,
            IsWinHvPlatformDllPresent: isDllPresent,
            IsHypervisorPresentViaApi: true,
            IsQemuWhpxCompiledIn: true,
            DidQemuWhpxSmokeTestPass: false,
            UserFriendlyTitle: "Hardware acceleration partially configured",
            Explanation: $"Windows Hypervisor is active, but the WHPX partition API reported: {smokeDetail}. Enabling the 'Windows Hypervisor Platform' optional feature resolves this.",
            CanOfferElevatedEnable: true);
    }

    public static ProcessStartInfo CreateElevatedEnableWhpxProcessStartInfo()
    {
        // Least privilege by default (§7, §57): only the narrow DISM command requests elevation via runas.
        return new ProcessStartInfo
        {
            FileName = "dism.exe",
            Arguments = "/Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    public static bool CheckWinHvHypervisorPresent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0);
            var hr = WHvGetCapability(WHvCapabilityCodeHypervisorPresent, buffer, 4, out _);
            if (hr == 0)
            {
                return Marshal.ReadByte(buffer) != 0;
            }
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    private static async Task<bool> CheckQemuSupportsWhpxFlagAsync(string qemuPath, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = qemuPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-accel");
            psi.ArgumentList.Add("help");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output.Contains("whpx", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Passed, string Detail)> RunQemuWhpxSmokeTestAsync(
        string qemuPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = qemuPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-accel");
            psi.ArgumentList.Add("whpx");
            psi.ArgumentList.Add("-machine");
            psi.ArgumentList.Add("q35");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("64");
            psi.ArgumentList.Add("-display");
            psi.ArgumentList.Add("none");
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("-no-reboot");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "Could not start QEMU probe process.");
            }

            try
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                delayCts.CancelAfter(TimeSpan.FromMilliseconds(450));
                await proc.WaitForExitAsync(delayCts.Token).ConfigureAwait(false);

                // Process exited within 450ms -> likely failed WHPX initialization
                var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return (false, string.IsNullOrWhiteSpace(stderr) ? $"Exit code {proc.ExitCode}" : stderr.Trim());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Still alive in paused (-S) state after 450ms -> WHPX partition initialized cleanly!
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (true, "WHPX partition initialized successfully.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
