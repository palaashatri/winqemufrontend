using System.Text;
using WinQemu.Core.Models;

namespace WinQemu.Core.Presets;

public enum IsoRecognitionConfidence
{
    Unknown,
    Low,
    High
}

public sealed record IsoRecognitionResult(
    bool IsRecognized,
    IsoRecognitionConfidence Confidence,
    string? MatchedPresetId,
    string SuggestedVmName,
    GuestOsFamily OsFamily,
    VmArchitecture Architecture,
    VmFirmwareType SuggestedFirmware,
    string? IsoVolumeLabel,
    string Explanation);

public static class IsoClassifier
{
    public static IsoRecognitionResult ClassifyIso(string isoPath)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
        {
            return UnknownResult("No ISO file selected.");
        }

        var fileName = Path.GetFileNameWithoutExtension(isoPath);
        var volumeLabel = TryReadIso9660VolumeIdentifier(isoPath);
        var combinedSignal = $"{fileName} {volumeLabel}".ToLowerInvariant();

        var arch = InferArchitecture(combinedSignal);

        // Windows 11 detection
        if (combinedSignal.Contains("win11") ||
            combinedSignal.Contains("windows 11") ||
            combinedSignal.Contains("windows_11") ||
            combinedSignal.Contains("cccoma_x64fre") ||
            combinedSignal.Contains("cpba_x64fre"))
        {
            return BuildResult("windows-11", "Windows 11", volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Windows 11 installer media.");
        }

        // Windows 10 detection
        if (combinedSignal.Contains("win10") ||
            combinedSignal.Contains("windows 10") ||
            combinedSignal.Contains("windows_10") ||
            combinedSignal.Contains("esd-iso"))
        {
            return BuildResult("windows-10", "Windows 10", volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Windows 10 installer media.");
        }

        // Ubuntu detection
        if (combinedSignal.Contains("ubuntu"))
        {
            var presetId = arch switch
            {
                VmArchitecture.Aarch64 => "generic-aarch64",
                VmArchitecture.Riscv64 => "generic-riscv64",
                _ => "ubuntu"
            };
            return BuildResult(presetId, BeautifyOsName(fileName, "Ubuntu Linux"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Ubuntu installer media.");
        }

        // Fedora detection
        if (combinedSignal.Contains("fedora"))
        {
            var presetId = arch == VmArchitecture.Aarch64 ? "generic-aarch64" : "fedora";
            return BuildResult(presetId, BeautifyOsName(fileName, "Fedora Linux"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Fedora installer media.");
        }

        // Debian detection
        if (combinedSignal.Contains("debian"))
        {
            var presetId = arch switch
            {
                VmArchitecture.Aarch64 => "generic-aarch64",
                VmArchitecture.Riscv64 => "generic-riscv64",
                VmArchitecture.I386 => "generic-i386",
                _ => "debian"
            };
            return BuildResult(presetId, BeautifyOsName(fileName, "Debian GNU/Linux"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Debian installer media.");
        }

        // Arch Linux detection
        if (combinedSignal.Contains("archlinux") || combinedSignal.Contains("arch-linux") || combinedSignal.Contains("cachyos") || combinedSignal.Contains("manjaro"))
        {
            return BuildResult("arch", BeautifyOsName(fileName, "Arch Linux"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Arch-family Linux installer media.");
        }

        // Linux Mint detection
        if (combinedSignal.Contains("linuxmint") || combinedSignal.Contains("linux-mint"))
        {
            return BuildResult("linux-mint", BeautifyOsName(fileName, "Linux Mint"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Linux Mint installer media.");
        }

        // Alpine / TinyCore / Generic Linux detection
        if (combinedSignal.Contains("alpine") || combinedSignal.Contains("tinycore") || combinedSignal.Contains("linux") || combinedSignal.Contains("opensuse"))
        {
            var presetId = arch switch
            {
                VmArchitecture.Aarch64 => "generic-aarch64",
                VmArchitecture.Riscv64 => "generic-riscv64",
                VmArchitecture.I386 => "generic-i386",
                _ => "ubuntu"
            };
            return BuildResult(presetId, BeautifyOsName(fileName, "Linux VM"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected Linux installer media.");
        }

        // FreeBSD detection
        if (combinedSignal.Contains("freebsd"))
        {
            return BuildResult("freebsd", BeautifyOsName(fileName, "FreeBSD"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected FreeBSD installer media.");
        }

        // OpenBSD detection
        if (combinedSignal.Contains("openbsd"))
        {
            return BuildResult("openbsd", BeautifyOsName(fileName, "OpenBSD"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected OpenBSD installer media.");
        }

        // macOS / Darwin detection
        if (combinedSignal.Contains("macos") || combinedSignal.Contains("darwin") || combinedSignal.Contains("opencore") || combinedSignal.Contains("osx"))
        {
            var presetId = arch == VmArchitecture.Ppc64 ? "macos-ppc64" : "macos-x86_64";
            return BuildResult(presetId, BeautifyOsName(fileName, "macOS"), volumeLabel, arch,
                IsoRecognitionConfidence.High, "Detected macOS / Darwin installer media.");
        }

        // Per §14: Never confidently invent an OS identity from weak evidence.
        return new IsoRecognitionResult(
            IsRecognized: false,
            Confidence: IsoRecognitionConfidence.Unknown,
            MatchedPresetId: null,
            SuggestedVmName: string.IsNullOrWhiteSpace(fileName) ? "New Virtual Machine" : fileName,
            OsFamily: GuestOsFamily.Other,
            Architecture: arch,
            SuggestedFirmware: arch == VmArchitecture.I386 ? VmFirmwareType.Bios : VmFirmwareType.Uefi,
            IsoVolumeLabel: volumeLabel,
            Explanation: "Operating system could not be identified. Choose Linux, Windows, BSD, macOS, or Other to continue.");
    }

    public static string? TryReadIso9660VolumeIdentifier(string isoPath)
    {
        try
        {
            if (!File.Exists(isoPath))
            {
                return null;
            }

            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // Primary Volume Descriptor starts at sector 16 (0x8000)
            const long pvdOffset = 16L * 2048L;
            if (fs.Length < pvdOffset + 128)
            {
                return null;
            }

            fs.Seek(pvdOffset, SeekOrigin.Begin);
            Span<byte> header = stackalloc byte[72];
            var read = fs.Read(header);
            if (read < 72)
            {
                return null;
            }

            // Byte 0 = 1 (Primary Volume Descriptor), Bytes 1..5 = "CD001"
            if (header[0] != 0x01 ||
                header[1] != (byte)'C' ||
                header[2] != (byte)'D' ||
                header[3] != (byte)'0' ||
                header[4] != (byte)'0' ||
                header[5] != (byte)'1')
            {
                return null;
            }

            // Volume Identifier is at offset 40..71 (32 bytes ASCII)
            var label = Encoding.ASCII.GetString(header.Slice(40, 32)).Trim('\0', ' ');
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch
        {
            return null;
        }
    }

    private static VmArchitecture InferArchitecture(string signal)
    {
        if (signal.Contains("aarch64") || signal.Contains("arm64"))
        {
            return VmArchitecture.Aarch64;
        }
        if (signal.Contains("riscv64") || signal.Contains("riscv"))
        {
            return VmArchitecture.Riscv64;
        }
        if (signal.Contains("ppc64") || signal.Contains("powerpc") || signal.Contains("mac99"))
        {
            return VmArchitecture.Ppc64;
        }
        if (signal.Contains("s390x"))
        {
            return VmArchitecture.S390x;
        }
        if (signal.Contains("i386") || signal.Contains("i686") || signal.Contains("x86-32"))
        {
            return VmArchitecture.I386;
        }

        return VmArchitecture.X86_64;
    }

    private static IsoRecognitionResult BuildResult(
        string presetId,
        string suggestedName,
        string? volumeLabel,
        VmArchitecture arch,
        IsoRecognitionConfidence confidence,
        string explanation)
    {
        var preset = GuestPresetCatalog.GetByIdOrDefault(presetId);
        return new IsoRecognitionResult(
            IsRecognized: true,
            Confidence: confidence,
            MatchedPresetId: preset.Id,
            SuggestedVmName: suggestedName,
            OsFamily: preset.OsFamily,
            Architecture: arch,
            SuggestedFirmware: preset.FirmwareType,
            IsoVolumeLabel: volumeLabel,
            Explanation: explanation);
    }

    private static IsoRecognitionResult UnknownResult(string explanation) =>
        new(
            IsRecognized: false,
            Confidence: IsoRecognitionConfidence.Unknown,
            MatchedPresetId: null,
            SuggestedVmName: "New Virtual Machine",
            OsFamily: GuestOsFamily.Other,
            Architecture: VmArchitecture.X86_64,
            SuggestedFirmware: VmFirmwareType.Uefi,
            IsoVolumeLabel: null,
            Explanation: explanation);

    private static string BeautifyOsName(string rawName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        var cleaned = rawName.Replace('_', ' ').Replace('-', ' ').Trim();
        return cleaned.Length > 40 ? fallback : cleaned;
    }
}
