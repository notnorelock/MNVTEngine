using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MNVTEngine.GameHost;

/// <summary>
/// Resolves the exported game's dependencies from <c>Dependencies/Managed</c> and <c>Dependencies/Native</c>. Uses only
/// the base class library: it must run before any engine assembly is needed.
/// </summary>
internal static class HostLayout
{
    public static string ManagedDirectory { get; } = Path.Combine(AppContext.BaseDirectory, Runtime.GamePackage.ManagedFolder); // a const: no engine assembly is loaded

    public static string NativeDirectory { get; } = Path.Combine(AppContext.BaseDirectory, Runtime.GamePackage.NativeFolder);

    public static void Install()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(ManagedDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = Path.Combine(NativeDirectory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            return File.Exists(path) ? NativeLibrary.Load(path) : 0;
        };

        // Some bindings (Silk.NET's GLFW loader) probe fixed paths next to the executable. A library that is already
        // loaded is reused by name, so loading the game's native libraries up front covers every loader.
        if (Directory.Exists(NativeDirectory))
        {
            foreach (var library in Directory.EnumerateFiles(NativeDirectory, "*.dll"))
            {
                NativeLibrary.Load(library);
            }
        }
    }
}
