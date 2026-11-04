using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics.Vulkan.Device;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace MNVTEngine.Graphics.Vulkan.Instance;

/// <summary>Validation messages received so far. Errors also go to the log; tests assert that none occurred.</summary>
public sealed class VulkanValidationCounters
{
    public bool Enabled { get; internal set; }

    public int Errors { get; internal set; }

    public int Warnings { get; internal set; }

    /// <summary>Loader messages, e.g. about third-party implicit layers that failed to load. Logged as warnings, not counted as errors.</summary>
    public int LoaderMessages { get; internal set; }

    /// <summary>Most recent error text, for diagnostics panels; null when none.</summary>
    public string? LastError { get; internal set; }
}

/// <summary>
/// The Vulkan instance: requires API 1.4, enables the window's surface extensions, <c>VK_EXT_debug_utils</c> when
/// available (object names, validation messages) and the Khronos validation layer when requested and installed.
/// </summary>
internal sealed unsafe partial class VulkanInstance : IDisposable
{
    public const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    private readonly ILogger _logger;
    private readonly DebugUtilsMessengerEXT _messenger;
    private GCHandle _self;

    public VulkanInstance(Vk vk, string applicationName, IReadOnlyList<string> surfaceExtensions, bool validation, bool validationRequested, ILogger logger)
    {
        Vk = vk;
        _logger = logger;

        uint version = 0;
        Check(vk.EnumerateInstanceVersion(&version), "vkEnumerateInstanceVersion");
        InstanceVersion = version;
        if (!VulkanVersion.AtLeast14(version))
        {
            throw new GraphicsInitializationException(
                $"Vulkan 1.4 is required, but the installed Vulkan runtime only supports {VulkanVersion.Format(version)}. Update the GPU driver.");
        }

        var available = EnumerateExtensions(vk);
        var extensions = new List<string>(surfaceExtensions);
        foreach (var missing in extensions.Where(e => !available.Contains(e)))
        {
            throw new GraphicsInitializationException($"The Vulkan runtime lacks the instance extension {missing}, needed to present to the window.");
        }

        HasDebugUtils = available.Contains(ExtDebugUtils.ExtensionName);
        if (HasDebugUtils)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        var layers = new List<string>();
        if (validation)
        {
            if (EnumerateLayers(vk).Contains(ValidationLayerName) && HasDebugUtils)
            {
                layers.Add(ValidationLayerName);
            }
            else if (validationRequested)
            {
                LogValidationUnavailable(ValidationLayerName);
            }
            else
            {
                LogValidationNotInstalled(ValidationLayerName);
            }
        }

        Validation.Enabled = layers.Count > 0;
        _self = GCHandle.Alloc(this);
        var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            // Bool32 is a one-uint struct; UnmanagedCallersOnly methods cannot return it, so the uint version is cast.
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT((delegate* unmanaged[Cdecl]<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT*, void*, Silk.NET.Core.Bool32>)(delegate* unmanaged[Cdecl]<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT*, void*, uint>)&OnMessage),
            PUserData = (void*)GCHandle.ToIntPtr(_self),
        };

