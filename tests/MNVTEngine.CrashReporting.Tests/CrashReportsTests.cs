// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.CrashReporting;

namespace MNVTEngine.CrashReporting.Tests;

/// <summary>Reading back the report folders the handler leaves, and marking them dealt with.</summary>
public sealed class CrashReportsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mnvt-crash-reports-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Report(string name, string text, string? exception = null, bool dump = false)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;
        File.WriteAllText(Path.Combine(folder, CrashReports.ReportFileName), text);
        if (exception is not null)
        {
            File.WriteAllText(Path.Combine(folder, CrashReports.ExceptionFileName), exception);
        }

        if (dump)
        {
            File.WriteAllBytes(Path.Combine(folder, CrashReports.DumpFileName), [1, 2, 3]);
        }

        return folder;
    }

    [Fact]
    public void PendingReports_AreListedNewestFirst_UntilHandled()
    {
        Report("2026-11-05_13-12-48", "older", dump: true);
        Report("2026-11-06_09-00-01_2", "newer", "System.InvalidOperationException: boom");
        Directory.CreateDirectory(Path.Combine(_dir, "2026-11-07_00-00-00")); // no report.txt yet: being written

        var pending = CrashReports.Pending(_dir);

        Assert.Equal(["newer", "older"], pending.Select(r => r.Report));
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 1), pending[0].Time.DateTime);
        Assert.Equal(("System.InvalidOperationException: boom", false), (pending[0].Exception, pending[0].HasDump));
        Assert.Equal((null, true), (pending[1].Exception, pending[1].HasDump));

        CrashReports.MarkHandled(pending[0]);
        Assert.Equal(["older"], CrashReports.Pending(_dir).Select(r => r.Report));
    }

    [Fact]
    public void LongReports_AreCut_AndAMissingFolderHasNone()
    {
        Report("2026-11-05_13-12-48", new string('x', CrashReports.MaxTextLength + 10));

        Assert.Equal(CrashReports.MaxTextLength, CrashReports.Pending(_dir).Single().Report.Length);
        Assert.Empty(CrashReports.Pending(Path.Combine(_dir, "missing")));
    }
}
