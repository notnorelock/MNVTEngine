using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

public sealed record ScriptBuildResult(bool Succeeded, string? AssemblyPath, IReadOnlyList<ScriptDiagnostic> Diagnostics, string Output, TimeSpan Duration)
{
    public IEnumerable<ScriptDiagnostic> Errors => Diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Error);
}

/// <summary>The game's C# scripts project as configured in the .mnvtproj (<c>"scripts": { "project": ... }</c>).</summary>
public sealed class ScriptProject
{
    private ScriptProject(string projectFile, string outputDirectory)
    {
        ProjectFile = projectFile;
        OutputDirectory = outputDirectory;
        AssemblyName = ReadAssemblyName(projectFile);
    }

    public string ProjectFile { get; }

    public string SourceDirectory => Path.GetDirectoryName(ProjectFile)!;

    /// <summary><c>Library/ScriptAssemblies</c>: build output, never committed.</summary>
    public string OutputDirectory { get; }

    public string AssemblyName { get; }

    public string AssemblyPath => Path.Combine(OutputDirectory, AssemblyName + ".dll");

    private string StampFile => Path.Combine(OutputDirectory, ".sourcestamp");

    /// <summary>Null when the project has no C# scripts.</summary>
    /// <exception cref="FileNotFoundException">The configured project file does not exist.</exception>
    public static ScriptProject? For(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Scripts?.Project is not { Length: > 0 } relative)
        {
            return null;
        }

        var file = project.ResolveProjectFile(relative);
        return File.Exists(file)
            ? new ScriptProject(file, Path.Combine(project.RootDirectory, "Library", "ScriptAssemblies"))
            : throw new FileNotFoundException($"Scripts project '{relative}' not found.", file);
    }

    /// <summary>
    /// Fingerprint of every source input (paths, sizes, timestamps of .cs/.csproj/.props/.targets; bin and obj excluded)
    /// and of the engine build the scripts compile against: a script assembly built against an older engine (for example
    /// before a component moved to another assembly) would fail to load.
    /// </summary>
    public string ComputeSourceStamp()
    {
        var builder = new StringBuilder();
        foreach (var engine in EngineAssemblies.Value)
        {
            builder.Append(CultureInfo.InvariantCulture, $"engine:{engine}\n");
        }

        var files = Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToUpperInvariant() is ".CS" or ".CSPROJ" or ".PROPS" or ".TARGETS")
            .Where(f => !IsUnder(f, "bin") && !IsUnder(f, "obj"))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            builder.Append(CultureInfo.InvariantCulture, $"{Path.GetRelativePath(SourceDirectory, file)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}\n");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    // Module version ids of the engine assemblies scripts can reference (every module's, and what they reference), by name.
    private static readonly Lazy<IReadOnlyList<string>> EngineAssemblies = new(() =>
    {
        var found = new SortedDictionary<string, System.Reflection.Assembly>(StringComparer.Ordinal);
        var pending = new Stack<System.Reflection.Assembly>([typeof(Scene).Assembly, typeof(GameProject).Assembly, .. EngineModuleSet.All().Select(m => m.GetType().Assembly)]);
        while (pending.TryPop(out var assembly))
        {
            if (!found.TryAdd(assembly.GetName().Name!, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies().Where(r => r.Name?.StartsWith("MNVTEngine.", StringComparison.Ordinal) == true))
            {
                pending.Push(System.Reflection.Assembly.Load(reference));
            }
        }

        return [.. found.Select(p => $"{p.Key}:{p.Value.ManifestModule.ModuleVersionId}")];
    });

    /// <summary>True if the last successful build used exactly the current sources.</summary>
    public bool IsBuildUpToDate() =>
        File.Exists(AssemblyPath) && File.Exists(StampFile) && File.ReadAllText(StampFile) == ComputeSourceStamp();

    internal void WriteStamp(string stamp) => File.WriteAllText(StampFile, stamp);

    private bool IsUnder(string file, string folder) =>
        Path.GetRelativePath(SourceDirectory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(folder, StringComparer.OrdinalIgnoreCase);

    private static string ReadAssemblyName(string projectFile) =>
        XDocument.Load(projectFile).Descendants("AssemblyName").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0)
        ?? Path.GetFileNameWithoutExtension(projectFile);
}

