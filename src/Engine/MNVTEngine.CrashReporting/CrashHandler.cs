using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MNVTEngine.CrashReporting;

/// <summary>Named events between the child and its handler: "dump me" and "dumped" (Windows).</summary>
[SupportedOSPlatform("windows")]
internal sealed class CrashSignal(EventWaitHandle requested, EventWaitHandle done) : IDisposable
{
    public EventWaitHandle Requested { get; } = requested;

    public EventWaitHandle Done { get; } = done;

    private static string Name(string token, string suffix) => $@"Local\MNVTEngineCrash-{token}-{suffix}";

    public static CrashSignal Create(string token) =>
        new(new EventWaitHandle(false, EventResetMode.AutoReset, Name(token, "requested")), new EventWaitHandle(false, EventResetMode.AutoReset, Name(token, "done")));

    public static CrashSignal? TryOpen(string token) =>
        EventWaitHandle.TryOpenExisting(Name(token, "requested"), out var requested) && EventWaitHandle.TryOpenExisting(Name(token, "done"), out var done)
            ? new CrashSignal(requested, done)
            : null;

    /// <summary>The child: asks the handler for a dump and waits until it is written (or the handler is gone).</summary>
    public void RequestDump(TimeSpan timeout)
    {
        Requested.Set();
        Done.WaitOne(timeout);
    }

    public void Dispose()
    {
        Requested.Dispose();
        Done.Dispose();
    }
}

/// <summary>The handler process: starts the program as its child, dumps it on request, reports a crash.</summary>
internal static class CrashHandler
{
    // createdump's "1": a minidump with the threads, stacks and modules (heap dumps can fail on GPU-mapped memory).
    private const string CreatedumpType = "1";

    /// <summary>The handler process has no logging pipeline (it loads no engine code): its messages go to stderr.</summary>
    public static void Report(string message) => Console.Error.WriteLine(message);

    public static string DumpFile(int processId) => $"crash-{processId}.dmp";

    public static string ExceptionFile(int processId) => $"exception-{processId}.txt";

    public static int Run(string[] args, CrashReporterOptions options)
    {
        var token = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(options.ReportsDirectory, ".pending", token);
        try
        {
            Directory.CreateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashHandler.Report($"Crash reporting is off: cannot create '{staging}' ({ex.Message}).");
            return RunWithoutReporting(args);
        }

        var start = ChildStart(args);
        start.Environment[CrashReporter.ChildVariable] = "1";
        start.Environment[CrashReporter.TokenVariable] = token;
        start.Environment[CrashReporter.StagingVariable] = staging;
        start.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        start.Environment["DOTNET_DbgMiniDumpType"] = CreatedumpType;
        start.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(staging, "crash-%p.dmp");

        using var signal = OperatingSystem.IsWindows() ? CrashSignal.Create(token) : null;
        Console.CancelKeyPress += (_, e) => e.Cancel = true; // Ctrl+C reaches the child too; it decides
        var started = DateTime.Now;
        using var child = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{start.FileName}'.");
        WaitForExit(child, signal, staging);
        var exitCode = child.ExitCode;
        var dump = Path.Combine(staging, DumpFile(child.Id));
        var exception = Path.Combine(staging, ExceptionFile(child.Id));
        if (!File.Exists(dump) && !File.Exists(exception) && !IsCrashExitCode(exitCode))
        {
            TryDelete(staging);
            return exitCode;
        }

        var report = new CrashReport(options, staging, child.Id, exitCode, started, args);
        if (CrashWindow.ShouldShow())
        {
            CrashWindow.Show(options, report);
        }
        else
        {
            var result = report.Collect(null);
            CrashHandler.Report($"{options.ProductName} crashed ({CrashReport.Describe(exitCode)}). Crash report: {result.Folder}");
        }

        return exitCode;
    }

    /// <summary>The same program again: the executable, or <c>dotnet app.dll</c> when started through the dotnet host.</summary>
    private static ProcessStartInfo ChildStart(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    private static int RunWithoutReporting(string[] args)
    {
        var start = ChildStart(args);
        start.Environment[CrashReporter.ChildVariable] = "1";
        using var child = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{start.FileName}'.");
        child.WaitForExit();
        return child.ExitCode;
    }

    /// <summary>Waits for the child; writes a dump whenever it asks (its unhandled managed exception).</summary>
    private static void WaitForExit(Process child, CrashSignal? signal, string staging)
    {
        if (signal is null || !OperatingSystem.IsWindows())
        {
            child.WaitForExit();
            return;
        }

        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(child.Handle, ownsHandle: false) };
        while (WaitHandle.WaitAny([exited, signal.Requested]) == 1)
        {
            DumpWriter.Write(child, Path.Combine(staging, DumpFile(child.Id)));
            signal.Done.Set();
        }

        child.WaitForExit(); // lets Process read the exit code
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
            var pending = Path.GetDirectoryName(folder)!;
            if (Directory.Exists(pending) && !Directory.EnumerateFileSystemEntries(pending).Any())
            {
                Directory.Delete(pending);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty staging folder left behind is harmless; the next crash report prunes old folders.
        }
    }
}

/// <summary>Writes a minidump of another process with dbghelp (the managed-exception path; createdump covers native crashes).</summary>
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
