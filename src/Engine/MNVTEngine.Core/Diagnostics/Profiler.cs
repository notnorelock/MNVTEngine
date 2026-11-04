using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MNVTEngine.Core.Diagnostics;

/// <summary>Total time and call count of one region in a frame. <see cref="Path"/> joins the enclosing regions with '/'.</summary>
public sealed record ProfileSample(string Path, double Milliseconds, int Calls)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public int Depth => Path.Count(c => c == '/');
}

/// <summary>One recorded frame.</summary>
public sealed record ProfiledFrame(long Index, double Milliseconds, long AllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, IReadOnlyList<ProfileSample> Samples, IReadOnlyDictionary<string, long> Counters);

/// <summary>Per-region statistics over several frames.</summary>
public sealed record SampleSummary(string Path, double AverageMilliseconds, double MaxMilliseconds, double AverageCalls)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public int Depth => Path.Count(c => c == '/');
}

public sealed record ProfileSummary(int Frames, double AverageFrameMilliseconds, double P95FrameMilliseconds, double MaxFrameMilliseconds, double AverageAllocatedKilobytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, IReadOnlyList<SampleSummary> Samples, IReadOnlyDictionary<string, double> AverageCounters)
{
    public double MinFrameMilliseconds { get; init; }

    public double P50FrameMilliseconds { get; init; }

    public double P99FrameMilliseconds { get; init; }

    public double AverageFps => AverageFrameMilliseconds > 0 ? 1000.0 / AverageFrameMilliseconds : 0;
}

/// <summary>
/// Records where frame time goes. Code marks regions with <see cref="Profiler.Sample"/>; the host brackets frames with
/// <see cref="BeginFrame"/>/<see cref="EndFrame"/> and keeps the last <c>history</c> frames, including allocations and
/// garbage collections. Samples nest: a region's time includes its children. Use from one thread.
/// </summary>
public sealed class FrameProfiler(int history = 300)
{
    private readonly Queue<ProfiledFrame> _frames = new();
    private readonly List<(string Path, long Ticks, int Calls)> _samples = [];
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Stack<(string Path, long Start)> _open = new();
    private long _frameStart;
    private long _allocatedAtStart;
    private int _gen0;
    private int _gen1;
    private int _gen2;
    private long _index;
    private bool _inFrame;

    public IReadOnlyCollection<ProfiledFrame> Frames => _frames;

    public ProfiledFrame? LastFrame { get; private set; }

