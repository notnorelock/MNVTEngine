namespace MNVTEngine.Core.Timing;

/// <summary>
/// Accumulates variable frame time and reports how many fixed simulation steps to run.
/// Backlog beyond <see cref="MaxStepsPerFrame"/> is discarded so a slow frame cannot trigger a
/// spiral where each frame needs more steps than the last.
/// </summary>
public sealed class FixedTimestep
{
    private double _accumulator;

    public FixedTimestep(float stepSeconds = 1f / 60f, int maxStepsPerFrame = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsPerFrame);
        StepSeconds = stepSeconds;
        MaxStepsPerFrame = maxStepsPerFrame;
    }

    public float StepSeconds { get; }

    public int MaxStepsPerFrame { get; }

    /// <summary>Fraction of a step left over after the last <see cref="Accumulate"/>, in [0, 1). Use to interpolate rendering.</summary>
    public float Alpha => (float)(_accumulator / StepSeconds);

    /// <summary>Total simulation time discarded by the backlog guard since construction or <see cref="Reset"/>.</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>Adds <paramref name="deltaSeconds"/> and returns the number of fixed steps the caller must run now.</summary>
    public int Accumulate(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds), deltaSeconds, "Delta must be finite and non-negative.");
        }

        _accumulator += deltaSeconds;
        var steps = (int)Math.Floor(_accumulator / StepSeconds);

        if (steps > MaxStepsPerFrame)
        {
            DroppedSeconds += (steps - MaxStepsPerFrame) * (double)StepSeconds;
            steps = MaxStepsPerFrame;
            _accumulator %= StepSeconds;
        }
        else
        {
            _accumulator -= steps * (double)StepSeconds;
        }

        return steps;
    }

    public void Reset()
    {
        _accumulator = 0;
        DroppedSeconds = 0;
    }
}
