// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Reflection.PortableExecutable;
using Mono.Cecil;
using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Reporting;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Obfuscation.Analysis;

/// <summary>An assembly from the input folder: either protected (rewritten) or a reference (read only).</summary>
public sealed class LoadedAssembly
{
    public required string Path { get; init; }
    public required AssemblyDefinition Definition { get; init; }
    public required bool IsProtected { get; init; }
    public required long InputBytes { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Name => Definition.Name.Name;
    public ModuleDefinition Module => Definition.MainModule;
}

/// <summary>
/// The assemblies of one run. Input files are classified before Cecil sees them (native and mixed-mode files
/// are reported, never silently dropped); referenced assemblies resolve from the input set first, then the
/// reference folders, then the running framework.
/// </summary>
public sealed class AssemblySet : IDisposable
{
    public const long MaxAssemblyBytes = 512L * 1024 * 1024;

    private readonly SetResolver _resolver;
    private readonly List<LoadedAssembly> _assemblies = new();

    public IReadOnlyList<LoadedAssembly> All => _assemblies;
    public IEnumerable<LoadedAssembly> Protected => _assemblies.Where(a => a.IsProtected);
    public IEnumerable<LoadedAssembly> References => _assemblies.Where(a => !a.IsProtected);
    public IReadOnlyCollection<string> UnresolvedAssemblies => _resolver.Failed;
    internal IAssemblyResolver Resolver => _resolver;

    private AssemblySet(SetResolver resolver) => _resolver = resolver;

    /// <summary>
    /// Loads every assembly in <paramref name="inputDirectory"/> (top level only). Returns null when nothing
    /// usable could be loaded; problems are in <paramref name="diagnostics"/>.
    /// </summary>
    public static AssemblySet? Load(string inputDirectory, IReadOnlyList<string> referenceDirectories, IReadOnlyList<Glob> include, IReadOnlyList<Glob> exclude, DiagnosticBag diagnostics)
    {
        if (!Directory.Exists(inputDirectory))
        {
            diagnostics.Error(DiagnosticCodes.InputMissing, $"The input folder {inputDirectory} does not exist.");
            return null;
        }
        var resolver = new SetResolver();
        resolver.AddSearchDirectory(inputDirectory);
        foreach (string dir in referenceDirectories) resolver.AddSearchDirectory(dir);
        var set = new AssemblySet(resolver);

        var files = Directory.EnumerateFiles(inputDirectory)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (string file in files)
        {
            string name = System.IO.Path.GetFileName(file);
            bool wanted = include.Any(g => g.IsMatch(name)) && !exclude.Any(g => g.IsMatch(name));
            string? problem = Classify(file, out bool managed);
            if (problem != null)
            {
                // Targets must be protectable; anything else in the folder is only worth a note.
                if (wanted) diagnostics.Error(managed ? DiagnosticCodes.Unsupported : DiagnosticCodes.NotManaged, problem, name);
                else diagnostics.Info(managed ? DiagnosticCodes.Unsupported : DiagnosticCodes.NotManaged, problem + " It is not a target, so it is ignored.", name);
                continue;
            }
            AssemblyDefinition def;
            try
            {
                def = AssemblyDefinition.ReadAssembly(file, new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    InMemory = true,
                    ReadingMode = wanted ? ReadingMode.Immediate : ReadingMode.Deferred,
                    ReadSymbols = false,
                });
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or IndexOutOfRangeException or NullReferenceException)
            {
                string message = $"Cannot read the assembly: {e.Message}";
                if (wanted) diagnostics.Error(DiagnosticCodes.NotManaged, message, name);
                else diagnostics.Warning(DiagnosticCodes.NotManaged, message + " It is not a target, so it is ignored.", name);
                continue;
            }
            if (wanted && IsReferenceAssembly(def))
            {
                diagnostics.Error(DiagnosticCodes.Unsupported, "This is a reference assembly (metadata only, no code); protect the implementation assembly instead.", name);
                def.Dispose();
                continue;
            }
            if (set._assemblies.Any(a => string.Equals(a.Name, def.Name.Name, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Error(DiagnosticCodes.Unsupported, $"Two files define the assembly {def.Name.Name}.", name);
                def.Dispose();
                continue;
            }
            if (wanted && def.Name.HasPublicKey)
                diagnostics.Warning(DiagnosticCodes.StrongName, "The assembly is strong-named; rewriting it invalidates the signature, so re-sign it after protection if your runtime checks signatures.", name);
            if (wanted && File.Exists(System.IO.Path.ChangeExtension(file, ".pdb")))
                diagnostics.Info(DiagnosticCodes.SymbolsFile, "A .pdb sits next to this assembly. It holds the original names; do not ship it with the protected assembly (no .pdb is written for the output).", name);
            resolver.Register(def);
            set._assemblies.Add(new LoadedAssembly { Path = file, Definition = def, IsProtected = wanted, InputBytes = new FileInfo(file).Length });
        }
        if (!set.Protected.Any())
            diagnostics.Error(DiagnosticCodes.NoAssemblies, $"No assembly in {inputDirectory} matches {string.Join(", ", include.Select(g => g.Pattern))}" + (diagnostics.HasErrors ? " and loads." : "."));
        return set;
    }

    /// <summary>
    /// Returns why a file cannot be protected, or null when it is a plain IL-only managed assembly.
    /// <paramref name="managed"/> says whether it has .NET metadata at all.
    /// </summary>
    internal static string? Classify(string file, out bool managed)
    {
        managed = false;
        try
        {
            var info = new FileInfo(file);
            if (info.Length > MaxAssemblyBytes) return $"The file is larger than {MaxAssemblyBytes / (1024 * 1024)} MB.";
            using FileStream stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "Not a .NET assembly (no managed metadata; a native library?).";
            managed = true;
            CorHeader? cor = pe.PEHeaders.CorHeader;
            if (cor == null) return "Not a .NET assembly (no CLI header).";
            if ((cor.Flags & CorFlags.ILOnly) == 0) return "Mixed-mode assembly (native code alongside IL); it cannot be rewritten safely.";
            return null;
        }
        catch (BadImageFormatException e)
        {
            return $"Not a valid PE file: {e.Message}";
        }
        catch (IOException e)
        {
            return $"Cannot open the file: {e.Message}";
        }
        catch (UnauthorizedAccessException e)
        {
            return $"Cannot open the file: {e.Message}";
        }
    }

    private static bool IsReferenceAssembly(AssemblyDefinition def) =>
        def.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute");

    public LoadedAssembly? Find(string assemblyName) =>
        _assemblies.FirstOrDefault(a => string.Equals(a.Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (LoadedAssembly a in _assemblies) a.Definition.Dispose();
        _resolver.Dispose();
    }

    /// <summary>Serves the input set first; remembers names that could not be found at all.</summary>
    private sealed class SetResolver : DefaultAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _known = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

        public void Register(AssemblyDefinition def) => _known[def.Name.Name] = def;

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (_known.TryGetValue(name.Name, out AssemblyDefinition? def)) return def;
            if (Failed.Contains(name.Name)) throw new AssemblyResolutionException(name);
            try
            {
                def = base.Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred, InMemory = true });
                _known[name.Name] = def;
                return def;
            }
            catch (AssemblyResolutionException)
            {
                Failed.Add(name.Name);
                throw;
            }
            catch (Exception e) when (e is BadImageFormatException or IOException)
            {
                Failed.Add(name.Name);
                throw new AssemblyResolutionException(name, e);
            }
        }
    }
}
