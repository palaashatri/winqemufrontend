using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinQemu.Core.Models;

namespace WinQemu.Core.Configuration;

public static class VmDirectoryLayout
{
    public const string ConfigFileName = "vm.json";
    public const string LockFileName = "vm.lock.json";
    public const string SnapshotsFileName = "snapshots.json";
    public const string DisksSubdir = "disks";
    public const string NvramSubdir = "nvram";
    public const string SecuritySubdir = "security";
    public const string TpmSubdir = "security/tpm";
    public const string SnapshotsSubdir = "snapshots";
    public const string LogsSubdir = "logs";
    public const string ScreenshotsSubdir = "screenshots";

    public static void EnsureStructure(string vmDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectory);
        Directory.CreateDirectory(vmDirectory);
        Directory.CreateDirectory(Path.Combine(vmDirectory, DisksSubdir));
        Directory.CreateDirectory(Path.Combine(vmDirectory, NvramSubdir));
        Directory.CreateDirectory(Path.Combine(vmDirectory, SecuritySubdir));
        Directory.CreateDirectory(Path.Combine(vmDirectory, SecuritySubdir, "tpm"));
        Directory.CreateDirectory(Path.Combine(vmDirectory, SnapshotsSubdir));
        Directory.CreateDirectory(Path.Combine(vmDirectory, LogsSubdir));
        Directory.CreateDirectory(Path.Combine(vmDirectory, ScreenshotsSubdir));
    }

    public static string GetConfigPath(string vmDirectory) => Path.Combine(vmDirectory, ConfigFileName);
    public static string GetLockPath(string vmDirectory) => Path.Combine(vmDirectory, LockFileName);
    public static string GetSnapshotsMetadataPath(string vmDirectory) => Path.Combine(vmDirectory, SnapshotsSubdir, SnapshotsFileName);
    public static string GetDefaultSystemDiskPath(string vmDirectory, string extension = "qcow2") =>
        Path.Combine(vmDirectory, DisksSubdir, $"system.{extension}");
    public static string GetDefaultVarsPath(string vmDirectory) =>
        Path.Combine(vmDirectory, NvramSubdir, "vars.fd");
    public static string GetTpmStateDirectory(string vmDirectory) =>
        Path.Combine(vmDirectory, SecuritySubdir, "tpm");
    public static string GetLogsDirectory(string vmDirectory) =>
        Path.Combine(vmDirectory, LogsSubdir);
    public static string GetScreenshotsDirectory(string vmDirectory) =>
        Path.Combine(vmDirectory, ScreenshotsSubdir);
}

