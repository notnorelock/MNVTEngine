using MNVTEngine.Versioning;

namespace MNVTEngine.Build;

/// <summary>
/// The engine version of one pipeline build (<c>mnvt build</c>, <c>mnvt package build</c>, <c>mnvt package dlc</c>):
/// allocated once when the build starts and shared by everything it produces: the engine assemblies it publishes
/// (<c>-p:MnvtVersionSession</c>), the packages, the manifest and the build report. See docs/architecture/versioning.md.
/// </summary>
public sealed class PipelineVersion
{
    private readonly string _engineRoot;
    private readonly Func<string, string?> _environment;

    private PipelineVersion(string engineRoot, string sessionDirectory, EngineBuildInfo info, Func<string, string?> environment)
    {
        _engineRoot = engineRoot;
        _environment = environment;
        SessionDirectory = sessionDirectory;
        Info = info;
    }

    public EngineBuildInfo Info { get; private set; }

    /// <summary>The session folder that <c>dotnet publish</c> of the engine is pointed at.</summary>
    public string SessionDirectory { get; }

    /// <summary>The MSBuild argument that stamps the engine assemblies of a publish with this version.</summary>
    public string MsBuildProperty => $"-p:{VersionSession.Property}={SessionDirectory}";

    /// <summary>The package properties of the engine build (every package of the build carries them).</summary>
    public IEnumerable<KeyValuePair<string, string>> PackageProperties =>
        [new("engine", Info.Version), new("engineBuild", Info.BuildId.ToString("D"))];

    /// <summary>
    /// What the content build depends on of the engine: its source revision, not the build number or time, so that
    /// identical inputs still give identical packages (see <see cref="BuildIdentity"/>).
    /// </summary>
    public string SourceIdentity => $"{Info.Major}|{Info.Source.Commit ?? "unknown"}|{(Info.Source.Dirty ? "dirty" : "clean")}";

    /// <summary>Resolves and allocates the version; null (with error MNVB120) when the configuration or overrides are invalid.</summary>
    /// <param name="overrides">Version variables (MNVT_BUILD_NUMBER, ...) that take precedence over the process environment.</param>
    public static PipelineVersion? Begin(string engineRoot, string kind, BuildContext context, string sessionDirectory, IReadOnlyDictionary<string, string>? overrides, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        string? Environment(string name) => overrides is not null && overrides.TryGetValue(name, out var value) ? value : System.Environment.GetEnvironmentVariable(name);
        try
        {
            var info = VersionResolver.Resolve(new VersionRequest(engineRoot) { Allocate = true, Kind = kind, Context = context, Environment = Environment });
            if (info.Source.Problem is { } problem)
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB121", $"Engine version {info.Version}: {problem}"));
            }

            DeleteDirectory(sessionDirectory);
            VersionSession.Write(sessionDirectory, info);
            return new PipelineVersion(engineRoot, sessionDirectory, info, Environment);
        }
        catch (Exception ex) when (ex is VersionConfigException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB120", $"The engine version could not be resolved: {ex.Message}", VersionConfig.PathIn(engineRoot)));
            return null;
        }
    }

    /// <summary>Adds what the build learned later (its modules) to the session, before the engine is published.</summary>
    public void Update(Func<BuildContext, BuildContext> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Info = Info with { Context = change(Info.Context) };
        VersionSession.Write(SessionDirectory, Info);
    }

    /// <summary>Records how the build ended in the build state ("succeeded", "failed" or "canceled").</summary>
    /// <returns>Null, or why the record could not be written (for the caller's log: the build's own result stands).</returns>
    public string? Complete(string result)
    {
        if (Info.BuildNumberSource != "counter")
        {
            return null;
        }

        try
        {
            new BuildCounter(BuildCounter.DefaultDirectory(_engineRoot, _environment)).Complete(Info.BuildId, result, DateTimeOffset.UtcNow);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
