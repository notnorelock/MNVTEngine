using MNVTEngine.Core.Timing;

namespace MNVTEngine.Core.Tests;

public class TimingTests
{
    [Fact]
    public void GameClock_AccumulatesAndCountsFrames()
    {
        var clock = new GameClock();
        clock.Advance(0.1);
        var t = clock.Advance(0.1);
        Assert.Equal(0.1f, t.DeltaTime);
        Assert.Equal(0.2, t.TotalTime, 6);
        Assert.Equal(2, t.FrameCount);
    }

    [Fact]
    public void GameClock_ClampsHitches()
    {
        var clock = new GameClock(maxDeltaTime: 0.1f);
        var t = clock.Advance(5.0);
        Assert.Equal(0.1f, t.DeltaTime);
        Assert.Equal(0.1f, t.UnscaledDeltaTime);
    }

    [Fact]
    public void GameClock_TimeScaleAffectsOnlyScaledTime()
    {
        var clock = new GameClock { TimeScale = 0.5f };
        var t = clock.Advance(0.1);
        Assert.Equal(0.05f, t.DeltaTime);
        Assert.Equal(0.1f, t.UnscaledDeltaTime);
    }

    [Fact]
    public void GameClock_PauseFreezesScaledTimeOnly()
    {
        var clock = new GameClock { IsPaused = true };
        var t = clock.Advance(0.1);
        Assert.Equal(0f, t.DeltaTime);
        Assert.Equal(0.0, t.TotalTime);
        Assert.Equal(0.1, t.UnscaledTotalTime, 6);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void GameClock_RejectsInvalidElapsed(double elapsed) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameClock().Advance(elapsed));

    [Fact]
    public void GameClock_RejectsInvalidTimeScale()
    {
        var clock = new GameClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TimeScale = -1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.TimeScale = float.PositiveInfinity);
    }

    [Fact]
    public void GameClock_Reset()
    {
        var clock = new GameClock();
        clock.Advance(0.1);
        clock.Reset();
        Assert.Equal(1, clock.Advance(0.1).FrameCount);
    }

    [Fact]
    public void FixedTimestep_RunsWholeStepsAndKeepsRemainder()
    {
        var fixedStep = new FixedTimestep(0.1f);
        Assert.Equal(0, fixedStep.Accumulate(0.05f));
        Assert.Equal(1, fixedStep.Accumulate(0.07f));
        Assert.Equal(0.2f, fixedStep.Alpha, 3);
    }

    [Fact]
    public void FixedTimestep_TotalStepsMatchElapsedTime()
    {
        var fixedStep = new FixedTimestep(1f / 60f);
        var total = 0;
        for (var i = 0; i < 1000; i++)
        {
            total += fixedStep.Accumulate(1f / 144f);
        }

        // 1000 frames at 144 Hz = 6.944 s = 416 whole steps at 60 Hz.
        Assert.Equal(416, total);
    }

    [Fact]
    public void FixedTimestep_CapsStepsAndRecordsDroppedTime()
    {
        var fixedStep = new FixedTimestep(0.01f, maxStepsPerFrame: 5);
        Assert.Equal(5, fixedStep.Accumulate(0.105f));
        Assert.Equal(0.05, fixedStep.DroppedSeconds, 5);
        Assert.InRange(fixedStep.Alpha, 0.49f, 0.51f);
        Assert.Equal(0, fixedStep.Accumulate(0f));
    }

    [Fact]
    public void FixedTimestep_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestep(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestep(0.1f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTimestep().Accumulate(float.NaN));
    }
}
