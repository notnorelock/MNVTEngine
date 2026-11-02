namespace MNVTEngine.Core.Timing;

/// <summary>
/// Converts raw wall-clock frame durations into game time: clamps hitches, applies time scale and pause.
/// The clock does not read the system timer itself; the host loop passes measured elapsed time in.
/// </summary>
public sealed class GameClock
{
    private float _timeScale = 1f;
    private double _totalTime;
    private double _unscaledTotalTime;
    private long _frameCount;

    /// <param name="maxDeltaTime">Upper bound for a single frame delta, so a debugger pause or load hitch does not explode the simulation.</param>
    public GameClock(float maxDeltaTime = 0.25f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeltaTime);
        MaxDeltaTime = maxDeltaTime;
    }

    public float MaxDeltaTime { get; }

    /// <summary>Multiplier for scaled time. 0 freezes gameplay, 1 is real time.</summary>
    public float TimeScale
    {
        get => _timeScale;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Time scale must be finite.");
            }

            _timeScale = value;
        }
    }

    /// <summary>When paused, scaled delta is 0 while unscaled time keeps advancing (for UI and editor).</summary>
    public bool IsPaused { get; set; }

    public FrameTime Current { get; private set; }

    public FrameTime Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), elapsedSeconds, "Elapsed time must be finite and non-negative.");
        }

        var unscaled = (float)Math.Min(elapsedSeconds, MaxDeltaTime);
        var scaled = IsPaused ? 0f : unscaled * _timeScale;

        _totalTime += scaled;
        _unscaledTotalTime += unscaled;
        _frameCount++;

        Current = new FrameTime(scaled, unscaled, _totalTime, _unscaledTotalTime, _frameCount);
        return Current;
    }

    public void Reset()
    {
        _totalTime = 0;
        _unscaledTotalTime = 0;
        _frameCount = 0;
        Current = default;
    }
}
