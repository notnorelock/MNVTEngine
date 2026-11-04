using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan.Device;

/// <summary>What the engine needs to know about one GPU to decide whether and how much it wants it.</summary>
/// <param name="Missing">Requirements the device fails (empty when suitable), in words for the error message.</param>
internal sealed record PhysicalDeviceCandidate(int Index, string Name, PhysicalDeviceType Type, uint ApiVersion, long DeviceLocalBytes, IReadOnlyList<string> Missing)
{
    public bool IsSuitable => Missing.Count == 0;

    public override string ToString() =>
        $"{Name} ({Type}, Vulkan {VulkanVersion.Format(ApiVersion)}, {DeviceLocalBytes / (1024 * 1024)} MB device-local)" +
        (IsSuitable ? string.Empty : $": {string.Join("; ", Missing)}");
}

/// <summary>
/// Picks the GPU: only devices meeting every requirement are eligible; among them a name match with the preference wins,
/// otherwise discrete beats integrated beats virtual beats CPU, then more device-local memory wins.
/// </summary>
internal static class PhysicalDeviceSelector
{
    /// <returns>The chosen candidate, or null when none is suitable.</returns>
    public static PhysicalDeviceCandidate? Select(IReadOnlyList<PhysicalDeviceCandidate> candidates, string? preferredName)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var suitable = candidates.Where(c => c.IsSuitable).ToList();
        if (!string.IsNullOrWhiteSpace(preferredName) &&
            suitable.FirstOrDefault(c => c.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase)) is { } preferred)
        {
            return preferred;
        }

        return suitable
            .OrderByDescending(c => TypeRank(c.Type))
            .ThenByDescending(c => c.DeviceLocalBytes)
            .ThenBy(c => c.Index)
            .FirstOrDefault();
    }

    private static int TypeRank(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.DiscreteGpu => 4,
        PhysicalDeviceType.IntegratedGpu => 3,
        PhysicalDeviceType.VirtualGpu => 2,
        PhysicalDeviceType.Cpu => 1,
        _ => 0,
    };
}

internal static class VulkanVersion
{
    /// <summary>VK_API_VERSION_1_4.</summary>
    public const uint Api14 = (1u << 22) | (4u << 12);

    public static uint Major(uint version) => (version >> 22) & 0x7F;

    public static uint Minor(uint version) => (version >> 12) & 0x3FF;

    public static uint Patch(uint version) => version & 0xFFF;

    /// <summary>True when <paramref name="version"/> is 1.4 or newer (variant bits ignored).</summary>
    public static bool AtLeast14(uint version) => Major(version) > 1 || (Major(version) == 1 && Minor(version) >= 4);

    public static string Format(uint version) => $"{Major(version)}.{Minor(version)}.{Patch(version)}";
}
