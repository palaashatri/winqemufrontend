using System.Net;
using System.Net.Sockets;
using System.Text;
using WinQemu.Core.Models;
using WinQemu.Qemu.Capabilities;

namespace WinQemu.Qemu.CommandBuilder;

public sealed record QemuRuntimeEndpoints(
    int QmpPort,
    int? VncPort,
    int? SerialPort,
    int? GuestAgentPort,
    int? TpmEmulatorPort);

public sealed record QemuFirmwareResolution(
    string? CodeFilePath,
    string? WritableVarsFilePath,
    bool UsePflash);

public sealed record QemuLaunchSpec(
    string ExecutablePath,
    IReadOnlyList<string> ArgumentList,
    string WorkingDirectory,
    string ActiveAccelerator,
    QemuRuntimeEndpoints Endpoints,
    string FormattedCommandLine);

public static class QemuCommandBuilder
{
    public static QemuLaunchSpec Build(
        VirtualMachine vm,
        string executablePath,
        QemuTargetCapabilities? capabilities,
        bool isWhpxWorking,
        QemuRuntimeEndpoints endpoints,
        QemuFirmwareResolution? firmware = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(endpoints);

        var args = new List<string>();

        // 1. VM Identity & UUID
        args.Add("-name");
        args.Add(SanitizeArgumentValue(vm.Identity.Name));

        if (Guid.TryParse(vm.Identity.Id, out var uuid))
        {
            args.Add("-uuid");
            args.Add(uuid.ToString("D"));
        }

        // 2. Machine & Accelerator (§7, §11, §12) — NEVER HAXM
        var activeAccel = ResolveAccelerator(vm, capabilities, isWhpxWorking);
        var machineType = ResolveMachineType(vm, capabilities);

        args.Add("-machine");
        var machineSpec = new StringBuilder(machineType);
        if (vm.Architecture is VmArchitecture.X86_64 or VmArchitecture.I386)
        {
            machineSpec.Append(vm.Machine.EnableAcpi ? ",acpi=on" : ",acpi=off");
        }
        args.Add(machineSpec.ToString());

        args.Add("-accel");
        if (activeAccel == "whpx")
        {
            // kernel-irqchip=off improves compatibility across Windows 11 24H2/25H2 WHPX builds
            args.Add("whpx,kernel-irqchip=off");
        }
        else
        {
            args.Add("tcg");
        }

        // 3. CPU (§17)
        var cpuModel = ResolveCpuModel(vm, activeAccel, capabilities);
        if (!string.IsNullOrWhiteSpace(cpuModel))
        {
            args.Add("-cpu");
            if (vm.Cpu.CpuFlags.Count > 0)
            {
                args.Add($"{cpuModel},{string.Join(",", vm.Cpu.CpuFlags.Select(SanitizeArgumentValue))}");
            }
            else
            {
                args.Add(cpuModel);
            }
        }

        args.Add("-smp");
        var vcpus = string.Equals(machineType, "mac99", StringComparison.OrdinalIgnoreCase)
            ? 1
            : Math.Max(1, vm.Cpu.VcpuCount);
        var sockets = Math.Max(1, vm.Cpu.Sockets);
        var threads = Math.Max(1, vm.Cpu.Threads);
        var cores = Math.Max(1, vcpus / (sockets * threads));
        args.Add($"cpus={vcpus},sockets={sockets},cores={cores},threads={threads}");

        // 4. Memory (§16, §17)
        args.Add("-m");
        args.Add($"{Math.Max(64, vm.Memory.MemoryMiB)}M");

        if (vm.Memory.EnableBallooning &&
            (capabilities is null || capabilities.SupportsDevice("virtio-balloon-pci")) &&
            vm.Architecture is VmArchitecture.X86_64 or VmArchitecture.Aarch64)
        {
            args.Add("-device");
            args.Add("virtio-balloon-pci,id=balloon0");
        }

        // 5. RTC Clock
        if (vm.Architecture is VmArchitecture.X86_64 or VmArchitecture.I386)
        {
            args.Add("-rtc");
            args.Add($"base={(string.Equals(vm.Machine.RtcBase, "localtime", StringComparison.OrdinalIgnoreCase) ? "localtime" : "utc")},clock=host");
        }

        // 6. Firmware / UEFI pflash (§22, §23)
        if (firmware is not null && !string.IsNullOrWhiteSpace(firmware.CodeFilePath))
        {
            if (firmware.UsePflash)
            {
                args.Add("-drive");
                args.Add($"if=pflash,format=raw,unit=0,file={EscapeQemuCommaValue(firmware.CodeFilePath)},readonly=on");
                if (!string.IsNullOrWhiteSpace(firmware.WritableVarsFilePath))
                {
                    args.Add("-drive");
                    args.Add($"if=pflash,format=raw,unit=1,file={EscapeQemuCommaValue(firmware.WritableVarsFilePath)}");
                }
            }
            else
            {
                args.Add("-bios");
                args.Add(firmware.CodeFilePath);
            }
        }

        // 7. TPM 2.0 (§24)
        if (vm.Security.EnableTpm2 && endpoints.TpmEmulatorPort is > 0)
        {
            args.Add("-chardev");
            args.Add($"socket,id=chrtpm,host=127.0.0.1,port={endpoints.TpmEmulatorPort.Value}");
            args.Add("-tpmdev");
            args.Add("emulator,id=tpm0,chardev=chrtpm");
            args.Add("-device");
            var tpmDevice = string.IsNullOrWhiteSpace(vm.Security.TpmDeviceModel)
                ? (vm.Architecture == VmArchitecture.Aarch64 ? "tpm-tis-device" : "tpm-crb")
                : vm.Security.TpmDeviceModel;
            args.Add($"{tpmDevice},tpmdev=tpm0");
        }

        // 8. USB Controller & Input (§31, §35)
        var hasUsbController = false;
        if (!string.IsNullOrWhiteSpace(vm.Usb.ControllerModel) &&
            !string.Equals(vm.Usb.ControllerModel, "none", StringComparison.OrdinalIgnoreCase) &&
            (capabilities is null || capabilities.SupportsDevice(vm.Usb.ControllerModel)))
        {
            args.Add("-device");
            args.Add($"{vm.Usb.ControllerModel},id=usb0");
            hasUsbController = true;
        }
        else if (vm.Input.EnableUsbTablet || vm.Input.EnableUsbKeyboard)
        {
            args.Add("-usb");
            hasUsbController = true;
        }

        if (hasUsbController)
        {
            if (vm.Input.EnableUsbTablet && (capabilities is null || capabilities.SupportsDevice("usb-tablet")))
            {
                args.Add("-device");
                args.Add("usb-tablet,id=input-tablet0");
            }
            if (vm.Input.EnableUsbKeyboard && (capabilities is null || capabilities.SupportsDevice("usb-kbd")))
            {
                args.Add("-device");
                args.Add("usb-kbd,id=input-kbd0");
            }
            if (vm.Input.EnableUsbMouse && (capabilities is null || capabilities.SupportsDevice("usb-mouse")))
            {
                args.Add("-device");
                args.Add("usb-mouse,id=input-mouse0");
            }

            foreach (var usbDev in vm.Usb.AttachedDevices.Where(d => d.Attached))
            {
                if (!string.IsNullOrWhiteSpace(usbDev.VendorId) && !string.IsNullOrWhiteSpace(usbDev.ProductId))
                {
                    args.Add("-device");
                    args.Add($"usb-host,vendorid={usbDev.VendorId},productid={usbDev.ProductId}");
                }
            }
        }

        // 9. Storage Drives & Removable Media (§20, §34)
        AppendStorageArguments(args, vm, capabilities);

        // 10. Networking & Port Forwarding (§25, §26, §27)
        AppendNetworkArguments(args, vm, capabilities);

        // 11. Graphics & Display Transport (§28, §29)
        AppendGraphicsArguments(args, vm, capabilities, endpoints);

        // 12. Audio (§33)
        AppendAudioArguments(args, vm, capabilities);

        // 13. QEMU Guest Agent channel (§36)
        if (vm.Integration.EnableGuestAgent &&
            endpoints.GuestAgentPort is > 0 &&
            (capabilities is null || capabilities.SupportsDevice("virtio-serial-pci")))
        {
            args.Add("-chardev");
            args.Add($"socket,id=qga0,host=127.0.0.1,port={endpoints.GuestAgentPort.Value},server=on,wait=off");
            args.Add("-device");
            args.Add("virtio-serial-pci,id=virtio-serial0");
            args.Add("-device");
            args.Add("virtserialport,chardev=qga0,name=org.qemu.guest_agent.0");
        }

        // 14. Serial Console (§52)
        if (vm.Developer.EnableSerialConsole && endpoints.SerialPort is > 0)
        {
            args.Add("-chardev");
            args.Add($"socket,id=charserial0,host=127.0.0.1,port={endpoints.SerialPort.Value},server=on,wait=off,nodelay=on");
            args.Add("-serial");
            args.Add("chardev:charserial0");
        }

        // 15. QMP Control Socket — Localhost Only (§9, §10)
        args.Add("-qmp");
        args.Add($"tcp:127.0.0.1:{endpoints.QmpPort},server=on,wait=off");

        // 16. Direct Kernel Boot & Developer Options (§51)
        if (!string.IsNullOrWhiteSpace(vm.Boot.DirectKernelPath))
        {
            args.Add("-kernel");
            args.Add(vm.Boot.DirectKernelPath);

            if (!string.IsNullOrWhiteSpace(vm.Boot.InitrdPath))
            {
                args.Add("-initrd");
                args.Add(vm.Boot.InitrdPath);
            }

            if (!string.IsNullOrWhiteSpace(vm.Boot.KernelCommandLine))
            {
                args.Add("-append");
                args.Add(vm.Boot.KernelCommandLine);
            }
        }

        if (vm.Boot.PauseAtStartup)
        {
            args.Add("-S");
        }

        if (vm.Developer.EnableGdbStub && vm.Developer.GdbPort > 0)
        {
            args.Add("-gdb");
            args.Add($"tcp:127.0.0.1:{vm.Developer.GdbPort}");
        }

        // 17. Raw QEMU Escape Hatch (§18)
        foreach (var rawArg in vm.AdvancedQemuArgs)
        {
            if (!string.IsNullOrWhiteSpace(rawArg))
            {
                args.Add(rawArg.Trim());
            }
        }

        var workingDir = !string.IsNullOrWhiteSpace(vm.DirectoryPath) && Directory.Exists(vm.DirectoryPath)
            ? vm.DirectoryPath
            : Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory;

        return new QemuLaunchSpec(
            ExecutablePath: executablePath,
            ArgumentList: args,
            WorkingDirectory: workingDir,
            ActiveAccelerator: activeAccel,
            Endpoints: endpoints,
            FormattedCommandLine: FormatCommandLineForDisplay(executablePath, args));
    }

