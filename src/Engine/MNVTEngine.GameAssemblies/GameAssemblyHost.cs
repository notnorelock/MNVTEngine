using System.Reflection;
using MNVTEngine.Scenes;

namespace MNVTEngine.GameAssemblies;

public enum ScriptDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A compiler or loader message about game code. <see cref="File"/> and <see cref="Line"/> are set for source errors.</summary>
public sealed record ScriptDiagnostic(ScriptDiagnosticSeverity Severity, string Code, string Message, string? File = null, int Line = 0, int Column = 0)
{
    public override string ToString() =>
        File is null ? $"{Severity} {Code}: {Message}" : $"{File}({Line},{Column}): {Severity} {Code}: {Message}";
}

/// <summary>One loaded generation of game code.</summary>
public sealed class GameCode
{
    internal GameCode(GameAssemblyLoadContext context, Assembly mainAssembly, IReadOnlyList<Type> componentTypes)
    {
        Context = context;
        MainAssembly = mainAssembly;
        ComponentTypes = componentTypes;
    }

    public GameAssemblyLoadContext Context { get; }

    public Assembly MainAssembly { get; }

    /// <summary>Concrete components with a public parameterless constructor.</summary>
    public IReadOnlyList<Type> ComponentTypes { get; }

    /// <summary>Every assembly loaded into this generation's context.</summary>
    public IEnumerable<Assembly> Assemblies => Context.Assemblies;
}

public sealed record GameCodeLoadResult(GameCode? Code, IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public bool Succeeded => Code is not null && Diagnostics.All(d => d.Severity != ScriptDiagnosticSeverity.Error);
}

/// <summary>
/// Loads game assemblies into a fresh collectible context per generation, registers their components, and unloads old
/// generations. Unloading only completes once nothing references the old types; <see cref="IsCollected"/> verifies it.
/// </summary>
public sealed class GameAssemblyHost
{
    private int _generation;

    public GameCode? Current { get; private set; }

    /// <summary>Loads <paramref name="assemblyPath"/> (its dependencies resolve from the same folder) as the new current generation.</summary>
    public GameCodeLoadResult Load(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyPath);
        var context = new GameAssemblyLoadContext($"GameCode#{++_generation}", Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        return Load(context, () => context.LoadFromFileBytes(assemblyPath), assemblyPath);
    }

    /// <summary>Loads game code from any source (exported games: the content archive) as the new current generation.</summary>
    /// <param name="readFile">Returns a file's bytes by file name (main assembly, dependencies, .pdb), or null.</param>
    public GameCodeLoadResult Load(string mainFileName, Func<string, byte[]?> readFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(mainFileName);
        var context = new GameAssemblyLoadContext($"GameCode#{++_generation}", readFile);
        return Load(context, () => context.LoadByFileName(mainFileName), mainFileName);
    }

    private GameCodeLoadResult Load(GameAssemblyLoadContext context, Func<Assembly> loadMain, string source)
    {
        var diagnostics = new List<ScriptDiagnostic>();
        Assembly main;
        try
        {
            main = loadMain();
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidDataException)
        {
            context.Unload();
            diagnostics.Add(new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "LOAD001", $"Cannot load '{source}': {ex.Message}"));
            return new GameCodeLoadResult(null, diagnostics);
        }

        Current = new GameCode(context, main, Components(main, diagnostics));
        return new GameCodeLoadResult(Current, diagnostics);
    }

    /// <summary>
    /// Loads another assembly into the current generation's context (DLC code): it sees the game's own types, and the
    /// result's <see cref="GameCode.ComponentTypes"/> are its components only, ready for <see cref="Register"/>.
    /// </summary>
    /// <param name="fileName">A file name the current generation's source can read.</param>
    /// <exception cref="InvalidOperationException">No game code is loaded.</exception>
    public GameCodeLoadResult LoadAdditional(string fileName)
    {
        return LoadInto(Current ?? throw new InvalidOperationException("Load the game's code before additional assemblies."), fileName);
    }

    /// <summary>Loads another assembly into <paramref name="current"/>'s context; see <see cref="LoadAdditional"/>.</summary>
    public static GameCodeLoadResult LoadInto(GameCode current, string fileName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        var diagnostics = new List<ScriptDiagnostic>();
        Assembly assembly;
        try
        {
            assembly = current.Context.LoadByFileName(fileName);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "LOAD001", $"Cannot load '{fileName}': {ex.Message}"));
            return new GameCodeLoadResult(null, diagnostics);
        }

        return new GameCodeLoadResult(new GameCode(current.Context, assembly, Components(assembly, diagnostics)), diagnostics);
    }

    private static List<Type> Components(Assembly main, List<ScriptDiagnostic> diagnostics)
    {
        var components = new List<Type>();
        foreach (var type in ExportedTypes(main, diagnostics))
        {
            if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract)
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                diagnostics.Add(new ScriptDiagnostic(ScriptDiagnosticSeverity.Warning, "LOAD003", $"Component {type.FullName} has no public parameterless constructor and cannot be added to objects."));
                continue;
            }

            components.Add(type);
        }

        return components;
    }

    /// <summary>
    /// Adds the current generation's components to <paramref name="registry"/> after removing those of
    /// <paramref name="previous"/>. Id conflicts are reported, not thrown.
    /// </summary>
    public static IReadOnlyList<ScriptDiagnostic> Register(ComponentRegistry registry, GameCode? previous, GameCode? current)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var diagnostics = new List<ScriptDiagnostic>();
        foreach (var assembly in previous?.Assemblies ?? [])
        {
            registry.Unregister(assembly);
            ComponentTypeInfo.Evict(assembly);
        }

        foreach (var type in current?.ComponentTypes ?? [])
        {
            try
            {
                registry.Register(type);
            }
            catch (InvalidOperationException ex)
            {
                diagnostics.Add(new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "LOAD004", ex.Message));
            }
        }

        return diagnostics;
    }

    /// <summary>Starts unloading a generation. Returns a handle for <see cref="IsCollected"/>.</summary>
    public static WeakReference Unload(GameCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var assemblies = code.Assemblies.ToList();
        foreach (var assembly in assemblies)
        {
            ComponentTypeInfo.Evict(assembly);
        }

        ReflectionCacheCleaner.Release(assemblies);
        var handle = new WeakReference(code.Context);
        code.Context.Unload();
        return handle;
    }

    /// <summary>Forces collections and reports whether an unloaded context is gone (false means something still references its types).</summary>
    public static bool IsCollected(WeakReference handle, int attempts = 10)
    {
        ArgumentNullException.ThrowIfNull(handle);
        for (var i = 0; i < attempts && handle.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !handle.IsAlive;
    }

    private static IEnumerable<Type> ExportedTypes(Assembly assembly, List<ScriptDiagnostic> diagnostics)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.OfType<Exception>().DistinctBy(e => e.Message))
            {
                diagnostics.Add(new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "LOAD002", $"Type load failure in {assembly.GetName().Name}: {loaderException.Message}"));
            }

            return ex.Types.OfType<Type>().Where(t => t.IsPublic);
        }
    }
}
