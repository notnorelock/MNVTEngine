namespace MNVTEngine.Core.Timing;

/// <summary>Timing snapshot for one frame. All values are seconds.</summary>
/// <param name="DeltaTime">Frame delta after time scale and pause are applied.</param>
/// <param name="UnscaledDeltaTime">Frame delta clamped against hitches, ignoring time scale and pause.</param>
/// <param name="TotalTime">Sum of scaled deltas since the clock started.</param>
/// <param name="UnscaledTotalTime">Sum of unscaled deltas since the clock started.</param>
/// <param name="FrameCount">Number of frames advanced, starting at 1 for the first frame.</param>
public readonly record struct FrameTime(
    float DeltaTime,
    float UnscaledDeltaTime,
    double TotalTime,
    double UnscaledTotalTime,
    long FrameCount);
