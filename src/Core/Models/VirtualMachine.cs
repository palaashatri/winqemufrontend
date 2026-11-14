using System.Text.Json.Serialization;

namespace WinQemu.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmArchitecture
{
    X86_64,
    I386,
    Aarch64,
    Riscv64,
    Ppc64,
    S390x,
    Mips64el,
    Sparc64
}

public static class VmArchitectureExtensions
{
    public static string ToQemuSystemBinaryName(this VmArchitecture arch) => arch switch
    {
        VmArchitecture.X86_64 => "qemu-system-x86_64",
        VmArchitecture.I386 => "qemu-system-i386",
        VmArchitecture.Aarch64 => "qemu-system-aarch64",
        VmArchitecture.Riscv64 => "qemu-system-riscv64",
        VmArchitecture.Ppc64 => "qemu-system-ppc64",
        VmArchitecture.S390x => "qemu-system-s390x",
        VmArchitecture.Mips64el => "qemu-system-mips64el",
        VmArchitecture.Sparc64 => "qemu-system-sparc64",
        _ => "qemu-system-x86_64"
    };

    public static string ToDisplayName(this VmArchitecture arch) => arch switch
    {
        VmArchitecture.X86_64 => "x86_64 (64-bit PC)",
        VmArchitecture.I386 => "i386 (32-bit PC)",
        VmArchitecture.Aarch64 => "ARM64 (aarch64)",
        VmArchitecture.Riscv64 => "RISC-V 64 (riscv64)",
        VmArchitecture.Ppc64 => "PowerPC 64 (ppc64)",
        VmArchitecture.S390x => "IBM Z (s390x)",
        VmArchitecture.Mips64el => "MIPS64 Little-Endian (mips64el)",
        VmArchitecture.Sparc64 => "SPARC64 (sparc64)",
        _ => arch.ToString()
    };

