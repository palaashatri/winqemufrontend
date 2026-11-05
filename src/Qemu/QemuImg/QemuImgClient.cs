using System.Diagnostics;
using System.Text.Json.Nodes;

namespace WinQemu.Qemu.QemuImg;

public sealed record DiskImageInspection(
    string FilePath,
    string Format,
    long VirtualSizeBytes,
    long ActualDiskUsageBytes,
    string? BackingFilePath,
    bool IsClean,
    IReadOnlyList<QemuImgSnapshotEntry> Snapshots);

public sealed record QemuImgSnapshotEntry(
    string Id,
    string Name,
    long VmStateSizeBytes,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Typed wrapper around qemu-img for disk creation, inspection, resizing, conversion,
/// consistency checks, cloning, and offline snapshot management (§20, §44, §45).
/// </summary>
public sealed class QemuImgClient
{
    private readonly string _qemuImgPath;

    public QemuImgClient(string qemuImgPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qemuImgPath);
        _qemuImgPath = qemuImgPath;
    }

    public string ExecutablePath => _qemuImgPath;

    public async Task CreateDiskAsync(
        string filePath,
        long sizeBytes,
        string format = "qcow2",
        string? backingFilePath = null,
        string? backingFormat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (sizeBytes <= 0 && string.IsNullOrWhiteSpace(backingFilePath))
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Disk size must be positive.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

        var args = new List<string> { "create", "-f", format.ToLowerInvariant() };
        if (!string.IsNullOrWhiteSpace(backingFilePath))
        {
            args.Add("-b");
            args.Add(Path.GetFullPath(backingFilePath));
            args.Add("-F");
            args.Add(string.IsNullOrWhiteSpace(backingFormat) ? "qcow2" : backingFormat.ToLowerInvariant());
        }

        args.Add(Path.GetFullPath(filePath));
        if (string.IsNullOrWhiteSpace(backingFilePath) || sizeBytes > 0)
        {
            args.Add(sizeBytes.ToString());
        }

        await RunQemuImgAsync(args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiskImageInspection> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Disk image not found: {filePath}", filePath);
        }

        var output = await RunQemuImgAsync(
            ["info", "--output=json", Path.GetFullPath(filePath)],
            cancellationToken).ConfigureAwait(false);

        var obj = JsonNode.Parse(output) as JsonObject
            ?? throw new InvalidDataException("qemu-img returned invalid JSON output.");

        var format = obj["format"]?.GetValue<string>()?.ToUpperInvariant() ?? "QCOW2";
        var virtualBytes = obj["virtual-size"]?.GetValue<long>() ?? 0L;
        var actualBytes = obj["actual-size"]?.GetValue<long>() ?? new FileInfo(filePath).Length;
        var backingFile = obj["backing-filename"]?.GetValue<string>();
        var dirtyFlag = obj["dirty-flag"]?.GetValue<bool>() ?? false;

        var snapshots = new List<QemuImgSnapshotEntry>();
        if (obj["snapshots"] is JsonArray snapArr)
        {
            foreach (var snap in snapArr.OfType<JsonObject>())
            {
                var id = snap["id"]?.GetValue<string>() ?? "1";
                var name = snap["name"]?.GetValue<string>() ?? id;
                var vmStateSize = snap["vm-state-size"]?.GetValue<long>() ?? 0L;
                var dateSec = snap["date-sec"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                snapshots.Add(new QemuImgSnapshotEntry(
                    id,
                    name,
                    vmStateSize,
                    DateTimeOffset.FromUnixTimeSeconds(dateSec)));
            }
        }

        return new DiskImageInspection(
            FilePath: Path.GetFullPath(filePath),
            Format: format,
            VirtualSizeBytes: virtualBytes,
            ActualDiskUsageBytes: actualBytes,
            BackingFilePath: backingFile,
            IsClean: !dirtyFlag,
            Snapshots: snapshots);
    }

    public async Task ResizeAsync(
        string filePath,
        long newSizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var current = await InspectAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (newSizeBytes < current.VirtualSizeBytes)
        {
            throw new InvalidOperationException("Shrinking a virtual disk in-place can cause data loss and is blocked.");
        }

        await RunQemuImgAsync(
            ["resize", Path.GetFullPath(filePath), newSizeBytes.ToString()],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ConvertAsync(
        string sourcePath,
        string destinationPath,
        string destinationFormat = "qcow2",
        bool compress = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        var args = new List<string> { "convert" };
        if (compress && string.Equals(destinationFormat, "qcow2", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-c");
        }
        args.Add("-O");
        args.Add(destinationFormat.ToLowerInvariant());
        args.Add(Path.GetFullPath(sourcePath));
        args.Add(Path.GetFullPath(destinationPath));

        await RunQemuImgAsync(args, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool IsHealthy, string Summary)> CheckAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            var output = await RunQemuImgAsync(["check", Path.GetFullPath(filePath)], cancellationToken)
                .ConfigureAwait(false);
            return (true, string.IsNullOrWhiteSpace(output) ? "No errors were found on the image." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task CreateSnapshotAsync(string filePath, string snapshotTag, CancellationToken cancellationToken = default) =>
        RunQemuImgAsync(["snapshot", "-c", snapshotTag, Path.GetFullPath(filePath)], cancellationToken);

    public Task ApplySnapshotAsync(string filePath, string snapshotTag, CancellationToken cancellationToken = default) =>
        RunQemuImgAsync(["snapshot", "-a", snapshotTag, Path.GetFullPath(filePath)], cancellationToken);

    public Task DeleteSnapshotAsync(string filePath, string snapshotTag, CancellationToken cancellationToken = default) =>
        RunQemuImgAsync(["snapshot", "-d", snapshotTag, Path.GetFullPath(filePath)], cancellationToken);

    private async Task<string> RunQemuImgAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!File.Exists(_qemuImgPath))
        {
            throw new FileNotFoundException($"qemu-img executable not found at '{_qemuImgPath}'.", _qemuImgPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _qemuImgPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start qemu-img process.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"qemu-img exited with code {proc.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        }

        return stdout;
    }
}