    public static QemuRuntimeEndpoints AllocateLocalEndpoints(VirtualMachine vm)
    {
        var qmpPort = AllocateFreeLoopbackPort();
        int? vncPort = vm.Graphics.Transport == VmDisplayTransport.VncEmbedded
            ? AllocateFreeVncLoopbackPort()
            : null;
        int? serialPort = vm.Developer.EnableSerialConsole
            ? AllocateFreeLoopbackPort()
            : null;
        int? qgaPort = vm.Integration.EnableGuestAgent
            ? AllocateFreeLoopbackPort()
            : null;
        int? tpmPort = vm.Security.EnableTpm2
            ? AllocateFreeLoopbackPort()
            : null;

        return new QemuRuntimeEndpoints(qmpPort, vncPort, serialPort, qgaPort, tpmPort);
    }

    public static int AllocateFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int AllocateFreeVncLoopbackPort()
    {
        // QEMU -vnc 127.0.0.1:<N> listens on TCP port 5900 + N.
        for (var display = 10; display < 600; display++)
        {
            var port = 5900 + display;
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
            }
        }

        return AllocateFreeLoopbackPort();
    }

    public static string FormatCommandLineForDisplay(string executablePath, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteIfNeeded(executablePath));
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(QuoteIfNeeded(arg));
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> ValidateCustomArguments(IReadOnlyList<string> customArgs)
    {
        var warnings = new List<string>();
        for (var i = 0; i < customArgs.Count; i++)
        {
            var arg = customArgs[i].Trim();
            if (string.Equals(arg, "-accel", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < customArgs.Count &&
                customArgs[i + 1].Contains("hax", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("HAXM is discontinued and unsupported (§7). Use WHPX or TCG instead.");
            }

            if (arg.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Argument '{arg}' binds to 0.0.0.0, which exposes VM services outside localhost (§10, §56).");
            }
        }
        return warnings;
    }

    private static string ResolveAccelerator(
        VirtualMachine vm,
        QemuTargetCapabilities? capabilities,
        bool isWhpxWorking)
    {
        if (!vm.Architecture.SupportsWhpxOnX64Host())
        {
            return "tcg";
        }

        var qemuSupportsWhpx = capabilities is null || capabilities.SupportsAccelerator("whpx");
        return vm.Accelerator switch
        {
            VmAcceleratorMode.Whpx when isWhpxWorking && qemuSupportsWhpx => "whpx",
            VmAcceleratorMode.Auto when isWhpxWorking && qemuSupportsWhpx => "whpx",
            _ => "tcg"
        };
    }

    private static string ResolveMachineType(VirtualMachine vm, QemuTargetCapabilities? capabilities)
    {
        var requested = string.IsNullOrWhiteSpace(vm.Machine.MachineType)
            ? GetDefaultMachineForArch(vm.Architecture)
            : vm.Machine.MachineType;

        if (capabilities is not null && !capabilities.SupportsMachine(requested))
        {
            return GetDefaultMachineForArch(vm.Architecture);
        }

        return requested;
    }

    private static string GetDefaultMachineForArch(VmArchitecture arch) => arch switch
    {
        VmArchitecture.X86_64 => "q35",
        VmArchitecture.I386 => "pc",
        VmArchitecture.Aarch64 => "virt",
        VmArchitecture.Riscv64 => "virt",
        VmArchitecture.Ppc64 => "mac99",
        VmArchitecture.S390x => "s390-ccw-virtio",
        VmArchitecture.Mips64el => "malta",
        VmArchitecture.Sparc64 => "sun4u",
        _ => "q35"
    };

    private static string ResolveCpuModel(
        VirtualMachine vm,
        string activeAccel,
        QemuTargetCapabilities? capabilities)
    {
        var model = vm.Cpu.CpuModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = vm.Architecture switch
            {
                VmArchitecture.Aarch64 => "cortex-a72",
                VmArchitecture.Riscv64 => "rv64",
                VmArchitecture.Ppc64 => "970fx",
                _ => "max"
            };
        }

        if (activeAccel == "whpx" &&
            (string.Equals(model, "max", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(model, "Penryn", StringComparison.OrdinalIgnoreCase)))
        {
            // Under WHPX on Windows 11, qemu64,+ssse3,+sse4.1,+sse4.2,+popcnt
            // avoids unsupported MSR faults that -cpu max / Penryn can trigger on hybrid Intel CPUs.
            return "qemu64,+ssse3,+sse4.1,+sse4.2,+popcnt";
        }

        return model;
    }

    private static void AppendStorageArguments(
        List<string> args,
        VirtualMachine vm,
        QemuTargetCapabilities? capabilities)
    {
        var needsVirtioScsiController = vm.Storage.Any(d =>
            string.Equals(d.Bus, "virtio-scsi", StringComparison.OrdinalIgnoreCase));

        if (needsVirtioScsiController && (capabilities is null || capabilities.SupportsDevice("virtio-scsi-pci")))
        {
            args.Add("-device");
            args.Add("virtio-scsi-pci,id=scsi0");
        }

        // Compute bootindex based on vm.Boot.BootOrder ("cdrom", "disk", "network")
        var cdromOrderBase = GetBootOrderPriority(vm.Boot, "cdrom") * 10;
        var diskOrderBase = GetBootOrderPriority(vm.Boot, "disk") * 10;

        var diskIndex = 0;
        var cdIndex = 0;
        var ahciBusIndex = 0;
        var isQ35 = string.Equals(vm.Machine.MachineType, "q35", StringComparison.OrdinalIgnoreCase);

        foreach (var dev in vm.Storage)
        {
            var isCdrom = string.Equals(dev.Media, "cdrom", StringComparison.OrdinalIgnoreCase);
            if (!isCdrom && string.IsNullOrWhiteSpace(dev.Path))
            {
                continue;
            }

            var driveId = string.IsNullOrWhiteSpace(dev.Id)
                ? (isCdrom ? $"cdrom{cdIndex}" : $"disk{diskIndex}")
                : SanitizeIdentifier(dev.Id);

            if (isCdrom)
            {
                cdIndex++;
                var bootIndex = cdromOrderBase + cdIndex;
                var driveSpec = new StringBuilder($"if=none,id={driveId},media=cdrom,readonly=on");
                if (!string.IsNullOrWhiteSpace(dev.Path))
                {
                    driveSpec.Append($",file={EscapeQemuCommaValue(dev.Path)},format=raw");
                }
                args.Add("-drive");
                args.Add(driveSpec.ToString());

                args.Add("-device");
                if (vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64)
                {
                    if (!needsVirtioScsiController)
                    {
                        args.Insert(args.Count - 1, "-device");
                        args.Insert(args.Count - 1, "virtio-scsi-pci,id=scsi0");
                        needsVirtioScsiController = true;
                    }
                    args.Add($"scsi-cd,drive={driveId},id=dev-{driveId},bootindex={bootIndex}");
                }
                else
                {
                    var busAttr = isQ35 ? $",bus=ide.{ahciBusIndex++}" : string.Empty;
                    args.Add($"ide-cd,drive={driveId},id=dev-{driveId}{busAttr},bootindex={bootIndex}");
                }
                continue;
            }

            diskIndex++;
            var diskBootIdx = (dev.IsBootDrive ? diskOrderBase : diskOrderBase + 5) + diskIndex;
            var format = string.IsNullOrWhiteSpace(dev.Format) ? "qcow2" : dev.Format.ToLowerInvariant();
            var cache = string.IsNullOrWhiteSpace(dev.CacheMode) ? "writeback" : dev.CacheMode.ToLowerInvariant();

            args.Add("-drive");
            args.Add($"if=none,id={driveId},file={EscapeQemuCommaValue(dev.Path)},format={format},cache={cache},discard={(string.IsNullOrWhiteSpace(dev.Discard) ? "unmap" : dev.Discard)}");

            var bus = dev.Bus?.ToLowerInvariant() ?? "virtio-blk";
            args.Add("-device");
            switch (bus)
            {
                case "virtio-scsi":
                    args.Add($"scsi-hd,drive={driveId},id=dev-{driveId},bootindex={diskBootIdx}");
                    break;
                case "nvme":
                    args.Add($"nvme,drive={driveId},serial=nvme{diskIndex},id=dev-{driveId},bootindex={diskBootIdx}");
                    break;
                case "sata" or "ide":
                    var ahciAttr = isQ35 ? $",bus=ide.{ahciBusIndex++}" : string.Empty;
                    args.Add($"ide-hd,drive={driveId},id=dev-{driveId}{ahciAttr},bootindex={diskBootIdx}");
                    break;
                case "usb-storage":
                    args.Add($"usb-storage,drive={driveId},id=dev-{driveId},bootindex={diskBootIdx}");
                    break;
                default:
                    var blkDev = vm.Architecture == VmArchitecture.S390x ? "virtio-blk-ccw" : "virtio-blk-pci";
                    args.Add($"{blkDev},drive={driveId},id=dev-{driveId},bootindex={diskBootIdx}");
                    break;
            }
        }

        // Attach Unattended Seed ISO if enabled and present (§37)
        if (vm.Integration.UnattendedInstall.Enabled &&
            !string.IsNullOrWhiteSpace(vm.Integration.UnattendedInstall.GeneratedSeedMediaPath) &&
            File.Exists(vm.Integration.UnattendedInstall.GeneratedSeedMediaPath))
        {
            args.Add("-drive");
            args.Add($"if=none,id=seediso0,media=cdrom,readonly=on,file={EscapeQemuCommaValue(vm.Integration.UnattendedInstall.GeneratedSeedMediaPath)},format=raw");
            args.Add("-device");
            if (vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64)
            {
                if (!needsVirtioScsiController)
                {
                    args.Insert(args.Count - 1, "-device");
                    args.Insert(args.Count - 1, "virtio-scsi-pci,id=scsi0");
                }
                args.Add("scsi-cd,drive=seediso0,id=dev-seediso0");
            }
            else
            {
                var seedBusAttr = isQ35 ? $",bus=ide.{ahciBusIndex++}" : string.Empty;
                args.Add($"ide-cd,drive=seediso0,id=dev-seediso0{seedBusAttr}");
            }
        }
    }

    private static int GetBootOrderPriority(VmBootConfig boot, string targetMedia)
    {
        if (!string.IsNullOrWhiteSpace(boot.TemporaryBootOverride) &&
            string.Equals(boot.TemporaryBootOverride, targetMedia, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var idx = boot.BootOrder.FindIndex(b => string.Equals(b, targetMedia, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx + 1 : 5;
    }

    private static void AppendNetworkArguments(
        List<string> args,
        VirtualMachine vm,
        QemuTargetCapabilities? capabilities)
    {
        if (vm.Network.Count == 0)
        {
            args.Add("-nic");
            args.Add("none");
            return;
        }

        for (var i = 0; i < vm.Network.Count; i++)
        {
            var nic = vm.Network[i];
            var netId = $"net{i}";
            var model = string.IsNullOrWhiteSpace(nic.Model) ? "virtio-net-pci" : nic.Model;
            if (capabilities is not null && !capabilities.SupportsDevice(model))
            {
                model = vm.Architecture == VmArchitecture.Ppc64 ? "sungem" : "e1000";
            }

            if (nic.Mode == VmNetworkMode.TapBridged && !string.IsNullOrWhiteSpace(nic.TapInterfaceName))
            {
                args.Add("-netdev");
                args.Add($"tap,id={netId},ifname={SanitizeArgumentValue(nic.TapInterfaceName)}");
            }
            else
            {
                var netdevSpec = new StringBuilder($"user,id={netId}");
                if (nic.Mode == VmNetworkMode.Isolated || vm.Security.IsolateFromHostNetwork)
                {
                    netdevSpec.Append(",restrict=on");
                }

                foreach (var rule in nic.PortForwards.Where(r => r.Enabled && r.HostPort is > 0 and <= 65535 && r.GuestPort is > 0 and <= 65535))
                {
                    var proto = string.Equals(rule.Protocol, "udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
                    // Bind only to 127.0.0.1 by default for security (§10, §27, §56)
                    var bindAddr = string.IsNullOrWhiteSpace(rule.HostBindAddress) || rule.HostBindAddress == "0.0.0.0"
                        ? "127.0.0.1"
                        : rule.HostBindAddress;
                    netdevSpec.Append($",hostfwd={proto}:{bindAddr}:{rule.HostPort}-:{rule.GuestPort}");
                }

                args.Add("-netdev");
                args.Add(netdevSpec.ToString());
            }

            args.Add("-device");
            var macPart = !string.IsNullOrWhiteSpace(nic.MacAddress) ? $",mac={nic.MacAddress}" : string.Empty;
            var romPart = vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64
                ? ",romfile="
                : string.Empty;
            args.Add($"{model},netdev={netId},id=nic{i}{macPart}{romPart}");
        }
    }

    private static void AppendGraphicsArguments(
        List<string> args,
        VirtualMachine vm,
        QemuTargetCapabilities? capabilities,
        QemuRuntimeEndpoints endpoints)
    {
        var gpu = vm.Graphics.GpuModel;
        if (!string.IsNullOrWhiteSpace(gpu) && !string.Equals(gpu, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (capabilities is not null && !capabilities.SupportsDevice(gpu) && gpu != "std" && gpu != "VGA")
            {
                gpu = vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64
                    ? "virtio-gpu-pci"
                    : "VGA";
            }

            if (string.Equals(gpu, "std", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(gpu, "VGA", StringComparison.OrdinalIgnoreCase))
            {
                if (vm.Architecture is VmArchitecture.Aarch64 or VmArchitecture.Riscv64)
                {
                    args.Add("-device");
                    args.Add("virtio-gpu-pci");
                }
                else
                {
                    args.Add("-vga");
                    args.Add("std");
                }
            }
            else
            {
                args.Add("-device");
                args.Add(gpu);
            }
        }

        switch (vm.Graphics.Transport)
        {
            case VmDisplayTransport.VncEmbedded when endpoints.VncPort is >= 5900:
                var displayIndex = endpoints.VncPort.Value - 5900;
                args.Add("-display");
                args.Add("none");
                args.Add("-vnc");
                args.Add($"127.0.0.1:{displayIndex}");
                break;

            case VmDisplayTransport.QemuWindow:
                var preferredGui = capabilities?.DisplayBackends.Contains("sdl", StringComparer.OrdinalIgnoreCase) == true
                    ? "sdl"
                    : "gtk";
                args.Add("-display");
                args.Add(preferredGui);
                break;

            case VmDisplayTransport.Headless:
            default:
                args.Add("-display");
                args.Add("none");
                break;
        }
    }

    private static void AppendAudioArguments(
        List<string> args,
        VirtualMachine vm,
        QemuTargetCapabilities? capabilities)
    {
        if (!vm.Audio.Enabled ||
            string.IsNullOrWhiteSpace(vm.Audio.DeviceModel) ||
            string.Equals(vm.Audio.DeviceModel, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var backend = capabilities?.SelectBestAudioBackend(vm.Audio.HostBackend) ?? "dsound";
        if (string.Equals(backend, "none", StringComparison.OrdinalIgnoreCase) || vm.Audio.Muted)
        {
            backend = "none";
        }

        if (capabilities is not null && !capabilities.SupportsDevice(vm.Audio.DeviceModel))
        {
            return;
        }

        args.Add("-audiodev");
        args.Add($"{backend},id=snd0");

        args.Add("-device");
        if (vm.Audio.DeviceModel is "ich9-intel-hda" or "intel-hda")
        {
            args.Add($"{vm.Audio.DeviceModel},id=sound0");
            args.Add("-device");
            args.Add("hda-duplex,audiodev=snd0");
        }
        else
        {
            args.Add($"{vm.Audio.DeviceModel},audiodev=snd0");
        }
    }

    private static string EscapeQemuCommaValue(string value) =>
        value.Replace(",", ",,", StringComparison.Ordinal);

    private static string SanitizeArgumentValue(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "vm"
            : value.Replace("\r", string.Empty).Replace("\n", " ").Trim();

    private static string SanitizeIdentifier(string id)
    {
        var chars = id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "dev0" : new string(chars);
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }

        return arg;
    }
}
