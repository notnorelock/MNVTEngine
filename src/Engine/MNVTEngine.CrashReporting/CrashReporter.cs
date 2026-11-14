using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MNVTEngine.CrashReporting;

/// <summary>Where and how an executable reports crashes.</summary>
/// <param name="ProductName">Shown in the crash window's title (the game's name, "Editor", "Player").</param>
/// <param name="ReportsDirectory">Each crash gets a folder in it (<c>yyyy-MM-dd_HH-mm-ss</c>); the newest <see cref="KeepReports"/> are kept.</param>
/// <param name="LogFile">The process's log file, copied into the report (null: none).</param>
public sealed record CrashReporterOptions(string ProductName, string ReportsDirectory, string? LogFile = null)
{
    public int KeepReports { get; init; } = 10;

    /// <summary>The engine version shown in the title and report; defaults to this engine build's (its assemblies carry it).</summary>
    public string EngineVersion { get; init; } = DefaultEngineVersion;

    /// <summary>"1.1411.140.4-dev+sha.abcdef123456 (build 7b4e...)": the version, commit and build id of this engine build.</summary>
    internal static string DefaultEngineVersion
    {
        get
        {
            var assembly = typeof(CrashReporter).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var buildId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "MNVTEngine.BuildId")?.Value;
            return buildId is null ? version : $"{version} (build {buildId})";
        }
    }
}

public enum CrashKind
{
    /// <summary>An access violation in native code (as a GPU driver crash looks).</summary>
    Native,

    /// <summary>An unhandled managed exception on the main thread.</summary>
    Managed,
}

/// <summary>
/// Unity-style crash reporting with a separate crash handler, <c>MNVTCrashHandler.exe</c> next to the executable.
/// <see cref="Run"/>, called first in <c>Main</c>:
/// <list type="bullet">
/// <item>in the process the user started, starts the handler and ends (returns an exit code). The handler starts the
/// game again as its child, with .NET's createdump switched on, and watches it. A tool that runs the executable with
/// redirected output gets the game's exit code: the first process then waits for the handler;</item>
/// <item>in the game (started by the handler), installs the hook that writes an unhandled .NET exception down and
/// waits while the handler dumps the process, and returns null: <c>Main</c> goes on.</item>
/// </list>
/// Native crashes, fail-fast and stack overflows are dumped by createdump. After a crash the handler collects the
/// dump, the exception, the log and system facts into a report folder and shows a small window while it does.
/// Off (the game runs in the first process) with a debugger attached, <c>--no-crash-reporter</c>,
/// <c>MNVT_CRASH_REPORTER=0</c>, or without the handler executable.
/// </summary>
public static partial class CrashReporter
{
    private const string DisableArgument = "--no-crash-reporter";

    // createdump's "1": a minidump with the threads, stacks and modules (heap dumps can fail on GPU-mapped memory).
    private const string CreatedumpType = "1";

    /// <summary>
    /// Null: this process is the game; continue <c>Main</c>. A number: this process started the crash handler, which
    /// runs the game; return it from <c>Main</c>.
    /// </summary>
    public static int? Run(string[] args, CrashReporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(options);
        if (Environment.GetEnvironmentVariable(CrashProtocol.GameVariable) == "1")
        {
            InstallInGame();
            return null;
        }

        if (args.Contains(DisableArgument) || Environment.GetEnvironmentVariable(CrashProtocol.DisableVariable) == "0" || Debugger.IsAttached || Environment.ProcessPath is null)
        {
            return null;
        }

        var handler = Path.Combine(AppContext.BaseDirectory, CrashProtocol.HandlerFileName);
        if (!File.Exists(handler))
        {
            Console.Error.WriteLine($"Crash reporting is off: {CrashProtocol.HandlerFileName} is not next to the executable.");
            return null;
        }

        var tool = Console.IsOutputRedirected && Console.IsErrorRedirected; // run by a tool: wait and report on stderr, no window
        var start = new ProcessStartInfo(handler) { UseShellExecute = false };
        foreach (var arg in HandlerArguments(options, tool, args))
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{CrashProtocol.HandlerFileName} did not start.");
            if (!tool)
            {
                return 0; // the handler runs the game from here on
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Crash reporting is off: {CrashProtocol.HandlerFileName} could not start ({ex.Message}).");
            return null;
        }
    }

    /// <summary>The environment the handler starts the game with: watched, and dumped by createdump when it crashes natively.</summary>
    public static void SetGameEnvironment(IDictionary<string, string?> environment, string token, string staging)
    {
        ArgumentNullException.ThrowIfNull(environment);
        environment[CrashProtocol.GameVariable] = "1";
        environment[CrashProtocol.TokenVariable] = token;
        environment[CrashProtocol.StagingVariable] = staging;
        environment["DOTNET_DbgEnableMiniDump"] = "1";
        environment["DOTNET_DbgMiniDumpType"] = CreatedumpType;
        environment["DOTNET_DbgMiniDumpName"] = Path.Combine(staging, "crash-%p.dmp");
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
            RtlZeroMemory(0, 16); // writes to address 0 from native code
        }

        Environment.FailFast("Crash requested with CrashReporter.ForceCrash(CrashKind.Native).");
    }

    private static List<string> HandlerArguments(CrashReporterOptions options, bool quiet, string[] args)
    {
        List<string> list =
        [
            CrashProtocol.Reports, options.ReportsDirectory, CrashProtocol.Product, options.ProductName, CrashProtocol.Engine, options.EngineVersion,
            CrashProtocol.Keep, options.KeepReports.ToString(CultureInfo.InvariantCulture),
        ];
        if (options.LogFile is { } log)
        {
            list.AddRange([CrashProtocol.Log, log]);
        }

        if (quiet)
        {
            list.Add(CrashProtocol.Quiet);
        }

        // The game again: the executable, or `dotnet app.dll` when started through the dotnet host.
        list.Add("--");
        list.Add(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            list.Add(Environment.GetCommandLineArgs()[0]);
        }

        list.AddRange(args);
        return list;
    }

    /// <summary>The game: an unhandled .NET exception is written down, and the handler dumps the process before it ends.</summary>
    private static void InstallInGame()
    {
        var staging = Environment.GetEnvironmentVariable(CrashProtocol.StagingVariable);
        var token = Environment.GetEnvironmentVariable(CrashProtocol.TokenVariable);

        // Processes this one starts (the editor's Project Hub, a game run from the editor) get crash reporting of their
        // own: they must not take these variables for theirs. The runtime has read the createdump settings already.
        foreach (var variable in new[] { CrashProtocol.GameVariable, CrashProtocol.TokenVariable, CrashProtocol.StagingVariable, "DOTNET_DbgEnableMiniDump", "DOTNET_DbgMiniDumpType", "DOTNET_DbgMiniDumpName" })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (staging is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, CrashProtocol.ExceptionFile(Environment.ProcessId)), $"{e.ExceptionObject}{Environment.NewLine}Thread: {Environment.CurrentManagedThreadId} ({Thread.CurrentThread.Name ?? "unnamed"}){Environment.NewLine}");
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

    [LibraryImport("kernel32")]
    private static partial void RtlZeroMemory(nint destination, nint length);
}