    public void BeginFrame()
    {
        _samples.Clear();
        _counters.Clear();
        _open.Clear();
        _frameStart = Stopwatch.GetTimestamp();
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        (_gen0, _gen1, _gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        _inFrame = true;
    }

    public void EndFrame()
    {
        if (!_inFrame)
        {
            return;
        }

        _inFrame = false;
        var elapsed = Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds;
        var frame = new ProfiledFrame(
            _index++,
            elapsed,
            GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart,
            GC.CollectionCount(0) - _gen0,
            GC.CollectionCount(1) - _gen1,
            GC.CollectionCount(2) - _gen2,
            [.. _samples.Select(s => new ProfileSample(s.Path, Stopwatch.GetElapsedTime(0, s.Ticks).TotalMilliseconds, s.Calls))],
            new Dictionary<string, long>(_counters, StringComparer.Ordinal));
        _frames.Enqueue(frame);
        while (_frames.Count > history)
        {
            _frames.Dequeue();
        }

        LastFrame = frame;
    }

    /// <summary>Sets a per-frame counter (draw calls, bodies, ...); the last value in a frame wins.</summary>
    public void SetCounter(string name, long value)
    {
        if (_inFrame)
        {
            _counters[name] = value;
        }
    }

    internal void Begin(string name)
    {
        if (_inFrame)
        {
            _open.Push((_open.TryPeek(out var parent) ? parent.Path + "/" + name : name, Stopwatch.GetTimestamp()));
        }
    }

    internal void End()
    {
        if (!_inFrame || !_open.TryPop(out var open))
        {
            return;
        }

        var ticks = Stopwatch.GetTimestamp() - open.Start;
        for (var i = 0; i < _samples.Count; i++)
        {
            if (string.Equals(_samples[i].Path, open.Path, StringComparison.Ordinal))
            {
                _samples[i] = (open.Path, _samples[i].Ticks + ticks, _samples[i].Calls + 1);
                return;
            }
        }

        _samples.Add((open.Path, ticks, 1));
    }

    /// <summary>Statistics over the last <paramref name="frames"/> recorded frames (all if fewer).</summary>
    public ProfileSummary Summarize(int frames = int.MaxValue)
    {
        var recent = _frames.Skip(Math.Max(0, _frames.Count - frames)).ToList();
        if (recent.Count == 0)
        {
            return new ProfileSummary(0, 0, 0, 0, 0, 0, 0, 0, [], new Dictionary<string, double>());
        }

        var times = recent.Select(f => f.Milliseconds).Order().ToList();
        double Percentile(double p) => times[Math.Min(times.Count - 1, (int)(times.Count * p))];
        var samples = recent.SelectMany(f => f.Samples)
            .GroupBy(s => s.Path, StringComparer.Ordinal)
            .Select(g => new SampleSummary(g.Key, g.Sum(s => s.Milliseconds) / recent.Count, g.Max(s => s.Milliseconds), (double)g.Sum(s => s.Calls) / recent.Count))
            .OrderBy(s => s.Path, StringComparer.Ordinal) // parents before their children
            .ToList();
        var counters = recent.SelectMany(f => f.Counters).GroupBy(c => c.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Average(c => (double)c.Value), StringComparer.Ordinal);
        return new ProfileSummary(
            recent.Count,
            times.Average(),
            times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))],
            times[^1],
            recent.Average(f => f.AllocatedBytes) / 1024.0,
            recent.Sum(f => f.Gen0Collections),
            recent.Sum(f => f.Gen1Collections),
            recent.Sum(f => f.Gen2Collections),
            samples,
            counters)
        {
            MinFrameMilliseconds = times[0],
            P50FrameMilliseconds = Percentile(0.5),
            P99FrameMilliseconds = Percentile(0.99),
        };
    }

    /// <summary>Plain-text report of <see cref="Summarize"/>.</summary>
    public string FormatReport(int frames = int.MaxValue)
    {
        var summary = Summarize(frames);
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.AppendLine(c, $"MNVTEngine profile: {summary.Frames} frames");
        text.AppendLine(c, $"Frame time: avg {summary.AverageFrameMilliseconds:0.000} ms ({summary.AverageFps:0} fps), min {summary.MinFrameMilliseconds:0.000}, p50 {summary.P50FrameMilliseconds:0.000}, p95 {summary.P95FrameMilliseconds:0.000}, p99 {summary.P99FrameMilliseconds:0.000}, max {summary.MaxFrameMilliseconds:0.000} ms");
        text.AppendLine(c, $"Allocated: {summary.AverageAllocatedKilobytes:0.0} KB/frame; GC gen0 {summary.Gen0Collections}, gen1 {summary.Gen1Collections}, gen2 {summary.Gen2Collections}");
        text.AppendLine();
        text.AppendLine(c, $"{"Sample",-40} {"avg ms",9} {"max ms",9} {"calls",7}");
        foreach (var sample in summary.Samples)
        {
            text.AppendLine(c, $"{new string(' ', sample.Depth * 2) + sample.Name,-40} {sample.AverageMilliseconds,9:0.000} {sample.MaxMilliseconds,9:0.000} {sample.AverageCalls,7:0.#}");
        }

        if (summary.AverageCounters.Count > 0)
        {
            text.AppendLine();
            foreach (var (name, value) in summary.AverageCounters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.AppendLine(c, $"{name,-40} {value,9:0.#}");
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// Profiling entry points for engine and game code. Recording goes to the profiler the host made current on this
/// thread; with none, every call is a no-op costing a null check.
/// </summary>
public static class Profiler
{
    [ThreadStatic]
    private static FrameProfiler? _current;

    /// <summary>
    /// Instrumentation level: <see cref="ProfilerDetail.Systems"/> times engine phases and systems; <see cref="ProfilerDetail.Scripts"/>
    /// also times every component callback per type ("Player.Update"), which costs a dictionary lookup per call.
    /// </summary>
    public static ProfilerDetail Detail { get; set; } = ProfilerDetail.Systems;

    /// <summary>True when per-component timing is on and a profiler records on this thread.</summary>
    public static bool TimesComponents => Detail >= ProfilerDetail.Scripts && _current is not null;

    /// <summary>Same as <see cref="Sample"/>: <c>using (Profiler.BeginSample("PlayerController.Update")) { ... }</c>.</summary>
    public static ProfileScope BeginSample(string name) => Sample(name);

    public static FrameProfiler? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>Times a region until the returned scope is disposed: <c>using var _ = Profiler.Sample("AI");</c>. Use constant names.</summary>
    public static ProfileScope Sample(string name)
    {
        var profiler = Detail > ProfilerDetail.Frame ? _current : null; // begin and end are skipped together
        profiler?.Begin(name);
        return new ProfileScope(profiler);
    }

    public static void Count(string name, long value) => _current?.SetCounter(name, value);
}

public enum ProfilerDetail
{
    /// <summary>Frame totals only.</summary>
    Frame,

    /// <summary>Engine phases and systems (default).</summary>
    Systems,

    /// <summary>Also every component callback, per component type.</summary>
    Scripts,
}

public readonly struct ProfileScope(FrameProfiler? profiler) : IDisposable
{
    public void Dispose() => profiler?.End();
}