    public static bool SupportsWhpxOnX64Host(this VmArchitecture arch) =>
        arch is VmArchitecture.X86_64 or VmArchitecture.I386;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GuestOsFamily
{
    Windows,
    Linux,
    Bsd,
    MacOs,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmAcceleratorMode
{
    Auto,
    Whpx,
    Tcg
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmFirmwareType
{
    Uefi,
    Bios,
    OpenBios,
    OpenSbi,
    Custom,
    None
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmDisplayTransport
{
    VncEmbedded,
    SpiceEmbedded,
    QemuWindow,
    Headless
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmDisplayScalingMode
{
    FitAspectRatio,
    Stretch,
    OneToOne
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VmNetworkMode
{
    UserNat,
    Isolated,
    TapBridged
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnattendedInstallMode
{
    Manual,
    WindowsAutounattend,
    CloudInit,
    Kickstart,
    Preseed
}

public sealed class VirtualMachine
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public VmIdentity Identity { get; set; } = new();
    public VmMetadata Metadata { get; set; } = new();
    public VmGuestProfile GuestProfile { get; set; } = new();
    public VmArchitecture Architecture { get; set; } = VmArchitecture.X86_64;
    public VmMachineConfig Machine { get; set; } = new();
    public VmAcceleratorMode Accelerator { get; set; } = VmAcceleratorMode.Auto;
    public VmCpuConfig Cpu { get; set; } = new();
    public VmMemoryConfig Memory { get; set; } = new();
    public VmFirmwareConfig Firmware { get; set; } = new();
    public VmSecurityConfig Security { get; set; } = new();
    public List<VmStorageDevice> Storage { get; set; } = [];
    public List<VmNetworkAdapter> Network { get; set; } = [];
    public VmGraphicsConfig Graphics { get; set; } = new();
    public VmAudioConfig Audio { get; set; } = new();
    public VmInputConfig Input { get; set; } = new();
    public VmUsbConfig Usb { get; set; } = new();
    public VmSharedServicesConfig SharedServices { get; set; } = new();
    public VmIntegrationConfig Integration { get; set; } = new();
    public VmBootConfig Boot { get; set; } = new();
    public VmDeveloperConfig Developer { get; set; } = new();
    public List<string> AdvancedQemuArgs { get; set; } = [];

    [JsonIgnore]
    public string? DirectoryPath { get; set; }
}

public sealed class VmIdentity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "New Virtual Machine";
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
}

public sealed class VmMetadata
{
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastStartedAtUtc { get; set; }
    public string IconGlyph { get; set; } = "\uE7F4";
    public string AccentColorHex { get; set; } = "#4F8DFA";
    public string? LastScreenshotPath { get; set; }
    public string? ClonedFromVmId { get; set; }
    public string? ClonedFromSnapshotName { get; set; }
}

public sealed class VmGuestProfile
{
    public GuestOsFamily OsFamily { get; set; } = GuestOsFamily.Linux;
    public string PresetId { get; set; } = "ubuntu";
    public string OsDisplayName { get; set; } = "Ubuntu Linux";
    public string? InstallerIsoPath { get; set; }
    public bool DetectedAutomatically { get; set; }
    public bool InstallationCompleted { get; set; }
}

public sealed class VmMachineConfig
{
    public string MachineType { get; set; } = "q35";
    public string Chipset { get; set; } = "ICH9";
    public bool EnableAcpi { get; set; } = true;
    public bool EnableApic { get; set; } = true;
    public string RtcBase { get; set; } = "utc";
}

public sealed class VmCpuConfig
{
    public int VcpuCount { get; set; } = 4;
    public int Sockets { get; set; } = 1;
    public int Cores { get; set; } = 4;
    public int Threads { get; set; } = 1;
    public string CpuModel { get; set; } = "max";
    public List<string> CpuFlags { get; set; } = [];
}

public sealed class VmMemoryConfig
{
    public int MemoryMiB { get; set; } = 4096;
    public bool EnableBallooning { get; set; } = true;
}

public sealed class VmFirmwareConfig
{
    public VmFirmwareType FirmwareType { get; set; } = VmFirmwareType.Uefi;
    public string? CustomCodePath { get; set; }
    public string? CustomVarsTemplatePath { get; set; }
    public string? VarsFilePath { get; set; }
    public bool EnableSecureBoot { get; set; }
}

public sealed class VmSecurityConfig
{
    public bool EnableTpm2 { get; set; }
    public string TpmDeviceModel { get; set; } = "tpm-crb";
    public bool EnableSecureBoot { get; set; }
    public bool IsolateFromHostNetwork { get; set; }
}

public sealed class VmStorageDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "System Disk";
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = "qcow2";
    public string Bus { get; set; } = "virtio-blk";
    public string Media { get; set; } = "disk"; // "disk" or "cdrom"
    public string CacheMode { get; set; } = "writeback";
    public string Discard { get; set; } = "unmap";
    public bool ReadOnly { get; set; }
    public bool IsBootDrive { get; set; } = true;
    public long VirtualSizeBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    public string? ParentBackingFilePath { get; set; }
}

public sealed class VmNetworkAdapter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Default NAT Adapter";
    public VmNetworkMode Mode { get; set; } = VmNetworkMode.UserNat;
    public string Model { get; set; } = "virtio-net-pci";
    public string MacAddress { get; set; } = GenerateRandomMacAddress();
    public string? TapInterfaceName { get; set; }
    public List<PortForwardRule> PortForwards { get; set; } = [];

    public static string GenerateRandomMacAddress()
    {
        Span<byte> bytes = stackalloc byte[3];
        Random.Shared.NextBytes(bytes);
        return $"52:54:00:{bytes[0]:X2}:{bytes[1]:X2}:{bytes[2]:X2}";
    }
}

public sealed class PortForwardRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "SSH";
    public string Protocol { get; set; } = "tcp"; // "tcp" or "udp"
    public string HostBindAddress { get; set; } = "127.0.0.1";
    public int HostPort { get; set; } = 2222;
    public int GuestPort { get; set; } = 22;
    public bool Enabled { get; set; } = true;
}

public sealed class VmGraphicsConfig
{
    public string GpuModel { get; set; } = "virtio-vga";
    public VmDisplayTransport Transport { get; set; } = VmDisplayTransport.VncEmbedded;
    public VmDisplayScalingMode ScalingMode { get; set; } = VmDisplayScalingMode.FitAspectRatio;
    public int PreferredWidth { get; set; } = 1280;
    public int PreferredHeight { get; set; } = 800;
    public int VramMiB { get; set; } = 64;
}

public sealed class VmAudioConfig
{
    public bool Enabled { get; set; } = true;
    public string DeviceModel { get; set; } = "ich9-intel-hda";
    public string HostBackend { get; set; } = "auto";
    public bool Muted { get; set; }
    public int VolumePercent { get; set; } = 100;
}

public sealed class VmInputConfig
{
    public bool EnableUsbTablet { get; set; } = true;
    public bool EnableUsbKeyboard { get; set; } = true;
    public bool EnableUsbMouse { get; set; }
    public string HostKeyShortcut { get; set; } = "Ctrl+Alt";
}

public sealed class VmUsbConfig
{
    public string ControllerModel { get; set; } = "qemu-xhci";
    public List<AttachedUsbDevice> AttachedDevices { get; set; } = [];
}

public sealed class AttachedUsbDevice
{
    public string VendorId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsHostCritical { get; set; }
    public bool Attached { get; set; }
}

public sealed class VmSharedServicesConfig
{
    public bool EnableSharedFolder { get; set; }
    public string? HostFolderPath { get; set; }
    public string MountTag { get; set; } = "hostshare";
    public bool ReadOnly { get; set; }
}

public sealed class VmIntegrationConfig
{
    public bool EnableGuestAgent { get; set; } = true;
    public bool EnableClipboardSharing { get; set; } = true;
    public bool AttachVirtioWinDriverIso { get; set; }
    public string? VirtioWinDriverIsoPath { get; set; }
    public UnattendedInstallConfig UnattendedInstall { get; set; } = new();
}

public sealed class UnattendedInstallConfig
{
    public bool Enabled { get; set; }
    public UnattendedInstallMode Mode { get; set; } = UnattendedInstallMode.Manual;
    public string Username { get; set; } = "user";
    public string FullName { get; set; } = "VM User";
    public string Hostname { get; set; } = "winqemu-vm";
    public bool AutoLogin { get; set; } = true;
    public bool InstallGuestAgent { get; set; } = true;
    public bool BypassWindows11Requirements { get; set; }
    public string? GeneratedSeedMediaPath { get; set; }
}

public sealed class VmBootConfig
{
    public List<string> BootOrder { get; set; } = ["cdrom", "disk", "network"];
    public bool EnableBootMenu { get; set; } = true;
    public string? TemporaryBootOverride { get; set; }
    public bool PauseAtStartup { get; set; }
    public string? DirectKernelPath { get; set; }
    public string? InitrdPath { get; set; }
    public string? KernelCommandLine { get; set; }
}

public sealed class VmDeveloperConfig
{
    public bool EnableDeveloperMode { get; set; }
    public bool EnableSerialConsole { get; set; } = true;
    public bool EnableQmpInspector { get; set; } = true;
    public bool EnableHumanMonitor { get; set; }
    public bool EnableGdbStub { get; set; }
    public int GdbPort { get; set; } = 1234;
    public string? DebugLogFlags { get; set; }
    public bool AllowDestructiveRawQmp { get; set; }
}
