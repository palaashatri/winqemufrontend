using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinQemu.Core.Configuration;

namespace WinQemu.Core.Lifecycle;

public sealed class VmProcessLockRecord
{
    public string VmId { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public DateTimeOffset ProcessStartTimeUtc { get; set; }
    public int QmpPort { get; set; }
    public int? VncPort { get; set; }
    public int? SerialPort { get; set; }
    public string ActiveAccelerator { get; set; } = "tcg";
    public DateTimeOffset AcquiredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class VmLockManager
{
    public static async Task WriteLockFileAsync(
        string vmDirectory,
        VmProcessLockRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectory);
        ArgumentNullException.ThrowIfNull(record);

        VmDirectoryLayout.EnsureStructure(vmDirectory);
        var lockPath = VmDirectoryLayout.GetLockPath(vmDirectory);
        var tempPath = $"{lockPath}.{Guid.NewGuid():N}.tmp";

        var json = JsonSerializer.Serialize(record, VmConfigurationStore.JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, lockPath, overwrite: true);
    }

    public static async Task<VmProcessLockRecord?> TryReadActiveLockAsync(
        string vmDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vmDirectory))
        {
            return null;
        }

        var lockPath = VmDirectoryLayout.GetLockPath(vmDirectory);
        if (!File.Exists(lockPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(lockPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var record = JsonSerializer.Deserialize<VmProcessLockRecord>(json, VmConfigurationStore.JsonOptions);
            if (record is null || record.ProcessId <= 0)
            {
                ReleaseLockFile(vmDirectory);
                return null;
            }

            if (IsMatchingQemuProcessAlive(record))
            {
                return record;
            }

            // Stale lock cleanup (§58)
            ReleaseLockFile(vmDirectory);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static void ReleaseLockFile(string? vmDirectory)
    {
        if (string.IsNullOrWhiteSpace(vmDirectory))
        {
            return;
        }

        var lockPath = VmDirectoryLayout.GetLockPath(vmDirectory);
        try
        {
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }
        }
        catch
        {
        }
    }

    public static bool IsMatchingQemuProcessAlive(VmProcessLockRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
            {
                return false;
            }

            var processName = process.ProcessName;
            if (!processName.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