        var name = (byte*)SilkMarshal.StringToPtr(applicationName);
        var engine = (byte*)SilkMarshal.StringToPtr("MNVTEngine");
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        var layerNames = (byte**)SilkMarshal.StringArrayToPtr(layers);
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = name,
                ApplicationVersion = 1,
                PEngineName = engine,
                EngineVersion = 1,
                ApiVersion = VulkanVersion.Api14,
            };
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = extensionNames,
                EnabledLayerCount = (uint)layers.Count,
                PpEnabledLayerNames = layerNames,
                PNext = Validation.Enabled ? &messengerInfo : null, // also reports problems inside vkCreateInstance/vkDestroyInstance
            };

            Silk.NET.Vulkan.Instance handle;
            Check(vk.CreateInstance(&info, null, &handle), "vkCreateInstance");
            Handle = handle;
        }
        finally
        {
            SilkMarshal.Free((nint)name);
            SilkMarshal.Free((nint)engine);
            SilkMarshal.FreeString((nint)extensionNames);
            SilkMarshal.FreeString((nint)layerNames);
        }

        if (HasDebugUtils && vk.TryGetInstanceExtension(Handle, out ExtDebugUtils debugUtils))
        {
            DebugUtils = debugUtils;
            if (Validation.Enabled)
            {
                DebugUtilsMessengerEXT messenger;
                Check(debugUtils.CreateDebugUtilsMessenger(Handle, &messengerInfo, null, &messenger), "vkCreateDebugUtilsMessengerEXT");
                _messenger = messenger;
            }
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var versionText = VulkanVersion.Format(version);
            var extensionList = string.Join(", ", extensions);
            LogCreated(versionText, Validation.Enabled, extensionList);
        }
    }

    public Vk Vk { get; }

    public Silk.NET.Vulkan.Instance Handle { get; }

    public uint InstanceVersion { get; }

    public bool HasDebugUtils { get; }

    /// <summary>Null when the runtime has no debug utils; used for object names and the messenger.</summary>
    public ExtDebugUtils? DebugUtils { get; }

    public VulkanValidationCounters Validation { get; } = new();

    public static HashSet<string> EnumerateLayers(Vk vk)
    {
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(&count, null);
        var layers = new LayerProperties[count];
        fixed (LayerProperties* p = layers)
        {
            vk.EnumerateInstanceLayerProperties(&count, p);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            fixed (byte* n = layers[i].LayerName)
            {
                names.Add(SilkMarshal.PtrToString((nint)n)!);
            }
        }

        return names;
    }

    public void Dispose()
    {
        if (_messenger.Handle != 0)
        {
            DebugUtils!.DestroyDebugUtilsMessenger(Handle, _messenger, null);
        }

        DebugUtils?.Dispose();
        Vk.DestroyInstance(Handle, null);
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    internal static void Check(Result result, string call)
    {
        if (result != Result.Success)
        {
            throw result is Result.ErrorIncompatibleDriver or Result.ErrorInitializationFailed or Result.ErrorExtensionNotPresent or Result.ErrorLayerNotPresent or Result.ErrorFeatureNotPresent
                ? new GraphicsInitializationException($"{call} failed: {result}.")
                : new InvalidOperationException($"{call} failed: {result}.");
        }
    }

    private static HashSet<string> EnumerateExtensions(Vk vk)
    {
        uint count = 0;
        vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null);
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = properties)
        {
            vk.EnumerateInstanceExtensionProperties((byte*)null, &count, p);
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnMessage(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT types, DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        if (GCHandle.FromIntPtr((nint)user).Target is VulkanInstance self)
        {
            var text = SilkMarshal.PtrToString((nint)data->PMessage) ?? string.Empty;
            var id = SilkMarshal.PtrToString((nint)data->PMessageIdName) ?? data->MessageIdNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (id == "Loader Message")
            {
                // The loader reporting on other software's implicit layers (overlays, capture tools): not the engine's error.
                self.Validation.LoaderMessages++;
                self.LogValidation(LogLevel.Warning, types, id, text);
            }
            else if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            {
                self.Validation.Errors++;
                self.Validation.LastError = text;
                self.LogValidation(LogLevel.Error, types, id, text);
            }
            else
            {
                self.Validation.Warnings++;
                self.LogValidation(LogLevel.Warning, types, id, text);
            }
        }

        return Vk.False;
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Vulkan instance {Version} created (validation: {Validation}; extensions: {Extensions})")]
    private partial void LogCreated(string version, bool validation, string extensions);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Vulkan validation was requested but {Layer} is not installed; continuing without it. Install the Vulkan SDK to enable it")]
    private partial void LogValidationUnavailable(string layer);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Information, Message = "Vulkan validation is off: {Layer} is not installed (install the Vulkan SDK to validate development builds)")]
    private partial void LogValidationNotInstalled(string layer);

    [LoggerMessage(EventId = 1102, Message = "Vulkan {Types} [{Id}]: {Text}")]
    private partial void LogValidation(LogLevel level, DebugUtilsMessageTypeFlagsEXT types, string id, string text);
}
