// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Core.Diagnostics;

/// <summary>Frame times over a stretch of play (<see cref="FrameTimeRecorder"/>), in milliseconds.</summary>
/// <param name="Frames">Frames recorded.</param>
/// <param name="Seconds">Their total time.</param>
/// <param name="MeanMs">The mean frame.</param>
/// <param name="P50Ms">Half the frames took at most this.</param>
/// <param name="P95Ms">95 % of the frames took at most this.</param>
/// <param name="P99Ms">99 % of the frames took at most this: the hitches players notice.</param>
/// <param name="MaxMs">The longest frame.</param>
/// <param name="OverBudget">Frames longer than the recorder's budget.</param>
public sealed record FrameTimeStats(int Frames, double Seconds, double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs, int OverBudget)
{
    public double FramesPerSecond => Seconds > 0 ? Frames / Seconds : 0;
}

/// <summary>
/// Collects frame times in shipped builds, where the <see cref="Profiler"/> is off: a game feeds it each frame's
/// unscaled delta and takes <see cref="FrameTimeStats"/> now and then (say, a minute) to log them. Bounded: past
/// <see cref="Capacity"/> frames it keeps the newest.
/// </summary>
public sealed class FrameTimeRecorder(double budgetMs = 1000.0 / 30, int capacity = 1 << 16)
{
    private readonly float[] _frames = new float[Math.Max(1, capacity)];
    private int _count;
    private int _next;

    public int Capacity => _frames.Length;

    /// <summary>Frames longer than this count as <see cref="FrameTimeStats.OverBudget"/>.</summary>
    public double BudgetMs { get; } = budgetMs;

    public int Count => _count;

    public void Add(double frameMs)
    {
        if (!double.IsFinite(frameMs) || frameMs < 0)
        {
            return;
        }

        _frames[_next] = (float)frameMs;
        _next = (_next + 1) % _frames.Length;
        _count = Math.Min(_count + 1, _frames.Length);
    }

    /// <summary>The frames since the last take, then starts over; null when none were recorded.</summary>
    public FrameTimeStats? Take()
    {
        if (_count == 0)
        {
            return null;
        }

        var sorted = new float[_count];
        Array.Copy(_frames, sorted, _count); // the order is lost on sorting anyway, wrapped or not
        Array.Sort(sorted);
        double total = 0;
        var over = 0;
        foreach (var ms in sorted)
        {
            total += ms;
            over += ms > BudgetMs ? 1 : 0;
        }

        var stats = new FrameTimeStats(_count, total / 1000, total / _count, Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1], over);
        (_count, _next) = (0, 0);
        return stats;
    }

    /// <summary>Nearest rank: the smallest frame that at least <paramref name="share"/> of the frames do not exceed.</summary>
    private static double Percentile(float[] sorted, double share) => sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)];
}
