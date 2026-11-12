using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MNVTEngine.BrowserHosting.Host;

/// <summary>
/// In an exported game the host sits next to the game's executable and shares its layout: CefSharp and the protocol in
/// <c>Dependencies/Managed</c>, CEF (libcef.dll, its data files and locales) with CefSharp's C++/CLI assemblies and
/// Ijwhost.dll in <c>Dependencies/Native/Chromium</c>, the
/// .NET runtime in <c>Dependencies/Runtime</c>. CEF has a folder of its own because the game loads every library
/// directly in <c>Dependencies/Native</c> at startup. In development (the host's build output) everything is flat and
/// this does nothing. Uses only the base class library: it runs before CefSharp is loaded.
/// </summary>
internal static partial class HostLayout
{
    /// <summary>Where libcef.dll and CEF's resources are in an exported game; null in development.</summary>
    public static string? ChromiumDirectory { get; private set; }

    public static void Install()
    {
        var chromium = Path.Combine(AppContext.BaseDirectory, "Dependencies", "Native", "Chromium");
        if (!Directory.Exists(chromium))
        {
            return;
        }

        ChromiumDirectory = chromium;
        var managed = Path.Combine(AppContext.BaseDirectory, "Dependencies", "Managed");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            // CefSharp's C++/CLI assemblies sit with CEF: they need Ijwhost.dll next to them.
            foreach (var folder in (string[])[managed, chromium])
            {
                var path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }

            return null;
        };

        // CefSharp's C++/CLI assemblies import libcef.dll and Ijwhost.dll. The runtime loads them with the
        // LOAD_LIBRARY_SEARCH_* flags (AddDllDirectory folders); plain LoadLibrary calls (CEF's own) use SetDllDirectory.
        if (AddDllDirectory(chromium) == 0 || !SetDllDirectory(chromium))
        {
            Console.Error.WriteLine($"MNVTBrowserHost: adding {chromium} to the DLL search path failed with error {Marshal.GetLastPInvokeError()}.");
        }
    }

    [LibraryImport("kernel32", EntryPoint = "AddDllDirectory", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AddDllDirectory(string path);

    [LibraryImport("kernel32", EntryPoint = "SetDllDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectory(string path);
}
