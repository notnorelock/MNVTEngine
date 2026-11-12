using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Win32.SafeHandles;

namespace MNVTEngine.CrashReporting;

/// <summary>
/// <c>MNVTCrashHandler.exe</c>: started by a game's first process (<see cref="CrashReporter.Run"/>), it starts the game
/// as its child with .NET's createdump switched on and watches it: dumps it when it asks (an unhandled .NET
/// exception), and after a crash collects the report and shows the crash window. Returns the game's exit code. Loads no
/// engine code: only MNVTEngine.CrashReporting (the protocol) and the image libraries of the window.
/// </summary>
public static class CrashHandler
{
    public static int Main(string[] args)
    {
        // In an exported game the handler's dependencies sit in Dependencies/Managed, as the game's do.
        var managed = Path.Combine(AppContext.BaseDirectory, "Dependencies", "Managed");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(managed, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };

        return Run(args);
    }

    /// <summary>The handler has no logging pipeline: its messages go to stderr (inherited from the first process).</summary>
    public static void Report(string message) => Console.Error.WriteLine(message);

    [MethodImpl(MethodImplOptions.NoInlining)] // the resolver above must be installed before CrashReporting loads
    private static int Run(string[] args)
    {
        if (Arguments.Parse(args) is not { } arguments)
        {
            Report($"Usage: {CrashProtocol.HandlerFileName} {CrashProtocol.Reports} <dir> {CrashProtocol.Product} <name> [...] -- <game executable> <arguments>. Games start it themselves.");
            return 2;
        }

        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(arguments.Options.ReportsDirectory, ".pending", token);
        var start = new ProcessStartInfo(arguments.Game[0]) { UseShellExecute = false };
        foreach (var arg in arguments.Game.Skip(1))
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            Directory.CreateDirectory(staging);
            CrashReporter.SetGameEnvironment(start.Environment, token, staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Crash reporting is off: cannot create '{staging}' ({ex.Message}).");
            start.Environment[CrashProtocol.GameVariable] = "1";
        }

        using var signal = OperatingSystem.IsWindows() ? CrashSignal.Create(token) : null;
        Console.CancelKeyPress += (_, e) => e.Cancel = true; // Ctrl+C reaches the game too; it decides
        var started = DateTime.Now;
        using var game = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{start.FileName}'.");
        WaitForExit(game, signal, staging);
        var exitCode = game.ExitCode;
        var dump = Path.Combine(staging, CrashProtocol.DumpFile(game.Id));
        var exception = Path.Combine(staging, CrashProtocol.ExceptionFile(game.Id));
        if (!File.Exists(dump) && !File.Exists(exception) && !IsCrashExitCode(exitCode))
        {
            TryDelete(staging);
            return exitCode;
        }

        var report = new CrashReport(arguments.Options, staging, game.Id, exitCode, started, [.. arguments.Game.Skip(1)]);
        if (CrashWindow.ShouldShow(arguments.Quiet))
        {
            CrashWindow.Show(arguments.Options, report);
        }
        else
        {
            var result = report.Collect(null);
            Report($"{arguments.Options.ProductName} crashed ({CrashReport.Describe(exitCode)}). Crash report: {result.Folder}");
        }

        return exitCode;
    }

    /// <summary>Waits for the game; writes a dump whenever it asks (its unhandled .NET exception).</summary>
    private static void WaitForExit(Process game, CrashSignal? signal, string staging)
    {
        if (signal is null || !OperatingSystem.IsWindows())
        {
            game.WaitForExit();
            return;
        }

        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(game.Handle, ownsHandle: false) };
        while (WaitHandle.WaitAny([exited, signal.Requested]) == 1)
        {
            DumpWriter.Write(game, Path.Combine(staging, CrashProtocol.DumpFile(game.Id)));
            signal.Done.Set();
        }

        game.WaitForExit(); // lets Process read the exit code
    }

    /// <summary>
    /// Windows: NTSTATUS errors (0xC...: access violation, stack overflow, fail fast) and the CLR's unhandled exception
    /// code. Elsewhere: killed by a signal (128 + signal).
    /// </summary>
    public static bool IsCrashExitCode(int exitCode) =>
        OperatingSystem.IsWindows() ? (uint)exitCode >= 0xC0000000u || (uint)exitCode == 0xE0434352u || (uint)exitCode == 0x80000003u : exitCode > 128;

    public static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
            var pending = Path.GetDirectoryName(folder);
            if (pending is not null && Directory.Exists(pending) && !Directory.EnumerateFileSystemEntries(pending).Any())
            {
                Directory.Delete(pending);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty staging folder left behind is harmless; the next crash report prunes old folders.
        }
    }

    /// <summary>The handler's command line (see <see cref="CrashProtocol"/>).</summary>
    private sealed record Arguments(CrashReporterOptions Options, bool Quiet, IReadOnlyList<string> Game)
    {
        public static Arguments? Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var quiet = false;
            var i = 0;
            for (; i < args.Length && args[i] != "--"; i++)
            {
                if (args[i] == CrashProtocol.Quiet)
                {
                    quiet = true;
                }
                else if (i + 1 < args.Length)
                {
                    values[args[i]] = args[++i];
                }
            }

            string? Get(string name) => values.GetValueOrDefault(name);
            if (Get(CrashProtocol.Reports) is not { } reports || Get(CrashProtocol.Product) is not { } product || i + 1 >= args.Length)
            {
                return null;
            }

            var options = new CrashReporterOptions(product, reports, Get(CrashProtocol.Log))
            {
                EngineVersion = Get(CrashProtocol.Engine) ?? "dev",
                KeepReports = int.TryParse(Get(CrashProtocol.Keep), CultureInfo.InvariantCulture, out var keep) ? keep : 10,
            };
            return new Arguments(options, quiet, [.. args.Skip(i + 1)]);
        }
    }
}

/// <summary>Writes a minidump of the game with dbghelp (the managed-exception path; createdump covers native crashes).</summary>
internal static class DumpWriter
{
    // MiniDumpWithDataSegs | WithHandleData | WithUnloadedModules | WithIndirectlyReferencedMemory | WithThreadInfo:
    // threads, stacks, globals and what they point to, without whole heaps (GPU-mapped memory cannot always be read).
    private const uint DumpType = 0x1 | 0x4 | 0x20 | 0x40 | 0x1000;

    public static bool Write(Process process, string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            return Native.MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle.DangerousGetHandle(), DumpType, 0, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            CrashHandler.Report($"Could not write the crash dump '{path}': {ex.Message}");
            return false;
        }
    }
}
