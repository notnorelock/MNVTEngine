# Crash Reporting

Crash reporting turns a crash of an exported game, the Player or the editor into a report folder with a memory dump.
It is two parts: `MNVTEngine.CrashReporting`, a small library in the executable, and `MNVTCrashHandler.exe`
(`MNVTEngine.CrashHandler`), a separate executable next to it, like Unity's `UnityCrashHandler64.exe`. While the
report is written, the handler shows a small window: the title "&lt;Game&gt; - MNVTEngine &lt;version&gt;", the engine wordmark
with a red "!" badge, and a progress bar. It then says where the report is, with **Open Folder** and **Close**.

## How it works

The .NET runtime cannot run handlers after a native crash: an access violation in a GPU driver ends the process at
once, and the runtime's own dumper, createdump, can only be switched on before the process starts. So the handler
starts the game:

1. `CrashReporter.Run(args, options)`, first in `Main` of the process the user started, starts `MNVTCrashHandler.exe`
   with the executable's path and arguments, and exits.
2. The handler starts the executable again as the **game**, with the runtime's crash dump variables set
   (`DOTNET_DbgEnableMiniDump` and friends), and watches it. A native crash, a fail fast or a stack overflow is then
   dumped by createdump before the game ends.
3. An unhandled managed exception is written to a file by the game. The game then asks the handler for a dump, through
   a named event, and waits while the handler writes it with `MiniDumpWriteDump`.
4. When the game ends with a crash (a dump, an exception, or an NTSTATUS crash exit code), the handler collects the
   report and shows the window.

The process the user started exits as soon as the handler runs the game, so launchers see the game's process ID
change once. When it is run by a tool with redirected output (tests, `mnvt build --run`, `mnvt validate --run`), it
waits instead and returns the game's exit code, and the handler reports on stderr without a window.

`Run` returns null in the game (and when reporting is off), and the program continues as usual:

```csharp
if (CrashReporter.Run(args, new CrashReporterOptions("My Tool", reportsFolder, logFile)) is { } exitCode)
{
    return exitCode; // the crash handler runs the program from here on
}
```

The game clears the handler's variables once it has read them, so the processes it starts (the editor's Project Hub,
a built game run from the editor) get a handler of their own.

**Where the handler comes from.** The editor, the Player and the test app reference `MNVTEngine.CrashHandler`, so the
handler is built next to them. `mnvt build` publishes it next to the game:
- **JIT builds:** a framework-dependent handler on the game's private runtime (`Dependencies/Runtime`), with its
  libraries in `Dependencies/Managed`.
- **AOT builds:** a native handler compiled ahead of time (about 2.7 MB).

Without the handler executable, crash reporting is off, and stderr says so.

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

NativeAOT games have no createdump (there is no .NET runtime): their native crashes, fail fasts and stack overflows are
reported with the exit code, the log and the system facts, without a dump. Unhandled managed exceptions are dumped in
both kinds of build.

## Testing it

- In a running game or the Player, the developer console's `crash managed` and `crash native` crash on purpose.
- From code: `CrashReporter.ForceCrash(CrashKind.Native)`, like Unity's `Utils.ForceCrash`.
- `tests/MNVTEngine.CrashReporting.Tests` runs a small app under the handler and checks both crash kinds with their
  dumps, the forwarded exit codes, that the app ran as a process of its own, pruning and the window. `MNVT_CRASH_UI_SCREENSHOT=<file.png>` makes the window save itself twice: during
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
