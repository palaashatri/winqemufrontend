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

public sealed class VmExclusiveLockHandle : IAsyncDisposable, IDisposable
{
    private FileStream? _stream;
    private readonly string _vmDirectory;
    private bool _deleteOnDispose = true;

    internal VmExclusiveLockHandle(string vmDirectory, FileStream stream)
    {
        _vmDirectory = vmDirectory;
        _stream = stream;
    }

    public string VmDirectory => _vmDirectory;

    /// <summary>
    /// When set to false (e.g., when closing the app with 'Keep VMs running'),
    /// disposing the handle releases the FileStream without deleting vm.lock.json.
    /// </summary>
    public bool DeleteFileOnDispose
    {
        get => _deleteOnDispose;
        set => _deleteOnDispose = value;
    }

    public async Task WriteRecordAsync(VmProcessLockRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_stream is null)
        {
            throw new ObjectDisposedException(nameof(VmExclusiveLockHandle));
        }

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, VmConfigurationStore.JsonOptions));
        _stream.Position = 0;
        _stream.SetLength(0);
        await _stream.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        var s = Interlocked.Exchange(ref _stream, null);
        if (s is not null)
        {
            try { s.Dispose(); } catch { }
            if (_deleteOnDispose)
            {
                VmLockManager.ReleaseLockFile(_vmDirectory);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class VmLockManager
{
    /// <summary>
    /// Acquires and retains an exclusive write lock on vm.lock.json before QEMU starts (§58),
    /// preventing concurrent app instances from starting the same VM simultaneously.
    /// </summary>
    public static async Task<VmExclusiveLockHandle> AcquireExclusiveLockAsync(
        string vmDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectory);
        VmDirectoryLayout.EnsureStructure(vmDirectory);

        var activeLock = await TryReadActiveLockAsync(vmDirectory, cancellationToken).ConfigureAwait(false);
        if (activeLock is not null)
        {
            throw new InvalidOperationException(
                $"VM at '{vmDirectory}' is already locked by running QEMU process PID {activeLock.ProcessId} (QMP port {activeLock.QmpPort}).");
        }

        var lockPath = VmDirectoryLayout.GetLockPath(vmDirectory);
        try
        {
            // FileShare.Read allows TryReadActiveLockAsync to inspect the file while preventing concurrent writers.
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            return new VmExclusiveLockHandle(vmDirectory, stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"VM at '{vmDirectory}' is currently locked by another WinQEMU session.", ex);
        }
    }

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
            await using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

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

            if (record.ProcessStartTimeUtc != default &&
                Math.Abs((process.StartTime.ToUniversalTime() - record.ProcessStartTimeUtc.UtcDateTime).TotalSeconds) > 2)
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
