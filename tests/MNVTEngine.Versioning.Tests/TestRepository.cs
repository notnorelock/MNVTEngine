using System.Diagnostics;

namespace MNVTEngine.Versioning.Tests;

/// <summary>A throwaway engine tree: <c>eng/version.json</c>, optionally in a Git repository with some commits.</summary>
public sealed class TestRepository : IDisposable
{
    public const string DefaultConfig = """{ "major": 1 }""";

    public TestRepository(int commits = 3, string config = DefaultConfig, bool git = true)
    {
        Root = Directory.CreateTempSubdirectory("mnvt-version-").FullName;
        Directory.CreateDirectory(Path.Combine(Root, "eng"));
        File.WriteAllText(Path.Combine(Root, "eng", "version.json"), config);
        File.WriteAllText(Path.Combine(Root, ".gitignore"), "state/\n");
        if (git)
        {
            Git("init", "-q", "-b", "main");
            for (var i = 0; i < commits; i++)
            {
                File.WriteAllText(Path.Combine(Root, "file.txt"), $"change {i}");
                Git("add", "-A");
                Git("commit", "-q", "-m", $"Commit \"{i}\" with a \\ backslash");
            }
        }
    }

    public string Root { get; }

    public string State => Path.Combine(Root, "state");

    /// <summary>An environment with only the given variables (the test process's own CI variables must not leak in).</summary>
    public Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        var all = variables.Append(("MNVT_BUILD_STATE_DIR", State)).ToDictionary(v => v.Item1, v => v.Item2, StringComparer.Ordinal);
        return name => all.GetValueOrDefault(name);
    }

    public VersionRequest Request(bool allocate = false, params (string Name, string Value)[] variables) =>
        new(Root) { Allocate = allocate, Environment = Environment(variables), Now = new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.Zero), DotnetSdk = "test-sdk" };

    public string Git(params string[] arguments) => Run(Root, arguments);

    public static string Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", .. arguments])
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
        }

        Directory.Delete(Root, recursive: true);
    }
}
