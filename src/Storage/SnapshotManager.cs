using System.Text;
using System.Text.Json;
using WinQemu.Core.Configuration;
using WinQemu.Core.Models;
using WinQemu.Qemu.Processes;
using WinQemu.Qemu.QemuImg;

namespace WinQemu.Storage;

/// <summary>
/// Manages first-class VM snapshots (§44) across both offline (qemu-img snapshot) and
/// running (QMP human-monitor-command savevm/loadvm/delvm + QGA fsfreeze) states.
/// Never reports success unless confirmed by QEMU / qemu-img.
/// </summary>
public sealed class SnapshotManager
{
    public async Task<VmSnapshotCatalog> LoadCatalogAsync(
        VirtualMachine vm,
        QemuImgClient? qemuImg = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        var vmDir = vm.DirectoryPath
            ?? throw new InvalidOperationException("VM directory path is not set.");

        var metaPath = VmDirectoryLayout.GetSnapshotsMetadataPath(vmDir);
        VmSnapshotCatalog catalog;

        if (File.Exists(metaPath))
        {
            var json = await File.ReadAllTextAsync(metaPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            catalog = JsonSerializer.Deserialize<VmSnapshotCatalog>(json, VmConfigurationStore.JsonOptions)
                ?? new VmSnapshotCatalog { VmId = vm.Identity.Id };
        }
        else
        {
            catalog = new VmSnapshotCatalog { VmId = vm.Identity.Id };
        }

        // Reconcile against actual qcow2 disk snapshots when qemu-img is available and VM is offline
        var addedImportedSnapshots = false;
        var primaryDisk = vm.Storage.FirstOrDefault(d => d.Media == "disk" && d.Format.Equals("qcow2", StringComparison.OrdinalIgnoreCase));
        if (qemuImg is not null && primaryDisk is not null && File.Exists(primaryDisk.Path))
        {
            try
            {
                var inspection = await qemuImg.InspectAsync(primaryDisk.Path, cancellationToken).ConfigureAwait(false);
                foreach (var diskSnap in inspection.Snapshots)
                {
                    if (!catalog.Snapshots.Any(s => string.Equals(s.Tag, diskSnap.Name, StringComparison.Ordinal)))
                    {
                        var stableId = $"qcow2_{diskSnap.Name}";
                        catalog.Snapshots.Add(new VmSnapshotRecord
                        {
                            Id = stableId,
                            Tag = diskSnap.Name,
                            DisplayName = diskSnap.Name,
                            Description = "Imported from QCOW2 snapshot table",
                            CreatedAtUtc = diskSnap.CreatedAtUtc,
                            VmStateSizeBytes = diskSnap.VmStateSizeBytes,
                            IncludesRamState = diskSnap.VmStateSizeBytes > 0
                        });
                        addedImportedSnapshots = true;
                    }
                }
            }
            catch
            {
            }
        }

        if (addedImportedSnapshots)
        {
            try { await SaveCatalogAsync(vm, catalog, cancellationToken).ConfigureAwait(false); } catch { }
        }

        foreach (var snap in catalog.Snapshots)
        {
            snap.IsCurrent = string.Equals(snap.Id, catalog.CurrentSnapshotId, StringComparison.Ordinal);
        }

        return catalog;
    }

    public async Task<VmSnapshotRecord> CreateSnapshotAsync(
        VirtualMachine vm,
        string displayName,
        string description,
        QemuImgClient qemuImg,
        ManagedVmSession? activeSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(qemuImg);

        var primaryDisk = vm.Storage.FirstOrDefault(d => d.Media == "disk")
            ?? throw new InvalidOperationException("VM has no disk attached to snapshot.");

        if (!string.Equals(primaryDisk.Format, "qcow2", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Snapshots require a QCOW2 disk image (current format: {primaryDisk.Format}).");
        }

        var catalog = await LoadCatalogAsync(vm, qemuImg, cancellationToken).ConfigureAwait(false);
        var snapId = Guid.NewGuid().ToString("N")[..10];
        var tag = $"snap_{snapId}";
        var includesRam = false;

        if (activeSession is not null && activeSession.StateMachine.IsActiveProcessState)
        {
            // Online live snapshot via QMP savevm with optional QGA filesystem freeze (§36, §44)
            var frozen = false;
            if (activeSession.GuestAgent is not null)
            {
                frozen = await activeSession.GuestAgent.FsFreezeAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var hmpOut = await activeSession.Qmp.HumanMonitorCommandAsync($"savevm {tag}", cancellationToken)
                    .ConfigureAwait(false);
                if (hmpOut.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                    hmpOut.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"QEMU savevm reported an error: {hmpOut.Trim()}");
                }
                includesRam = true;
            }
            finally
            {
                if (frozen && activeSession.GuestAgent is not null)
                {
                    await activeSession.GuestAgent.FsThawAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            // Offline disk snapshot via qemu-img snapshot -c (§44)
            await qemuImg.CreateSnapshotAsync(primaryDisk.Path, tag, cancellationToken).ConfigureAwait(false);
            var verify = await qemuImg.InspectAsync(primaryDisk.Path, cancellationToken).ConfigureAwait(false);
            if (!verify.Snapshots.Any(s => string.Equals(s.Name, tag, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("QCOW2 image did not confirm creation of the snapshot.");
            }
        }

        var record = new VmSnapshotRecord
        {
            Id = snapId,
            Tag = tag,
            DisplayName = displayName.Trim(),
            Description = description?.Trim() ?? string.Empty,
            ParentSnapshotId = catalog.CurrentSnapshotId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IncludesRamState = includesRam,
            IsCurrent = true
        };

        foreach (var existing in catalog.Snapshots)
        {
            existing.IsCurrent = false;
        }

        catalog.Snapshots.Add(record);
        catalog.CurrentSnapshotId = record.Id;
        await SaveCatalogAsync(vm, catalog, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task RestoreSnapshotAsync(
        VirtualMachine vm,
        string snapshotId,
        QemuImgClient qemuImg,
        ManagedVmSession? activeSession = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<VirtualMachine>? allVms = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        ArgumentNullException.ThrowIfNull(qemuImg);

        CloneManager.EnsureNoDependentLinkedClones(vm, allVms, "restore a snapshot on");

        var catalog = await LoadCatalogAsync(vm, qemuImg, cancellationToken).ConfigureAwait(false);
        var record = catalog.Snapshots.FirstOrDefault(s => string.Equals(s.Id, snapshotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' was not found.");

        var primaryDisk = vm.Storage.FirstOrDefault(d => d.Media == "disk")
            ?? throw new InvalidOperationException("VM has no primary disk.");

        if (activeSession is not null && activeSession.StateMachine.IsActiveProcessState)
        {
            var hmpOut = await activeSession.Qmp.HumanMonitorCommandAsync($"loadvm {record.Tag}", cancellationToken)
                .ConfigureAwait(false);
            if (hmpOut.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                hmpOut.Contains("does not have", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"QEMU loadvm failed: {hmpOut.Trim()}");
            }
        }
        else
        {
            await qemuImg.ApplySnapshotAsync(primaryDisk.Path, record.Tag, cancellationToken).ConfigureAwait(false);
        }

        catalog.CurrentSnapshotId = record.Id;
        foreach (var snap in catalog.Snapshots)
        {
            snap.IsCurrent = string.Equals(snap.Id, record.Id, StringComparison.Ordinal);
        }

        await SaveCatalogAsync(vm, catalog, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameSnapshotAsync(
        VirtualMachine vm,
        string snapshotId,
        string newDisplayName,
        string? newDescription = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDisplayName);

        var catalog = await LoadCatalogAsync(vm, null, cancellationToken).ConfigureAwait(false);
        var record = catalog.Snapshots.FirstOrDefault(s => string.Equals(s.Id, snapshotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' was not found.");

        record.DisplayName = newDisplayName.Trim();
        if (newDescription is not null)
        {
            record.Description = newDescription.Trim();
        }

        await SaveCatalogAsync(vm, catalog, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSnapshotAsync(
        VirtualMachine vm,
        string snapshotId,
        QemuImgClient qemuImg,
        ManagedVmSession? activeSession = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<VirtualMachine>? allVms = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(qemuImg);

        CloneManager.EnsureNoDependentLinkedClones(vm, allVms, "delete a snapshot on");

        var catalog = await LoadCatalogAsync(vm, qemuImg, cancellationToken).ConfigureAwait(false);
        var record = catalog.Snapshots.FirstOrDefault(s => string.Equals(s.Id, snapshotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Snapshot '{snapshotId}' was not found.");

        var primaryDisk = vm.Storage.FirstOrDefault(d => d.Media == "disk")
            ?? throw new InvalidOperationException("VM has no primary disk.");

        if (activeSession is not null && activeSession.StateMachine.IsActiveProcessState)
        {
            var hmpOut = await activeSession.Qmp.HumanMonitorCommandAsync($"delvm {record.Tag}", cancellationToken)
                .ConfigureAwait(false);
            if (hmpOut.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                hmpOut.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                hmpOut.Contains("does not", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"QEMU delvm failed: {hmpOut.Trim()}");
            }
        }
        else if (File.Exists(primaryDisk.Path))
        {
            await qemuImg.DeleteSnapshotAsync(primaryDisk.Path, record.Tag, cancellationToken).ConfigureAwait(false);
        }

        // Re-parent any child snapshots to the deleted snapshot's parent
        foreach (var child in catalog.Snapshots.Where(s => string.Equals(s.ParentSnapshotId, record.Id, StringComparison.Ordinal)))
        {
            child.ParentSnapshotId = record.ParentSnapshotId;
        }

        catalog.Snapshots.Remove(record);
        if (string.Equals(catalog.CurrentSnapshotId, record.Id, StringComparison.Ordinal))
        {
            catalog.CurrentSnapshotId = record.ParentSnapshotId ?? catalog.Snapshots.LastOrDefault()?.Id;
        }

        await SaveCatalogAsync(vm, catalog, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveCatalogAsync(
        VirtualMachine vm,
        VmSnapshotCatalog catalog,
        CancellationToken cancellationToken)
    {
        var vmDir = vm.DirectoryPath
            ?? throw new InvalidOperationException("VM directory path is not set.");

        VmDirectoryLayout.EnsureStructure(vmDir);
        var metaPath = VmDirectoryLayout.GetSnapshotsMetadataPath(vmDir);
        var tempPath = $"{metaPath}.{Guid.NewGuid():N}.tmp";

        var json = JsonSerializer.Serialize(catalog, VmConfigurationStore.JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, metaPath, overwrite: true);
    }
}
