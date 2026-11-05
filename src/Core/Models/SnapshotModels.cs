namespace WinQemu.Core.Models;

public sealed class VmSnapshotRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Tag { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ParentSnapshotId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long VmStateSizeBytes { get; set; }
    public bool IncludesRamState { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class VmSnapshotCatalog
{
    public string VmId { get; set; } = string.Empty;
    public string? CurrentSnapshotId { get; set; }
    public List<VmSnapshotRecord> Snapshots { get; set; } = [];
}

public sealed class DiskDependencyNode
{
    public string VmId { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public string DiskPath { get; set; } = string.Empty;
    public string? ParentDiskPath { get; set; }
    public List<string> ChildDiskPaths { get; set; } = [];
}
