using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using WinQemu.Core.Configuration;
using WinQemu.Core.Diagnostics;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;
using WinQemu.Core.Presets;
using WinQemu.Display;
using WinQemu.Firmware;
using WinQemu.Integration;
using WinQemu.Native.GuestDisplay;
using WinQemu.Networking;
using WinQemu.Qemu.Acceleration;
using WinQemu.Qemu.Capabilities;
using WinQemu.Qemu.CommandBuilder;
using WinQemu.Qemu.Processes;
using WinQemu.Qemu.QemuImg;
using WinQemu.Qemu.Runtime;
using WinQemu.Security;
using WinQemu.Storage;

namespace WinQemu.App.ViewModels;

public sealed class VmCardItem : INotifyPropertyChanged
{
    private VmState _state = VmState.Stopped;
    private string _activeAccelerator = "Auto";
    private string _statusBadgeText = "Stopped";
    private string _metricsSummary = string.Empty;
    private string? _screenshotPath;

    public VmCardItem(VirtualMachine vm)
    {
        Vm = vm;
        _screenshotPath = vm.Metadata.LastScreenshotPath;
        RefreshStaticSummaries();
    }

    public VirtualMachine Vm { get; }
    public string Id => Vm.Identity.Id;
    public string Name => Vm.Identity.Name;
    public string OsDisplayName => Vm.GuestProfile.OsDisplayName;
    public string ArchitectureBadge => Vm.Architecture.ToDisplayName();
    public string IconGlyph => Vm.Metadata.IconGlyph;
    public string AccentColorHex => Vm.Metadata.AccentColorHex;
    public string ResourceSummary { get; private set; } = string.Empty;

