using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Versioning.Tests;

/// <summary>The engine's MSBuild integration (Directory.Build.targets, eng/Version.proj) on real builds of engine projects.</summary>
public sealed class MsBuildIntegrationTests
{
    private static readonly string Engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Version, file version, informational version and build id of an engine assembly, read without running it.</summary>
    private static (string? File, string? Product, string? BuildId, string? Assembly) Stamp(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var context = new AssemblyLoadContext("inspect", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            var buildId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "MNVTEngine.BuildId")?.Value;
            return (info.FileVersion, info.ProductVersion, buildId, assembly.GetName().Version?.ToString());
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void EngineAssembliesOfOneBuild_CarryTheSameVersion_AsTheRuntimeApi()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "MNVTEngine.*.dll").Where(f => !Path.GetFileName(f).Contains("Tests", StringComparison.Ordinal) && !Path.GetFileName(f).Contains("Versioning", StringComparison.Ordinal)).ToList();
        Assert.True(assemblies.Count >= 4, string.Join(", ", assemblies)); // Core, Mathematics, ECS, Scenes, ...
        var stamps = assemblies.Select(Stamp).Distinct().ToList();
        var stamp = Assert.Single(stamps);

        Assert.Equal(EngineVersion.NumericVersion.Replace("." + EngineVersion.MinorText + ".", "." + EngineVersion.Minor + ".", StringComparison.Ordinal), stamp.File);
        Assert.Equal(EngineVersion.InformationalVersion, stamp.Product);
        Assert.Equal(EngineVersion.BuildId, stamp.BuildId);
        Assert.Equal($"{EngineVersion.Major}.0.0.0", stamp.Assembly);
        Assert.NotEqual("1.0.0.0", stamp.File);

        // The runtime API, and the metadata file next to the assemblies that tools read without loading the engine.
        Assert.StartsWith($"{EngineVersion.Major}.{EngineVersion.MinorText}.{EngineVersion.Patch}.{EngineVersion.Build}", EngineVersion.Version, StringComparison.Ordinal);
        Assert.Equal(DateTimeKind.Utc, EngineVersion.BuildTimestampUtc.Kind);
        Assert.Equal(EngineVersion.CommitHash == "unknown", EngineVersion.InformationalVersion.Contains("+source.unknown", StringComparison.Ordinal));
        var metadata = JObject.Parse(EngineVersion.MetadataJson);
        Assert.Equal(JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MNVTEngine.version.json"))).ToString(), metadata.ToString());
        Assert.Equal(EngineVersion.Version, (string?)metadata["version"]);
        Assert.Contains(EngineVersion.Version, EngineVersion.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionBuild_StampsEveryProject_AndAllocatesNothingItself()
    {
        using var repo = new TestRepository(commits: 1); // only for its build state folder
        var session = Path.Combine(repo.State, "session");
        var info = VersionResolver.Resolve(new VersionRequest(Engine) { Allocate = true, Environment = repo.Environment(), Kind = "test" });
        VersionSession.Write(session, info);
        var artifacts = Path.Combine(repo.State, "artifacts");

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Engine };
        foreach (var a in (string[])["build", Path.Combine(Engine, "src", "Engine", "MNVTEngine.Scenes", "MNVTEngine.Scenes.csproj"), "--artifacts-path", artifacts, "-nologo", "-v:q", "-nodeReuse:false", $"-p:MnvtVersionSession={session}"])
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["MNVT_BUILD_STATE_DIR"] = repo.State;
        using (var build = Process.Start(start)!)
        {
            var output = build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = await build.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(build.ExitCode == 0, await output + error);
        }

        var built = Directory.GetFiles(Path.Combine(artifacts, "bin"), "MNVTEngine.*.dll", SearchOption.AllDirectories);
        Assert.True(built.Length >= 4, string.Join(", ", built));
        Assert.All(built, dll => Assert.Equal((info.FileVersion, info.InformationalVersion, info.BuildId.ToString("D"), info.AssemblyVersion), Stamp(dll)));
        Assert.Single(new BuildCounter(repo.State).Attempts()); // the test's allocation only: projects and nested builds allocate none
    }
}
