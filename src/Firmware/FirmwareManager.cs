using WinQemu.Core.Configuration;
using WinQemu.Core.Models;
using WinQemu.Qemu.CommandBuilder;
using WinQemu.Qemu.Runtime;

namespace WinQemu.Firmware;

/// <summary>
/// Resolves architecture-appropriate UEFI (OVMF/EDK2), OpenSBI, OpenBIOS, and legacy BIOS firmware
/// and provisions isolated per-VM writable UEFI variable stores (`nvram/vars.fd`) (§22, §23).
/// </summary>
public static class FirmwareManager
{
    public static QemuFirmwareResolution? PrepareFirmwareForVm(
        VirtualMachine vm,
        QemuRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(runtime);

        if (vm.Firmware.FirmwareType == VmFirmwareType.None ||
            !string.IsNullOrWhiteSpace(vm.Boot.DirectKernelPath))
        {
            return null;
        }

        if (vm.Firmware.FirmwareType == VmFirmwareType.Custom &&
            !string.IsNullOrWhiteSpace(vm.Firmware.CustomCodePath) &&
            File.Exists(vm.Firmware.CustomCodePath))
        {
            var varsPath = EnsureWritableVarsCopy(vm, vm.Firmware.CustomVarsTemplatePath);
            var isEdk2Pflash = vm.Firmware.CustomCodePath.EndsWith(".fd", StringComparison.OrdinalIgnoreCase);
            return new QemuFirmwareResolution(
                CodeFilePath: vm.Firmware.CustomCodePath,
                WritableVarsFilePath: varsPath,
                UsePflash: isEdk2Pflash || !string.IsNullOrWhiteSpace(varsPath));
        }

        if (vm.Firmware.FirmwareType == VmFirmwareType.Uefi)
        {
            var (codeFile, varsTemplate) = FindUefiFirmwarePair(
                vm.Architecture,
                vm.Firmware.EnableSecureBoot || vm.Security.EnableSecureBoot,
                runtime.FirmwareFiles);

            if (!string.IsNullOrWhiteSpace(codeFile) && File.Exists(codeFile))
            {
                var writableVars = EnsureWritableVarsCopy(vm, varsTemplate);
                return new QemuFirmwareResolution(
                    CodeFilePath: codeFile,
                    WritableVarsFilePath: writableVars,
                    UsePflash: true);
            }
        }

        // Non-pflash architecture firmware defaults (QEMU automatically loads bundled OpenSBI/OpenBIOS/SeaBIOS from share/)
        return null;
    }

    public static (string? CodePath, string? VarsTemplatePath) FindUefiFirmwarePair(
        VmArchitecture architecture,
        bool preferSecureBoot,
        IReadOnlyList<string> discoveredFirmwareFiles)
    {
        if (discoveredFirmwareFiles.Count == 0)
        {
            return (null, null);
        }

        string? MatchFile(params string[] fileNames)
        {
            foreach (var target in fileNames)
            {
                var hit = discoveredFirmwareFiles.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), target, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return hit;
                }
            }
            return null;
        }

        return architecture switch
        {
            VmArchitecture.X86_64 => (
                CodePath: preferSecureBoot
                    ? (MatchFile("edk2-x86_64-secure-code.fd", "OVMF_CODE.secboot.fd", "edk2-x86_64-code.fd", "OVMF_CODE.fd"))
                    : (MatchFile("edk2-x86_64-code.fd", "OVMF_CODE.fd", "edk2-x86_64-secure-code.fd")),
                VarsTemplatePath: MatchFile("edk2-i386-vars.fd", "OVMF_VARS.fd")),

            VmArchitecture.I386 => (
                CodePath: MatchFile("edk2-i386-code.fd"),
                VarsTemplatePath: MatchFile("edk2-i386-vars.fd")),

            VmArchitecture.Aarch64 => (
                CodePath: MatchFile("edk2-aarch64-code.fd", "QEMU_EFI.fd"),
                VarsTemplatePath: preferSecureBoot ? MatchFile("edk2-arm-vars.fd", "QEMU_VARS.fd") : null),

            VmArchitecture.Riscv64 => (
                CodePath: MatchFile("edk2-riscv-code.fd"),
                VarsTemplatePath: preferSecureBoot ? MatchFile("edk2-riscv-vars.fd") : null),

            _ => (null, null)
        };
    }

    public static bool IsSecureBootFirmwareAvailable(QemuRuntimeInfo runtime)
    {
        return runtime.FirmwareFiles.Any(f =>
        {
            var name = Path.GetFileName(f);
            return string.Equals(name, "edk2-x86_64-secure-code.fd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "OVMF_CODE.secboot.fd", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? EnsureWritableVarsCopy(VirtualMachine vm, string? templatePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
        {
            return null;
        }

        var vmDir = vm.DirectoryPath;
        if (string.IsNullOrWhiteSpace(vmDir))
        {
            return null;
        }

        VmDirectoryLayout.EnsureStructure(vmDir);
        var targetVars = !string.IsNullOrWhiteSpace(vm.Firmware.VarsFilePath)
            ? vm.Firmware.VarsFilePath
            : VmDirectoryLayout.GetDefaultVarsPath(vmDir);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetVars))!);
        if (!File.Exists(targetVars))
        {
            File.Copy(templatePath, targetVars, overwrite: false);
            // Ensure the per-VM copy is writable even if the template was read-only
            var attrs = File.GetAttributes(targetVars);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(targetVars, attrs & ~FileAttributes.ReadOnly);
            }
        }

        vm.Firmware.VarsFilePath = targetVars;
        return targetVars;
    }
}
