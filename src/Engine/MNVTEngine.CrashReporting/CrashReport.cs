using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MNVTEngine.CrashReporting;

/// <summary>What a collected crash report holds.</summary>
/// <param name="Folder">The report folder: <c>report.txt</c>, <c>crash.dmp</c> (when one was written), <c>exception.txt</c>, the log.</param>
public sealed record CrashReportResult(string Folder, bool HasDump, string? Exception);

/// <summary>Collects a crash's files from the staging folder into a report folder, with progress.</summary>
internal sealed class CrashReport(CrashReporterOptions options, string staging, int processId, int exitCode, DateTime started, IReadOnlyList<string> args)
{
    private const int LogTailLines = 200;

    public string ProductName => options.ProductName;

    /// <summary>Moves the dump (with progress: it is most of the work), writes the report and prunes old reports.</summary>
    public CrashReportResult Collect(IProgress<double>? progress)
    {
        var folder = NewFolder();
        var dumpSource = Path.Combine(staging, CrashHandler.DumpFile(processId));
        var hasDump = File.Exists(dumpSource) && new FileInfo(dumpSource).Length > 0;
        if (hasDump)
        {
            Copy(dumpSource, Path.Combine(folder, "crash.dmp"), p => progress?.Report(p * 0.85));
        }

        progress?.Report(0.85);
        var exceptionSource = Path.Combine(staging, CrashHandler.ExceptionFile(processId));
        var exception = File.Exists(exceptionSource) ? File.ReadAllText(exceptionSource) : null;
        if (exception is not null)
        {
            File.WriteAllText(Path.Combine(folder, "exception.txt"), exception);
        }

        string[] logTail = [];
        if (options.LogFile is { } log && File.Exists(log))
        {
            var target = Path.Combine(folder, Path.GetFileName(log));
            Copy(log, target, null);
            logTail = [.. File.ReadLines(target).TakeLast(LogTailLines)];
        }

        progress?.Report(0.95);
        File.WriteAllText(Path.Combine(folder, "report.txt"), Text(hasDump, exception, logTail));
        CrashHandler.TryDelete(staging);
        Prune();
        progress?.Report(1.0);
        return new CrashReportResult(folder, hasDump, exception);
    }

    /// <summary>The exit code in decimal and hex, with its meaning when it is a known crash code.</summary>
    public static string Describe(int exitCode)
    {
        var meaning = (uint)exitCode switch
        {
            0xC0000005u => "access violation",
            0xC00000FDu => "stack overflow",
            0xC0000409u => "fail fast (security check or Environment.FailFast)",
            0xC000001Du => "illegal instruction",
            0xC0000094u => "integer division by zero",
            0xE0434352u => "unhandled .NET exception",
            0x80000003u => "breakpoint",
            _ => null,
        };
        var hex = ((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture);
        return meaning is null ? $"exit code {exitCode} (0x{hex})" : $"exit code 0x{hex}, {meaning}";
    }

    private string Text(bool hasDump, string? exception, string[] logTail)
    {
        var text = new StringBuilder();
        void Line(string line) => text.Append(line).Append(Environment.NewLine);
        Line($"{options.ProductName} crashed");
        Line($"Engine:        MNVTEngine {options.EngineVersion}");
        Line($"Started:       {started.ToString("u", CultureInfo.InvariantCulture)}");
        Line($"Crashed:       {DateTime.Now.ToString("u", CultureInfo.InvariantCulture)}");
        Line($"Result:        {Describe(exitCode)}");
        Line($"Process:       {processId}, {Environment.ProcessPath} {string.Join(' ', args)}");
        Line($"OS:            {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Line($".NET:          {RuntimeInformation.FrameworkDescription}");
        Line($"CPU:           {Environment.ProcessorCount} logical processors");
        Line($"Dump:          {(hasDump ? "crash.dmp (open with Visual Studio, WinDbg or dotnet-dump analyze)" : "none (the process ended without one: fail fast, stack overflow, or a killed process)")}");
        Line("");
        if (exception is not null)
        {
            Line("Unhandled exception:");
            Line(exception.TrimEnd());
            Line("");
        }

        if (logTail.Length > 0)
        {
            Line($"Last {logTail.Length} log lines ({Path.GetFileName(options.LogFile)}):");
            foreach (var line in logTail)
            {
                Line(line);
            }
        }

        return text.ToString();
    }

    private string NewFolder()
    {
        var name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(options.ReportsDirectory, name);
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(options.ReportsDirectory, $"{name}_{i}");
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Copies in 1 MB chunks so the window can show the progress of large dumps; the source may still be open for writing (logs).</summary>
    private static void Copy(string source, string target, Action<double>? progress)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[1 << 20];
        var total = Math.Max(1L, input.Length);
        long copied = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
            copied += read;
            progress?.Invoke((double)copied / total);
        }
    }

    /// <summary>Keeps the newest reports (folders are named by time, so names sort by age).</summary>
    private void Prune()
    {
        foreach (var old in Directory.EnumerateDirectories(options.ReportsDirectory).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal).SkipLast(Math.Max(1, options.KeepReports)))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CrashHandler.Report($"Could not delete the old crash report '{old}': {ex.Message}");
            }
        }
    }
}
