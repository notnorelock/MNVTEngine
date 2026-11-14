namespace MNVTEngine.Versioning.Tests;

public sealed class VersionResolverTests
{
    [Fact]
    public void Config_Defaults_AndTheRepositoryFile_AreValid()
    {
        Assert.Empty(new VersionConfig().Problems());
        var engine = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        Assert.Empty(VersionConfig.Load(engine).Problems()); // eng/version.json of this repository
    }

    [Theory]
    [InlineData("""{ "major": 1, "majr": 2 }""", "Unknown setting 'majr'")]
    [InlineData("""{ "major": "1" }""", "'major' must be an integer")]
    [InlineData("""{ "major": 70000 }""", "'major' must be an integer from 0 to 65534")]
    [InlineData("""{ "minorStrategy": "DateMMDD" }""", "'minorStrategy' must be \"DateDDMM\"")]
    [InlineData("""{ "patchStrategy": "Commits" }""", "'patchStrategy'")]
    [InlineData("""{ "buildCounterScope": "Day" }""", "'buildCounterScope'")]
    [InlineData("""{ "channel": "alpha" }""", "'channel'")]
    [InlineData("""{ "prerelease": "beta 1" }""", "'prerelease'")]
    [InlineData("""{ "dateTimezone": "Mars/Olympus" }""", "not a known time zone")]
    [InlineData("""{ "missingGit": "Ignore" }""", "'missingGit'")]
    [InlineData("""{ "major": 1, "major": 2 }""", "major")]
    [InlineData("""[1]""", "JSON object")]
    [InlineData("""{ "major": """, "version.json")]
    public void Config_RejectsInvalidSettings_WithTheirNames(string json, string message)
    {
        var ex = Assert.Throws<VersionConfigException>(() => VersionConfig.Parse(json));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_ListsEveryProblem()
    {
        var ex = Assert.Throws<VersionConfigException>(() => VersionConfig.Parse("""{ "channel": "alpha", "prerelease": "x y", "buildStrategy": "Random" }"""));
        Assert.Contains("'channel'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'prerelease'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'buildStrategy'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Git_CountsCommits_AndSeesTheBranchAndDirtyTree()
    {
        using var repo = new TestRepository(commits: 3);
        var source = GitSource.Read(repo.Root, firstParent: false, _ => null);
        Assert.Equal(("git", 3, "main", false, false, false), (source.Kind, source.CommitCount, source.Branch, source.Dirty, source.Detached, source.Shallow));
        Assert.Equal(repo.Git("rev-parse", "HEAD"), source.Commit);
        Assert.Equal("Commit \"2\" with a \\ backslash", source.Subject);

        File.WriteAllText(Path.Combine(repo.Root, "file.txt"), "edited");
        Assert.True(GitSource.Read(repo.Root, false, _ => null).Dirty);
        File.WriteAllText(Path.Combine(repo.Root, "file.txt"), "change 2");
        File.WriteAllText(Path.Combine(repo.Root, "new.txt"), "untracked"); // a new file is built too (globs): dirty
        Assert.True(GitSource.Read(repo.Root, false, _ => null).Dirty);
    }

    [Fact]
    public void Git_FirstParentCount_CountsAMergeOnce()
    {
        using var repo = new TestRepository(commits: 2);
        repo.Git("checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "a");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "feature 1");
        File.WriteAllText(Path.Combine(repo.Root, "b.txt"), "b");
        repo.Git("add", "-A");
        repo.Git("commit", "-q", "-m", "feature 2");
        repo.Git("checkout", "-q", "main");
        repo.Git("merge", "-q", "--no-ff", "-m", "merge", "feature");
        Assert.Equal(5, GitSource.Read(repo.Root, false, _ => null).CommitCount);
        Assert.Equal(3, GitSource.Read(repo.Root, true, _ => null).CommitCount);
    }

    [Fact]
    public void Git_DetachedHead_HasNoBranch_UnlessTheCiNamesIt()
    {
        using var repo = new TestRepository(commits: 3);
        repo.Git("checkout", "-q", "--detach", "HEAD~1");
        var source = GitSource.Read(repo.Root, false, _ => null);
        Assert.Equal((true, null, 2), (source.Detached, source.Branch, source.CommitCount));
        Assert.Equal("release/1.2", GitSource.Read(repo.Root, false, n => n == "GITHUB_REF_NAME" ? "release/1.2" : null).Branch);
    }

    [Fact]
    public void Git_ShallowClone_IsReported_NotCountedWrong()
    {
        using var repo = new TestRepository(commits: 4);
        var clone = Path.Combine(repo.Root, "state", "clone");
        TestRepository.Run(repo.Root, "clone", "-q", "--depth", "1", "file://" + repo.Root.Replace('\\', '/'), clone);
        var source = GitSource.Read(clone, false, _ => null);
        Assert.True(source.Shallow);
        Assert.Null(source.CommitCount);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), source.Commit);
        Assert.Contains("Shallow clone", source.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutGit_TheFallbackIsMarked_OrAnErrorWhenConfigured()
    {
        using var archive = new TestRepository(git: false);
        var source = GitSource.Read(archive.Root, false, _ => null);
        Assert.Equal(("unavailable", null, null), (source.Kind, source.Commit, source.CommitCount));
        Assert.NotNull(source.Problem);

        var info = VersionResolver.Resolve(archive.Request());
        Assert.Equal(0, info.Patch);
        Assert.EndsWith("+source.unknown", info.InformationalVersion, StringComparison.Ordinal); // never presented as a revision
        Assert.Equal(17, VersionResolver.Resolve(archive.Request(false, ("MNVT_PATCH", "17"))).Patch);

        File.WriteAllText(Path.Combine(archive.Root, "eng", "version.json"), """{ "major": 1, "missingGit": "Error" }""");
        var ex = Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(archive.Request()));
        Assert.Contains("MNVT_PATCH", ex.Message, StringComparison.Ordinal);
        Assert.Equal(17, VersionResolver.Resolve(archive.Request(false, ("MNVT_PATCH", "17"))).Patch);
    }

    [Fact]
    public void DevelopmentBuilds_AllocateNothing_AndKeepTheirIdentity()
    {
        using var repo = new TestRepository(commits: 3);
        var first = VersionResolver.Resolve(repo.Request() with { Context = new BuildContext { Configuration = "Debug" } });
        Assert.Equal((1, 1411, 3, 0, "dev", "development"), (first.Major, first.Minor, first.Patch, first.Build, first.Prerelease, first.BuildNumberSource));
        Assert.Equal("1.1411.3.0-dev", first.Version);
        Assert.False(Directory.Exists(repo.State));

        var later = VersionResolver.Resolve(repo.Request() with { Context = new BuildContext { Configuration = "Debug" }, Now = first.TimestampUtc.AddMinutes(5), Previous = first });
        Assert.Equal((first.BuildId, first.TimestampUtc), (later.BuildId, later.TimestampUtc)); // same source state: same build

        File.WriteAllText(Path.Combine(repo.Root, "file.txt"), "edited");
        var dirty = VersionResolver.Resolve(repo.Request() with { Context = new BuildContext { Configuration = "Debug" }, Previous = first });
        Assert.NotEqual(first.BuildId, dirty.BuildId);
        Assert.True(dirty.Source.Dirty);
    }

    [Fact]
    public void PipelineBuilds_IncrementPerVersionIdentity_AndRecordTheirResult()
    {
        using var repo = new TestRepository(commits: 3);
        var builds = Enumerable.Range(0, 4).Select(_ => VersionResolver.Resolve(repo.Request(allocate: true))).ToList();
        Assert.Equal(["1.1411.3.1-dev", "1.1411.3.2-dev", "1.1411.3.3-dev", "1.1411.3.4-dev"], builds.Select(b => b.Version));
        Assert.Equal(4, builds.Select(b => b.BuildId).Distinct().Count());

        var counter = new BuildCounter(repo.State);
        Assert.True(counter.Complete(builds[0].BuildId, "succeeded", DateTimeOffset.UtcNow));
        Assert.True(counter.Complete(builds[1].BuildId, "failed", DateTimeOffset.UtcNow));
        Assert.True(counter.Complete(builds[2].BuildId, "canceled", DateTimeOffset.UtcNow));
        Assert.False(counter.Complete(Guid.NewGuid(), "succeeded", DateTimeOffset.UtcNow));
        Assert.Equal(["succeeded", "failed", "canceled", "started"], counter.Attempts().Select(a => a.Result));
        Assert.Throws<ArgumentException>(() => counter.Complete(builds[3].BuildId, "exploded", DateTimeOffset.UtcNow));

        // A new commit is a new identity: its numbers start again at 1. The Global scope never restarts.
        File.WriteAllText(Path.Combine(repo.Root, "file.txt"), "next");
        repo.Git("commit", "-qam", "next");
        Assert.Equal("1.1411.4.1-dev", VersionResolver.Resolve(repo.Request(allocate: true)).Version);
        File.WriteAllText(Path.Combine(repo.Root, "eng", "version.json"), """{ "major": 2, "buildCounterScope": "Global", "channel": "release" }""");
        repo.Git("commit", "-qam", "global");
        Assert.Equal("2.1411.5.1", VersionResolver.Resolve(repo.Request(allocate: true)).Version);
        Assert.Equal("2.1411.5.2", VersionResolver.Resolve(repo.Request(allocate: true)).Version);
    }

    [Fact]
    public async Task ConcurrentAllocations_NeverCollide()
    {
        using var repo = new TestRepository(commits: 1);
        var counter = new BuildCounter(repo.State);
        var numbers = await Task.WhenAll(Enumerable.Range(0, 120).Select(i => Task.Run(() => counter.Allocate("1.1411.1", Guid.NewGuid(), "engine", n => $"1.1411.1.{n}", DateTimeOffset.UtcNow))));
        Assert.Equal(Enumerable.Range(1, 120), numbers.Order());
        Assert.Equal(120, counter.Attempts().Count);
    }

    [Fact]
    public async Task ConcurrentProcesses_NeverCollide()
    {
        using var repo = new TestRepository(commits: 1);
        var tool = Path.Combine(AppContext.BaseDirectory, "MNVTEngine.Versioning.dll");
        var runs = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in (string[])[tool, "resolve", "--allocate", "--root", repo.Root, "--out", Path.Combine(repo.State, "session" + i)])
            {
                start.ArgumentList.Add(a);
            }

            start.Environment["MNVT_BUILD_STATE_DIR"] = repo.State;
            using var process = System.Diagnostics.Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return VersionSession.Read(Path.Combine(repo.State, "session" + i))!.Build;
        }));
        Assert.Equal(Enumerable.Range(1, 6), (await Task.WhenAll(runs)).Order());
    }

    [Fact]
    public void BrokenState_IsReported_NotReset()
    {
        using var repo = new TestRepository(commits: 1);
        Directory.CreateDirectory(repo.State);
        File.WriteAllText(Path.Combine(repo.State, BuildCounter.StateFile), "{ broken");
        var ex = Assert.Throws<InvalidDataException>(() => VersionResolver.Resolve(repo.Request(allocate: true)));
        Assert.Contains("delete it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CiOverrides_ReplaceTheCounter_AndAreValidated()
    {
        using var repo = new TestRepository(commits: 3);
        var ci = VersionResolver.Resolve(repo.Request(true, ("MNVT_BUILD_NUMBER", "812"), ("GITHUB_ACTIONS", "true"), ("GITHUB_RUN_ID", "99"), ("MNVT_CHANNEL", "nightly"), ("GITHUB_TOKEN", "secret")));
        Assert.Equal(("1.1411.3.812-nightly", "ci"), (ci.Version, ci.BuildNumberSource));
        Assert.Equal(new CiInfo("github-actions", "99.1"), ci.Ci);
        Assert.DoesNotContain("secret", ci.ToJson().ToString(), StringComparison.Ordinal); // only the provider and run id are recorded
        Assert.False(File.Exists(Path.Combine(repo.State, BuildCounter.StateFile)));

        var release = VersionResolver.Resolve(repo.Request(true, ("MNVT_VERSION", "1.0301.140.4"), ("MNVT_CHANNEL", "release")));
        Assert.Equal(("1.0301.140.4", "1.301.140.4", "override"), (release.Version, release.FileVersion, release.BuildNumberSource));
        Assert.Equal("1.0301.140.4-beta.1", VersionResolver.Resolve(repo.Request(true, ("MNVT_VERSION", "1.301.140.4"), ("MNVT_PRERELEASE", "beta.1"))).Version);

        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_BUILD_NUMBER", "0"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_BUILD_NUMBER", "70000"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_VERSION", "1.3102.1.1"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_VERSION", "1.1411.1"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_CHANNEL", "alpha"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_BUILD_ID", "not-a-guid"))));
        Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_BUILD_TIMESTAMP", "yesterday"))));
    }

    [Fact]
    public void ReproducibleBuilds_NeedFixedInputs_AndGiveTheSameIdentity()
    {
        using var repo = new TestRepository(commits: 3);
        (string, string)[] fixedInputs = [("MNVT_REPRODUCIBLE", "true"), ("MNVT_BUILD_NUMBER", "7"), ("SOURCE_DATE_EPOCH", "1798977600")]; // 2027-01-03 12:00 UTC
        var a = VersionResolver.Resolve(repo.Request(true, fixedInputs) with { Now = DateTimeOffset.UtcNow });
        var b = VersionResolver.Resolve(repo.Request(true, fixedInputs) with { Now = DateTimeOffset.UtcNow.AddHours(3) });
        Assert.Equal("1.0301.3.7-dev", a.Version); // MINOR follows the fixed time
        Assert.Equal(a.ToJson().ToString(), b.ToJson().ToString());
        Assert.True(a.Reproducible);

        Assert.Contains("MNVT_BUILD_TIMESTAMP", Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_REPRODUCIBLE", "1"), ("MNVT_BUILD_NUMBER", "7")))).Message, StringComparison.Ordinal);
        Assert.Contains("MNVT_BUILD_NUMBER", Assert.Throws<VersionConfigException>(() => VersionResolver.Resolve(repo.Request(true, ("MNVT_REPRODUCIBLE", "1"), ("SOURCE_DATE_EPOCH", "1798977600")))).Message, StringComparison.Ordinal);
    }
}
