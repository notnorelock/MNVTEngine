// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Core.Diagnostics;

namespace MNVTEngine.Core.Tests;

public sealed class FrameTimeRecorderTests
{
    [Fact]
    public void Take_ReportsMeanPercentilesAndHitches_ThenStartsOver()
    {
        var recorder = new FrameTimeRecorder(budgetMs: 20);
        for (var i = 1; i <= 100; i++)
        {
            recorder.Add(i == 100 ? 80 : 10); // 99 smooth frames and one hitch
        }

        var stats = recorder.Take()!;

        Assert.Equal(100, stats.Frames);
        Assert.Equal(1.07, stats.Seconds, 3);
        Assert.Equal(10.7, stats.MeanMs, 3);
        Assert.Equal((10.0, 10.0, 10.0, 80.0), (stats.P50Ms, stats.P95Ms, stats.P99Ms, stats.MaxMs));
        Assert.Equal(1, stats.OverBudget);
        Assert.Null(recorder.Take());
    }

    [Fact]
    public void ARecorderKeepsTheNewestFrames_AndIgnoresNonsense()
    {
        var recorder = new FrameTimeRecorder(capacity: 4);
        foreach (var ms in new[] { 100.0, 1, 2, 3, 4, double.NaN, -1 })
        {
            recorder.Add(ms);
        }

        var stats = recorder.Take()!;
        Assert.Equal((4, 4.0), (stats.Frames, stats.MaxMs));
    }
}
