using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using MNVTEngine.Graphics.Vulkan.Instance;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace MNVTEngine.Graphics.Vulkan.Device;

/// <summary>
/// The chosen physical device and the logical device: one graphics queue (and a present queue, usually the same),
/// Vulkan 1.3/1.4 core features the renderer relies on (dynamic rendering, synchronization2, push descriptors),
/// <c>VK_KHR_swapchain</c> when presenting and <c>VK_EXT_memory_budget</c> when available.
/// </summary>
internal sealed unsafe partial class VulkanContext : IDisposable
{
    private const string MemoryBudgetExtension = "VK_EXT_memory_budget";
    private readonly ILogger _logger;

    public VulkanContext(VulkanInstance instance, SurfaceKHR surface, KhrSurface? khrSurface, string? preferredDevice, ILogger logger)
    {
        Instance = instance;
        _logger = logger;
        KhrSurface = khrSurface;
        var vk = instance.Vk;

        var candidates = new List<PhysicalDeviceCandidate>();
        var devices = EnumeratePhysicalDevices(vk, instance.Handle);
        for (var i = 0; i < devices.Length; i++)
        {
            candidates.Add(Inspect(vk, devices[i], i, surface, khrSurface));
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var candidate in candidates)
            {
                var text = candidate.ToString();
                LogCandidate(text);
            }
        }

