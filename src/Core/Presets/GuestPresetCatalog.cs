using WinQemu.Core.Configuration;
using WinQemu.Core.Models;

namespace WinQemu.Core.Presets;

public sealed record GuestPreset(
    string Id,
    string DisplayName,
    string Subtitle,
    GuestOsFamily OsFamily,
    VmArchitecture Architecture,
    string MachineType,
    string CpuModel,
    int RecommendedMemoryMiB,
    int MinimumMemoryMiB,
    int RecommendedVcpus,
    int RecommendedDiskGiB,
    VmFirmwareType FirmwareType,
    bool EnableSecureBoot,
    bool EnableTpm2,
    string StorageBus,
    string NetworkModel,
    string GpuModel,
    string AudioDeviceModel,
    string IconGlyph,
    string AccentColorHex,
    bool SupportsUnattendedInstall,
    UnattendedInstallMode DefaultUnattendedMode);

public static class GuestPresetCatalog
{
    private static readonly IReadOnlyList<GuestPreset> AllPresets =
    [
        new(
            Id: "windows-11",
            DisplayName: "Windows 11",
            Subtitle: "UEFI + TPM 2.0 + Secure Boot · Q35 · WHPX hardware acceleration",
            OsFamily: GuestOsFamily.Windows,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 8192,
            MinimumMemoryMiB: 4096,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 64,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: true,
            EnableTpm2: true,
            StorageBus: "sata",
            NetworkModel: "e1000e",
            GpuModel: "VGA",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE782",
            AccentColorHex: "#0078D4",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.WindowsAutounattend),

        new(
            Id: "windows-10",
            DisplayName: "Windows 10",
            Subtitle: "UEFI · Q35 · Compatible SATA + Intel Gigabit Ethernet",
            OsFamily: GuestOsFamily.Windows,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 2048,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 64,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "sata",
            NetworkModel: "e1000e",
            GpuModel: "VGA",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE782",
            AccentColorHex: "#2899F5",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.WindowsAutounattend),

        new(
            Id: "ubuntu",
            DisplayName: "Ubuntu Linux",
            Subtitle: "Modern VirtIO storage, network & GPU · UEFI · Cloud-Init Express ready",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 1024,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE950",
            AccentColorHex: "#E95420",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.CloudInit),

        new(
            Id: "fedora",
            DisplayName: "Fedora Workstation",
            Subtitle: "VirtIO high-throughput profile · UEFI · Kickstart / Cloud-Init ready",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 2048,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE950",
            AccentColorHex: "#3C6EB4",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.Kickstart),

        new(
            Id: "debian",
            DisplayName: "Debian GNU/Linux",
            Subtitle: "Stable VirtIO profile · UEFI · Preseed / Cloud-Init support",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 1024,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE950",
            AccentColorHex: "#D70A53",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.Preseed),

        new(
            Id: "arch",
            DisplayName: "Arch Linux",
            Subtitle: "Rolling-release VirtIO profile · UEFI · x86_64",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 1024,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE950",
            AccentColorHex: "#1793D1",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.CloudInit),

        new(
            Id: "linux-mint",
            DisplayName: "Linux Mint",
            Subtitle: "Desktop-ready VirtIO + HD Audio profile · UEFI",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 2048,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE950",
            AccentColorHex: "#87CF3E",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.CloudInit),

        new(
            Id: "freebsd",
            DisplayName: "FreeBSD",
            Subtitle: "UEFI · VirtIO block & networking · Serial console ready",
            OsFamily: GuestOsFamily.Bsd,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 1024,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-vga",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE977",
            AccentColorHex: "#AB2B28",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "openbsd",
            DisplayName: "OpenBSD",
            Subtitle: "UEFI · Safe VirtIO + Intel e1000 compatibility profile",
            OsFamily: GuestOsFamily.Bsd,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 2048,
            MinimumMemoryMiB: 512,
            RecommendedVcpus: 2,
            RecommendedDiskGiB: 24,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "e1000",
            GpuModel: "VGA",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE977",
            AccentColorHex: "#F2CA30",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "macos-x86_64",
            DisplayName: "macOS / Darwin (x86_64)",
            Subtitle: "Q35 + UEFI + Penryn/Skylake ISA + SATA AHCI + VMXnet3/e1000e",
            OsFamily: GuestOsFamily.MacOs,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "Penryn",
            RecommendedMemoryMiB: 8192,
            MinimumMemoryMiB: 4096,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 64,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "sata",
            NetworkModel: "e1000e",
            GpuModel: "VGA",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE770",
            AccentColorHex: "#A2AAAD",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "macos-ppc64",
            DisplayName: "Mac OS / Darwin PowerPC (mac99)",
            Subtitle: "PowerMac G4/G5 mac99 · OpenBIOS · Sungem Ethernet · TCG Emulation",
            OsFamily: GuestOsFamily.MacOs,
            Architecture: VmArchitecture.Ppc64,
            MachineType: "mac99",
            CpuModel: "970fx",
            RecommendedMemoryMiB: 2048,
            MinimumMemoryMiB: 512,
            RecommendedVcpus: 1,
            RecommendedDiskGiB: 20,
            FirmwareType: VmFirmwareType.OpenBios,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "ide",
            NetworkModel: "sungem",
            GpuModel: "VGA",
            AudioDeviceModel: "none",
            IconGlyph: "\uE770",
            AccentColorHex: "#8E8E93",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "generic-x86_64",
            DisplayName: "Generic PC (x86_64)",
            Subtitle: "Standard 64-bit x86 PC · Q35 · UEFI or BIOS",
            OsFamily: GuestOsFamily.Other,
            Architecture: VmArchitecture.X86_64,
            MachineType: "q35",
            CpuModel: "max",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 512,
            RecommendedVcpus: 2,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "e1000e",
            GpuModel: "VGA",
            AudioDeviceModel: "ich9-intel-hda",
            IconGlyph: "\uE7F4",
            AccentColorHex: "#4F8DFA",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "generic-i386",
            DisplayName: "Generic 32-bit PC (i386)",
            Subtitle: "Legacy 32-bit x86 PC · i440FX · SeaBIOS · IDE + e1000",
            OsFamily: GuestOsFamily.Other,
            Architecture: VmArchitecture.I386,
            MachineType: "pc",
            CpuModel: "max",
            RecommendedMemoryMiB: 1024,
            MinimumMemoryMiB: 128,
            RecommendedVcpus: 2,
            RecommendedDiskGiB: 16,
            FirmwareType: VmFirmwareType.Bios,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "ide",
            NetworkModel: "e1000",
            GpuModel: "VGA",
            AudioDeviceModel: "ac97",
            IconGlyph: "\uE7F4",
            AccentColorHex: "#5C6BC0",
            SupportsUnattendedInstall: false,
            DefaultUnattendedMode: UnattendedInstallMode.Manual),

        new(
            Id: "generic-aarch64",
            DisplayName: "Generic ARM64 (aarch64)",
            Subtitle: "ARMv8-A virt machine · EDK2 UEFI · VirtIO GPU/Block/Net",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.Aarch64,
            MachineType: "virt",
            CpuModel: "cortex-a72",
            RecommendedMemoryMiB: 4096,
            MinimumMemoryMiB: 1024,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 32,
            FirmwareType: VmFirmwareType.Uefi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-gpu-pci",
            AudioDeviceModel: "none",
            IconGlyph: "\uE964",
            AccentColorHex: "#00ACC1",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.CloudInit),

        new(
            Id: "generic-riscv64",
            DisplayName: "Generic RISC-V 64 (riscv64)",
            Subtitle: "RISC-V 64-bit virt machine · OpenSBI · VirtIO GPU/Block/Net",
            OsFamily: GuestOsFamily.Linux,
            Architecture: VmArchitecture.Riscv64,
            MachineType: "virt",
            CpuModel: "rv64",
            RecommendedMemoryMiB: 2048,
            MinimumMemoryMiB: 512,
            RecommendedVcpus: 4,
            RecommendedDiskGiB: 24,
            FirmwareType: VmFirmwareType.OpenSbi,
            EnableSecureBoot: false,
            EnableTpm2: false,
            StorageBus: "virtio-blk",
            NetworkModel: "virtio-net-pci",
            GpuModel: "virtio-gpu-pci",
            AudioDeviceModel: "none",
            IconGlyph: "\uE964",
            AccentColorHex: "#FFB300",
            SupportsUnattendedInstall: true,
            DefaultUnattendedMode: UnattendedInstallMode.CloudInit)
    ];

