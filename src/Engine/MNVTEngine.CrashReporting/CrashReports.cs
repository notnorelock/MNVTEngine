// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Globalization;

namespace MNVTEngine.CrashReporting;

/// <summary>A crash report folder the handler wrote (docs/engine/crash-reporting.md), read back after a restart.</summary>
/// <param name="Folder">The report's folder.</param>
/// <param name="Time">When the crash was reported (local time, from the folder's name).</param>
/// <param name="Report">report.txt: the system facts, the exit code, the exception and the last log lines.</param>
/// <param name="Exception">exception.txt, for an unhandled managed exception; null otherwise.</param>
/// <param name="HasDump">Whether the folder has a memory dump (it stays on the machine: it holds the process's memory).</param>
public sealed record CrashReportEntry(string Folder, DateTimeOffset Time, string Report, string? Exception, bool HasDump);

/// <summary>
/// The crash reports a program left, so it can show them or (with the user's consent) send them the next time it
/// starts. A report is pending until <see cref="MarkHandled"/>; reports stay on the machine otherwise.
/// </summary>
public static class CrashReports
{
    public const string ReportFileName = "report.txt";
    public const string ExceptionFileName = "exception.txt";
    public const string DumpFileName = "crash.dmp";

    /// <summary>Written into a folder once the program dealt with it (sent or dismissed).</summary>
    public const string HandledFileName = ".handled";

    /// <summary>The longest text read from a report file; the rest is cut (report.txt holds 200 log lines).</summary>
    public const int MaxTextLength = 256 * 1024;

    /// <summary>Where an exported game's reports go: Logs/Crashes next to the executable.</summary>
    public static string GameDirectory => Path.Combine(AppContext.BaseDirectory, "Logs", "Crashes");

    /// <summary>The reports in <paramref name="directory"/> not yet handled, newest first; none when it does not exist.</summary>
    public static IReadOnlyList<CrashReportEntry> Pending(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var reports = new List<CrashReportEntry>();
        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var report = Path.Combine(folder, ReportFileName);
            if (!File.Exists(report) || File.Exists(Path.Combine(folder, HandledFileName)))
            {
                continue; // still being written, or dealt with
            }

            var exception = Path.Combine(folder, ExceptionFileName);
            reports.Add(new CrashReportEntry(folder, TimeOf(folder), ReadText(report), File.Exists(exception) ? ReadText(exception) : null,
                File.Exists(Path.Combine(folder, DumpFileName))));
        }

        return [.. reports.OrderByDescending(r => r.Time)];
    }

    /// <summary>Marks a report as dealt with, so <see cref="Pending"/> no longer lists it. The folder is kept.</summary>
    public static void MarkHandled(CrashReportEntry report)
    {
        ArgumentNullException.ThrowIfNull(report);
        File.WriteAllText(Path.Combine(report.Folder, HandledFileName), DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset TimeOf(string folder)
    {
        var name = Path.GetFileName(folder);
        var stamp = name.Length >= 19 ? name[..19] : name; // "_2" and on: several crashes in one second
        return DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            ? new DateTimeOffset(time)
            : new DateTimeOffset(Directory.GetCreationTime(folder));
    }

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxTextLength];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }
}
