using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace WinQemu.Qemu.Runtime;

public sealed record QemuRuntimeReleaseDescriptor(
    string Version,
    string DownloadUrl,
    string ExpectedSha256Hex,
    string ReleaseDate);

/// <summary>
/// Manages downloading, cryptographically verifying (§65), and extracting the managed
/// QEMU runtime without replacing binaries while VMs are running.
/// Uses multi-stream HTTP range downloading for high throughput.
/// </summary>
public sealed class QemuRuntimeInstaller
{
    public static readonly QemuRuntimeReleaseDescriptor DefaultRelease = new(
        Version: "11.1.0 (20260811)",
        DownloadUrl: "https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe",
        ExpectedSha256Hex: "f98a8aeb5f7faea9765b6dee28316c266cd179d80354a2fed8e50176f9a2e59f",
        ReleaseDate: "2026-08-11");

    public async Task<QemuRuntimeInfo> DownloadVerifyAndExtractAsync(
        string targetDirectory,
        Func<bool> areAnyVmsRunning,
        QemuRuntimeReleaseDescriptor? release = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentNullException.ThrowIfNull(areAnyVmsRunning);

        if (areAnyVmsRunning())
        {
            throw new InvalidOperationException(
                "Cannot update or replace QEMU runtime binaries while virtual machines are running (§65).");
        }

        var descriptor = release ?? DefaultRelease;
        var tempInstallerPath = Path.Combine(Path.GetTempPath(), $"qemu-w64-setup-{descriptor.ReleaseDate.Replace("-", "")}.exe");

        var needDownload = true;
        if (File.Exists(tempInstallerPath))
        {
            var existingHash = await ComputeSha256HexAsync(tempInstallerPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingHash, descriptor.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                needDownload = false;
            }
        }

        if (needDownload)
        {
            progress?.Report((5, $"Downloading QEMU {descriptor.Version}..."));
            await DownloadFileParallelRangesAsync(
                descriptor.DownloadUrl,
                tempInstallerPath,
                parallelStreams: 16,
                progress,
                cancellationToken).ConfigureAwait(false);

            progress?.Report((85, "Verifying SHA-256 cryptographic signature..."));
            var actualHash = await ComputeSha256HexAsync(tempInstallerPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, descriptor.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempInstallerPath); } catch { }
                throw new InvalidDataException(
                    $"SHA-256 checksum mismatch for downloaded QEMU runtime. Expected {descriptor.ExpectedSha256Hex}, got {actualHash}.");
            }
        }

        if (areAnyVmsRunning())
        {
            throw new InvalidOperationException(
                "Cannot replace QEMU runtime binaries while virtual machines are running (§65).");
        }

        progress?.Report((90, "Extracting QEMU runtime binaries and UEFI firmware..."));
        await ExtractNsisArchiveAsync(tempInstallerPath, targetDirectory, cancellationToken).ConfigureAwait(false);

        progress?.Report((100, "QEMU runtime ready."));
        return QemuRuntimeLocator.Discover(new Core.Models.AppSettings { ManagedQemuDirectory = targetDirectory });
    }

    public static async Task DownloadFileParallelRangesAsync(
        string url,
        string destinationPath,
        int parallelStreams = 16,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(4, parallelStreams * 2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
        using var headResp = await client.SendAsync(headReq, cancellationToken).ConfigureAwait(false);
        headResp.EnsureSuccessStatusCode();

        var totalBytes = headResp.Content.Headers.ContentLength ?? 0L;
        var supportsByteRanges = headResp.Headers.AcceptRanges.Any(r =>
            string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
        if (totalBytes <= 0 || !supportsByteRanges)
        {
            await DownloadSingleStreamAsync(client, url, destinationPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using (var prealloc = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            prealloc.SetLength(totalBytes);
        }

        var chunkSize = (totalBytes + parallelStreams - 1) / parallelStreams;
        long totalDownloaded = 0;

        try
        {
            var tasks = new List<Task>();
            for (var i = 0; i < parallelStreams; i++)
            {
                var start = i * chunkSize;
                if (start >= totalBytes)
                {
                    break;
                }
                var end = Math.Min(totalBytes - 1, start + chunkSize - 1);

                tasks.Add(Task.Run(async () =>
                {
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            req.Headers.Range = new RangeHeaderValue(start, end);
                            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                .ConfigureAwait(false);
                            resp.EnsureSuccessStatusCode();
                            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                            {
                                throw new NotSupportedException("Server ignored the HTTP Range request.");
                            }

                            await using var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            await using var fs = new FileStream(
                                destinationPath,
                                FileMode.Open,
                                FileAccess.Write,
                                FileShare.ReadWrite,
                                bufferSize: 65536,
                                useAsync: true);
                            fs.Seek(start, SeekOrigin.Begin);

                            var buffer = new byte[65536];
                            int read;
                            while ((read = await netStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                            {
                                await fs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                var current = Interlocked.Add(ref totalDownloaded, read);
                                var pct = 5.0 + (current / (double)totalBytes) * 80.0;
                                progress?.Report((pct, $"Downloading QEMU ({current / (1024 * 1024)} / {totalBytes / (1024 * 1024)} MiB)..."));
                            }
                            break;
                        }
                        catch (NotSupportedException)
                        {
                            throw;
                        }
                        catch when (attempt < 3 && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(500 * (attempt + 1), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            await DownloadSingleStreamAsync(client, url, destinationPath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DownloadSingleStreamAsync(
        HttpClient client,
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var s = await client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        await using var outFs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await s.CopyToAsync(outFs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractNsisArchiveAsync(
        string installerPath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);
        var sevenZipCandidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            "7z.exe"
        };

        var sevenZipExe = sevenZipCandidates.FirstOrDefault(File.Exists) ?? "7z.exe";
        var psi = new ProcessStartInfo
        {
            FileName = sevenZipExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add($"-o{targetDirectory}");
        psi.ArgumentList.Add("-y");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 7-Zip extractor.");
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to extract QEMU runtime (exit code {proc.ExitCode}): {err}");
        }
    }

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(fs, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
