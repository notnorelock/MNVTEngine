using System.Diagnostics;

namespace MNVTEngine.CrashReporting.Tests;

/// <summary>Runs the test app under the crash reporter and checks the reports its crashes leave.</summary>
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-crash-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Reports => Path.Combine(_root, "Crashes");

    private string Log => Path.Combine(_root, "game.log");

    private (int ExitCode, string StdErr) Run(string mode, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "MNVTEngine.CrashReporting.TestApp.dll"), mode, Reports, Log })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["MNVT_CRASH_UI"] = "0";
        foreach (var (key, value) in environment ?? [])
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "the app did not end");
        return (process.ExitCode, stderr.Result);
    }

    private string SingleReport()
    {
        var folders = Directory.GetDirectories(Reports).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList();
        return Assert.Single(folders);
    }

    [Fact]
    public void NormalExit_IsForwarded_AndLeavesNoReport()
    {
        var (exitCode, _) = Run("ok");
        Assert.Equal(7, exitCode);
        Assert.Contains("running ok", File.ReadAllText(Log), StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(Reports) ? Directory.GetFileSystemEntries(Reports) : []);
    }

    [Fact]
    public void ManagedCrash_IsReported_WithTheExceptionTheLogAndADump()
    {
        var (exitCode, stderr) = Run("managed");

        Assert.Equal(unchecked((int)0xE0434352), exitCode);
        var report = SingleReport();
        Assert.Contains($"Crash report: {report}", stderr, StringComparison.Ordinal);
        Assert.Contains("ForceCrash(CrashKind.Managed)", File.ReadAllText(Path.Combine(report, "exception.txt")), StringComparison.Ordinal);
        var text = File.ReadAllText(Path.Combine(report, "report.txt"));
        Assert.Contains("unhandled .NET exception", text, StringComparison.Ordinal);
        Assert.Contains("running managed", text, StringComparison.Ordinal); // the log's last lines
        Assert.True(File.Exists(Path.Combine(report, "game.log")));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(new FileInfo(Path.Combine(report, "crash.dmp")).Length > 10_000, "the handler dumped the process before it ended");
        }
    }

    [Fact]
    public void NativeCrash_IsDumpedByTheRuntime_AndReported()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The native crash is an access violation in kernel32.");
        var (exitCode, _) = Run("native");

        Assert.Equal(unchecked((int)0xC0000005), exitCode);
        var report = SingleReport();
        Assert.Contains("access violation", File.ReadAllText(Path.Combine(report, "report.txt")), StringComparison.Ordinal);
        Assert.True(new FileInfo(Path.Combine(report, "crash.dmp")).Length > 10_000, "createdump wrote the dump");
        Assert.False(File.Exists(Path.Combine(report, "exception.txt")));
        Assert.False(Directory.Exists(Path.Combine(Reports, ".pending")) && Directory.EnumerateFileSystemEntries(Path.Combine(Reports, ".pending")).Any());
    }

    [Fact]
    public void OldReports_ArePruned_AndTheReporterStepsAside_WhenAsked()
    {
        Directory.CreateDirectory(Reports);
        for (var i = 0; i < 12; i++)
        {
            Directory.CreateDirectory(Path.Combine(Reports, $"2020-01-01_00-00-{i:00}"));
        }

        Run("managed");
        var kept = Directory.GetDirectories(Reports).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList();
        Assert.Equal(10, kept.Count);
        Assert.DoesNotContain(kept, d => d.EndsWith("00-00-00", StringComparison.Ordinal));

        // Off: the program runs in the first process, so a crash is just a crash.
        var before = Directory.GetDirectories(Reports).Length;
        var (exitCode, _) = Run("ok", new() { ["MNVT_CRASH_REPORTER"] = "0" });
        Assert.Equal(7, exitCode);
        Assert.Equal(before, Directory.GetDirectories(Reports).Length);
    }

    [Fact]
    public void Window_ShowsProgress_ThenWhereTheReportIs()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The crash window is Win32.");
        var shot = Path.Combine(_root, "window.png");
        Run("managed", new() { ["MNVT_CRASH_UI"] = "1", ["MNVT_CRASH_UI_SCREENSHOT"] = shot });

        Assert.True(new FileInfo(Path.Combine(_root, "window-progress.png")).Length > 1000, "the progress state was captured");
        Assert.True(new FileInfo(shot).Length > 1000, "the result state was captured (the window then closed itself)");
        var copy = Environment.GetEnvironmentVariable("MNVT_CRASH_TEST_SHOTS"); // to look at them: set a folder
        if (copy is not null)
        {
            File.Copy(shot, Path.Combine(copy, "crash-window.png"), overwrite: true);
            File.Copy(Path.Combine(_root, "window-progress.png"), Path.Combine(copy, "crash-window-progress.png"), overwrite: true);
        }
    }
}