        var chosen = PhysicalDeviceSelector.Select(candidates, preferredDevice)
            ?? throw new GraphicsInitializationException(candidates.Count == 0
                ? "No Vulkan GPU was found. Install or update the GPU driver."
                : $"No GPU meets the engine's Vulkan 1.4 requirements:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", candidates)}");

        PhysicalDevice = devices[chosen.Index];
        PhysicalDeviceProperties properties;
        vk.GetPhysicalDeviceProperties(PhysicalDevice, &properties);
        Properties = properties;
        Limits = properties.Limits;
        PhysicalDeviceMemoryProperties memory;
        vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &memory);
        MemoryProperties = memory;
        DeviceName = chosen.Name;
        DriverDescription = DescribeDriver(vk, PhysicalDevice);

        var families = QueueFamilies(vk, PhysicalDevice);
        (GraphicsFamily, PresentFamily) = PickQueues(vk, PhysicalDevice, families, surface, khrSurface)!.Value;
        TimestampValidBits = families[GraphicsFamily].TimestampValidBits;
        var extensions = DeviceExtensions(vk, PhysicalDevice);
        MemoryBudgetSupported = extensions.Contains(MemoryBudgetExtension);

        var enabled = new List<string>();
        if (surface.Handle != 0)
        {
            enabled.Add(KhrSwapchain.ExtensionName);
        }

        if (MemoryBudgetSupported)
        {
            enabled.Add(MemoryBudgetExtension);
        }

        var priority = 1f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[2];
        queueInfos[0] = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = GraphicsFamily, QueueCount = 1, PQueuePriorities = &priority };
        queueInfos[1] = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = PresentFamily, QueueCount = 1, PQueuePriorities = &priority };

        var features14 = new PhysicalDeviceVulkan14Features { SType = StructureType.PhysicalDeviceVulkan14Features, PushDescriptor = true };
        var features13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, PNext = &features14, DynamicRendering = true, Synchronization2 = true };
        var features12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &features13, HostQueryReset = true };
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features12 };
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(enabled);
        try
        {
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features,
                QueueCreateInfoCount = GraphicsFamily == PresentFamily ? 1u : 2u,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)enabled.Count,
                PpEnabledExtensionNames = extensionNames,
            };
            Silk.NET.Vulkan.Device device;
            VulkanInstance.Check(vk.CreateDevice(PhysicalDevice, &info, null, &device), "vkCreateDevice");
            Device = device;
        }
        finally
        {
            SilkMarshal.FreeString((nint)extensionNames);
        }

        Queue graphics, present;
        vk.GetDeviceQueue(Device, GraphicsFamily, 0, &graphics);
        vk.GetDeviceQueue(Device, PresentFamily, 0, &present);
        GraphicsQueue = graphics;
        PresentQueue = present;
        if (surface.Handle != 0)
        {
            if (!vk.TryGetDeviceExtension(instance.Handle, Device, out KhrSwapchain swapchain))
            {
                throw new GraphicsInitializationException("VK_KHR_swapchain could not be loaded.");
            }

            Swapchain = swapchain;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var api = VulkanVersion.Format(properties.ApiVersion);
            LogSelected(DeviceName, api, DriverDescription, GraphicsFamily, PresentFamily, MemoryBudgetSupported);
        }
    }

    public VulkanInstance Instance { get; }

    public Vk Vk => Instance.Vk;

    public PhysicalDevice PhysicalDevice { get; }

    public Silk.NET.Vulkan.Device Device { get; }

    public PhysicalDeviceProperties Properties { get; }

    public PhysicalDeviceLimits Limits { get; }

    public PhysicalDeviceMemoryProperties MemoryProperties { get; }

    public string DeviceName { get; }

    public string DriverDescription { get; }

    public uint GraphicsFamily { get; }

    public uint PresentFamily { get; }

    public Queue GraphicsQueue { get; }

    public Queue PresentQueue { get; }

    /// <summary>0 when the graphics queue cannot write timestamps.</summary>
    public uint TimestampValidBits { get; }

    public bool MemoryBudgetSupported { get; }

    public KhrSurface? KhrSurface { get; }

    /// <summary>Null for a headless device.</summary>
    public KhrSwapchain? Swapchain { get; }

    /// <summary>Gives the object a name in validation messages and graphics debuggers (no-op without debug utils).</summary>
    public void SetName(ObjectType type, ulong handle, string name)
    {
        if (Instance.DebugUtils is not { } utils || handle == 0)
        {
            return;
        }

        var text = (byte*)SilkMarshal.StringToPtr(name);
        try
        {
            var info = new DebugUtilsObjectNameInfoEXT { SType = StructureType.DebugUtilsObjectNameInfoExt, ObjectType = type, ObjectHandle = handle, PObjectName = text };
            utils.SetDebugUtilsObjectName(Device, &info);
        }
        finally
        {
            SilkMarshal.Free((nint)text);
        }
    }

    public void Dispose()
    {
        Swapchain?.Dispose();
        Vk.DestroyDevice(Device, null);
    }

    /// <summary>Checks one GPU against the requirements; exposed for the selection rules in isolation.</summary>
    internal static PhysicalDeviceCandidate Inspect(Vk vk, PhysicalDevice device, int index, SurfaceKHR surface, KhrSurface? khrSurface)
    {
        PhysicalDeviceProperties properties;
        vk.GetPhysicalDeviceProperties(device, &properties);
        var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? $"GPU {index}";
        var missing = new List<string>();

        PhysicalDeviceMemoryProperties memory;
        vk.GetPhysicalDeviceMemoryProperties(device, &memory);
        long deviceLocal = 0;
        for (var h = 0; h < memory.MemoryHeapCount; h++)
        {
            if ((memory.MemoryHeaps[h].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
            {
                deviceLocal += (long)memory.MemoryHeaps[h].Size;
            }
        }

        if (!VulkanVersion.AtLeast14(properties.ApiVersion))
        {
            missing.Add($"supports Vulkan {VulkanVersion.Format(properties.ApiVersion)}, 1.4 is required");
        }
        else
        {
            var features14 = new PhysicalDeviceVulkan14Features { SType = StructureType.PhysicalDeviceVulkan14Features };
            var features13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, PNext = &features14 };
            var features12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &features13 };
            var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features12 };
            vk.GetPhysicalDeviceFeatures2(device, &features);
            if (!features13.DynamicRendering)
            {
                missing.Add("no dynamicRendering");
            }

            if (!features13.Synchronization2)
            {
                missing.Add("no synchronization2");
            }

            if (!features14.PushDescriptor)
            {
                missing.Add("no pushDescriptor");
            }

            if (!features12.HostQueryReset)
            {
                missing.Add("no hostQueryReset");
            }
        }

        if (PickQueues(vk, device, QueueFamilies(vk, device), surface, khrSurface) is null)
        {
            missing.Add(surface.Handle == 0 ? "no graphics queue" : "no queue that can draw and present to the window");
        }

        if (surface.Handle != 0 && !DeviceExtensions(vk, device).Contains(KhrSwapchain.ExtensionName))
        {
            missing.Add("no VK_KHR_swapchain");
        }

        return new PhysicalDeviceCandidate(index, name, properties.DeviceType, properties.ApiVersion, deviceLocal, missing);
    }

    private static PhysicalDevice[] EnumeratePhysicalDevices(Vk vk, Silk.NET.Vulkan.Instance instance)
    {
        uint count = 0;
        VulkanInstance.Check(vk.EnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            VulkanInstance.Check(vk.EnumeratePhysicalDevices(instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        return devices;
    }

    private static QueueFamilyProperties[] QueueFamilies(Vk vk, PhysicalDevice device)
    {
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        }

        return families;
    }

    /// <summary>A family that draws and presents if there is one, otherwise separate families; null if impossible.</summary>
    private static (uint Graphics, uint Present)? PickQueues(Vk vk, PhysicalDevice device, QueueFamilyProperties[] families, SurfaceKHR surface, KhrSurface? khrSurface)
    {
        uint? graphics = null, present = null;
        for (var i = 0u; i < families.Length; i++)
        {
            var draws = (families[i].QueueFlags & QueueFlags.GraphicsBit) != 0;
            var presents = surface.Handle == 0 || Presents(device, i, surface, khrSurface!);
            if (draws && presents)
            {
                return (i, i);
            }

            graphics ??= draws ? i : null;
            present ??= presents ? i : null;
        }

        return graphics is { } g && present is { } p ? (g, p) : null;
    }

    private static bool Presents(PhysicalDevice device, uint family, SurfaceKHR surface, KhrSurface khrSurface)
    {
        Bool32 supported;
        return khrSurface.GetPhysicalDeviceSurfaceSupport(device, family, surface, &supported) == Result.Success && supported;
    }

    private static HashSet<string> DeviceExtensions(Vk vk, PhysicalDevice device)
    {
        uint count = 0;
        vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = properties)
        {
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            fixed (byte* n = properties[i].ExtensionName)
            {
                names.Add(SilkMarshal.PtrToString((nint)n)!);
            }
        }

        return names;
    }

    private static string DescribeDriver(Vk vk, PhysicalDevice device)
    {
        var driver = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driver };
        vk.GetPhysicalDeviceProperties2(device, &properties);
        var name = SilkMarshal.PtrToString((nint)driver.DriverName) ?? "unknown driver";
        var info = SilkMarshal.PtrToString((nint)driver.DriverInfo) ?? string.Empty;
        return string.IsNullOrEmpty(info) ? name : $"{name} {info}";
    }

    [LoggerMessage(EventId = 1103, Level = LogLevel.Debug, Message = "Vulkan GPU candidate: {Candidate}")]
    private partial void LogCandidate(string candidate);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information, Message = "Vulkan device: {Device} (API {Api}, {Driver}); queues graphics={Graphics} present={Present}; memory budget: {Budget}")]
    private partial void LogSelected(string device, string api, string driver, uint graphics, uint present, bool budget);
}
