namespace WinQemu.Core.Presets;

public sealed record ResourceRecommendation(
    int RecommendedMemoryMiB,
    int MinimumMemoryMiB,
    int MaximumSafeMemoryMiB,
    int HostTotalMemoryMiB,
    int RecommendedVcpus,
    int MinimumVcpus,
    int MaximumSafeVcpus,
    int HostLogicalProcessors,
    int RecommendedDiskGiB,
    string? WarningMessage);

public static class ResourceRecommender
{
    public static ResourceRecommendation RecommendForHost(
        GuestPreset preset,
        int? hostTotalMemoryMiB = null,
        int? hostLogicalProcessors = null)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var totalHostRamMiB = hostTotalMemoryMiB ?? GetHostTotalMemoryMiB();
        var totalHostCpus = hostLogicalProcessors ?? Math.Max(1, Environment.ProcessorCount);

        // Leave at least 4 GiB or 25% of host RAM for Windows 11 so the host never starves (§16)
        var reservedForHostMiB = Math.Max(4096, totalHostRamMiB / 4);
        var maxSafeRamMiB = Math.Max(preset.MinimumMemoryMiB, totalHostRamMiB - reservedForHostMiB);

        var recRamMiB = Math.Clamp(preset.RecommendedMemoryMiB, preset.MinimumMemoryMiB, maxSafeRamMiB);
        var maxSafeCpus = Math.Max(1, totalHostCpus - 2);
        var recCpus = Math.Clamp(preset.RecommendedVcpus, 1, maxSafeCpus);

        string? warning = null;
        if (preset.RecommendedMemoryMiB > maxSafeRamMiB)
        {
            warning = $"Recommended memory ({preset.RecommendedMemoryMiB / 1024.0:0.#} GB) was adjusted to {recRamMiB / 1024.0:0.#} GB to keep enough memory free for Windows.";
        }

        return new ResourceRecommendation(
            RecommendedMemoryMiB: recRamMiB,
            MinimumMemoryMiB: preset.MinimumMemoryMiB,
            MaximumSafeMemoryMiB: maxSafeRamMiB,
            HostTotalMemoryMiB: totalHostRamMiB,
            RecommendedVcpus: recCpus,
            MinimumVcpus: 1,
            MaximumSafeVcpus: totalHostCpus,
            HostLogicalProcessors: totalHostCpus,
            RecommendedDiskGiB: preset.RecommendedDiskGiB,
            WarningMessage: warning);
    }

    public static string? ValidateResourceSelection(
        GuestPreset preset,
        int selectedMemoryMiB,
        int selectedVcpus,
        int selectedDiskGiB,
        int? hostTotalMemoryMiB = null,
        int? hostLogicalProcessors = null)
    {
        var totalHostRamMiB = hostTotalMemoryMiB ?? GetHostTotalMemoryMiB();
        var totalHostCpus = hostLogicalProcessors ?? Math.Max(1, Environment.ProcessorCount);

        if (selectedMemoryMiB < 128)
        {
            return "Virtual machine memory cannot be less than 128 MB.";
        }

        if (selectedMemoryMiB < preset.MinimumMemoryMiB)
        {
            return $"{preset.DisplayName} requires at least {preset.MinimumMemoryMiB} MB of memory to boot reliably.";
        }

        if (selectedMemoryMiB > totalHostRamMiB - 2048)
        {
            return $"Allocating {selectedMemoryMiB / 1024.0:0.#} GB out of {totalHostRamMiB / 1024.0:0.#} GB host RAM may make Windows unresponsive.";
        }

        if (selectedVcpus > totalHostCpus)
        {
            return $"Allocating more virtual CPUs ({selectedVcpus}) than host logical processors ({totalHostCpus}) can degrade performance.";
        }

        if (selectedDiskGiB < 1)
        {
            return "Virtual disk size must be at least 1 GB.";
        }

        return null;
    }

    public static int GetHostTotalMemoryMiB()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        if (gcInfo.TotalAvailableMemoryBytes > 0)
        {
            return (int)(gcInfo.TotalAvailableMemoryBytes / (1024 * 1024));
        }

        return 16384;
    }
}
