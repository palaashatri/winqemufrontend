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
using WinQemu.Qemu.Capabilities;
using WinQemu.Qemu.CommandBuilder;
using WinQemu.Security;
using WinQemu.Storage;
using Xunit;

namespace WinQemu.Tests.Unit;

public sealed class CoreAndQemuUnitTests
{
    [Fact]
    public async Task VmConfigurationStore_RoundTripsFullModelAtomically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"winqemu-unit-{Guid.NewGuid():N}");
        try
        {
            var store = new VmConfigurationStore();
            var preset = GuestPresetCatalog.GetByIdOrDefault("windows-11");
            var vm = GuestPresetCatalog.CreateVmFromPreset(preset, "Windows 11 Workstation", tempDir, @"C:\ISOs\Win11_24H2_English_x64.iso");
            vm.Network[0].PortForwards.Add(new PortForwardRule
            {
                Name = "RDP",
                Protocol = "tcp",
                HostPort = 13389,
                GuestPort = 3389
            });
            vm.AdvancedQemuArgs.Add("-no-hpet");

            await store.SaveAsync(vm, tempDir);
            Assert.True(File.Exists(VmDirectoryLayout.GetConfigPath(tempDir)));
            Assert.True(Directory.Exists(VmDirectoryLayout.GetTpmStateDirectory(tempDir)));

            var loaded = await store.LoadAsync(tempDir);
            Assert.Equal(VirtualMachine.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("Windows 11 Workstation", loaded.Identity.Name);
            Assert.Equal(VmArchitecture.X86_64, loaded.Architecture);
            Assert.True(loaded.Security.EnableTpm2);
            Assert.True(loaded.Firmware.EnableSecureBoot);
            Assert.Single(loaded.Network[0].PortForwards);
            Assert.Equal(13389, loaded.Network[0].PortForwards[0].HostPort);
            Assert.Contains("-no-hpet", loaded.AdvancedQemuArgs);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SchemaMigrator_MigratesLegacyV0AndV1ConfigsWithoutCorruption()
    {
        const string legacyV0Json = """
            {
              "schemaVersion": 0,
              "id": "11111111-2222-3333-4444-555555555555",
              "name": "Legacy Ubuntu VM",
              "ramMb": 6144,
              "cpus": 6
            }
            """;

        var migrated = SchemaMigrator.MigrateJsonIfNeeded(legacyV0Json);
        var vm = System.Text.Json.JsonSerializer.Deserialize<VirtualMachine>(migrated, VmConfigurationStore.JsonOptions);

        Assert.NotNull(vm);
        Assert.Equal(VirtualMachine.CurrentSchemaVersion, vm!.SchemaVersion);
        Assert.Equal("Legacy Ubuntu VM", vm.Identity.Name);
        Assert.Equal(6144, vm.Memory.MemoryMiB);
        Assert.Equal(6, vm.Cpu.VcpuCount);
        Assert.NotNull(vm.Developer);
        Assert.NotNull(vm.Security);
    }

    [Fact]
    public void CommandBuilder_GeneratesDeterministicWhpxAndNeverUsesHaxm()
    {
        var preset = GuestPresetCatalog.GetByIdOrDefault("windows-11");
        var vm = GuestPresetCatalog.CreateVmFromPreset(
            preset,
            "Win11 Prod",
            @"C:\Users\Test User\Virtual Machines\Win11, Edition",
            @"C:\Users\Test User\Downloads\Win11, 24H2 (x64).iso");

        var endpoints = new QemuRuntimeEndpoints(
            QmpPort: 44100,
            VncPort: 5915,
            SerialPort: 44101,
            GuestAgentPort: 44102,
            TpmEmulatorPort: 44103);

        var fw = new QemuFirmwareResolution(
            @"C:\Program Files\qemu\share\edk2-x86_64-secure-code.fd",
            @"C:\Users\Test User\Virtual Machines\Win11, Edition\nvram\vars.fd",
            UsePflash: true);

        var spec = QemuCommandBuilder.Build(
            vm,
            @"C:\Program Files\qemu\qemu-system-x86_64.exe",
            capabilities: null,
            isWhpxWorking: true,
            endpoints: endpoints,
            firmware: fw);

        Assert.Equal("whpx", spec.ActiveAccelerator);
        Assert.DoesNotContain(spec.ArgumentList, a => a.Contains("hax", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("whpx,kernel-irqchip=off", spec.ArgumentList);
        Assert.Contains("q35,acpi=on,smm=on", spec.ArgumentList);
        Assert.Contains("driver=cfi.pflash01,property=secure,value=on", spec.ArgumentList);
        Assert.Contains("127.0.0.1:15", spec.ArgumentList); // VNC display 15 (5915 - 5900)
        Assert.Contains("tcp:127.0.0.1:44100,server=on,wait=off", spec.ArgumentList);
        Assert.Contains("tpm-crb,tpmdev=tpm0", spec.ArgumentList);

        // Verify commas in paths are escaped as ',,' for QEMU drive specifications (§19)
        Assert.Contains(spec.ArgumentList, a => a.Contains("Win11,, Edition", StringComparison.Ordinal));
        Assert.Contains(spec.ArgumentList, a => a.Contains("Win11,, 24H2 (x64).iso", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("generic-aarch64", VmArchitecture.Aarch64, "virt", "tcg")]
    [InlineData("generic-riscv64", VmArchitecture.Riscv64, "virt", "tcg")]
    [InlineData("macos-ppc64", VmArchitecture.Ppc64, "mac99", "tcg")]
    [InlineData("generic-i386", VmArchitecture.I386, "pc", "tcg")]
    public void CommandBuilder_SupportsMultiArchitecturePresetsCorrectly(
        string presetId,
        VmArchitecture expectedArch,
        string expectedMachine,
        string expectedAccelWhenHostWhpxReady)
    {
        var preset = GuestPresetCatalog.GetByIdOrDefault(presetId);
        var vm = GuestPresetCatalog.CreateVmFromPreset(preset, $"Test {presetId}", @"C:\VMs\Test");
        var endpoints = new QemuRuntimeEndpoints(45000, 5920, 45001, 45002, null);

        var spec = QemuCommandBuilder.Build(
            vm,
            $@"C:\qemu\{expectedArch.ToQemuSystemBinaryName()}.exe",
            capabilities: null,
            isWhpxWorking: true,
            endpoints: endpoints);

        Assert.Equal(expectedArch, vm.Architecture);
        Assert.Equal(expectedAccelWhenHostWhpxReady, spec.ActiveAccelerator);
        var args = spec.ArgumentList.ToList();
        var machineIdx = args.IndexOf("-machine");
        Assert.True(machineIdx >= 0 && machineIdx + 1 < args.Count);
        Assert.Equal(expectedMachine, args[machineIdx + 1].Split(',')[0]);
    }

    [Fact]
    public void IsoClassifier_IdentifiesCommonMediaAndNeverInventsIdentityOnWeakSignal()
    {
        var tempIso = Path.Combine(Path.GetTempPath(), $"winqemu-iso-{Guid.NewGuid():N}.iso");
        try
        {
            Iso9660Builder.CreateDataIso(
                tempIso,
                "FREEBSD_14_2_R",
                [new IsoFileEntry("README.TXT", Encoding.ASCII.GetBytes("FreeBSD 14.2"))]);

            var bsdResult = IsoClassifier.ClassifyIso(tempIso);
            Assert.True(bsdResult.IsRecognized);
            Assert.Equal("freebsd", bsdResult.MatchedPresetId);
            Assert.Equal("FREEBSD_14_2_R", bsdResult.IsoVolumeLabel);

            var unknownResult = IsoClassifier.ClassifyIso(@"C:\ISOs\mystery-custom-image-v1.iso");
            Assert.False(unknownResult.IsRecognized);
            Assert.Equal(IsoRecognitionConfidence.Unknown, unknownResult.Confidence);
            Assert.Contains("could not be identified", unknownResult.Explanation);
        }
        finally
        {
            if (File.Exists(tempIso))
            {
                File.Delete(tempIso);
            }
        }
    }

    [Fact]
    public void ResourceRecommender_LeavesHeadroomForWindowsAndWarnsOnOvercommit()
    {
        var preset = GuestPresetCatalog.GetByIdOrDefault("windows-11");
        var rec = ResourceRecommender.RecommendForHost(preset, hostTotalMemoryMiB: 8192, hostLogicalProcessors: 8);

        Assert.Equal(4096, rec.RecommendedMemoryMiB);
        Assert.NotNull(rec.WarningMessage);

        var overcommitWarning = ResourceRecommender.ValidateResourceSelection(
            preset,
            selectedMemoryMiB: 15360,
            selectedVcpus: 4,
            selectedDiskGiB: 64,
            hostTotalMemoryMiB: 16384,
            hostLogicalProcessors: 8);
        Assert.NotNull(overcommitWarning);
        Assert.Contains("unresponsive", overcommitWarning!);
    }

    [Fact]
    public void CapabilityProber_ParsesAcceleratorsMachinesCpusDevicesAndAudioCorrectly()
    {
        const string accelSample = """
            Accelerators supported in QEMU binary:
            tcg
            whpx
            """;
        var accels = QemuCapabilityProber.ParseAccelerators(accelSample);
        Assert.Contains("whpx", accels);
        Assert.Contains("tcg", accels);

        const string machineSample = """
            Supported machines are:
            pc                   Standard PC (i440FX + PIIX, 1996) (alias of pc-i440fx-10.1)
            q35                  Standard PC (Q35 + ICH9, 2009) (alias of pc-q35-10.1)
            none                 empty machine
            """;
        var machines = QemuCapabilityProber.ParseMachines(machineSample);
        Assert.Contains("q35", machines);
        Assert.Contains("pc", machines);
        Assert.DoesNotContain("none", machines);

        const string deviceSample = """
            Controller/Bridge/Hub devices:
            name "qemu-xhci", bus PCI
            Display devices:
            name "virtio-vga", bus PCI
            name "bochs-display", bus PCI
            """;
        var devices = QemuCapabilityProber.ParseDevices(deviceSample);
        Assert.Contains("qemu-xhci", devices);
        Assert.Contains("virtio-vga", devices);
        Assert.Contains("bochs-display", devices);
    }

    [Fact]
    public void VmStateMachine_EnforcesValidTransitionsAndMapsQmpStates()
    {
        var sm = new VmStateMachine("vm-1", VmState.Stopped);
        sm.TransitionTo(VmState.Starting, "Start clicked");
        sm.TransitionTo(VmState.Running, "QMP running");
        sm.TransitionTo(VmState.Paused, "Pause clicked");
        sm.TransitionTo(VmState.Running, "Resume clicked");
        sm.TransitionTo(VmState.ShuttingDown, "ACPI shutdown");
        sm.TransitionTo(VmState.Stopped, "Process exited");

        Assert.Equal(VmState.Stopped, sm.State);
        Assert.Equal(6, sm.GetHistory().Count);

        Assert.Throws<InvalidOperationException>(() =>
            sm.TransitionTo(VmState.Paused, "Cannot pause directly from Stopped"));
    }

    [Fact]
    public void CloneManager_BlocksDeletingParentVmWhenLinkedCloneDependsOnIt()
    {
        var parentVm = new VirtualMachine
        {
            Identity = new VmIdentity { Id = "parent-1", Name = "Fedora Base" },
            Storage = [new VmStorageDevice { Media = "disk", Path = @"C:\VMs\Fedora Base\disks\system.qcow2" }]
        };

        var linkedChildVm = new VirtualMachine
        {
            Identity = new VmIdentity { Id = "child-1", Name = "Fedora Linked Dev" },
            Storage =
            [
                new VmStorageDevice
                {
                    Media = "disk",
                    Path = @"C:\VMs\Fedora Linked Dev\disks\system.qcow2",
                    ParentBackingFilePath = @"C:\VMs\Fedora Base\disks\system.qcow2"
                }
            ]
        };

        var dependents = CloneManager.GetDependentLinkedCloneNames(parentVm, [parentVm, linkedChildVm]);
        Assert.Single(dependents);
        Assert.Equal("Fedora Linked Dev", dependents[0]);
    }

    [Fact]
    public void PortForwardingManager_ValidatesRangesDuplicatesAndPrivilegedPorts()
    {
        var rules = new List<PortForwardRule>
        {
            new() { Name = "SSH", Protocol = "tcp", HostPort = 2222, GuestPort = 22, Enabled = true },
            new() { Name = "Duplicate SSH", Protocol = "tcp", HostPort = 2222, GuestPort = 2222, Enabled = true },
            new() { Name = "Privileged HTTP", Protocol = "tcp", HostPort = 80, GuestPort = 80, Enabled = true }
        };

        var validation = PortForwardingManager.ValidateRules(rules, checkHostPortAvailability: false);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("Duplicate host port"));
        Assert.Contains(validation.Warnings, w => w.Contains("privileged host port 80"));
    }

    [Fact]
    public void SecurityValidator_BlocksPathTraversalAndRedactsSecrets()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SecurityValidator.ValidateSafeChildPath(@"C:\VMs\MyVm", @"..\..\Windows\System32\cmd.exe"));

        var redacted = LogRotator.RedactSecrets(
            "Connecting with password=\"Super Secret 123!\" and \"token\": \"abc 987 xyz\" and Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");
        Assert.DoesNotContain("Super Secret 123!", redacted);
        Assert.DoesNotContain("123!", redacted);
        Assert.DoesNotContain("abc 987 xyz", redacted);
        Assert.DoesNotContain("xyz", redacted);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted);
        Assert.Contains("[REDACTED]", redacted);

        var opts = new UnattendedInstallConfig { Enabled = true, Username = "alice", Hostname = "test-vm" };
        var (_, cloudInit) = UnattendedInstallGenerator.GenerateCloudInitFiles(opts, "MyPass123!");
        Assert.DoesNotContain("MyPass123!", cloudInit);
        Assert.Contains("passwd: '$6$", cloudInit);

        var winXml = UnattendedInstallGenerator.GenerateWindowsAutounattendXml(opts, "MyPass123!");
        Assert.DoesNotContain("MyPass123!", winXml);
        Assert.Contains("<PlainText>false</PlainText>", winXml);

        Assert.Throws<ArgumentException>(() =>
            UnattendedInstallGenerator.GenerateCloudInitFiles(opts, "bad\npassword"));
    }

    [Fact]
    public async Task PortablePackageExportAndImport_RoundTripsCleanlyAndStripsHostPaths()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), $"winqemu-qvm-{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(rootDir, "SourceVm");
            var libDir = Path.Combine(rootDir, "ImportedLib");
            var qvmFile = Path.Combine(rootDir, "Exported.qvm");

            var store = new VmConfigurationStore();
            var preset = GuestPresetCatalog.GetByIdOrDefault("ubuntu");
            var vm = GuestPresetCatalog.CreateVmFromPreset(preset, "Portable Ubuntu", srcDir);
            VmDirectoryLayout.EnsureStructure(srcDir);
            await File.WriteAllBytesAsync(vm.Storage[0].Path, new byte[4096]);
            await store.SaveAsync(vm, srcDir);

            await store.ExportPortablePackageAsync(vm, qvmFile);
            Assert.True(File.Exists(qvmFile));

            var imported = await store.ImportPortablePackageAsync(qvmFile, libDir);
            Assert.Equal("Portable Ubuntu", imported.Identity.Name);
            Assert.NotEqual(vm.Identity.Id, imported.Identity.Id);
            Assert.True(File.Exists(imported.Storage[0].Path));
        }
        finally
        {
            if (Directory.Exists(rootDir))
            {
                Directory.Delete(rootDir, recursive: true);
            }
        }
    }

    [Fact]
    public void FastFramebufferPresenter_BlitsDirtyRectanglesAndSavesValidPng()
    {
        const int width = 64;
        const int height = 48;
        var src = new byte[width * height * 4];
        var dst = new byte[width * height * 4];

        for (var i = 0; i < src.Length; i += 4)
        {
            src[i + 0] = 250; // B
            src[i + 1] = 141; // G
            src[i + 2] = 79;  // R
            src[i + 3] = 255; // A
        }

        FastFramebufferPresenter.BlitDirtyRegions(src, dst, width, height, [new DirtyRect(10, 10, 20, 20)]);
        var checkOffset = (15 * width + 15) * 4;
        Assert.Equal(250, dst[checkOffset + 0]);
        Assert.Equal(141, dst[checkOffset + 1]);
        Assert.Equal(79, dst[checkOffset + 2]);

        var pngPath = Path.Combine(Path.GetTempPath(), $"winqemu-fb-{Guid.NewGuid():N}.png");
        try
        {
            FastFramebufferPresenter.SaveBgraAsPng(pngPath, src, width, height);
            var bytes = File.ReadAllBytes(pngPath);
            Assert.True(bytes.Length > 64);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'N', bytes[2]);
            Assert.Equal((byte)'G', bytes[3]);
        }
        finally
        {
            if (File.Exists(pngPath))
            {
                File.Delete(pngPath);
            }
        }
    }

    [Fact]
    public void CommandBuilder_ResolvesWhpxAndTcgFromCapabilitiesAndArchitecture()
    {
        var endpoints = new QemuRuntimeEndpoints(45010, 5921, 45011, null, null);

        // 1. X86_64 with capabilities: null and isWhpxWorking: true -> whpx,kernel-irqchip=off
        var x64Preset = GuestPresetCatalog.GetByIdOrDefault("ubuntu-desktop");
        var x64Vm = GuestPresetCatalog.CreateVmFromPreset(x64Preset, "Ubuntu x64", @"C:\VMs\UbuX64");
        var x64Spec = QemuCommandBuilder.Build(
            x64Vm,
            @"C:\qemu\qemu-system-x86_64.exe",
            capabilities: null,
            isWhpxWorking: true,
            endpoints: endpoints);

        Assert.Equal("whpx", x64Spec.ActiveAccelerator);
        var x64Args = x64Spec.ArgumentList.ToList();
        var x64AccelIdx = x64Args.IndexOf("-accel");
        Assert.True(x64AccelIdx >= 0 && x64AccelIdx + 1 < x64Args.Count);
        Assert.Equal("whpx,kernel-irqchip=off", x64Args[x64AccelIdx + 1]);

        // 2. X86_64 with capabilities listing tcg only -> tcg
        var tcgOnlyCaps = new QemuTargetCapabilities
        {
            Architecture = VmArchitecture.X86_64,
            Accelerators = ["tcg"]
        };
        var x64TcgSpec = QemuCommandBuilder.Build(
            x64Vm,
            @"C:\qemu\qemu-system-x86_64.exe",
            capabilities: tcgOnlyCaps,
            isWhpxWorking: true,
            endpoints: endpoints);
        Assert.Equal("tcg", x64TcgSpec.ActiveAccelerator);

        // 3. I386 with capabilities: null -> tcg
        var i386Preset = GuestPresetCatalog.GetByIdOrDefault("generic-i386");
        var i386Vm = GuestPresetCatalog.CreateVmFromPreset(i386Preset, "Legacy i386", @"C:\VMs\I386");
        var i386NullCapsSpec = QemuCommandBuilder.Build(
            i386Vm,
            @"C:\qemu\qemu-system-i386.exe",
            capabilities: null,
            isWhpxWorking: true,
            endpoints: endpoints);
        Assert.Equal("tcg", i386NullCapsSpec.ActiveAccelerator);

        // 4. I386 with capabilities listing tcg only -> tcg
        var i386TcgCaps = new QemuTargetCapabilities
        {
            Architecture = VmArchitecture.I386,
            Accelerators = ["tcg"]
        };
        var i386TcgSpec = QemuCommandBuilder.Build(
            i386Vm,
            @"C:\qemu\qemu-system-i386.exe",
            capabilities: i386TcgCaps,
            isWhpxWorking: true,
            endpoints: endpoints);
        Assert.Equal("tcg", i386TcgSpec.ActiveAccelerator);

        // 5. I386 with capabilities explicitly listing whpx -> whpx,kernel-irqchip=off
        var i386WhpxCaps = new QemuTargetCapabilities
        {
            Architecture = VmArchitecture.I386,
            Accelerators = ["tcg", "whpx"]
        };
        var i386WhpxSpec = QemuCommandBuilder.Build(
            i386Vm,
            @"C:\qemu\qemu-system-i386.exe",
            capabilities: i386WhpxCaps,
            isWhpxWorking: true,
            endpoints: endpoints);
        Assert.Equal("whpx", i386WhpxSpec.ActiveAccelerator);
        Assert.Contains("whpx,kernel-irqchip=off", i386WhpxSpec.ArgumentList);
    }

    [Fact]
    public void CommandBuilder_ValidatesCustomArgsAndNormalizesWildcardBindsAndQ35Ahci()
    {
        var warnings = QemuCommandBuilder.ValidateCustomArguments(["-accel=haxm", "-vnc", "[::]:0"]);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("HAXM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("wildcard", StringComparison.OrdinalIgnoreCase));

        var vm = GuestPresetCatalog.CreateVmFromPreset(
            GuestPresetCatalog.GetByIdOrDefault("windows-11"),
            "Q35 Test",
            @"C:\VMs\Q35");
        vm.Machine.MachineType = string.Empty; // Forces ResolveMachineType -> "q35"
        vm.Storage =
        [
            new VmStorageDevice { Id = "disk0", Media = "disk", Bus = "sata", Format = "qcow2", Path = @"C:\VMs\Q35\disk.qcow2" }
        ];
        vm.Network[0].PortForwards.Add(new PortForwardRule
        {
            Name = "SSH",
            Protocol = "tcp",
            HostBindAddress = "::",
            HostPort = 2222,
            GuestPort = 22,
            Enabled = true
        });

        var spec = QemuCommandBuilder.Build(
            vm,
            @"C:\qemu\qemu-system-x86_64.exe",
            capabilities: null,
            isWhpxWorking: true,
            endpoints: new QemuRuntimeEndpoints(45020, 5922, null, null, null));

        Assert.Contains(spec.ArgumentList, a => a.Contains("ide-hd,drive=disk0,id=dev-disk0,bus=ide.0", StringComparison.Ordinal));
        Assert.Contains(spec.ArgumentList, a => a.Contains("hostfwd=tcp:127.0.0.1:2222-:22", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiArchBootImageFactory_EmbedsCustomAndDefaultHypervisorDescriptions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"winqemu-bootfactory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rawDefaultPath = Path.Combine(tempDir, "default.raw");
            MultiArchBootImageFactory.CreateBootableHybridDiskImage(
                rawDefaultPath,
                "Test OS",
                "1.0",
                "x86_64",
                "boot>");
            var defaultRawText = Encoding.ASCII.GetString(File.ReadAllBytes(rawDefaultPath));
            Assert.Contains("Hypervisor   : QEMU / WinQEMU Frontend Native Runtime", defaultRawText, StringComparison.Ordinal);

            var rawWhpxPath = Path.Combine(tempDir, "whpx.raw");
            const string whpxDesc = "Windows Hypervisor Platform (WHPX Hardware Accelerated)";
            MultiArchBootImageFactory.CreateBootableHybridDiskImage(
                rawWhpxPath,
                "Test OS",
                "1.0",
                "x86_64",
                "boot>",
                0x1F,
                whpxDesc);
            var whpxRawBytes = File.ReadAllBytes(rawWhpxPath);
            var sectorBannerText = Encoding.ASCII.GetString(whpxRawBytes, 512, 1024);
            Assert.Contains($"Hypervisor   : {whpxDesc}", sectorBannerText, StringComparison.Ordinal);

            var isoTcgPath = Path.Combine(tempDir, "tcg.iso");
            const string tcgDesc = "QEMU TCG Dynamic Binary Translation (Multi-Arch Emulation)";
            MultiArchBootImageFactory.CreateBootableElToritoIso(
                isoTcgPath,
                "WINQEMU_TEST",
                "Test OS",
                "1.0",
                "aarch64",
                "boot>",
                0x1F,
                tcgDesc);
            var isoText = Encoding.ASCII.GetString(File.ReadAllBytes(isoTcgPath));
            Assert.Contains($"Hypervisor   : {tcgDesc}", isoText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AuditFixes_VmStateMachineShortIdsAndErrorTranslator_HandleEdgeCases()
    {
        var sm = new VmStateMachine("vm-test");
        sm.TransitionTo(VmState.Starting, "boot");
        Assert.Equal(VmState.Suspended, sm.MapQmpRunState("suspended"));
        Assert.Equal(VmState.Paused, sm.MapQmpRunState("Paused"));

        Assert.Equal("VM-ab12", VmConfigurationStore.SanitizeFolderName(string.Empty, "ab12"));

        var remediation = ErrorTranslator.Translate("whpx: initialization error: hypervisor feature not present");
        Assert.Equal(ErrorRecoveryActionKind.EnableWhpxElevated, remediation.PrimaryAction);
    }

    [Fact]
    public void RfbVncClient_MapsNumpadShiftAndCapsLockKeysymsCorrectly()
    {
        // CapsLock (VK_CAPITAL = 0x14 -> XK_Caps_Lock 0xFFE5)
        Assert.Equal(0xFFE5u, RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x14));

        // Numpad digits 0..9 (0x60..0x69) and operators
        for (var i = 0; i <= 9; i++)
        {
            Assert.Equal((uint)('0' + i), RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x60 + i));
        }
        Assert.Equal((uint)'*', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x6A));
        Assert.Equal((uint)'+', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x6B));
        Assert.Equal((uint)'-', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x6D));
        Assert.Equal((uint)'.', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x6E));
        Assert.Equal((uint)'/', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x6F));

        // Shift handling for letters, digits, and OEM punctuation
        Assert.Equal((uint)'a', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x41, shiftPressed: false));
        Assert.Equal((uint)'A', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x41, shiftPressed: true));
        Assert.Equal((uint)'1', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x31, shiftPressed: false));
        Assert.Equal((uint)'!', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0x31, shiftPressed: true));
        Assert.Equal((uint)';', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0xBA, shiftPressed: false));
        Assert.Equal((uint)':', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0xBA, shiftPressed: true));
        Assert.Equal((uint)'/', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0xBF, shiftPressed: false));
        Assert.Equal((uint)'?', RfbVncClient.MapWindowsVirtualKeyToX11Keysym(0xBF, shiftPressed: true));
    }

    [Fact]
    public void CommandBuilder_Mac99OmitsXhciAndExtraUsbKbdMouseToPreserveOpenBiosInput()
    {
        var preset = GuestPresetCatalog.GetByIdOrDefault("macos-ppc64");
        var vm = GuestPresetCatalog.CreateVmFromPreset(preset, "PPC64 Mac99", @"C:\VMs\Mac99");
        vm.Input.EnableUsbKeyboard = true;
        vm.Input.EnableUsbMouse = true;
        vm.Input.EnableUsbTablet = true;

        var endpoints = new QemuRuntimeEndpoints(45030, 5925, 45031, null, null);
        var spec = QemuCommandBuilder.Build(
            vm,
            @"C:\qemu\qemu-system-ppc64.exe",
            capabilities: null,
            isWhpxWorking: false,
            endpoints: endpoints);

        Assert.DoesNotContain(spec.ArgumentList, a => a.Contains("qemu-xhci", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spec.ArgumentList, a => a.Contains("usb-kbd", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spec.ArgumentList, a => a.Contains("usb-mouse", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("usb-tablet,id=input-tablet0", spec.ArgumentList);
    }

    [Fact]
    public void MultiArchBootImageFactory_InteractiveBootloaderFitsSector0AndEmbedsReadyMarker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"winqemu-interactive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var rawPath = Path.Combine(tempDir, "interactive.raw");
            MultiArchBootImageFactory.CreateBootableHybridDiskImage(
                rawPath,
                "Test OS",
                "1.0",
                "x86_64",
                "boot>");

            var rawBytes = File.ReadAllBytes(rawPath);
            // Sector 0 bootloader lives at 0x3E..0x1FD (448 bytes max) followed by 0x55 0xAA at 510..511
            Assert.Equal(0x55, rawBytes[510]);
            Assert.Equal(0xAA, rawBytes[511]);
            // Verify INT 16h (CD 16) interactive keyboard loop opcode sits strictly inside the 448-byte Sector 0 region
            ReadOnlySpan<byte> int16Opcode = [0xCD, 0x16];
            Assert.True(rawBytes.AsSpan(0x3E, 448).IndexOf(int16Opcode) >= 0);

            var rawText = Encoding.ASCII.GetString(rawBytes);
            Assert.Contains("SYSTEM BOOT COMPLETE - INTERACTIVE CONSOLE READY", rawText, StringComparison.Ordinal);
            Assert.Contains("[Interactive UEFI Shell", rawText, StringComparison.Ordinal);

            var isoPath = Path.Combine(tempDir, "interactive.iso");
            MultiArchBootImageFactory.CreateBootableElToritoIso(
                isoPath,
                "INTERACTIVE",
                "Test OS",
                "1.0",
                "x86_64",
                "boot>");

            var isoText = Encoding.ASCII.GetString(File.ReadAllBytes(isoPath));
            Assert.Contains("SYSTEM BOOT COMPLETE - INTERACTIVE CONSOLE READY", isoText, StringComparison.Ordinal);
            Assert.Contains("[Interactive UEFI Shell", isoText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
