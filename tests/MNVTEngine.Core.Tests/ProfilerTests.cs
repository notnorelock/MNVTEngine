using MNVTEngine.Core.Diagnostics;

namespace MNVTEngine.Core.Tests;

public sealed class ProfilerTests : IDisposable
{
    private readonly FrameProfiler? _previous = Profiler.Current;

    public void Dispose() => Profiler.Current = _previous;

    [Fact]
    public void WithoutAProfiler_SamplingIsANoOp()
    {
        Profiler.Current = null;
        using (Profiler.Sample("Nothing"))
        {
            Profiler.Count("Things", 3);
        }
    }

    [Fact]
    public void Samples_NestByPath_AndAggregatePerFrame()
    {
        var profiler = new FrameProfiler();
        Profiler.Current = profiler;

        profiler.BeginFrame();
        using (Profiler.Sample("Update"))
        {
            for (var i = 0; i < 3; i++)
            {
                using (Profiler.Sample("AI"))
                {
                    Thread.Sleep(1);
                }
            }
        }

        using (Profiler.Sample("Render"))
        {
            using (Profiler.Sample("AI")) // same name, different parent
            {
            }
        }

        Profiler.Count("Enemies", 5);
        profiler.EndFrame();

        var frame = profiler.LastFrame!;
        Assert.Equal(["Update/AI", "Update", "Render/AI", "Render"], frame.Samples.Select(s => s.Path));
        var ai = frame.Samples[0];
        Assert.Equal(("AI", 1, 3), (ai.Name, ai.Depth, ai.Calls));
        Assert.True(ai.Milliseconds >= 3, $"three 1 ms sleeps, got {ai.Milliseconds}");
        Assert.True(frame.Samples[1].Milliseconds >= ai.Milliseconds); // a region includes its children
        Assert.Equal(5, frame.Counters["Enemies"]);
        Assert.True(frame.Milliseconds >= frame.Samples[1].Milliseconds);
    }

    [Fact]
    public void Summary_AveragesOverFrames_OrdersParentsFirst_AndKeepsTheHistoryBounded()
    {
        var profiler = new FrameProfiler(history: 10);
        Profiler.Current = profiler;
        for (var i = 0; i < 25; i++)
        {
            profiler.BeginFrame();
            using (Profiler.Sample("Update"))
            {
                if (i % 2 == 0)
                {
                    using (Profiler.Sample("Physics"))
                    {
                    }
                }
            }

            Profiler.Count("Bodies", i);
            profiler.EndFrame();
        }

        Assert.Equal(10, profiler.Frames.Count);
        Assert.Equal(24, profiler.Frames.Last().Index);
        var summary = profiler.Summarize(4);
        Assert.Equal(4, summary.Frames);
        Assert.Equal(["Update", "Update/Physics"], summary.Samples.Select(s => s.Path));
        Assert.Equal(1.0, summary.Samples[0].AverageCalls);
        Assert.Equal(0.5, summary.Samples[1].AverageCalls); // in 2 of the last 4 frames
        Assert.Equal(22.5, summary.AverageCounters["Bodies"]);
        Assert.True(summary.MaxFrameMilliseconds >= summary.P95FrameMilliseconds);

        var report = profiler.FormatReport(4);
        Assert.Contains("MNVTEngine profile: 4 frames", report, StringComparison.Ordinal);
        Assert.Contains("  Physics", report, StringComparison.Ordinal);
        Assert.Contains("Bodies", report, StringComparison.Ordinal);
    }

    [Fact]
    public void UnbalancedOrOutOfFrameCalls_AreIgnored()
    {
        var profiler = new FrameProfiler();
        Profiler.Current = profiler;
        using (Profiler.Sample("BeforeAnyFrame"))
        {
        }

        profiler.EndFrame(); // no frame open
        Assert.Empty(profiler.Frames);
        Assert.Equal(0, profiler.Summarize().Frames);
    }
}
