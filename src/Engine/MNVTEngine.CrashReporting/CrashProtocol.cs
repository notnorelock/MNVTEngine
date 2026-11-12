using System.Runtime.Versioning;

namespace MNVTEngine.CrashReporting;

/// <summary>
/// What a game and its crash handler (<c>MNVTCrashHandler.exe</c>) agree on: the handler's file name and command line,
/// the environment the handler starts the game with, the files the game leaves in the staging folder when it
/// crashes, and the named events between them.
/// </summary>
public static class CrashProtocol
{
    /// <summary>The handler executable, next to the game's executable.</summary>
    public const string HandlerFileName = "MNVTCrashHandler.exe";

    public const string WindowVariable = "MNVT_CRASH_UI";
    public const string ScreenshotVariable = "MNVT_CRASH_UI_SCREENSHOT";
    public const string DisableVariable = "MNVT_CRASH_REPORTER";

    /// <summary>Set by the handler on the game it starts: this process is the game, watched by a handler.</summary>
    public const string GameVariable = "MNVT_CRASH_GAME";
    public const string TokenVariable = "MNVT_CRASH_TOKEN";
    public const string StagingVariable = "MNVT_CRASH_STAGING";

    /// <summary>
    /// The handler's arguments: <c>--reports dir --product name --engine version --keep n [--log file] [--quiet] -- game
    /// executable and arguments</c>.
    /// </summary>
    public const string Reports = "--reports", Product = "--product", Engine = "--engine", Keep = "--keep", Log = "--log", Quiet = "--quiet";

    /// <summary>A minidump of the game: written by .NET's createdump (native crashes) or by the handler (managed ones).</summary>
    public static string DumpFile(int processId) => $"crash-{processId}.dmp";

    /// <summary>The game's unhandled .NET exception, as text.</summary>
    public static string ExceptionFile(int processId) => $"exception-{processId}.txt";
}

/// <summary>Named events between the game and its handler: "dump me" and "dumped" (Windows). The handler creates them; the game opens them.</summary>
[SupportedOSPlatform("windows")]
public sealed class CrashSignal(EventWaitHandle requested, EventWaitHandle done) : IDisposable
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

    /// <summary>The game: asks the handler for a dump and waits until it is written (or the time is up: the handler is gone).</summary>
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