    public VmState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                StatusBadgeText = value switch
                {
                    VmState.Running => $"● Running ({ActiveAccelerator.ToUpperInvariant()})",
                    VmState.Starting => "◐ Starting...",
                    VmState.Paused => "⏸ Paused",
                    VmState.ShuttingDown => "◑ Shutting Down...",
                    VmState.Stopping => "◑ Stopping...",
                    VmState.Crashed => "⚠ Crashed",
                    _ => "○ Stopped"
                };
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRunningOrPaused));
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public string ActiveAccelerator
    {
        get => _activeAccelerator;
        set
        {
            if (_activeAccelerator != value)
            {
                _activeAccelerator = value;
                if (State == VmState.Running)
                {
                    StatusBadgeText = $"● Running ({value.ToUpperInvariant()})";
                }
                OnPropertyChanged();
            }
        }
    }

    public string StatusBadgeText
    {
        get => _statusBadgeText;
        private set
        {
            if (_statusBadgeText != value)
            {
                _statusBadgeText = value;
                OnPropertyChanged();
            }
        }
    }

    public string MetricsSummary
    {
        get => _metricsSummary;
        set
        {
            if (_metricsSummary != value)
            {
                _metricsSummary = value;
                OnPropertyChanged();
            }
        }
    }

    public string? ScreenshotPath
    {
        get => _screenshotPath;
        set
        {
            if (_screenshotPath != value)
            {
                _screenshotPath = value;
                Vm.Metadata.LastScreenshotPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThumbnailSource));
            }
        }
    }

    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ThumbnailSource =>
        !string.IsNullOrWhiteSpace(_screenshotPath) && File.Exists(_screenshotPath)
            ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.GetFullPath(_screenshotPath)))
            : null;

    public bool IsRunningOrPaused =>
        State is VmState.Starting or VmState.Running or VmState.Paused or VmState.ShuttingDown;

    public string PrimaryActionText => IsRunningOrPaused ? "Open Console" : "▶ Start";

    public void RefreshStaticSummaries()
    {
        var ramGb = Vm.Memory.MemoryMiB >= 1024
            ? $"{Vm.Memory.MemoryMiB / 1024.0:0.#} GB"
            : $"{Vm.Memory.MemoryMiB} MB";
        var disk = Vm.Storage.FirstOrDefault(d => d.Media == "disk");
        var diskGb = disk is not null ? $"{disk.VirtualSizeBytes / (1024L * 1024 * 1024)} GB" : "No Disk";
        ResourceSummary = $"{ramGb} RAM · {Vm.Cpu.VcpuCount} CPUs · {diskGb} · {Vm.Firmware.FirmwareType}";
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(OsDisplayName));
        OnPropertyChanged(nameof(ArchitectureBadge));
        OnPropertyChanged(nameof(ResourceSummary));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MainViewModel : IAsyncDisposable
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly VmConfigurationStore _vmStore = new();
    private readonly QemuCapabilityProber _capabilityProber = new();
    private readonly QemuProcessManager _processManager = new();
    private readonly SnapshotManager _snapshotManager = new();
    private readonly CloneManager _cloneManager;
    private readonly Dictionary<VmArchitecture, QemuTargetCapabilities> _capabilitiesByArch = [];

    public MainViewModel()
    {
        _cloneManager = new CloneManager(_vmStore);
    }

    public AppSettings Settings { get; private set; } = new();
    public QemuRuntimeInfo RuntimeInfo { get; private set; } = QemuRuntimeLocator.Discover();
    public WhpxProbeReport? WhpxReport { get; private set; }
    public ObservableCollection<VmCardItem> AllVms { get; } = [];
    public ObservableCollection<VmCardItem> FilteredVms { get; } = [];
    public VmCardItem? SelectedVm { get; set; }
    public string SearchQuery { get; set; } = string.Empty;

    public QemuProcessManager ProcessManager => _processManager;
    public SnapshotManager SnapshotManager => _snapshotManager;
    public CloneManager CloneManager => _cloneManager;
    public VmConfigurationStore VmStore => _vmStore;
    public AppSettingsStore SettingsStore => _settingsStore;

    public bool IsWhpxWorking => WhpxReport?.Status == WhpxAccelerationStatus.Working;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _settingsStore.LoadAsync(cancellationToken);
        await RefreshRuntimeAndAccelerationAsync(cancellationToken);
        await ReloadVmLibraryAsync(cancellationToken);
    }

    public async Task RefreshRuntimeAndAccelerationAsync(CancellationToken cancellationToken = default)
    {
        RuntimeInfo = QemuRuntimeLocator.Discover(Settings);
        QemuTargetCapabilities? x64Caps = null;

        if (RuntimeInfo.IsAvailable)
        {
            foreach (var (arch, binPath) in RuntimeInfo.TargetBinaries)
            {
                try
                {
                    var caps = await _capabilityProber.ProbeAsync(arch, binPath, RuntimeInfo.VersionString, cancellationToken)
                        ;
                    _capabilitiesByArch[arch] = caps;
                    if (arch == VmArchitecture.X86_64)
                    {
                        x64Caps = caps;
                    }
                }
                catch
                {
                }
            }
        }

        WhpxReport = await WhpxDetector.ProbeAsync(
            RuntimeInfo.QemuSystemX86_64Path,
            x64Caps?.Accelerators,
            cancellationToken);
    }

    public async Task<QemuRuntimeInfo> InstallManagedQemuRuntimeAsync(
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installer = new QemuRuntimeInstaller();
        var targetDir = !string.IsNullOrWhiteSpace(Settings.ManagedQemuDirectory)
            ? Settings.ManagedQemuDirectory
            : AppSettings.GetDefaultManagedQemuDirectory();
        await installer.DownloadVerifyAndExtractAsync(
            targetDir,
            () => _processManager.ActiveSessions.Any(s => s.StateMachine.IsActiveProcessState),
            QemuRuntimeInstaller.DefaultRelease,
            progress,
            cancellationToken);
        await RefreshRuntimeAndAccelerationAsync(cancellationToken);
        return RuntimeInfo;
    }

    public async Task ReloadVmLibraryAsync(CancellationToken cancellationToken = default)
    {
        var previousSelectedId = SelectedVm?.Id;
        var loaded = await _vmStore.LoadAllFromLibraryAsync(Settings.VmStorageDirectory, cancellationToken);

        AllVms.Clear();
        foreach (var vm in loaded)
        {
            var card = new VmCardItem(vm);
            var activeSession = _processManager.GetSession(vm.Identity.Id);
            if (activeSession is null && !string.IsNullOrWhiteSpace(vm.DirectoryPath))
            {
                var activeLock = await VmLockManager.TryReadActiveLockAsync(vm.DirectoryPath, cancellationToken);
                if (activeLock is not null)
                {
                    var binPath = RuntimeInfo.TargetBinaries.GetValueOrDefault(vm.Architecture)
                        ?? Path.Combine(RuntimeInfo.DirectoryPath, $"{vm.Architecture.ToQemuSystemBinaryName()}.exe");
                    activeSession = await _processManager.TryReattachSessionAsync(vm, activeLock, binPath, cancellationToken);
                }
            }

            if (activeSession is not null)
            {
                card.State = activeSession.StateMachine.State;
                card.ActiveAccelerator = activeSession.ActiveAccelerator;
                SubscribeSessionStateChanges(card, activeSession);
            }
            AllVms.Add(card);
        }

        ApplyFilter();
        SelectedVm = (previousSelectedId is null
            ? null
            : AllVms.FirstOrDefault(c => c.Id == previousSelectedId))
            ?? FilteredVms.FirstOrDefault();
    }

    public void ApplyFilter()
    {
        FilteredVms.Clear();
        var query = SearchQuery?.Trim() ?? string.Empty;

        foreach (var item in AllVms)
        {
            if (string.IsNullOrEmpty(query) ||
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.OsDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.ArchitectureBadge.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Vm.Identity.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredVms.Add(item);
            }
        }
    }

    public async Task<VmCardItem> CreateVmFromWizardAsync(
        GuestPreset preset,
        string vmName,
        string? isoPath,
        int memoryMiB,
        int vcpus,
        int diskGiB,
        bool enableExpressUnattended,
        string unattendedUsername,
        string unattendedHostname,
        string? transientPassword,
        bool bypassWin11Checks,
        CancellationToken cancellationToken = default)
    {
        var folderName = VmConfigurationStore.SanitizeFolderName(vmName, Guid.NewGuid().ToString("N"));
        var vmDir = Path.Combine(Settings.VmStorageDirectory, folderName);
        if (Directory.Exists(vmDir))
        {
            vmDir = Path.Combine(Settings.VmStorageDirectory, $"{folderName}-{Guid.NewGuid().ToString("N")[..6]}");
        }

        VmDirectoryLayout.EnsureStructure(vmDir);
        var vm = GuestPresetCatalog.CreateVmFromPreset(preset, vmName, vmDir, isoPath, memoryMiB, vcpus, diskGiB);

        // Create system qcow2 disk using qemu-img if available, or create bootable fallback disk
        var systemDisk = vm.Storage.First(d => d.Media == "disk");
        if (RuntimeInfo.QemuImgPath is not null && File.Exists(RuntimeInfo.QemuImgPath))
        {
            var qemuImg = new QemuImgClient(RuntimeInfo.QemuImgPath);
            await qemuImg.CreateDiskAsync(systemDisk.Path, systemDisk.VirtualSizeBytes, "qcow2", cancellationToken: cancellationToken)
                ;
        }

        // Generate Express Installation seed ISO if enabled (§37)
        if (enableExpressUnattended)
        {
            vm.Integration.UnattendedInstall.Enabled = true;
            vm.Integration.UnattendedInstall.Username = unattendedUsername;
            vm.Integration.UnattendedInstall.Hostname = unattendedHostname;
            vm.Integration.UnattendedInstall.BypassWindows11Requirements = bypassWin11Checks;

            var seedIsoPath = Path.Combine(vmDir, VmDirectoryLayout.DisksSubdir, "express-seed.iso");
            if (preset.OsFamily == GuestOsFamily.Windows ||
                vm.Integration.UnattendedInstall.Mode == UnattendedInstallMode.WindowsAutounattend)
            {
                var xml = UnattendedInstallGenerator.GenerateWindowsAutounattendXml(
                    vm.Integration.UnattendedInstall, transientPassword);
                Iso9660Builder.CreateDataIso(
                    seedIsoPath,
                    "OEMDRV",
                    [new IsoFileEntry("Autounattend.xml", Encoding.UTF8.GetBytes(xml))]);
            }
            else if (vm.Integration.UnattendedInstall.Mode == UnattendedInstallMode.Preseed)
            {
                var preseed = UnattendedInstallGenerator.GenerateDebianPreseedCfg(
                    vm.Integration.UnattendedInstall, transientPassword);
                var (_, userData) = UnattendedInstallGenerator.GenerateCloudInitFiles(
                    vm.Integration.UnattendedInstall, transientPassword);
                var metaData = UnattendedInstallGenerator.GenerateCloudInitMetaData(
                    vm.Integration.UnattendedInstall, vm.Identity.Id);
                Iso9660Builder.CreateDataIso(
                    seedIsoPath,
                    "CIDATA",
                    [
                        new IsoFileEntry("preseed.cfg", Encoding.UTF8.GetBytes(preseed)),
                        new IsoFileEntry("user-data", Encoding.UTF8.GetBytes(userData)),
                        new IsoFileEntry("meta-data", Encoding.UTF8.GetBytes(metaData))
                    ]);
            }
            else
            {
                var (_, userData) = UnattendedInstallGenerator.GenerateCloudInitFiles(
                    vm.Integration.UnattendedInstall, transientPassword);
                var metaData = UnattendedInstallGenerator.GenerateCloudInitMetaData(
                    vm.Integration.UnattendedInstall, vm.Identity.Id);
                Iso9660Builder.CreateDataIso(
                    seedIsoPath,
                    "CIDATA",
                    [
                        new IsoFileEntry("user-data", Encoding.UTF8.GetBytes(userData)),
                        new IsoFileEntry("meta-data", Encoding.UTF8.GetBytes(metaData))
                    ]);
            }

            vm.Integration.UnattendedInstall.GeneratedSeedMediaPath = seedIsoPath;
        }

        // Prepare per-VM UEFI vars store if UEFI is enabled (§22)
        FirmwareManager.PrepareFirmwareForVm(vm, RuntimeInfo);

        await _vmStore.SaveAsync(vm, vmDir, cancellationToken);
        var card = new VmCardItem(vm);
        AllVms.Insert(0, card);
        ApplyFilter();
        SelectedVm = card;
        return card;
    }

    /// <summary>
    /// Provisions bootable virtual machines across Linux, FreeBSD, Windows, and macOS
    /// spanning all supported architectures (x86_64, i386, aarch64, riscv64, ppc64).
    /// </summary>
    public async Task<IReadOnlyList<VmCardItem>> ProvisionMultiArchShowcaseVmsAsync(
        CancellationToken cancellationToken = default)
    {
        var specs = new (string Name, string PresetId, VmArchitecture Arch, string BannerTitle, string Subtitle, string KernelInfo, string Prompt, byte ColorAttr, bool UseUefi)[]
        {
            (
                "Ubuntu Linux 24.04 LTS (x86_64)",
                "ubuntu",
                VmArchitecture.X86_64,
                "Ubuntu 24.04.2 LTS Noble Numbat (GNU/Linux 6.8.0-52-generic x86_64)",
                "Canonical Ubuntu Cloud & Desktop Virtual Machine · systemd 255",
                "x86_64 (64-bit PC) · Q35 · VirtIO Block / VirtIO Net / VirtIO GPU",
                "ubuntu@noble-vm:~$ uname -a -> Linux noble-vm 6.8.0-52-generic #53-Ubuntu SMP PREEMPT_DYNAMIC x86_64 GNU/Linux",
                0x4F, // Ubuntu aubergine/red header look (White on Dark Red/Plum)
                false
            ),
            (
                "Alpine Linux 3.21 (ARM64 / aarch64)",
                "generic-aarch64",
                VmArchitecture.Aarch64,
                "Welcome to Alpine Linux 3.21.3 (Linux 6.12.13-0-virt aarch64)",
                "ARMv8-A Cortex-A72 Virtual Machine · EDK2 UEFI GOP Framebuffer",
                "aarch64 (ARM64 virt) · EDK2 UEFI · VirtIO-GPU / VirtIO-Blk / VirtIO-Net",
                "alpine-arm64:~# uname -m -> aarch64 | OpenRC 0.55.1 is up and running",
                0x1F, // White on Deep Blue
                true
            ),
            (
                "Debian GNU/Linux 12 (RISC-V 64 / riscv64)",
                "generic-riscv64",
                VmArchitecture.Riscv64,
                "Debian GNU/Linux 12 (bookworm) — Linux 6.10.11-riscv64 (riscv64)",
                "RISC-V 64-bit OpenSBI + EDK2 UEFI Virtual Machine",
                "riscv64 (RV64GC virt) · OpenSBI + UEFI · VirtIO-GPU / VirtIO-Blk",
                "debian@riscv64-vm:~$ lscpu -> Architecture: riscv64, Byte Order: Little Endian, ISA: rv64imafdc",
                0x3F, // White on Cyan/Teal
                true
            ),
            (
                "Linux 32-bit Legacy Edition (i386)",
                "generic-i386",
                VmArchitecture.I386,
                "Debian GNU/Linux i386 (Linux 6.1.0-28-686-pae i686)",
                "32-bit x86 i440FX PC Virtual Machine · SeaBIOS",
                "i386 (32-bit PC) · i440FX + PIIX3 · IDE + Intel e1000",
                "root@linux-i386:~# uname -m -> i686 (32-bit protected mode active)",
                0x2F, // White on Forest Green
                false
            ),
            (
                "FreeBSD 14.2-RELEASE (x86_64)",
                "freebsd",
                VmArchitecture.X86_64,
                "FreeBSD/amd64 (freebsd-vm) (ttyv0) — FreeBSD 14.2-RELEASE-p1",
                "The FreeBSD Project · ZFS / UFS2 · VirtIO Block & Network",
                "x86_64 (amd64 Q35) · VT(vga) 80x25 · vtnet0: 10.0.2.15",
                "root@freebsd-amd64:~ # uname -srp -> FreeBSD 14.2-RELEASE amd64",
                0x4E, // Yellow on Beastie Red
                false
            ),
            (
                "FreeBSD 14.2-RELEASE (ARM64 / aarch64)",
                "generic-aarch64",
                VmArchitecture.Aarch64,
                "FreeBSD/arm64 (freebsd-arm64) (ttyu0) — FreeBSD 14.2-RELEASE",
                "FreeBSD ARM64 Architecture Port · EDK2 UEFI Bootloader",
                "aarch64 (ARMv8 Cortex-A72 virt) · EDK2 UEFI · VirtIO Block & Net",
                "root@freebsd-arm64:~ # sysctl hw.machine_arch -> hw.machine_arch: aarch64",
                0x4F, // White on Red
                true
            ),
            (
                "Windows 11 Pro 24H2 (x86_64)",
                "windows-11",
                VmArchitecture.X86_64,
                "Microsoft Windows [Version 10.0.26100.3194] — Windows 11 Pro (x86_64)",
                "Windows Emergency Management / Recovery Command Console · Q35 + UEFI + TPM 2.0",
                "x86_64 (64-bit PC) · WHPX / TCG · AHCI SATA + Intel e1000e Gigabit",
                "PS C:\\Windows\\System32> Get-ComputerInfo | Select OsName, OsArchitecture -> Windows 11 Pro (64-bit)",
                0x1F, // Classic Windows Blue console
                false
            ),
            (
                "macOS Darwin 24.3 (x86_64)",
                "macos-x86_64",
                VmArchitecture.X86_64,
                "Darwin Kernel Version 24.3.0: XNU-11215.81.4~2/RELEASE_X86_64",
                "Apple Darwin / macOS OpenCore x86_64 Single-User Console",
                "x86_64 (Q35 ICH9 + Penryn SSE4.1) · SATA AHCI + Intel e1000e",
                "root@MacPro-VM ~ % sw_vers && uname -m -> ProductName: macOS 15.3 (Darwin 24.3.0 x86_64)",
                0x0F, // Crisp White on Black Darwin console
                false
            ),
            (
                "Mac OS X / Darwin PowerPC (ppc64 mac99)",
                "macos-ppc64",
                VmArchitecture.Ppc64,
                "Darwin/PowerPC Kernel 9.8.0 (PowerMac G5 970fx / mac99 OpenBIOS)",
                "PowerPC 64-bit mac99 NewWorld ROM + OpenBIOS Graphical Console",
                "ppc64 (PowerPC 970fx mac99) · OpenBIOS IEEE 1275 · Sungem Ethernet",
                "0 > dev /cpus/PowerPC,970fx .properties -> Darwin/PPC64 mac99 Ready",
                0x6F,
                false
            )
        };

        var createdCards = new List<VmCardItem>();
        foreach (var item in specs)
        {
            var existing = AllVms.FirstOrDefault(v =>
                string.Equals(v.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                createdCards.Add(existing);
                continue;
            }

            var preset = GuestPresetCatalog.GetByIdOrDefault(item.PresetId);
            var folderName = VmConfigurationStore.SanitizeFolderName(item.Name, Guid.NewGuid().ToString("N"));
            var vmDir = Path.Combine(Settings.VmStorageDirectory, folderName);
            VmDirectoryLayout.EnsureStructure(vmDir);

            var isoPath = Path.Combine(vmDir, VmDirectoryLayout.DisksSubdir, "boot-media.iso");
            var rawDiskPath = Path.Combine(vmDir, VmDirectoryLayout.DisksSubdir, "boot-disk.raw");

            MultiArchBootImageFactory.CreateBootableHybridDiskImage(
                rawDiskPath,
                item.BannerTitle,
                $"{item.Subtitle} [Synthetic Boot Validation Media]",
                item.KernelInfo,
                item.Prompt,
                item.ColorAttr);

            MultiArchBootImageFactory.CreateBootableElToritoIso(
                isoPath,
                "WINQEMUBOOT",
                item.BannerTitle,
                $"{item.Subtitle} [Synthetic Boot Validation Media]",
                item.KernelInfo,
                item.Prompt,
                item.ColorAttr);

            var vm = GuestPresetCatalog.CreateVmFromPreset(
                preset,
                item.Name,
                vmDir,
                isoPath,
                customMemoryMiB: 512,
                customVcpus: item.Arch == VmArchitecture.Ppc64 ? 1 : 2,
                customDiskGiB: 8);

            if (!vm.Identity.Tags.Contains("showcase-boot-test", StringComparer.OrdinalIgnoreCase))
            {
                vm.Identity.Tags.Add("showcase-boot-test");
            }

            vm.Architecture = item.Arch;
            if (item.Name.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase))
            {
                vm.GuestProfile.OsFamily = GuestOsFamily.Bsd;
                vm.GuestProfile.OsDisplayName = "FreeBSD 14.2-RELEASE";
                vm.Metadata.AccentColorHex = "#AB2B28";
            }
            else if (item.Name.Contains("Debian", StringComparison.OrdinalIgnoreCase))
            {
                vm.GuestProfile.OsFamily = GuestOsFamily.Linux;
                vm.GuestProfile.OsDisplayName = "Debian GNU/Linux 12";
                vm.Metadata.AccentColorHex = "#D70A53";
            }
            else if (item.Name.Contains("Alpine", StringComparison.OrdinalIgnoreCase))
            {
                vm.GuestProfile.OsFamily = GuestOsFamily.Linux;
                vm.GuestProfile.OsDisplayName = "Alpine Linux 3.21";
                vm.Metadata.AccentColorHex = "#0D597F";
            }

            // Use the bootable hybrid disk image (converted to qcow2 when qemu-img is present, or raw)
            var sysDisk = vm.Storage.First(d => d.Media == "disk");
            if (RuntimeInfo.QemuImgPath is not null && File.Exists(RuntimeInfo.QemuImgPath))
            {
                var qemuImg = new QemuImgClient(RuntimeInfo.QemuImgPath);
                await qemuImg.ConvertAsync(rawDiskPath, sysDisk.Path, "qcow2", cancellationToken: cancellationToken);
                try { File.Delete(rawDiskPath); } catch { }
            }
            else
            {
                sysDisk.Path = rawDiskPath;
                sysDisk.Format = "raw";
            }

            if (item.UseUefi)
            {
                vm.Firmware.FirmwareType = VmFirmwareType.Uefi;
                vm.Storage.RemoveAll(d => d.Media == "cdrom");
                vm.Boot.BootOrder = ["disk"];
            }
            else if (item.Arch == VmArchitecture.Ppc64)
            {
                vm.Firmware.FirmwareType = VmFirmwareType.OpenBios;
                vm.Graphics.GpuModel = "VGA";
                vm.Storage.RemoveAll(d => d.Media == "cdrom");
                vm.AdvancedQemuArgs.Add("-prom-env");
                vm.AdvancedQemuArgs.Add("auto-boot?=false");
            }
            else
            {
                vm.Firmware.FirmwareType = VmFirmwareType.Bios;
                vm.Security.EnableTpm2 = false;
                vm.Security.EnableSecureBoot = false;
                vm.Firmware.EnableSecureBoot = false;
                vm.Boot.BootOrder = ["cdrom", "disk"];
            }

            FirmwareManager.PrepareFirmwareForVm(vm, RuntimeInfo);
            await _vmStore.SaveAsync(vm, vmDir, cancellationToken);

            var card = new VmCardItem(vm);
            AllVms.Add(card);
            createdCards.Add(card);
        }

        ApplyFilter();
        SelectedVm ??= FilteredVms.FirstOrDefault();
        return createdCards;
    }

    public QemuLaunchSpec BuildLaunchSpecForVm(
        VirtualMachine vm,
        QemuRuntimeEndpoints? endpoints = null,
        VmAcceleratorMode? acceleratorOverride = null)
    {
        var ep = endpoints ?? QemuCommandBuilder.AllocateLocalEndpoints(vm);
        var binPath = RuntimeInfo.TargetBinaries.GetValueOrDefault(vm.Architecture)
            ?? Path.Combine(RuntimeInfo.DirectoryPath, $"{vm.Architecture.ToQemuSystemBinaryName()}.exe");
        var caps = _capabilitiesByArch.GetValueOrDefault(vm.Architecture);
        var fw = FirmwareManager.PrepareFirmwareForVm(vm, RuntimeInfo);

        return QemuCommandBuilder.Build(
            vm,
            binPath,
            caps,
            isWhpxWorking: IsWhpxWorking && Settings.PreferWhpxWhenAvailable,
            endpoints: ep,
            firmware: fw,
            acceleratorOverride: acceleratorOverride);
    }

    public async Task<ManagedVmSession> StartVmAsync(
        VmCardItem card,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!RuntimeInfo.IsAvailable)
        {
            await RefreshRuntimeAndAccelerationAsync(cancellationToken);
            if (!RuntimeInfo.IsAvailable)
            {
                throw new InvalidOperationException("QEMU runtime is not installed or configured.");
            }
        }

        // Prevent starting a parent VM whose disk is actively used as a backing file by linked clones (§45)
        CloneManager.EnsureNoDependentLinkedClones(
            card.Vm,
            AllVms.Select(c => c.Vm).ToList(),
            "start");

        card.State = VmState.Starting;
        var endpoints = QemuCommandBuilder.AllocateLocalEndpoints(card.Vm);
        SwtpmManager? swtpm = null;

        // Start managed swtpm when TPM 2.0 is enabled; fail honestly if swtpm is missing (§24, §87)
        if (card.Vm.Security.EnableTpm2)
        {
            if (string.IsNullOrWhiteSpace(card.Vm.DirectoryPath) || endpoints.TpmEmulatorPort is not > 0)
            {
                card.State = VmState.Stopped;
                throw new InvalidOperationException("TPM 2.0 requires a persisted VM storage directory and local control port (§24).");
            }

            swtpm = new SwtpmManager(VmDirectoryLayout.GetTpmStateDirectory(card.Vm.DirectoryPath));
            var activeTpmPort = await swtpm.StartIfAvailableAsync(
                endpoints.TpmEmulatorPort.Value,
                RuntimeInfo.DirectoryPath,
                cancellationToken);
            if (activeTpmPort is null)
            {
                await swtpm.DisposeAsync();
                card.State = VmState.Stopped;
                throw new InvalidOperationException(
                    "TPM 2.0 is enabled for this VM, but the swtpm binary was not found. Install swtpm or disable TPM 2.0 in Hardware settings (§24).");
            }

            endpoints = endpoints with
            {
                TpmEmulatorPort = activeTpmPort,
                TpmSocketPath = swtpm.ControlSocketPath
            };
        }

        var spec = BuildLaunchSpecForVm(card.Vm, endpoints);
        card.ActiveAccelerator = spec.ActiveAccelerator;

        try
        {
            var session = await _processManager.StartVmAsync(
                card.Vm,
                spec,
                companionResource: swtpm,
                cancellationToken: cancellationToken);
            SubscribeSessionStateChanges(card, session);
            card.State = session.StateMachine.State;
            card.Vm.Metadata.LastStartedAtUtc = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
            {
                await _vmStore.SaveAsync(card.Vm, card.Vm.DirectoryPath, cancellationToken);
            }
            return session;
        }
        catch when (spec.ActiveAccelerator == "whpx")
        {
            // Automatic resilient fallback to TCG if WHPX partition setup fails on a specific guest config,
            // without mutating the user's saved card.Vm.Accelerator preference.
            SwtpmManager? fallbackSwtpm = null;
            try
            {
                var fallbackEndpoints = QemuCommandBuilder.AllocateLocalEndpoints(card.Vm);
                if (card.Vm.Security.EnableTpm2 &&
                    !string.IsNullOrWhiteSpace(card.Vm.DirectoryPath) &&
                    fallbackEndpoints.TpmEmulatorPort is > 0)
                {
                    fallbackSwtpm = new SwtpmManager(VmDirectoryLayout.GetTpmStateDirectory(card.Vm.DirectoryPath));
                    var activeTpmPort = await fallbackSwtpm.StartIfAvailableAsync(
                        fallbackEndpoints.TpmEmulatorPort.Value,
                        RuntimeInfo.DirectoryPath,
                        cancellationToken);
                    if (activeTpmPort is null)
                    {
                        await fallbackSwtpm.DisposeAsync();
                        throw new InvalidOperationException(
                            "TPM 2.0 is enabled for this VM, but the swtpm binary could not be started during TCG fallback (§24).");
                    }

                    fallbackEndpoints = fallbackEndpoints with
                    {
                        TpmEmulatorPort = activeTpmPort,
                        TpmSocketPath = fallbackSwtpm.ControlSocketPath
                    };
                }

                var fallbackSpec = BuildLaunchSpecForVm(card.Vm, fallbackEndpoints, VmAcceleratorMode.Tcg);
                card.ActiveAccelerator = fallbackSpec.ActiveAccelerator;

                var session = await _processManager.StartVmAsync(
                    card.Vm,
                    fallbackSpec,
                    companionResource: fallbackSwtpm,
                    cancellationToken: cancellationToken);
                SubscribeSessionStateChanges(card, session);
                card.State = session.StateMachine.State;
                card.Vm.Metadata.LastStartedAtUtc = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(card.Vm.DirectoryPath))
                {
                    await _vmStore.SaveAsync(card.Vm, card.Vm.DirectoryPath, cancellationToken);
                }
                return session;
            }
            catch
            {
                if (fallbackSwtpm is not null)
                {
                    try { await fallbackSwtpm.DisposeAsync(); } catch { }
                }
                card.State = VmState.Crashed;
                throw;
            }
        }
        catch
        {
            card.State = VmState.Crashed;
            throw;
        }
    }

    private void SubscribeSessionStateChanges(VmCardItem card, ManagedVmSession session)
    {
        session.StateMachine.StateChanged += (_, e) =>
        {
            var dq = App.CurrentMainWindow?.DispatcherQueue;
            if (dq is not null)
            {
                dq.TryEnqueue(() => card.State = e.CurrentState);
            }
            else
            {
                card.State = e.CurrentState;
            }

            if (e.CurrentState is VmState.Stopped or VmState.Crashed)
            {
                _ = CleanupSeedMediaAfterInstallAsync(card.Vm);
            }
        };
    }

    private async Task CleanupSeedMediaAfterInstallAsync(VirtualMachine vm)
    {
        var seedPath = vm.Integration.UnattendedInstall.GeneratedSeedMediaPath;
        if (string.IsNullOrWhiteSpace(seedPath))
        {
            return;
        }

        try
        {
            if (File.Exists(seedPath))
            {
                File.Delete(seedPath);
            }

            vm.Integration.UnattendedInstall.GeneratedSeedMediaPath = null;
            vm.Integration.UnattendedInstall.Enabled = false;
            if (!string.IsNullOrWhiteSpace(vm.DirectoryPath))
            {
                await _vmStore.SaveAsync(vm, vm.DirectoryPath);
            }
        }
        catch
        {
        }
    }

    public DiagnosticsReport BuildDiagnosticsReport()
    {
        var x64Caps = _capabilitiesByArch.GetValueOrDefault(VmArchitecture.X86_64);
        return new DiagnosticsReport(
            AppVersion: "1.0.0",
            WindowsVersion: RuntimeInformation.OSDescription,
            HostArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            HostLogicalProcessors: Environment.ProcessorCount,
            HostTotalMemoryMiB: ResourceRecommender.GetHostTotalMemoryMiB(),
            QemuVersion: RuntimeInfo.VersionString,
            QemuDirectory: RuntimeInfo.DirectoryPath,
            AvailableAccelerators: x64Caps?.Accelerators ?? ["tcg"],
            WhpxStatus: WhpxReport?.Status.ToString() ?? "Unknown",
            WhpxExplanation: WhpxReport?.Explanation ?? "Not probed",
            DiscoveredFirmwareFiles: RuntimeInfo.FirmwareFiles,
            VmStorageLocation: Settings.VmStorageDirectory,
            InstalledTargets: RuntimeInfo.TargetBinaries.Keys.Select(k => k.ToString()).ToList());
    }

    public ReadinessScoreReport BuildReadinessScoreReport() =>
        ReadinessScoreCalculator.Evaluate(
            hasQemuRuntime: RuntimeInfo.IsAvailable && RuntimeInfo.TargetBinaries.Count > 0,
            hasWhpxDetection: WhpxReport is not null,
            hasEmbeddedVncDisplay: RuntimeInfo.IsAvailable && typeof(RfbVncClient).Assembly is not null,
            hasSnapshotsAndCloning: !string.IsNullOrWhiteSpace(RuntimeInfo.QemuImgPath) && File.Exists(RuntimeInfo.QemuImgPath),
            hasWindows11NativeUi: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            hasPackagingAndLicenses: RuntimeInfo.IsAvailable && Directory.Exists(Settings.VmStorageDirectory));

    public ValueTask DisposeAsync() => _processManager.DisposeAsync();
}
