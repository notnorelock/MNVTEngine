using MNVTEngine.CrashReporting;

// Usage: <mode: ok | managed | native> <reports folder> <log file>
var (mode, reports, log) = (args[0], args[1], args[2]);
if (CrashReporter.Run(args, new CrashReporterOptions("Crash Test", reports, log)) is { } code)
{
    return code;
}

File.AppendAllText(log, $"game {Environment.ProcessId} running {mode}, inherited by children: {Environment.GetEnvironmentVariable("MNVT_CRASH_GAME") ?? "nothing"}{Environment.NewLine}");
switch (mode)
{
    case "managed":
        CrashReporter.ForceCrash(CrashKind.Managed);
        break;
    case "native":
        CrashReporter.ForceCrash(CrashKind.Native);
        break;
}

return 7;
