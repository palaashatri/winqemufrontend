using System.Diagnostics;
using WinQemu.Core.Models;

namespace WinQemu.Qemu.Runtime;

public sealed record QemuRuntimeInfo(
    bool IsAvailable,
    string DirectoryPath,
    string VersionString,
    string? QemuSystemX86_64Path,
    string? QemuImgPath,
    IReadOnlyDictionary<VmArchitecture, string> TargetBinaries,
    IReadOnlyList<string> FirmwareFiles,
    string HealthSummary);

public sealed class QemuRuntimeLocator
{
    public static QemuRuntimeInfo Discover(AppSettings? settings = null)
    {
        var candidateDirs = GetCandidateDirectories(settings);
        foreach (var dir in candidateDirs)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            var x64Path = Path.Combine(dir, "qemu-system-x86_64.exe");
            var imgPath = Path.Combine(dir, "qemu-img.exe");
            if (!File.Exists(x64Path) && !File.Exists(imgPath))
            {
                continue;
            }

            var targets = new Dictionary<VmArchitecture, string>();
            foreach (var arch in Enum.GetValues<VmArchitecture>())
            {
                var exeName = $"{arch.ToQemuSystemBinaryName()}.exe";
                var fullExePath = Path.Combine(dir, exeName);
                if (File.Exists(fullExePath))
                {
                    targets[arch] = fullExePath;
                }
            }

            if (targets.Count == 0)
            {
                continue;
            }

            var primaryBinary = targets.GetValueOrDefault(VmArchitecture.X86_64) ?? targets.Values.First();
            var version = QueryBinaryVersion(primaryBinary);
            var firmwareFiles = DiscoverFirmwareFiles(dir);

            return new QemuRuntimeInfo(
                IsAvailable: true,
                DirectoryPath: Path.GetFullPath(dir),
                VersionString: version,
                QemuSystemX86_64Path: targets.GetValueOrDefault(VmArchitecture.X86_64),
                QemuImgPath: File.Exists(imgPath) ? imgPath : null,
                TargetBinaries: targets,
                FirmwareFiles: firmwareFiles,
                HealthSummary: $"Healthy ({version}, {targets.Count} target architectures, {firmwareFiles.Count} firmware images)");
        }

        return new QemuRuntimeInfo(
            IsAvailable: false,
            DirectoryPath: settings?.ManagedQemuDirectory ?? AppSettings.GetDefaultManagedQemuDirectory(),
            VersionString: "Not installed",
            QemuSystemX86_64Path: null,
            QemuImgPath: null,
            TargetBinaries: new Dictionary<VmArchitecture, string>(),
            FirmwareFiles: [],
            HealthSummary: "QEMU runtime not found. Use Runtime Manager in Settings to install or locate QEMU.");
    }

    public static IReadOnlyList<string> GetCandidateDirectories(AppSettings? settings = null)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings?.CustomQemuDirectory))
        {
            list.Add(settings.CustomQemuDirectory);
        }

        if (!string.IsNullOrWhiteSpace(settings?.ManagedQemuDirectory))
        {
            list.Add(settings.ManagedQemuDirectory);
        }

        list.Add(AppSettings.GetDefaultManagedQemuDirectory());
        list.Add(Path.Combine(AppContext.BaseDirectory, "Runtime", "qemu"));
        list.Add(@"C:\Program Files\qemu");
        list.Add(@"C:\Program Files (x86)\qemu");
        list.Add(@"C:\msys64\ucrt64\bin");
        list.Add(@"C:\msys64\mingw64\bin");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(Path.Combine(userProfile, "scoop", "apps", "qemu", "current"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Contains("qemu", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(part);
                }
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string QueryBinaryVersion(string executablePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return "Unknown version";
            }

            var firstLine = proc.StandardOutput.ReadLine();
            proc.WaitForExit(3000);
            if (!string.IsNullOrWhiteSpace(firstLine))
            {
                // e.g. "QEMU emulator version 10.1.0 (v10.1.0-12089-...)"
                return firstLine.Trim();
            }
        }
        catch
        {
        }

        return "Unknown version";
    }

    public static IReadOnlyList<string> DiscoverFirmwareFiles(string qemuDirectory)
    {
        var results = new List<string>();
        var shareDirs = new[]
        {
            Path.Combine(qemuDirectory, "share"),
            Path.Combine(qemuDirectory, "..", "share", "qemu"),
            qemuDirectory
        };

        foreach (var shareDir in shareDirs)
        {
            if (!Directory.Exists(shareDir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(shareDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("edk2-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("ovmf", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("opensbi-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("openbios-", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("bios", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("u-boot", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(Path.GetFullPath(file));
                    }
                }
            }
            catch
            {
            }

            if (results.Count > 0)
            {
                break;
            }
        }

        return results;
    }
}