public sealed class VmConfigurationStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task SaveAsync(VirtualMachine vm, string vmDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectory);

        VmDirectoryLayout.EnsureStructure(vmDirectory);
        vm.DirectoryPath = vmDirectory;
        vm.SchemaVersion = VirtualMachine.CurrentSchemaVersion;
        vm.Metadata.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var configPath = VmDirectoryLayout.GetConfigPath(vmDirectory);
        var tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";

        var json = JsonSerializer.Serialize(vm, JsonOptions);
        await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, configPath, overwrite: true);
    }

    public async Task<VirtualMachine> LoadAsync(string vmDirectoryOrConfigFile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmDirectoryOrConfigFile);

        var configPath = Directory.Exists(vmDirectoryOrConfigFile)
            ? VmDirectoryLayout.GetConfigPath(vmDirectoryOrConfigFile)
            : vmDirectoryOrConfigFile;

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"VM configuration file not found: {configPath}", configPath);
        }

        var fileInfo = new FileInfo(configPath);
        if (fileInfo.Length > 10 * 1024 * 1024)
        {
            throw new InvalidDataException("VM configuration file exceeds maximum allowed size (10 MiB).");
        }

        var rawJson = await File.ReadAllTextAsync(configPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var migratedJson = SchemaMigrator.MigrateJsonIfNeeded(rawJson);

        var vm = JsonSerializer.Deserialize<VirtualMachine>(migratedJson, JsonOptions)
            ?? throw new InvalidDataException($"Failed to deserialize VM configuration at {configPath}.");

        var vmDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        vm.DirectoryPath = vmDir;
        RebaseRelativePaths(vm, vmDir);
        return vm;
    }

    public async Task<IReadOnlyList<VirtualMachine>> LoadAllFromLibraryAsync(
        string libraryDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(libraryDirectory) || !Directory.Exists(libraryDirectory))
        {
            return [];
        }

        var results = new List<VirtualMachine>();
        foreach (var dir in Directory.EnumerateDirectories(libraryDirectory))
        {
            var configPath = VmDirectoryLayout.GetConfigPath(dir);
            if (!File.Exists(configPath))
            {
                continue;
            }

            try
            {
                var vm = await LoadAsync(dir, cancellationToken).ConfigureAwait(false);
                results.Add(vm);
            }
            catch
            {
                // Corrupt or unreadable VM directories are skipped from normal listing
                // and reported separately by diagnostics.
            }
        }

        return results
            .OrderByDescending(v => v.Metadata.LastStartedAtUtc ?? v.Metadata.CreatedAtUtc)
            .ThenBy(v => v.Identity.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> MoveVmDirectoryAsync(
        VirtualMachine vm,
        string targetParentDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (string.IsNullOrWhiteSpace(vm.DirectoryPath) || !Directory.Exists(vm.DirectoryPath))
        {
            throw new InvalidOperationException("VM does not have an existing source directory.");
        }

        var oldDir = Path.GetFullPath(vm.DirectoryPath);
        var folderName = SanitizeFolderName(vm.Identity.Name, vm.Identity.Id);
        var newDir = Path.GetFullPath(Path.Combine(targetParentDirectory, folderName));
        if (string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            return oldDir;
        }

        if (Directory.Exists(newDir))
        {
            newDir = Path.GetFullPath(Path.Combine(targetParentDirectory, $"{folderName}-{vm.Identity.Id[..8]}"));
        }

        Directory.CreateDirectory(targetParentDirectory);
        Directory.Move(oldDir, newDir);

        RetargetDirectoryPaths(vm, oldDir, newDir);

        await SaveAsync(vm, newDir, cancellationToken).ConfigureAwait(false);
        return newDir;
    }

    public async Task ExportPortablePackageAsync(
        VirtualMachine vm,
        string destinationQvmFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (string.IsNullOrWhiteSpace(vm.DirectoryPath) || !Directory.Exists(vm.DirectoryPath))
        {
            throw new InvalidOperationException("VM directory does not exist.");
        }

        var vmDir = Path.GetFullPath(vm.DirectoryPath);
        var portableCopy = JsonSerializer.Deserialize<VirtualMachine>(
            JsonSerializer.Serialize(vm, JsonOptions), JsonOptions)!;

        var seedPath = vm.Integration.UnattendedInstall.GeneratedSeedMediaPath;
        var seedFull = string.IsNullOrWhiteSpace(seedPath)
            ? null
            : Path.GetFullPath(Path.IsPathRooted(seedPath) ? seedPath : Path.Combine(vmDir, seedPath));

        // Never export generated unattended seed media containing credentials (§37, §58)
        portableCopy.Integration.UnattendedInstall.GeneratedSeedMediaPath = null;
        portableCopy.Integration.UnattendedInstall.Enabled = false;
        portableCopy.Storage.RemoveAll(d =>
            !string.IsNullOrWhiteSpace(d.Path) &&
            ((seedFull is not null && string.Equals(
                Path.GetFullPath(Path.IsPathRooted(d.Path) ? d.Path : Path.Combine(vmDir, d.Path)),
                seedFull,
                StringComparison.OrdinalIgnoreCase)) ||
             string.Equals(Path.GetFileName(d.Path), "express-seed.iso", StringComparison.OrdinalIgnoreCase)));

        // Strip host-specific absolute paths inside the VM directory into portable relative paths (§47)
        foreach (var disk in portableCopy.Storage)
        {
            if (!string.IsNullOrWhiteSpace(disk.Path) && IsUnderDirectory(vmDir, disk.Path))
            {
                disk.Path = Path.GetRelativePath(vmDir, Path.GetFullPath(disk.Path)).Replace('\\', '/');
            }
            disk.ParentBackingFilePath = null;
        }

        if (!string.IsNullOrWhiteSpace(portableCopy.Firmware.VarsFilePath) &&
            IsUnderDirectory(vmDir, portableCopy.Firmware.VarsFilePath))
        {
            portableCopy.Firmware.VarsFilePath = Path.GetRelativePath(vmDir, Path.GetFullPath(portableCopy.Firmware.VarsFilePath)).Replace('\\', '/');
        }

        portableCopy.GuestProfile.InstallerIsoPath = null;
        portableCopy.SharedServices.HostFolderPath = null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationQvmFilePath))!);
        if (File.Exists(destinationQvmFilePath))
        {
            File.Delete(destinationQvmFilePath);
        }

        using var archiveStream = new FileStream(destinationQvmFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);

        var configEntry = zip.CreateEntry(VmDirectoryLayout.ConfigFileName, CompressionLevel.Optimal);
        await using (var entryStream = configEntry.Open())
        {
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(portableCopy, JsonOptions));
            await entryStream.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
        }

        foreach (var file in Directory.EnumerateFiles(vmDir, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (seedFull is not null && string.Equals(Path.GetFullPath(file), seedFull, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(fileName, "express-seed.iso", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, VmDirectoryLayout.ConfigFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, VmDirectoryLayout.LockFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relPath = Path.GetRelativePath(vmDir, file).Replace('\\', '/');
            var entry = zip.CreateEntry(relPath, CompressionLevel.Fastest);
            await using var destStream = entry.Open();
            await using var srcStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await srcStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<VirtualMachine> ImportPortablePackageAsync(
        string qvmFilePath,
        string targetLibraryDirectory,
        CancellationToken cancellationToken = default,
        bool allowExternalHostPathsAndRawArgs = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qvmFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLibraryDirectory);

        if (!File.Exists(qvmFilePath))
        {
            throw new FileNotFoundException("Portable VM package (.qvm) not found.", qvmFilePath);
        }

        var tempExtractDir = Path.Combine(targetLibraryDirectory, $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            var fullDestRoot = Path.GetFullPath(tempExtractDir + Path.DirectorySeparatorChar);
            using (var zip = ZipFile.OpenRead(qvmFilePath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    // Protect against Zip-Slip path traversal (§56)
                    var destinationPath = Path.GetFullPath(Path.Combine(tempExtractDir, entry.FullName));
                    if (!destinationPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SecurityException($"Unsafe archive entry path detected: {entry.FullName}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    await using var inStream = entry.Open();
                    await using var outStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await inStream.CopyToAsync(outStream, cancellationToken).ConfigureAwait(false);
                }
            }

            var vm = await LoadAsync(tempExtractDir, cancellationToken).ConfigureAwait(false);
            ValidateImportedVmConfiguration(vm, tempExtractDir, allowExternalHostPathsAndRawArgs);

            vm.Identity.Id = Guid.NewGuid().ToString("D");
            vm.Metadata.CreatedAtUtc = DateTimeOffset.UtcNow;
            vm.Metadata.UpdatedAtUtc = DateTimeOffset.UtcNow;

            var finalFolderName = SanitizeFolderName(vm.Identity.Name, vm.Identity.Id);
            var finalDir = Path.Combine(targetLibraryDirectory, finalFolderName);
            if (Directory.Exists(finalDir))
            {
                finalDir = Path.Combine(targetLibraryDirectory, $"{finalFolderName}-{vm.Identity.Id[..8]}");
            }

            Directory.Move(tempExtractDir, finalDir);
            RetargetDirectoryPaths(vm, tempExtractDir, finalDir);
            await SaveAsync(vm, finalDir, cancellationToken).ConfigureAwait(false);
            return vm;
        }
        catch
        {
            if (Directory.Exists(tempExtractDir))
            {
                try { Directory.Delete(tempExtractDir, recursive: true); } catch { }
            }
            throw;
        }
    }

    public static string SanitizeFolderName(string vmName, string fallbackId)
    {
        if (string.IsNullOrWhiteSpace(vmName))
        {
            return $"VM-{fallbackId[..8]}";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(vmName.Length);
        foreach (var ch in vmName.Trim())
        {
            if (Array.IndexOf(invalidChars, ch) >= 0 || ch is '.' or '/' or '\\')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        var result = sb.ToString().Trim('_', ' ');
        if (string.IsNullOrWhiteSpace(result) ||
            IsReservedWindowsDeviceName(result))
        {
            return $"VM-{fallbackId[..Math.Min(8, fallbackId.Length)]}";
        }

        return result.Length > 64 ? result[..64] : result;
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ];
        return reserved.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsUnderDirectory(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? RetargetPathIfUnderDirectory(string? path, string oldDir, string newDir)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsUnderDirectory(oldDir, path))
        {
            return path;
        }

        var relative = Path.GetRelativePath(Path.GetFullPath(oldDir), Path.GetFullPath(path));
        return Path.GetFullPath(Path.Combine(newDir, relative));
    }

    private static void RetargetDirectoryPaths(VirtualMachine vm, string oldDir, string newDir)
    {
        foreach (var disk in vm.Storage)
        {
            disk.Path = RetargetPathIfUnderDirectory(disk.Path, oldDir, newDir) ?? string.Empty;
            disk.ParentBackingFilePath = RetargetPathIfUnderDirectory(disk.ParentBackingFilePath, oldDir, newDir);
        }

        vm.Firmware.VarsFilePath = RetargetPathIfUnderDirectory(vm.Firmware.VarsFilePath, oldDir, newDir);
        vm.Firmware.CustomCodePath = RetargetPathIfUnderDirectory(vm.Firmware.CustomCodePath, oldDir, newDir);
        vm.Firmware.CustomVarsTemplatePath = RetargetPathIfUnderDirectory(vm.Firmware.CustomVarsTemplatePath, oldDir, newDir);
        vm.Metadata.LastScreenshotPath = RetargetPathIfUnderDirectory(vm.Metadata.LastScreenshotPath, oldDir, newDir);
        vm.Integration.UnattendedInstall.GeneratedSeedMediaPath =
            RetargetPathIfUnderDirectory(vm.Integration.UnattendedInstall.GeneratedSeedMediaPath, oldDir, newDir);
        vm.Boot.DirectKernelPath = RetargetPathIfUnderDirectory(vm.Boot.DirectKernelPath, oldDir, newDir);
        vm.Boot.InitrdPath = RetargetPathIfUnderDirectory(vm.Boot.InitrdPath, oldDir, newDir);
        vm.DirectoryPath = Path.GetFullPath(newDir);
    }

    private static void ValidateImportedVmConfiguration(
        VirtualMachine vm,
        string extractDir,
        bool allowExternalHostPathsAndRawArgs)
    {
        if (!allowExternalHostPathsAndRawArgs)
        {
            foreach (var disk in vm.Storage)
            {
                if (!string.IsNullOrWhiteSpace(disk.Path) && !IsUnderDirectory(extractDir, disk.Path))
                {
                    throw new SecurityException($"Imported VM configuration references an external disk path '{disk.Path}' without explicit user approval (§58).");
                }

                if (!string.IsNullOrWhiteSpace(disk.ParentBackingFilePath) && !IsUnderDirectory(extractDir, disk.ParentBackingFilePath))
                {
                    throw new SecurityException($"Imported VM configuration references an external backing disk path '{disk.ParentBackingFilePath}' without explicit user approval (§58).");
                }
            }

            string?[] sensitivePaths =
            [
                vm.Firmware.VarsFilePath,
                vm.Firmware.CustomCodePath,
                vm.Firmware.CustomVarsTemplatePath,
                vm.Boot.DirectKernelPath,
                vm.Boot.InitrdPath,
                vm.Integration.UnattendedInstall.GeneratedSeedMediaPath
            ];

            foreach (var p in sensitivePaths)
            {
                if (!string.IsNullOrWhiteSpace(p) && !IsUnderDirectory(extractDir, p))
                {
                    throw new SecurityException($"Imported VM configuration references an external host path '{p}' without explicit user approval (§58).");
                }
            }

            if (vm.AdvancedQemuArgs.Count > 0)
            {
                throw new SecurityException(
                    $"Imported VM configuration contains raw QEMU arguments ({string.Join(" ", vm.AdvancedQemuArgs)}) which require explicit user approval before import (§58).");
            }
        }

        // Strip any residual host-specific optional references outside the package directory
        if (!string.IsNullOrWhiteSpace(vm.GuestProfile.InstallerIsoPath) && !IsUnderDirectory(extractDir, vm.GuestProfile.InstallerIsoPath))
        {
            vm.GuestProfile.InstallerIsoPath = null;
        }
        if (!string.IsNullOrWhiteSpace(vm.Integration.VirtioWinDriverIsoPath) && !IsUnderDirectory(extractDir, vm.Integration.VirtioWinDriverIsoPath))
        {
            vm.Integration.VirtioWinDriverIsoPath = null;
        }
        if (!string.IsNullOrWhiteSpace(vm.SharedServices.HostFolderPath) && !IsUnderDirectory(extractDir, vm.SharedServices.HostFolderPath))
        {
            vm.SharedServices.HostFolderPath = null;
        }
        if (!string.IsNullOrWhiteSpace(vm.Metadata.LastScreenshotPath) && !IsUnderDirectory(extractDir, vm.Metadata.LastScreenshotPath))
        {
            vm.Metadata.LastScreenshotPath = null;
        }
    }

    private static void RebaseRelativePaths(VirtualMachine vm, string vmDir)
    {
        foreach (var disk in vm.Storage)
        {
            if (!string.IsNullOrWhiteSpace(disk.Path) && !Path.IsPathRooted(disk.Path))
            {
                var resolved = Path.GetFullPath(Path.Combine(vmDir, disk.Path));
                if (!IsUnderDirectory(vmDir, resolved))
                {
                    throw new SecurityException($"Relative disk path '{disk.Path}' escapes the VM directory (§58).");
                }
                disk.Path = resolved;
            }
            else if (!string.IsNullOrWhiteSpace(disk.Path) && !File.Exists(disk.Path))
            {
                var candidateInDisks = Path.Combine(vmDir, VmDirectoryLayout.DisksSubdir, Path.GetFileName(disk.Path));
                if (File.Exists(candidateInDisks))
                {
                    disk.Path = candidateInDisks;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(vm.Firmware.VarsFilePath) && !Path.IsPathRooted(vm.Firmware.VarsFilePath))
        {
            var resolvedVars = Path.GetFullPath(Path.Combine(vmDir, vm.Firmware.VarsFilePath));
            if (!IsUnderDirectory(vmDir, resolvedVars))
            {
                throw new SecurityException($"Relative firmware vars path '{vm.Firmware.VarsFilePath}' escapes the VM directory (§58).");
            }
            vm.Firmware.VarsFilePath = resolvedVars;
        }
        else if (!string.IsNullOrWhiteSpace(vm.Firmware.VarsFilePath) && !File.Exists(vm.Firmware.VarsFilePath))
        {
            var candidateVars = VmDirectoryLayout.GetDefaultVarsPath(vmDir);
            if (File.Exists(candidateVars))
            {
                vm.Firmware.VarsFilePath = candidateVars;
            }
        }
    }
}

public sealed class SecurityException(string message) : Exception(message);

public static class SchemaMigrator
{
    public static string MigrateJsonIfNeeded(string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);

        var node = JsonNode.Parse(rawJson) as JsonObject
            ?? throw new InvalidDataException("VM configuration root must be a JSON object.");

        var version = node["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version >= VirtualMachine.CurrentSchemaVersion)
        {
            return rawJson;
        }

        // Migrate v0 / v1 -> v2 (§11)
        if (version < 1)
        {
            // v0 legacy flat fields support: name, ramMb, cpus, arch
            if (node["identity"] is null)
            {
                var legacyName = node["name"]?.GetValue<string>() ?? "Migrated VM";
                var legacyId = node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("D");
                node["identity"] = new JsonObject
                {
                    ["id"] = legacyId,
                    ["name"] = legacyName,
                    ["description"] = "Migrated from schema v0"
                };
            }

            if (node["memory"] is null && node["ramMb"] is JsonNode ramNode)
            {
                node["memory"] = new JsonObject
                {
                    ["memoryMiB"] = ramNode.GetValue<int>(),
                    ["enableBallooning"] = true
                };
            }

            if (node["cpu"] is null && node["cpus"] is JsonNode cpuNode)
            {
                var cpus = cpuNode.GetValue<int>();
                node["cpu"] = new JsonObject
                {
                    ["vcpuCount"] = cpus,
                    ["sockets"] = 1,
                    ["cores"] = cpus,
                    ["threads"] = 1,
                    ["cpuModel"] = "max"
                };
            }

            version = 1;
        }

        if (version == 1)
        {
            // Ensure v2 developer, security, and integration sections exist
            node["developer"] ??= new JsonObject
            {
                ["enableDeveloperMode"] = false,
                ["enableSerialConsole"] = true,
                ["enableQmpInspector"] = true
            };
            node["security"] ??= new JsonObject
            {
                ["enableTpm2"] = false,
                ["tpmDeviceModel"] = "tpm-crb",
                ["enableSecureBoot"] = false
            };
            node["schemaVersion"] = VirtualMachine.CurrentSchemaVersion;
        }

        return node.ToJsonString(VmConfigurationStore.JsonOptions);
    }
}
