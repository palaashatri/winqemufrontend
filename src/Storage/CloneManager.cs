using System.Text.Json;
using WinQemu.Core.Configuration;
using WinQemu.Core.Models;
using WinQemu.Qemu.QemuImg;

namespace WinQemu.Storage;

public enum VmCloneKind
{
    FullClone,
    LinkedClone
}

/// <summary>
/// Implements full and linked VM cloning, cloning from a specific snapshot, and
/// explicit backing-file dependency graph protection so parent disks with dependent
/// linked clones can never be accidentally deleted (§44, §45).
/// </summary>
public sealed class CloneManager
{
    private readonly VmConfigurationStore _configStore;

    public CloneManager(VmConfigurationStore configStore)
    {
        _configStore = configStore;
    }

    public async Task<VirtualMachine> CloneVmAsync(
        VirtualMachine sourceVm,
        string cloneName,
        VmCloneKind cloneKind,
        string targetLibraryDirectory,
        QemuImgClient qemuImg,
        string? fromSnapshotTag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceVm);
        ArgumentException.ThrowIfNullOrWhiteSpace(cloneName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLibraryDirectory);
        ArgumentNullException.ThrowIfNull(qemuImg);

        // Deep copy configuration via JSON round-trip
        var clonedVm = JsonSerializer.Deserialize<VirtualMachine>(
            JsonSerializer.Serialize(sourceVm, VmConfigurationStore.JsonOptions),
            VmConfigurationStore.JsonOptions)!;

        clonedVm.Identity.Id = Guid.NewGuid().ToString("D");
        clonedVm.Identity.Name = cloneName.Trim();
        clonedVm.Metadata.CreatedAtUtc = DateTimeOffset.UtcNow;
        clonedVm.Metadata.UpdatedAtUtc = DateTimeOffset.UtcNow;
        clonedVm.Metadata.LastStartedAtUtc = null;
        clonedVm.Metadata.ClonedFromVmId = sourceVm.Identity.Id;
        clonedVm.Metadata.ClonedFromSnapshotName = fromSnapshotTag;

        var folderName = VmConfigurationStore.SanitizeFolderName(clonedVm.Identity.Name, clonedVm.Identity.Id);
        var cloneDir = Path.Combine(targetLibraryDirectory, folderName);
        if (Directory.Exists(cloneDir))
        {
            cloneDir = Path.Combine(targetLibraryDirectory, $"{folderName}-{clonedVm.Identity.Id[..8]}");
        }

        VmDirectoryLayout.EnsureStructure(cloneDir);
        clonedVm.DirectoryPath = cloneDir;

        // Regenerate MAC addresses on all network adapters so clones do not collide
        foreach (var nic in clonedVm.Network)
        {
            nic.Id = Guid.NewGuid().ToString("N")[..8];
            nic.MacAddress = VmNetworkAdapter.GenerateRandomMacAddress();
        }

        // Copy per-VM UEFI NVRAM vars file so VMs never share a mutable vars store (§22)
        if (!string.IsNullOrWhiteSpace(sourceVm.Firmware.VarsFilePath) && File.Exists(sourceVm.Firmware.VarsFilePath))
        {
            var destVars = VmDirectoryLayout.GetDefaultVarsPath(cloneDir);
            File.Copy(sourceVm.Firmware.VarsFilePath, destVars, overwrite: true);
            clonedVm.Firmware.VarsFilePath = destVars;
        }
        else if (clonedVm.Firmware.FirmwareType == VmFirmwareType.Uefi)
        {
            clonedVm.Firmware.VarsFilePath = VmDirectoryLayout.GetDefaultVarsPath(cloneDir);
        }

