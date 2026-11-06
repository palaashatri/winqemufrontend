using WinQemu.Core.Models;

namespace WinQemu.Integration;

public sealed record OfficialOsDownloadEntry(
    string Id,
    string Name,
    string Version,
    GuestOsFamily OsFamily,
    VmArchitecture Architecture,
    string PresetId,
    string OfficialSourceUrl,
    string PublisherName,
    long ApproximateSizeBytes,
    string? Sha256Checksum,
    bool IsDirectIsoDownload);

/// <summary>
/// Curated catalog of official operating system downloads and acquisition links (§38).
/// Never redistributes proprietary Windows media; links only to official distribution infrastructure.
/// </summary>
public static class OsDownloadCatalog
{
    private static readonly IReadOnlyList<OfficialOsDownloadEntry> Entries =
    [
        new(
            Id: "alpine-virt-x86_64",
            Name: "Alpine Linux Virtual (x86_64)",
            Version: "3.21.3",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            PresetId: "ubuntu",
            OfficialSourceUrl: "https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/x86_64/alpine-virt-3.21.3-x86_64.iso",
            PublisherName: "Alpine Linux Development Team",
            ApproximateSizeBytes: 66_060_288,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "alpine-virt-aarch64",
            Name: "Alpine Linux Virtual (ARM64 / aarch64)",
            Version: "3.21.3",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.Aarch64,
            PresetId: "generic-aarch64",
            OfficialSourceUrl: "https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/aarch64/alpine-virt-3.21.3-aarch64.iso",
            PublisherName: "Alpine Linux Development Team",
            ApproximateSizeBytes: 63_963_136,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "alpine-uboot-riscv64",
            Name: "Alpine Linux (RISC-V 64 / riscv64)",
            Version: "3.21.3",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.Riscv64,
            PresetId: "generic-riscv64",
            OfficialSourceUrl: "https://dl-cdn.alpinelinux.org/alpine/v3.21/releases/riscv64/alpine-uboot-3.21.3-riscv64.tar.gz",
            PublisherName: "Alpine Linux Development Team",
            ApproximateSizeBytes: 58_720_256,
            Sha256Checksum: null,
            IsDirectIsoDownload: false),

        new(
            Id: "ubuntu-desktop-amd64",
            Name: "Ubuntu Desktop 24.04.4 LTS (x86_64)",
            Version: "24.04.4 LTS",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            PresetId: "ubuntu",
            OfficialSourceUrl: "https://releases.ubuntu.com/24.04/ubuntu-24.04.4-desktop-amd64.iso",
            PublisherName: "Canonical Ltd.",
            ApproximateSizeBytes: 6_184_752_947L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "fedora-workstation-x86_64",
            Name: "Fedora Workstation Live (x86_64)",
            Version: "43",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            PresetId: "fedora",
            OfficialSourceUrl: "https://download.fedoraproject.org/pub/fedora/linux/releases/43/Workstation/x86_64/iso/Fedora-Workstation-Live-43-1.6.x86_64.iso",
            PublisherName: "Fedora Project",
            ApproximateSizeBytes: 2_469_606_195L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "debian-netinst-amd64",
            Name: "Debian 13 Trixie Netinst (x86_64)",
            Version: "13.7.0",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            PresetId: "debian",
            OfficialSourceUrl: "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso",
            PublisherName: "Debian Project",
            ApproximateSizeBytes: 661_651_456L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "freebsd-bootonly-amd64",
            Name: "FreeBSD 14.5-RELEASE Bootonly (x86_64)",
            Version: "14.5-RELEASE",
            OsFamily: GuestOsFamily.Bsd,
            Architecture: VmArchitecture.X86_64,
            PresetId: "freebsd",
            OfficialSourceUrl: "https://download.freebsd.org/releases/amd64/amd64/ISO-IMAGES/14.5/FreeBSD-14.5-RELEASE-amd64-bootonly.iso",
            PublisherName: "The FreeBSD Project",
            ApproximateSizeBytes: 429_916_160L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "freebsd-bootonly-aarch64",
            Name: "FreeBSD 14.5-RELEASE Bootonly (ARM64)",
            Version: "14.5-RELEASE",
            OsFamily: GuestOsFamily.Bsd,
            Architecture: VmArchitecture.Aarch64,
            PresetId: "generic-aarch64",
            OfficialSourceUrl: "https://download.freebsd.org/releases/arm64/aarch64/ISO-IMAGES/14.5/FreeBSD-14.5-RELEASE-arm64-aarch64-bootonly.iso",
            PublisherName: "The FreeBSD Project",
            ApproximateSizeBytes: 398_458_880L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true),

        new(
            Id: "windows-11-official",
            Name: "Windows 11 (Official Microsoft Download Page)",
            Version: "24H2",
            OsFamily: GuestOsFamily.Windows,
            Architecture: VmArchitecture.X86_64,
            PresetId: "windows-11",
            OfficialSourceUrl: "https://www.microsoft.com/software-download/windows11",
            PublisherName: "Microsoft Corporation",
            ApproximateSizeBytes: 5_583_457_484L,
            Sha256Checksum: null,
            IsDirectIsoDownload: false),

        new(
            Id: "virtio-win-drivers",
            Name: "Fedora Signed VirtIO Drivers for Windows (virtio-win.iso)",
            Version: "Stable",
            OsFamily: GuestOsFamily.Windows,
            Architecture: VmArchitecture.X86_64,
            PresetId: "windows-11",
            OfficialSourceUrl: "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso",
            PublisherName: "Fedora Project / Red Hat",
            ApproximateSizeBytes: 725_614_592L,
            Sha256Checksum: null,
            IsDirectIsoDownload: true)
    ];

    public static IReadOnlyList<OfficialOsDownloadEntry> GetAll() => Entries;
}
