using Newtonsoft.Json.Linq;

namespace MNVTEngine.Versioning.Tests;

public sealed class VersionFormatTests
{
    [Theory]
    [InlineData(2026, 11, 14, "1411", 1411)]
    [InlineData(2026, 11, 26, "2611", 2611)]
    [InlineData(2027, 1, 3, "0301", 301)]
    [InlineData(2026, 2, 14, "1402", 1402)]
    [InlineData(2026, 12, 31, "3112", 3112)]
    [InlineData(2026, 1, 31, "3101", 3101)] // month boundary
    [InlineData(2026, 2, 1, "0102", 102)]
    [InlineData(2028, 2, 29, "2902", 2902)] // leap day
    public void Ddmm_KeepsFourDigits_AndParsesToTheNumericMinor(int year, int month, int day, string text, int minor)
    {
        Assert.Equal(text, VersionDate.Ddmm(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(minor, VersionDate.Parse(text));
        Assert.Equal(text, VersionDate.Text(minor));
    }

    [Fact]
    public void Ddmm_UsesTheConfiguredTimeZone_AcrossTheYearBoundary()
    {
        var newYearsEve = new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal("3112", VersionDate.Ddmm(newYearsEve, TimeZoneInfo.Utc));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); // IANA ids work on Windows too (ICU)
        Assert.Equal("0101", VersionDate.Ddmm(newYearsEve, berlin));
    }

    [Theory]
    [InlineData("3102")] // 31 February
    [InlineData("3104")] // 31 April
    [InlineData("0013")]
    [InlineData("0000")]
    [InlineData("1400")]
    [InlineData("12a4")]
    [InlineData("411")]
    [InlineData("14110")]
    public void InvalidDates_AreRejected(string text)
    {
        Assert.Throws<FormatException>(() => VersionDate.Parse(text));
    }

    [Fact]
    public void Versions_MapToTheAssemblyFields()
    {
        var info = Sample(minor: 301, build: 4, prerelease: "dev", dirty: true);
        Assert.Equal("1.0301.140.4", info.NumericVersion);
        Assert.Equal("1.0301.140.4-dev", info.Version);
        Assert.Equal("1.0.0.0", info.AssemblyVersion);
        Assert.Equal("1.301.140.4", info.FileVersion);
        Assert.Equal("1.301.140.4-dev", info.PackageVersion);
        Assert.Equal("1.0301.140.4-dev+sha.abcdef123456.dirty", info.InformationalVersion);
        Assert.True(System.Version.TryParse(info.FileVersion, out _));

        var release = Sample(minor: 1411, build: 4, prerelease: null, dirty: false);
        Assert.Equal("1.1411.140.4", release.Version);
        Assert.Equal("1.1411.140.4+sha.abcdef123456", release.InformationalVersion);

        var archive = release with { Source = SourceRevision.Unavailable("no git") };
        Assert.Equal("1.1411.140.4+source.unknown", archive.InformationalVersion);
    }

    [Fact]
    public void Metadata_RoundTripsThroughJson()
    {
        var info = Sample(minor: 1411, build: 4, prerelease: "beta.1", dirty: true) with
        {
            Ci = new CiInfo("github-actions", "123.1"),
            Context = new BuildContext { Configuration = "Release", TargetPlatform = "win-x64", Profile = "3d", ScriptingBackend = "aot", GraphicsBackend = "vulkan", Modules = ["core", "world.3d"] },
        };
        var json = info.ToJson();
        Assert.Equal("1.1411.140.4-beta.1", (string?)json["version"]);
        Assert.Equal("1411", (string?)json["minorText"]);
        Assert.Equal(4, (int?)json["build"]);
        Assert.Equal("2026-11-14T13:00:00Z", (string?)json["buildTimestampUtc"]);
        Assert.Equal("abcdef1234567890abcdef1234567890abcdef12", (string?)json["source"]!["commit"]);

        var back = EngineBuildInfo.FromJson(JObject.Parse(json.ToString()));
        Assert.Equal(json.ToString(), back.ToJson().ToString());
        Assert.Throws<InvalidDataException>(() => EngineBuildInfo.FromJson(new JObject { ["schemaVersion"] = 99 }));
        Assert.Throws<InvalidDataException>(() => EngineBuildInfo.FromJson(new JObject { ["schemaVersion"] = 1, ["major"] = 1 }));
    }

    [Fact]
    public void Session_IsOnlyRewrittenWhenItChanges_AndItsSourceIsEscaped()
    {
        var directory = Directory.CreateTempSubdirectory("mnvt-session-").FullName;
        try
        {
            var info = Sample(minor: 1411, build: 4, prerelease: "dev", dirty: false) with { Source = Sample(1411, 4, null, false).Source with { Subject = "Fix \"quotes\" \\ and\nnewlines" } };
            VersionSession.Write(directory, info);
            var source = Path.Combine(directory, VersionSession.SourceFile);
            var written = File.GetLastWriteTimeUtc(source);
            File.SetLastWriteTimeUtc(source, written.AddHours(-1));
            VersionSession.Write(directory, info);
            Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(source)); // unchanged: not rewritten, nothing recompiles

            var text = File.ReadAllText(source);
            Assert.Contains("public static string CommitSubject => \"Fix \\\"quotes\\\" \\\\ and\\nnewlines\";", text.Replace("string?", "string", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("public static int Build => 4;", text, StringComparison.Ordinal);
            Assert.Contains("<MnvtFileVersion>1.1411.140.4</MnvtFileVersion>", File.ReadAllText(Path.Combine(directory, VersionSession.PropsFile)), StringComparison.Ordinal);
            Assert.Equal(info.ToJson().ToString(), VersionSession.Read(directory)!.ToJson().ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static EngineBuildInfo Sample(int minor, int build, string? prerelease, bool dirty) => new()
    {
        Major = 1,
        Minor = minor,
        Patch = 140,
        Build = build,
        Channel = prerelease is null ? "release" : "development",
        Prerelease = prerelease,
        BuildId = Guid.Parse("7b4e0000-0000-4000-8000-000000000001"),
        TimestampUtc = new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.Zero),
        BuildNumberSource = "counter",
        Source = new SourceRevision("git", "abcdef1234567890abcdef1234567890abcdef12", "main", "Subject", dirty, Detached: false, Shallow: false, 140, null),
        Host = new HostInfo("Windows", "x64", "11.0.100", "18.0", ".NET 11"),
    };
}
