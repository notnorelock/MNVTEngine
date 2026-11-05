using System.Diagnostics;
using System.Reflection;

namespace MNVTEngine.CrashReporting;

/// <summary>Where and how an executable reports crashes.</summary>
/// <param name="ProductName">Shown in the crash window's title (the game's name, "Editor", "Player").</param>
/// <param name="ReportsDirectory">Each crash gets a folder in it (<c>yyyy-MM-dd_HH-mm-ss</c>); the newest <see cref="KeepReports"/> are kept.</param>
/// <param name="LogFile">The process's log file, copied into the report (null: none).</param>
public sealed record CrashReporterOptions(string ProductName, string ReportsDirectory, string? LogFile = null)
{
    public int KeepReports { get; init; } = 10;

    /// <summary>The engine version shown in the title; defaults to this assembly's informational version.</summary>
    public string EngineVersion { get; init; } = DefaultEngineVersion;

    internal static string DefaultEngineVersion =>
        typeof(CrashReporter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
}

public enum CrashKind
{
    /// <summary>An access violation in native code (as a GPU driver crash looks).</summary>
    Native,

    /// <summary>An unhandled managed exception on the main thread.</summary>
    Managed,
}

/// <summary>
/// Unity-style crash reporting. <see cref="Run"/>, called first in <c>Main</c>, turns the process into a small crash
/// handler that starts the real program as a child process and waits for it:
/// <list type="bullet">
/// <item>native crashes are dumped by the .NET runtime's createdump, which the handler enables for the child;</item>
/// <item>unhandled managed exceptions are written by the child, which then waits while the handler dumps it;</item>
/// <item>after a crash the handler collects the dump, the exception, the log and system facts into a report folder,
/// showing a small window with a progress bar while it does (<see cref="CrashWindow"/>).</item>
/// </list>
/// The handler forwards the child's exit code. It steps aside (the program runs in this process) when a debugger is
/// attached, with <c>--no-crash-reporter</c>, or with <c>MNVT_CRASH_REPORTER=0</c>.
/// </summary>
public static class CrashReporter
{
    internal const string ChildVariable = "MNVT_CRASH_CHILD";
    internal const string TokenVariable = "MNVT_CRASH_TOKEN";
    internal const string StagingVariable = "MNVT_CRASH_STAGING";
    internal const string DisableVariable = "MNVT_CRASH_REPORTER";
    internal const string WindowVariable = "MNVT_CRASH_UI";
    internal const string ScreenshotVariable = "MNVT_CRASH_UI_SCREENSHOT";
    private const string DisableArgument = "--no-crash-reporter";

    /// <summary>
    /// Null: this process is the program; continue <c>Main</c>. A number: this process was the crash handler and the
    /// program has ended; return it from <c>Main</c>.
    /// </summary>
    public static int? Run(string[] args, CrashReporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        if (Environment.GetEnvironmentVariable(ChildVariable) == "1")
        {
            InstallChild();
            return null;
        }

        if (args.Contains(DisableArgument) || Environment.GetEnvironmentVariable(DisableVariable) == "0" || Debugger.IsAttached || Environment.ProcessPath is null)
        {
            return null;
        }

        return CrashHandler.Run(args, options);
    }

    /// <summary>Crashes the process on purpose, to test crash reporting (like Unity's <c>Utils.ForceCrash</c>).</summary>
    public static void ForceCrash(CrashKind kind)
    {
        if (kind == CrashKind.Managed)
        {
            throw new InvalidOperationException("Crash requested with CrashReporter.ForceCrash(CrashKind.Managed).");
        }

        if (OperatingSystem.IsWindows())
        {
            Native.RtlZeroMemory(0, 16); // writes to address 0 from native code
        }

        Environment.FailFast("Crash requested with CrashReporter.ForceCrash(CrashKind.Native).");
    }

    /// <summary>The child: unhandled managed exceptions are written down, and the handler dumps the process before it ends.</summary>
    private static void InstallChild()
    {
        var staging = Environment.GetEnvironmentVariable(StagingVariable);
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (staging is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, CrashHandler.ExceptionFile(Environment.ProcessId)), $"{e.ExceptionObject}{Environment.NewLine}Thread: {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.Name ?? "unnamed"}){Environment.NewLine}");
            }
            catch (IOException)
            {
                // The process is going down anyway: the report then has the dump and the exit code only.
            }

            if (OperatingSystem.IsWindows() && token is not null && CrashSignal.TryOpen(token) is { } signal)
            {
                using (signal)
                {
                    signal.RequestDump(TimeSpan.FromSeconds(60));
                }
            }
        };
    }
}