        // Clone storage disks
        for (var i = 0; i < clonedVm.Storage.Count; i++)
        {
            var srcDisk = sourceVm.Storage[i];
            var dstDisk = clonedVm.Storage[i];

            if (!string.Equals(srcDisk.Media, "disk", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(srcDisk.Path) ||
                !File.Exists(srcDisk.Path))
            {
                continue;
            }

            var dstDiskFileName = Path.GetFileName(srcDisk.Path);
            var dstDiskPath = Path.Combine(cloneDir, VmDirectoryLayout.DisksSubdir, dstDiskFileName);

            if (cloneKind == VmCloneKind.LinkedClone && string.IsNullOrWhiteSpace(fromSnapshotTag))
            {
                // Create qcow2 overlay backed by source disk (§45)
                dstDiskPath = Path.ChangeExtension(dstDiskPath, ".qcow2");
                await qemuImg.CreateDiskAsync(
                    filePath: dstDiskPath,
                    sizeBytes: 0,
                    format: "qcow2",
                    backingFilePath: Path.GetFullPath(srcDisk.Path),
                    backingFormat: srcDisk.Format,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                dstDisk.Path = dstDiskPath;
                dstDisk.Format = "qcow2";
                dstDisk.ParentBackingFilePath = Path.GetFullPath(srcDisk.Path);
            }
            else
            {
                // Independent full clone via qemu-img convert (§45)
                dstDiskPath = Path.ChangeExtension(dstDiskPath, ".qcow2");
                await qemuImg.ConvertAsync(
                    sourcePath: srcDisk.Path,
                    destinationPath: dstDiskPath,
                    destinationFormat: "qcow2",
                    compress: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(fromSnapshotTag))
                {
                    await qemuImg.ApplySnapshotAsync(dstDiskPath, fromSnapshotTag, cancellationToken).ConfigureAwait(false);
                }

                dstDisk.Path = dstDiskPath;
                dstDisk.Format = "qcow2";
                dstDisk.ParentBackingFilePath = null;
            }
        }

        await _configStore.SaveAsync(clonedVm, cloneDir, cancellationToken).ConfigureAwait(false);
        return clonedVm;
    }

    public static IReadOnlyList<DiskDependencyNode> BuildDependencyGraph(IReadOnlyList<VirtualMachine> allVms)
    {
        var nodesByPath = new Dictionary<string, DiskDependencyNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in allVms)
        {
            foreach (var disk in vm.Storage.Where(d => d.Media == "disk" && !string.IsNullOrWhiteSpace(d.Path)))
            {
                var fullPath = Path.GetFullPath(disk.Path);
                var parentPath = !string.IsNullOrWhiteSpace(disk.ParentBackingFilePath)
                    ? Path.GetFullPath(disk.ParentBackingFilePath)
                    : null;

                nodesByPath[fullPath] = new DiskDependencyNode
                {
                    VmId = vm.Identity.Id,
                    VmName = vm.Identity.Name,
                    DiskPath = fullPath,
                    ParentDiskPath = parentPath
                };
            }
        }

        foreach (var node in nodesByPath.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentDiskPath) &&
                nodesByPath.TryGetValue(node.ParentDiskPath, out var parentNode))
            {
                parentNode.ChildDiskPaths.Add(node.DiskPath);
            }
        }

        return nodesByPath.Values.ToList();
    }

    /// <summary>
    /// Verifies whether a VM can be safely deleted without breaking linked clones (§45).
    /// Returns the names of any dependent child VMs that would be broken.
    /// </summary>
    public static IReadOnlyList<string> GetDependentLinkedCloneNames(
        VirtualMachine targetVm,
        IReadOnlyList<VirtualMachine> allVms)
    {
        var targetDiskPaths = targetVm.Storage
            .Where(d => d.Media == "disk" && !string.IsNullOrWhiteSpace(d.Path))
            .Select(d => Path.GetFullPath(d.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (targetDiskPaths.Count == 0)
        {
            return [];
        }

        var dependentVms = new List<string>();
        foreach (var otherVm in allVms)
        {
            if (string.Equals(otherVm.Identity.Id, targetVm.Identity.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var disk in otherVm.Storage)
            {
                if (!string.IsNullOrWhiteSpace(disk.ParentBackingFilePath) &&
                    targetDiskPaths.Contains(Path.GetFullPath(disk.ParentBackingFilePath)))
                {
                    dependentVms.Add(otherVm.Identity.Name);
                    break;
                }
            }
        }

        return dependentVms;
    }
}
