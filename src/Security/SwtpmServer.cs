using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WinQemu.Security;

/// <summary>
/// Manages per-VM TPM 2.0 state in `security/tpm/` (§24).
/// Uses an external `swtpm` binary if installed, or provides a clean per-VM managed
/// TPM state lifecycle without global shared state or terminal flashes.
/// </summary>
public sealed class SwtpmManager : IAsyncDisposable
{
    private Process? _externalSwtpmProcess;
    private readonly string _tpmStateDirectory;

    public SwtpmManager(string tpmStateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tpmStateDirectory);
        _tpmStateDirectory = Path.GetFullPath(tpmStateDirectory);
        Directory.CreateDirectory(_tpmStateDirectory);
    }

    public string TpmStateDirectory => _tpmStateDirectory;
    public string ControlSocketPath => Path.Combine(_tpmStateDirectory, "swtpm-ctrl.sock");
    public bool IsExternalSwtpmRunning => _externalSwtpmProcess is { HasExited: false };

    public static string? LocateExternalSwtpmBinary(string? qemuDirectory = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(qemuDirectory))
        {
            candidates.Add(Path.Combine(qemuDirectory, "swtpm.exe"));
        }
        candidates.Add(@"C:\Program Files\qemu\swtpm.exe");
        candidates.Add(@"C:\msys64\ucrt64\bin\swtpm.exe");

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<int?> StartIfAvailableAsync(
        int preferredPort,
        string? qemuDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // swtpm creates and owns tpm2-00.permall; the directory only needs to exist (§24).
        Directory.CreateDirectory(_tpmStateDirectory);

        var swtpmExe = LocateExternalSwtpmBinary(qemuDirectory);
        if (string.IsNullOrWhiteSpace(swtpmExe))
        {
            // Per Rule #1 (§87): Never claim a live TPM emulator socket unless swtpm is actually running.
            return null;
        }

        if (File.Exists(ControlSocketPath))
        {
            try { File.Delete(ControlSocketPath); } catch { }
        }

        var psi = new ProcessStartInfo
        {
            FileName = swtpmExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("socket");
        psi.ArgumentList.Add("--tpm2");
        psi.ArgumentList.Add("--tpmstate");
        psi.ArgumentList.Add($"dir={_tpmStateDirectory}");
        psi.ArgumentList.Add("--ctrl");
        psi.ArgumentList.Add($"type=unixio,path={ControlSocketPath}");
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add($"type=tcp,port={preferredPort},bindaddr=127.0.0.1");
        psi.ArgumentList.Add("--flags");
        psi.ArgumentList.Add("not-need-init");

        _externalSwtpmProcess = Process.Start(psi);
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        return _externalSwtpmProcess is { HasExited: false } ? preferredPort : null;
    }

    public ValueTask DisposeAsync()
    {
        if (_externalSwtpmProcess is not null)
        {
            try
            {
                if (!_externalSwtpmProcess.HasExited)
                {
                    _externalSwtpmProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            _externalSwtpmProcess.Dispose();
            _externalSwtpmProcess = null;
        }

        return ValueTask.CompletedTask;
    }
}

public static class SecurityValidator
{
    public static string ValidateSafeChildPath(string baseDirectory, string relativeOrChildPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrChildPath);

        var root = Path.GetFullPath(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                   + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(baseDirectory, relativeOrChildPath));

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path traversal blocked: '{relativeOrChildPath}' escapes '{baseDirectory}'.");
        }

        return combined;
    }

    public static bool IsReparsePointOrSymlink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        var attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<bool> VerifySha256Async(
        string filePath,
        string expectedHexHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHexHash);
        var actual = await ComputeSha256HexAsync(filePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHexHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
