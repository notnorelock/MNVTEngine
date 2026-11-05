# Crash Reporting

`MNVTEngine.CrashReporting` turns a crash of an exported game, the Player or the editor into a report folder with a
memory dump. While the report is written, a small window shows, like Unity's crash handler: the title
"&lt;Game&gt; - MNVTEngine &lt;version&gt;", the engine wordmark with a red "!" badge, and a progress bar. It then says where the
report is, with **Open Folder** and **Close**.

## How it works

The .NET runtime cannot run handlers after a native crash (an access violation in a GPU driver ends the process at
once). So the executable splits in two, as Unity's player does with its crash handler process:

1. `CrashReporter.Run(args, options)`, first in `Main`, makes the process a small **handler**. It starts the same
   executable again as its **child**, with the same arguments, and waits. Only the child loads the engine.
2. The handler sets the runtime's crash dump variables for the child (`DOTNET_DbgEnableMiniDump` and friends). A native
   crash is then dumped by the runtime's own createdump before the child ends.
3. An unhandled managed exception is written to a file by the child. The child then asks the handler for a dump,
   through a named event, and waits while the handler writes it with `MiniDumpWriteDump`.
4. When the child ends with a crash (a dump, an exception, or an NTSTATUS crash exit code), the handler collects the
   report and shows the window. It always returns the child's exit code.

`Run` returns null in the child (and when reporting is off), and the program continues as usual:

```csharp
if (CrashReporter.Run(args, new CrashReporterOptions("My Tool", reportsFolder, logFile)) is { } exitCode)
{
    return exitCode; // this process was the handler; the program has ended
}
```

## Reports

Each crash gets a folder named by time (`2026-11-05_13-12-48`) in the reports folder; the newest 10 are kept.

| File | Contents |
|---|---|
| `report.txt` | Product, engine version, start and crash time, the exit code and its meaning, command line, OS, .NET, CPU count, the unhandled exception, the last 200 log lines |
| `crash.dmp` | The memory dump: threads, stacks, modules and the memory they reference (no whole heaps, which can contain unreadable GPU-mapped memory). Open it with Visual Studio, WinDbg or `dotnet-dump analyze` |
| `exception.txt` | The unhandled managed exception with its stack trace and thread |
| the log | A copy of the process's log file |

| Executable | Reports folder | Log |
|---|---|---|
| Exported game | `Logs/Crashes` next to the executable | `Logs/game.log` |
| Editor | `%LOCALAPPDATA%/MNVTEngine/Crashes/Editor` | `%LOCALAPPDATA%/MNVTEngine/Logs/editor.log` |
| Player | `%LOCALAPPDATA%/MNVTEngine/Crashes/Player` | none (console only) |

A fail fast (`Environment.FailFast`) or a stack overflow ends the process without a dump: the report then has the exit
code, the log and the system facts.

## Testing it

- In a running game or the Player, the developer console's `crash managed` and `crash native` crash on purpose.
- From code: `CrashReporter.ForceCrash(CrashKind.Native)`, like Unity's `Utils.ForceCrash`.
- `tests/MNVTEngine.CrashReporting.Tests` runs a small app under the handler and checks both crash kinds, the
  exit codes, pruning and the window. `MNVT_CRASH_UI_SCREENSHOT=<file.png>` makes the window save itself twice: during
  progress (`<file>-progress.png`) and with the result. It then closes itself.

## Switches

| Switch | Effect |
|---|---|
| `--no-crash-reporter`, `MNVT_CRASH_REPORTER=0` | No handler process: the program runs in the first process (no reports) |
| a debugger attached | Same: the debugger sees the crash itself |
| `MNVT_CRASH_UI=0` / `1` | Never / always show the window |

The window only shows for people. It does not show on CI (`CI`, `TF_BUILD`), without an interactive desktop, or when
both stdout and stderr go to pipes or files (a test runner or a script). Then the handler prints one line to stderr:
`<Game> crashed (exit code 0xC0000005, access violation). Crash report: <folder>`.

## Limits

- The window and the dumps are Windows-only. On other systems the handler still writes `report.txt` with the
  exception and the log, and the runtime's createdump variables still apply.
- The handler adds a second, idle process (a few megabytes) for the game's lifetime. `Ctrl+C` goes to the game;
  the handler ignores it and returns the game's exit code.
- Reports stay on the machine. There is no upload.