    public static IReadOnlyList<GuestPreset> GetAll() => AllPresets;

    public static GuestPreset GetByIdOrDefault(string? presetId)
    {
        if (!string.IsNullOrWhiteSpace(presetId))
        {
            var match = AllPresets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return AllPresets.First(p => p.Id == "ubuntu");
    }

    public static VirtualMachine CreateVmFromPreset(
        GuestPreset preset,
        string vmName,
        string vmDirectory,
        string? installerIsoPath = null,
        int? customMemoryMiB = null,
        int? customVcpus = null,
        int? customDiskGiB = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmName);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectory);

        var memMiB = customMemoryMiB ?? preset.RecommendedMemoryMiB;
        var vcpus = customVcpus ?? preset.RecommendedVcpus;
        var diskGiB = customDiskGiB ?? preset.RecommendedDiskGiB;

        var vm = new VirtualMachine
        {
            Identity = new VmIdentity
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = vmName.Trim(),
                Description = $"{preset.DisplayName} ({preset.Architecture.ToDisplayName()})",
                Tags = [preset.OsFamily.ToString(), preset.Architecture.ToString().ToLowerInvariant()]
            },
            Metadata = new VmMetadata
            {
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                IconGlyph = preset.IconGlyph,
                AccentColorHex = preset.AccentColorHex
            },
            GuestProfile = new VmGuestProfile
            {
                OsFamily = preset.OsFamily,
                PresetId = preset.Id,
                OsDisplayName = preset.DisplayName,
                InstallerIsoPath = installerIsoPath,
                DetectedAutomatically = !string.IsNullOrWhiteSpace(installerIsoPath)
            },
            Architecture = preset.Architecture,
            Machine = new VmMachineConfig
            {
                MachineType = preset.MachineType,
                Chipset = preset.MachineType == "q35" ? "ICH9" : preset.MachineType,
                EnableAcpi = true,
                EnableApic = true,
                RtcBase = preset.OsFamily == GuestOsFamily.Windows ? "localtime" : "utc"
            },
            Accelerator = preset.Architecture.SupportsWhpxOnX64Host()
                ? VmAcceleratorMode.Auto
                : VmAcceleratorMode.Tcg,
            Cpu = new VmCpuConfig
            {
                VcpuCount = vcpus,
                Sockets = 1,
                Cores = vcpus,
                Threads = 1,
                CpuModel = preset.CpuModel
            },
            Memory = new VmMemoryConfig
            {
                MemoryMiB = memMiB,
                EnableBallooning = preset.Architecture is VmArchitecture.X86_64 or VmArchitecture.Aarch64
            },
            Firmware = new VmFirmwareConfig
            {
                FirmwareType = preset.FirmwareType,
                VarsFilePath = preset.FirmwareType == VmFirmwareType.Uefi
                    ? VmDirectoryLayout.GetDefaultVarsPath(vmDirectory)
                    : null,
                EnableSecureBoot = preset.EnableSecureBoot
            },
            Security = new VmSecurityConfig
            {
                EnableTpm2 = preset.EnableTpm2,
                TpmDeviceModel = preset.Architecture == VmArchitecture.Aarch64 ? "tpm-tis-device" : "tpm-crb",
                EnableSecureBoot = preset.EnableSecureBoot
            },
            Graphics = new VmGraphicsConfig
            {
                GpuModel = preset.GpuModel,
                Transport = VmDisplayTransport.VncEmbedded,
                ScalingMode = VmDisplayScalingMode.FitAspectRatio,
                PreferredWidth = 1280,
                PreferredHeight = 800
            },
            Audio = new VmAudioConfig
            {
                Enabled = preset.AudioDeviceModel != "none",
                DeviceModel = preset.AudioDeviceModel,
                HostBackend = "auto"
            },
            Input = new VmInputConfig
            {
                EnableUsbTablet = true,
                EnableUsbKeyboard = preset.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64 or VmArchitecture.Ppc64
            },
            Usb = new VmUsbConfig
            {
                ControllerModel = preset.MachineType == "pc" ? "usb-ehci" : "qemu-xhci"
            },
            Integration = new VmIntegrationConfig
            {
                EnableGuestAgent = true,
                EnableClipboardSharing = true,
                UnattendedInstall = new UnattendedInstallConfig
                {
                    Enabled = false,
                    Mode = preset.DefaultUnattendedMode
                }
            },
            Developer = new VmDeveloperConfig
            {
                EnableDeveloperMode = false,
                EnableSerialConsole = true,
                EnableQmpInspector = true
            },
            DirectoryPath = vmDirectory
        };

        vm.Storage.Add(new VmStorageDevice
        {
            Id = "disk0",
            Name = "System Disk",
            Path = VmDirectoryLayout.GetDefaultSystemDiskPath(vmDirectory),
            Format = "qcow2",
            Bus = preset.StorageBus,
            Media = "disk",
            IsBootDrive = true,
            VirtualSizeBytes = (long)diskGiB * 1024L * 1024L * 1024L
        });

        if (!string.IsNullOrWhiteSpace(installerIsoPath))
        {
            vm.Storage.Add(new VmStorageDevice
            {
                Id = "cdrom0",
                Name = Path.GetFileName(installerIsoPath),
                Path = installerIsoPath,
                Format = "raw",
                Bus = preset.MachineType == "q35" ? "sata" : "ide",
                Media = "cdrom",
                ReadOnly = true,
                IsBootDrive = false
            });
        }

        vm.Network.Add(new VmNetworkAdapter
        {
            Id = "net0",
            Name = "Default User-Mode NAT",
            Mode = VmNetworkMode.UserNat,
            Model = preset.NetworkModel,
            MacAddress = VmNetworkAdapter.GenerateRandomMacAddress()
        });

        return vm;
    }
}
