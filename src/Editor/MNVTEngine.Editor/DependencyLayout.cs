// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MNVTEngine.Editor;

/// <summary>
/// Resolves the editor's managed assemblies from <c>Dependencies/Managed</c> when the editor is published in the clean
/// layout (the many libraries tucked into subfolders instead of a flat pile next to the executable), and native ones
/// from <c>Dependencies/Native</c>, as an exported game does (HostLayout). In a flat development build the subfolder
/// does not exist and this does nothing. Uses only the base class library and runs before any engine assembly is needed.
/// </summary>
internal static class DependencyLayout
{
    private static readonly string ManagedDirectory = Path.Combine(AppContext.BaseDirectory, "Dependencies", "Managed");
    private static readonly string NativeDirectory = Path.Combine(AppContext.BaseDirectory, "Dependencies", "Native");

    // A module initializer, not a call from Main: the top-level Main uses engine types throughout, so the JIT would
    // load them while compiling Main, before a first-line call could run. The module initializer runs before Main is
    // compiled, so the resolver is in place when the first engine assembly is needed.
    [ModuleInitializer]
    internal static void Install()
    {
        if (!Directory.Exists(ManagedDirectory))
        {
            return; // Flat development build: the dependencies sit next to the executable, resolved normally.
        }

        // The host still names the deleted MNVTEngine.Editor.deps.json here, so DependencyContext.Default throws
        // FileNotFoundException, which aborts Silk.NET's native library search (GLFW "not applicable"). Without the
        // stale entry it yields no context, as for a single-file app, and Silk.NET falls back to its other probes.
        if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is string depsFiles)
        {
            AppContext.SetData("APP_CONTEXT_DEPS_FILES", string.Join(';', depsFiles.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists)));
        }

        AssemblyLoadContext.Default.Resolving += static (context, name) =>
        {
            var path = Path.Combine(ManagedDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += static (_, name) =>
        {
            var path = Path.Combine(NativeDirectory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
            return File.Exists(path) ? NativeLibrary.Load(path) : 0;
        };

        // Silk.NET's loaders (GLFW, OpenAL, shaderc) probe fixed paths, not this resolver. A library that is already
        // loaded is reused by name, so loading the native libraries up front covers every loader.
        if (Directory.Exists(NativeDirectory))
        {
            foreach (var library in Directory.EnumerateFiles(NativeDirectory, "*.dll"))
            {
                NativeLibrary.Load(library);
            }
        }
    }
}
