using System.Globalization;

namespace MNVTEngine.Diagnostics;

public enum BudgetSeverity
{
    None,
    Information,
    Warning,
    Error,
    Critical,
}

/// <summary>Fractions of a budget's limit at which each severity starts. Defaults: 70 %, 85 %, 100 %, 150 %.</summary>
public sealed record BudgetThresholds(double Information = 0.70, double Warning = 0.85, double Error = 1.0, double Critical = 1.5)
{
    public static BudgetThresholds Default { get; } = new();

    /// <exception cref="ArgumentException">Not positive and strictly increasing.</exception>
    public void Validate()
    {
        if (!(Information > 0 && Information < Warning && Warning < Error && Error < Critical))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Budget thresholds must be positive and increasing (information < warning < error < critical); got {Information}, {Warning}, {Error}, {Critical}."));
        }
    }

    public BudgetSeverity Classify(double ratio) => ratio switch
    {
        _ when ratio >= Critical => BudgetSeverity.Critical,
        _ when ratio >= Error => BudgetSeverity.Error,
        _ when ratio >= Warning => BudgetSeverity.Warning,
        _ when ratio >= Information => BudgetSeverity.Information,
        _ => BudgetSeverity.None,
    };
}

/// <summary>A limit on one metric. <paramref name="Limit"/> is in the metric's unit (bytes, ms, count).</summary>
/// <param name="Hint">Where to look when it is exceeded.</param>
/// <param name="Thresholds">Overrides the monitor's default thresholds for this budget.</param>
public sealed record PerformanceBudget(string Id, string Metric, double Limit, string Subsystem, string Hint, BudgetThresholds? Thresholds = null);

/// <summary>Live state of one budget (updated in place every frame).</summary>
public sealed class BudgetState(PerformanceBudget budget)
{
    public PerformanceBudget Budget { get; internal set; } = budget;

    public MetricValue Value { get; internal set; }

    /// <summary>Value / limit; NaN while the metric is not measured.</summary>
    public double Ratio { get; internal set; } = double.NaN;

    public BudgetSeverity Severity { get; internal set; }

    /// <summary>Highest ratio seen since the monitor started (or <see cref="BudgetMonitor.ResetPeaks"/>).</summary>
    public double PeakRatio { get; internal set; }

    internal BudgetSeverity ReportedSeverity { get; set; }

    internal TimeSpan ReportedAt { get; set; }
}

public sealed record BudgetWarning(PerformanceBudget Budget, BudgetSeverity Severity, double Value, double Ratio, long Frame, TimeSpan Time)
{
    public string Message
    {
        get
        {
            var definition = Metrics.Find(Budget.Metric);
            var unit = definition?.Unit ?? MetricUnit.Count;
            var name = definition?.Name ?? Budget.Metric;
            var verb = Severity >= BudgetSeverity.Error ? "exceeded" : "at";
            return string.Create(CultureInfo.InvariantCulture,
                $"{Budget.Subsystem}: {name} budget {verb} {Ratio * 100:0.0}% ({Metrics.Format(MetricValue.Measured(Value), unit)} of {Metrics.Format(MetricValue.Measured(Budget.Limit), unit)}). {Budget.Hint}");
        }
    }
}

/// <summary>
/// Compares metrics against budgets every frame and raises <see cref="WarningRaised"/> without flooding: a budget
/// reports when it reaches a higher severity than it last reported, and repeats the same severity at most once per
/// <see cref="RepeatInterval"/> while it persists. After dropping back it must stay calm for that interval before the
/// same severity is reported again, so values flickering around a threshold report once. Unmeasured metrics never warn.
/// </summary>
public sealed class BudgetMonitor
{
    private readonly List<BudgetState> _states = [];
    private readonly Queue<BudgetWarning> _recent = new();
    private readonly Func<TimeSpan> _clock;

    /// <param name="clock">Elapsed time source (tests pass a fake); default is a stopwatch.</param>
    public BudgetMonitor(IEnumerable<PerformanceBudget> budgets, BudgetThresholds? thresholds = null, Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        Thresholds = thresholds ?? BudgetThresholds.Default;
        Thresholds.Validate();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        foreach (var budget in budgets)
        {
            Add(budget);
        }
    }

    public BudgetThresholds Thresholds { get; }

    public TimeSpan RepeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Lowest severity that raises <see cref="WarningRaised"/> (the overlay still shows lower ones).</summary>
    public BudgetSeverity ReportFrom { get; set; } = BudgetSeverity.Warning;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<BudgetState> States => _states;

    /// <summary>The last 64 reported warnings, oldest first.</summary>
    public IReadOnlyCollection<BudgetWarning> RecentWarnings => _recent;

    public event Action<BudgetWarning>? WarningRaised;

    /// <exception cref="ArgumentException">Invalid limit, duplicate id, or invalid thresholds.</exception>
    public void Add(PerformanceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        Validate(budget);
        if (_states.Any(s => s.Budget.Id == budget.Id))
        {
            throw new ArgumentException($"Budget '{budget.Id}' is already defined.", nameof(budget));
        }

        _states.Add(new BudgetState(budget));
    }

    /// <summary>Changes a limit for this session (console 'budget set', editor overrides).</summary>
    /// <exception cref="KeyNotFoundException">No such budget.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Limit not positive.</exception>
    public void SetLimit(string id, double limit)
    {
        var state = _states.FirstOrDefault(s => string.Equals(s.Budget.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No budget '{id}'. Budgets: {string.Join(", ", _states.Select(s => s.Budget.Id))}.");
        var changed = state.Budget with { Limit = limit };
        Validate(changed);
        state.Budget = changed;
        state.ReportedSeverity = BudgetSeverity.None;
    }

    public void ResetPeaks()
    {
        foreach (var state in _states)
        {
            state.PeakRatio = 0;
        }
    }

    public void Evaluate(MetricsSnapshot metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (!Enabled)
        {
            return;
        }

        var now = _clock();
        foreach (var state in _states)
        {
            var value = metrics.Get(state.Budget.Metric);
            state.Value = value;
            if (!value.IsMeasured)
            {
                state.Ratio = double.NaN;
                state.Severity = BudgetSeverity.None;
                continue;
            }

            state.Ratio = value.Value / state.Budget.Limit;
            state.PeakRatio = Math.Max(state.PeakRatio, state.Ratio);
            state.Severity = (state.Budget.Thresholds ?? Thresholds).Classify(state.Ratio);

            var calm = now - state.ReportedAt >= RepeatInterval;
            if (state.Severity < state.ReportedSeverity && calm)
            {
                state.ReportedSeverity = state.Severity; // forget an old report once things stayed calmer for a while
            }

            if (state.Severity >= ReportFrom && (state.Severity > state.ReportedSeverity || calm))
            {
                state.ReportedSeverity = state.Severity;
                state.ReportedAt = now;
                Report(new BudgetWarning(state.Budget, state.Severity, value.Value, state.Ratio, metrics.Frame, now));
            }
        }
    }

    private void Report(BudgetWarning warning)
    {
        _recent.Enqueue(warning);
        while (_recent.Count > 64)
        {
            _recent.Dequeue();
        }

        WarningRaised?.Invoke(warning);
    }

    private static void Validate(PerformanceBudget budget)
    {
        if (!(budget.Limit > 0) || double.IsInfinity(budget.Limit))
        {
            throw new ArgumentOutOfRangeException(nameof(budget), budget.Limit, $"Budget '{budget.Id}' needs a positive limit.");
        }

        budget.Thresholds?.Validate();
    }
}
