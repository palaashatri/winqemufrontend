using WinQemu.Core.Configuration;
using WinQemu.Core.Lifecycle;
using WinQemu.Core.Models;
using WinQemu.Core.Presets;
using WinQemu.Display;
using WinQemu.Firmware;
using WinQemu.Integration;
using WinQemu.Qemu.Acceleration;
using WinQemu.Qemu.Capabilities;
using WinQemu.Qemu.CommandBuilder;
using WinQemu.Qemu.Processes;
using WinQemu.Qemu.QemuImg;
using WinQemu.Qemu.Runtime;
using WinQemu.Storage;
using Xunit;

namespace WinQemu.Tests.QemuIntegration;

public sealed class RequiresQemuFactAttribute : FactAttribute
{
    public RequiresQemuFactAttribute(
        bool requireX86_64 = true,
        bool requireQemuImg = false,
        bool requireAnyMultiArchTarget = false)
    {
        var runtime = QemuRuntimeLocator.Discover();
        if (!runtime.IsAvailable)
        {
            Skip = "QEMU runtime is not installed on this machine.";
            return;
        }

        if (requireX86_64 && string.IsNullOrWhiteSpace(runtime.QemuSystemX86_64Path))
        {
            Skip = "qemu-system-x86_64 binary is not installed on this machine.";
            return;
        }

        if (requireQemuImg && string.IsNullOrWhiteSpace(runtime.QemuImgPath))
        {
            Skip = "qemu-img binary is not installed on this machine.";
            return;
        }

        if (requireAnyMultiArchTarget)
        {
            var multiArchTargets = new[]
            {
                VmArchitecture.I386,
                VmArchitecture.Aarch64,
                VmArchitecture.Riscv64,
                VmArchitecture.Ppc64
            };
            if (!multiArchTargets.Any(a =>
                    runtime.TargetBinaries.TryGetValue(a, out var path) &&
                    !string.IsNullOrWhiteSpace(path) &&
                    File.Exists(path)))
            {
                Skip = "None of the non-x86_64 multi-architecture QEMU target binaries are installed on this machine.";
            }
        }
    }
}