/// <summary>
/// Compiles game scripts by running <c>dotnet build</c> (requires the .NET SDK, so this is development tooling).
/// Note: building a project runs its MSBuild logic, so opening an untrusted project runs untrusted code, as in any IDE.
/// </summary>
public static partial class ScriptCompiler
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Development build (Debug) into <see cref="ScriptProject.OutputDirectory"/>; records the source stamp on success.</summary>
    public static Task<ScriptBuildResult> BuildAsync(ScriptProject project, CancellationToken cancellationToken = default) =>
        BuildAsync(project, "Debug", project?.OutputDirectory!, cancellationToken);

    /// <summary>Builds the scripts with <paramref name="configuration"/> into <paramref name="outputDirectory"/>.</summary>
    public static async Task<ScriptBuildResult> BuildAsync(ScriptProject project, string configuration, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        var development = Path.GetFullPath(outputDirectory) == Path.GetFullPath(project.OutputDirectory);
        var stamp = development ? project.ComputeSourceStamp() : null; // taken before building: edits made during the build trigger another one
        Directory.CreateDirectory(outputDirectory);

        var result = await DotnetCli.RunAsync(
            ["build", project.ProjectFile, "-c", configuration, "-o", outputDirectory, "-nologo", "-v:q", "-clp:NoSummary;ForceNoAlign", "-p:GenerateFullPaths=true"],
            project.SourceDirectory,
            Timeout,
            cancellationToken).ConfigureAwait(false);
        if (result.StartError is { } startError)
        {
            return Failed(startError, result.Duration);
        }

        var assemblyPath = Path.Combine(outputDirectory, project.AssemblyName + ".dll");
        var diagnostics = ParseDiagnostics(result.Output);
        var succeeded = result.ExitCode == 0 && File.Exists(assemblyPath);
        if (!succeeded && !diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error))
        {
            diagnostics = [.. diagnostics, new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "BUILD", $"dotnet build exited with code {result.ExitCode}.\n{result.Output.Trim()}")];
        }

        if (succeeded && stamp is not null)
        {
            project.WriteStamp(stamp);
        }

        return new ScriptBuildResult(succeeded, succeeded ? assemblyPath : null, diagnostics, result.Output, result.Duration);
    }

    /// <summary>Parses MSBuild/compiler lines like <c>C:\x\A.cs(12,5): error CS1002: ; expected [C:\x\A.csproj]</c>.</summary>
    public static IReadOnlyList<ScriptDiagnostic> ParseDiagnostics(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = new List<ScriptDiagnostic>();
        foreach (var line in output.Split('\n'))
        {
            var match = DiagnosticLine().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var severity = match.Groups["severity"].Value == "error" ? ScriptDiagnosticSeverity.Error : ScriptDiagnosticSeverity.Warning;
            var hasPosition = match.Groups["line"].Success;
            result.Add(new ScriptDiagnostic(
                severity,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                match.Groups["file"].Value,
                hasPosition ? int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture) : 0,
                hasPosition ? int.Parse(match.Groups["column"].Value, CultureInfo.InvariantCulture) : 0));
        }

        return [.. result.Distinct()]; // MSBuild repeats errors at the end of the log
    }

    private static ScriptBuildResult Failed(string message, TimeSpan duration) =>
        new(false, null, [new ScriptDiagnostic(ScriptDiagnosticSeverity.Error, "BUILD", message)], message, duration);

    [GeneratedRegex(@"^(?<file>.+?)(\((?<line>\d+),(?<column>\d+)\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(\s+\[[^\]]+\])?$")]
    private static partial Regex DiagnosticLine();
}
