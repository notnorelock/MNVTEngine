namespace MNVTEngine.Diagnostics.Tests;

public sealed class BudgetTests
{
    private TimeSpan _now;

    private BudgetMonitor Monitor(params PerformanceBudget[] budgets) => new(budgets, clock: () => _now) { RepeatInterval = TimeSpan.FromSeconds(10) };

    private static MetricsSnapshot Snapshot(string id, double value, long frame = 1)
    {
        var metrics = new MetricsSnapshot { Frame = frame };
        metrics.Set(id, value);
        return metrics;
    }

    [Theory]
    [InlineData(0.5, BudgetSeverity.None)]
    [InlineData(0.7, BudgetSeverity.Information)]
    [InlineData(0.86, BudgetSeverity.Warning)]
    [InlineData(1.0, BudgetSeverity.Error)]
    [InlineData(1.6, BudgetSeverity.Critical)]
    public void Thresholds_ClassifyTheRatio(double ratio, BudgetSeverity expected) =>
        Assert.Equal(expected, BudgetThresholds.Default.Classify(ratio));

    [Fact]
    public void Thresholds_MustIncrease() =>
        Assert.Throws<ArgumentException>(() => new BudgetThresholds(0.9, 0.8, 1.0, 1.5).Validate());

    [Fact]
    public void ExceededBudget_ReportsValueLimitPercentageSubsystemAndHint()
    {
        var monitor = Monitor(new PerformanceBudget("drawCalls", Metrics.DrawCalls, 1000, "Renderer", "Batch more."));
        BudgetWarning? warning = null;
        monitor.WarningRaised += w => warning = w;
        monitor.Evaluate(Snapshot(Metrics.DrawCalls, 1200));

        Assert.NotNull(warning);
        Assert.Equal(BudgetSeverity.Error, warning.Severity);
        Assert.Equal(1.2, warning.Ratio, 6);
        Assert.Equal("Renderer: Draw calls budget exceeded 120.0% (1,200 of 1,000). Batch more.", warning.Message);
        Assert.Equal(BudgetSeverity.Error, monitor.States[0].Severity);
    }

    [Fact]
    public void Warnings_AreDeduplicated_AndRepeatOnlyAfterTheInterval()
    {
        var monitor = Monitor(new PerformanceBudget("frame", Metrics.FrameTime, 16.67, "Frame", ""));
        var raised = new List<BudgetWarning>();
        monitor.WarningRaised += raised.Add;

        for (var frame = 0; frame < 100; frame++)
        {
            _now += TimeSpan.FromMilliseconds(16);
            monitor.Evaluate(Snapshot(Metrics.FrameTime, 20, frame)); // exceeded every frame for 1.6 s
        }

        Assert.Single(raised);

        _now += TimeSpan.FromSeconds(10);
        monitor.Evaluate(Snapshot(Metrics.FrameTime, 20));
        Assert.Equal(2, raised.Count); // still exceeded: one reminder per interval

        monitor.Evaluate(Snapshot(Metrics.FrameTime, 30));
        Assert.Equal(3, raised.Count); // escalation to critical reports at once
        Assert.Equal(BudgetSeverity.Critical, raised[^1].Severity);
    }

    [Fact]
    public void ValuesFlickeringAroundAThreshold_ReportOnce()
    {
        var monitor = Monitor(new PerformanceBudget("frame", Metrics.FrameTime, 10, "Frame", ""));
        var raised = 0;
        monitor.WarningRaised += _ => raised++;
        for (var i = 0; i < 50; i++)
        {
            _now += TimeSpan.FromMilliseconds(100);
            monitor.Evaluate(Snapshot(Metrics.FrameTime, i % 2 == 0 ? 11 : 5));
        }

        Assert.Equal(1, raised);
    }

    [Fact]
    public void InformationLevel_ShowsInTheStateButIsNotReportedByDefault()
    {
        var monitor = Monitor(new PerformanceBudget("memory", Metrics.ManagedHeap, 100, "Memory", ""));
        var raised = 0;
        monitor.WarningRaised += _ => raised++;
        monitor.Evaluate(Snapshot(Metrics.ManagedHeap, 75));
        Assert.Equal(BudgetSeverity.Information, monitor.States[0].Severity);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void UnmeasuredMetrics_NeverWarn_AndAreNotZero()
    {
        var monitor = Monitor(new PerformanceBudget("gpu", Metrics.GpuMemoryUsed, 100, "Memory", ""));
        var metrics = new MetricsSnapshot { Frame = 1 };
        metrics.SetUnsupported(Metrics.GpuMemoryUsed);
        monitor.Evaluate(metrics);

        var state = monitor.States[0];
        Assert.Equal(MetricStatus.Unsupported, state.Value.Status);
        Assert.True(double.IsNaN(state.Ratio));
        Assert.Equal(BudgetSeverity.None, state.Severity);
        Assert.Equal("n/a (unsupported)", Metrics.Format(Metrics.GpuMemoryUsed, state.Value));

        metrics.Frame = 2; // not set this frame: unavailable, not the old value
        Assert.Equal(MetricStatus.Unavailable, metrics.Get(Metrics.GpuMemoryUsed).Status);
    }

    [Fact]
    public void EstimatedMetrics_AreBudgeted_AndShownAsEstimates()
    {
        var monitor = Monitor(new PerformanceBudget("tex", Metrics.TextureMemory, 1000, "Memory", ""));
        var metrics = new MetricsSnapshot { Frame = 1 };
        metrics.Set(Metrics.TextureMemory, MetricValue.Estimated(2048));
        monitor.Evaluate(metrics);

        Assert.Equal(BudgetSeverity.Critical, monitor.States[0].Severity); // 205 %
        Assert.True(monitor.States[0].Value.HasValue);
        Assert.False(monitor.States[0].Value.IsMeasured);
        Assert.Equal("~2.0 KB", Metrics.Format(Metrics.TextureMemory, monitor.States[0].Value));
    }

    [Fact]
    public void SessionOverrides_ChangeTheLimit()
    {
        var monitor = Monitor(new PerformanceBudget("drawCalls", Metrics.DrawCalls, 1000, "Renderer", ""));
        monitor.SetLimit("DRAWCALLS", 100);
        monitor.Evaluate(Snapshot(Metrics.DrawCalls, 120));
        Assert.Equal(BudgetSeverity.Error, monitor.States[0].Severity);
        Assert.Throws<KeyNotFoundException>(() => monitor.SetLimit("nope", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.SetLimit("drawCalls", 0));
    }
}