public sealed class RealQemuIntegrationTests
{
    [RequiresQemuFact(requireX86_64: true, requireQemuImg: true)]
    public async Task RealQemu_TcgLifecycle_DiskSnapshotCloneAndVncFramebufferTest()
    {
        var runtime = QemuRuntimeLocator.Discover();
        Assert.True(runtime.IsAvailable && runtime.QemuSystemX86_64Path is not null && runtime.QemuImgPath is not null);

        var testRoot = Path.Combine(Path.GetTempPath(), $"winqemu-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        try
        {
            var vmDir = Path.Combine(testRoot, "LinuxTestVm");
            VmDirectoryLayout.EnsureStructure(vmDir);

            var isoPath = Path.Combine(testRoot, "linux-boot.iso");
            MultiArchBootImageFactory.CreateBootableElToritoIso(
                isoPath,
                "LINUX_X86_64",
                "Ubuntu Linux 24.04 LTS (6.8.0-generic x86_64)",
                "WinQEMU Real-QEMU Integration Verification Guest",
                "x86_64 (64-bit PC) · VirtIO Storage & Network",
                "ubuntu@winqemu-vm:~$ _");

            var preset = GuestPresetCatalog.GetByIdOrDefault("ubuntu");
            var vm = GuestPresetCatalog.CreateVmFromPreset(
                preset,
                "Integration Ubuntu",
                vmDir,
                isoPath,
                customMemoryMiB: 256,
                customVcpus: 1,
                customDiskGiB: 1);

            vm.Accelerator = VmAcceleratorMode.Tcg;
            vm.Firmware.FirmwareType = VmFirmwareType.Bios;

            var qemuImg = new QemuImgClient(runtime.QemuImgPath);
            await qemuImg.CreateDiskAsync(vm.Storage[0].Path, 64L * 1024 * 1024, "qcow2");

            var inspection = await qemuImg.InspectAsync(vm.Storage[0].Path);
            Assert.Equal("QCOW2", inspection.Format);
            Assert.Equal(64L * 1024 * 1024, inspection.VirtualSizeBytes);

            var store = new VmConfigurationStore();
            await store.SaveAsync(vm, vmDir);

            var prober = new QemuCapabilityProber();
            var caps = await prober.ProbeAsync(VmArchitecture.X86_64, runtime.QemuSystemX86_64Path, runtime.VersionString);
            var endpoints = QemuCommandBuilder.AllocateLocalEndpoints(vm);
            var spec = QemuCommandBuilder.Build(vm, runtime.QemuSystemX86_64Path, caps, isWhpxWorking: false, endpoints);

            await using var procManager = new QemuProcessManager();
            var session = await procManager.StartVmAsync(vm, spec);
            Assert.Equal(VmState.Running, session.StateMachine.State);

            // Connect VNC client and verify real framebuffer updates arrive
            await using var vnc = new RfbVncClient();
            await vnc.ConnectLocalhostAsync(endpoints.VncPort!.Value);
            await Task.Delay(600);
            Assert.True(vnc.FramebufferWidth > 0);
            Assert.True(vnc.FramebufferHeight > 0);

            // Pause & Resume via QMP (§71)
            await session.PauseAsync();
            Assert.Equal(VmState.Paused, session.StateMachine.State);
            var pausedStatus = await session.Qmp.QueryStatusAsync();
            Assert.False(pausedStatus.Running);

            await session.ResumeAsync();
            Assert.Equal(VmState.Running, session.StateMachine.State);
            var runningStatus = await session.Qmp.QueryStatusAsync();
            Assert.True(runningStatus.Running);

            // Reset via QMP
            await session.RestartAsync();
            await Task.Delay(300);

            // Stop VM and verify exit
            await session.ForceStopAsync();
            Assert.Equal(VmState.Stopped, session.StateMachine.State);

            // Offline Snapshot create -> restore -> delete (§44)
            var snapManager = new SnapshotManager();
            var snap = await snapManager.CreateSnapshotAsync(vm, "Clean Install", "Baseline snapshot", qemuImg);
            Assert.Equal("Clean Install", snap.DisplayName);

            await snapManager.RestoreSnapshotAsync(vm, snap.Id, qemuImg);

            // Clone VM (Linked Clone & Full Clone) (§45)
            var cloneManager = new CloneManager(store);
            var linkedClone = await cloneManager.CloneVmAsync(vm, "Linked Ubuntu", VmCloneKind.LinkedClone, testRoot, qemuImg);
            Assert.True(File.Exists(linkedClone.Storage[0].Path));
            Assert.NotNull(linkedClone.Storage[0].ParentBackingFilePath);

            // Verify dependency guard blocks deleting parent while linked clone exists
            var deps = CloneManager.GetDependentLinkedCloneNames(vm, [vm, linkedClone]);
            Assert.Contains("Linked Ubuntu", deps);

            await snapManager.DeleteSnapshotAsync(vm, snap.Id, qemuImg);
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    [RequiresQemuFact(requireX86_64: true, requireQemuImg: false)]
    public async Task RealQemu_WhpxProbeAndHardwareAcceleratedSmokeTest()
    {
        var runtime = QemuRuntimeLocator.Discover();
        Assert.True(runtime.IsAvailable && runtime.QemuSystemX86_64Path is not null);

        var report = await WhpxDetector.ProbeAsync(runtime.QemuSystemX86_64Path);
        Assert.NotNull(report.UserFriendlyTitle);

        if (report.Status == WhpxAccelerationStatus.Working)
        {
            var testRoot = Path.Combine(Path.GetTempPath(), $"winqemu-whpx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(testRoot);
            try
            {
                var isoPath = Path.Combine(testRoot, "whpx-boot.iso");
                MultiArchBootImageFactory.CreateBootableElToritoIso(
                    isoPath,
                    "WHPX_TEST",
                    "Windows 11 Pro (x86_64 WHPX Hardware Accelerated)",
                    "WHPX Partition Active",
                    "x86_64 · WHPX",
                    "PS C:\\Windows\\System32> _");

                var preset = GuestPresetCatalog.GetByIdOrDefault("windows-10");
                var vm = GuestPresetCatalog.CreateVmFromPreset(preset, "WHPX Smoke VM", testRoot, isoPath, 256, 2, 1);
                vm.Accelerator = VmAcceleratorMode.Whpx;
                vm.Firmware.FirmwareType = VmFirmwareType.Bios;
                vm.Storage.RemoveAll(d => d.Media == "disk");

                var endpoints = QemuCommandBuilder.AllocateLocalEndpoints(vm);
                var spec = QemuCommandBuilder.Build(vm, runtime.QemuSystemX86_64Path, null, isWhpxWorking: true, endpoints);
                Assert.Equal("whpx", spec.ActiveAccelerator);

                await using var procManager = new QemuProcessManager();
                var session = await procManager.StartVmAsync(vm, spec);
                Assert.Equal(VmState.Running, session.StateMachine.State);
                await Task.Delay(500);
                await session.ForceStopAsync();
                Assert.Equal(VmState.Stopped, session.StateMachine.State);
            }
            finally
            {
                try { Directory.Delete(testRoot, recursive: true); } catch { }
            }
        }
    }

    [RequiresQemuFact(requireX86_64: false, requireQemuImg: false, requireAnyMultiArchTarget: true)]
    public async Task RealQemu_MultiArchitectureBoot_I386_Aarch64_Riscv64_Ppc64Test()
    {
        var runtime = QemuRuntimeLocator.Discover();
        Assert.True(runtime.IsAvailable);

        var testRoot = Path.Combine(Path.GetTempPath(), $"winqemu-multiarch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        try
        {
            var presetIds = new[] { "generic-i386", "generic-aarch64", "generic-riscv64", "macos-ppc64" };
            await using var procManager = new QemuProcessManager();

            foreach (var presetId in presetIds)
            {
                var preset = GuestPresetCatalog.GetByIdOrDefault(presetId);
                if (!runtime.TargetBinaries.TryGetValue(preset.Architecture, out var binPath) || !File.Exists(binPath))
                {
                    continue;
                }

                var vmDir = Path.Combine(testRoot, presetId);
                VmDirectoryLayout.EnsureStructure(vmDir);
                var vm = GuestPresetCatalog.CreateVmFromPreset(preset, $"Test-{presetId}", vmDir, null, 256, 1, 1);
                vm.Storage.Clear();

                var fw = FirmwareManager.PrepareFirmwareForVm(vm, runtime);
                var endpoints = QemuCommandBuilder.AllocateLocalEndpoints(vm);
                var spec = QemuCommandBuilder.Build(vm, binPath, null, isWhpxWorking: false, endpoints, fw);

                var session = await procManager.StartVmAsync(vm, spec);
                Assert.Equal(VmState.Running, session.StateMachine.State);
                var qmpStatus = await session.Qmp.QueryStatusAsync();
                Assert.True(qmpStatus.Running);

                await session.ForceStopAsync();
                Assert.Equal(VmState.Stopped, session.StateMachine.State);
            }
        }
        finally
        {
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }
}
