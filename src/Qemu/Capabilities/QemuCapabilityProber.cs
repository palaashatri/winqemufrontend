using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinQemu.Core.Configuration;
using WinQemu.Core.Models;

namespace WinQemu.Qemu.Capabilities;

public sealed class QemuTargetCapabilities
{
    public VmArchitecture Architecture { get; set; } = VmArchitecture.X86_64;
    public string BinaryPath { get; set; } = string.Empty;
    public string QemuVersion { get; set; } = string.Empty;
    public DateTimeOffset BinaryLastWriteTimeUtc { get; set; }
    public List<string> Accelerators { get; set; } = [];
    public List<string> Machines { get; set; } = [];
    public List<string> CpuModels { get; set; } = [];
    public List<string> Devices { get; set; } = [];
    public List<string> DisplayBackends { get; set; } = [];
    public List<string> AudioBackends { get; set; } = [];

    public bool SupportsAccelerator(string accel) =>
        Accelerators.Contains(accel, StringComparer.OrdinalIgnoreCase);

    public bool SupportsDevice(string deviceName) =>
        Devices.Count == 0 || Devices.Contains(deviceName, StringComparer.OrdinalIgnoreCase);

    public bool SupportsMachine(string machineName) =>
        Machines.Count == 0 ||
        Machines.Any(m => string.Equals(m, machineName, StringComparison.OrdinalIgnoreCase) ||
                          m.StartsWith(machineName + "-", StringComparison.OrdinalIgnoreCase));

    public string SelectBestAudioBackend(string preferredBackend)
    {
        if (!string.IsNullOrWhiteSpace(preferredBackend) &&
            !string.Equals(preferredBackend, "auto", StringComparison.OrdinalIgnoreCase) &&
            AudioBackends.Contains(preferredBackend, StringComparer.OrdinalIgnoreCase))
        {
            return preferredBackend.ToLowerInvariant();
        }

        string[] priority = ["dsound", "wasapi", "sdl", "wav", "none"];
        foreach (var candidate in priority)
        {
            if (AudioBackends.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return AudioBackends.FirstOrDefault() ?? "none";
    }
}

public sealed class QemuCapabilityProber
{
    private readonly string _cacheDirectory;

    public QemuCapabilityProber(string? cacheDirectory = null)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _cacheDirectory = cacheDirectory ?? Path.Combine(localAppData, "WinQemu", "Cache", "Capabilities");
    }

    public async Task<QemuTargetCapabilities> ProbeAsync(
        VmArchitecture architecture,
        string binaryPath,
        string qemuVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException($"QEMU binary not found: {binaryPath}", binaryPath);
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(binaryPath);
        var cacheFile = Path.Combine(_cacheDirectory, $"caps-{architecture.ToString().ToLowerInvariant()}.json");

        if (File.Exists(cacheFile))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var cached = JsonSerializer.Deserialize<QemuTargetCapabilities>(cachedJson, VmConfigurationStore.JsonOptions);
                if (cached is not null &&
                    string.Equals(cached.BinaryPath, Path.GetFullPath(binaryPath), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(cached.QemuVersion, qemuVersion, StringComparison.Ordinal) &&
                    Math.Abs((cached.BinaryLastWriteTimeUtc - lastWriteUtc).TotalSeconds) < 2)
                {
                    return cached;
                }
            }
            catch
            {
            }
        }

        var accelOutput = await RunHelpCommandAsync(binaryPath, ["-accel", "help"], cancellationToken).ConfigureAwait(false);
        var machineOutput = await RunHelpCommandAsync(binaryPath, ["-machine", "help"], cancellationToken).ConfigureAwait(false);
        var cpuOutput = await RunHelpCommandAsync(binaryPath, ["-cpu", "help"], cancellationToken).ConfigureAwait(false);
        var deviceOutput = await RunHelpCommandAsync(binaryPath, ["-device", "help"], cancellationToken).ConfigureAwait(false);
        var displayOutput = await RunHelpCommandAsync(binaryPath, ["-display", "help"], cancellationToken).ConfigureAwait(false);
        var audioOutput = await RunHelpCommandAsync(binaryPath, ["-audiodev", "help"], cancellationToken).ConfigureAwait(false);

        var caps = new QemuTargetCapabilities
        {
            Architecture = architecture,
            BinaryPath = Path.GetFullPath(binaryPath),
            QemuVersion = qemuVersion,
            BinaryLastWriteTimeUtc = lastWriteUtc,
            Accelerators = ParseAccelerators(accelOutput),
            Machines = ParseMachines(machineOutput),
            CpuModels = ParseCpuModels(cpuOutput),
            Devices = ParseDevices(deviceOutput),
            DisplayBackends = ParseSimpleList(displayOutput, "Available display backend types:"),
            AudioBackends = ParseSimpleList(audioOutput, "Available audio drivers:")
        };

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var json = JsonSerializer.Serialize(caps, VmConfigurationStore.JsonOptions);
            await File.WriteAllTextAsync(cacheFile, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        return caps;
    }

    public static List<string> ParseAccelerators(string output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Accelerators supported", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Possible", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(':'))
            {
                continue;
            }

            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                list.Add(token.Trim());
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ParseMachines(string output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Supported machines", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token) && !string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(token.Trim());
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ParseCpuModels(string output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Available CPUs", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Recognized CPUID", StringComparison.OrdinalIgnoreCase) ||
                line.EndsWith(':'))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var candidate = parts[0] == "x86" && parts.Length > 1 ? parts[1] : parts[0];
            if (!string.IsNullOrWhiteSpace(candidate) &&
                candidate.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                list.Add(candidate);
            }
        }

        if (!list.Contains("max", StringComparer.OrdinalIgnoreCase))
        {
            list.Insert(0, "max");
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ParseDevices(string output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        const string marker = "name \"";
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + marker.Length;
            var end = rawLine.IndexOf('"', start);
            if (end > start)
            {
                list.Add(rawLine[start..end]);
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ParseSimpleList(string output, string headerPrefix)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return list;
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase) || line.EndsWith(':'))
            {
                continue;
            }

            var token = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token))
            {
                list.Add(token.Trim());
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<string> RunHelpCommandAsync(
        string binaryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return string.Empty;
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }
}
