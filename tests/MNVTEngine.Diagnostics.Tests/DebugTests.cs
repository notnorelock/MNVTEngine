using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
using MNVTEngine.Mathematics;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Diagnostics.Tests;

[CollectionDefinition(nameof(DebugStatics), DisableParallelization = true)]
public sealed class DebugStatics;

[Collection(nameof(DebugStatics))]
public sealed class DebugTests : IDisposable
{
    private readonly DebugCommandBuffer _buffer = new(capacity: 4);

    public DebugTests() => (Debug.Buffer, Debug.DrawingEnabled) = (_buffer, true);

    public void Dispose() => Debug.Buffer = null;

    [Fact]
    public void FrameTimedAndPersistentDraws_LiveAsLongAsAsked()
    {
        Debug.DrawLine(Vector2.Zero, Vector2.One, Color.Red);                       // one frame
        Debug.DrawCircle(Vector2.Zero, 1f, Color.Yellow, duration: 0.5f);           // timed
        var handle = Debug.DrawRect(new Rect(0, 0, 1, 1), Color.White, Debug.Persistent);
        Assert.Equal(3, _buffer.Count);

        _buffer.EndFrame(0.3f);
        Assert.Equal(2, _buffer.Count);
        _buffer.EndFrame(0.3f);
        Assert.Equal(DebugShape.Rect, Assert.Single(_buffer.Commands.ToArray()).Shape);
        _buffer.EndFrame(1000f);
        Assert.Equal(1, _buffer.Count); // persistent survives

        Assert.True(Debug.Remove(handle));
        Assert.Equal(0, _buffer.Count);
    }

    [Fact]
    public void FullBuffer_DropsAndCounts_InsteadOfGrowing()
    {
        for (var i = 0; i < 10; i++)
        {
            Debug.DrawPoint(new Vector2(i, 0), Color.Green);
        }

        Assert.Equal(4, _buffer.Count);
        Assert.Equal(6, _buffer.Dropped);
        Assert.Equal(10, _buffer.Requested);
        _buffer.EndFrame(0.016f);
        Assert.Equal(0, _buffer.Dropped);
    }

    [Fact]
    public void DisabledDrawing_AndNoBuffer_AreNoOps()
    {
        Debug.DrawingEnabled = false;
        Debug.DrawLine(Vector2.Zero, Vector2.One, Color.Red);
        Debug.DrawingEnabled = true;
        Assert.Equal(0, _buffer.Count);
        Debug.Buffer = null;
        Assert.Equal(DebugDrawHandle.None, Debug.DrawText("x", Vector2.Zero, Color.White));
    }

    [Fact]
    public void Text_KeepsSpaceAlignmentAndBackground()
    {
        Debug.DrawWorldText("hp 10", new Vector2(1, 2), Color.Green, 18f);
        Debug.DrawText("screen", new Vector2(20, 40), Color.White, alignment: TextAlignment.Right);
        Debug.DrawLabel("tag", Vector2.Zero, Color.Cyan);
        var commands = _buffer.Commands.ToArray();
        Assert.Equal((DebugSpace.World, TextAlignment.Center, 18f), (commands[0].Space, commands[0].Alignment, commands[0].Size));
        Assert.Equal((DebugSpace.Screen, TextAlignment.Right), (commands[1].Space, commands[1].Alignment));
        Assert.True(commands[2].Background.A > 0);
    }

    [Fact]
    public void Grid_IsOneCommandPerGridLine()
    {
        Debug.Buffer = new DebugCommandBuffer(100);
        Debug.DrawGrid(Vector2.Zero, Vector2.One, 3, 2, Color.Gray);
        Assert.Equal(4 + 3, Debug.Buffer.Count);
    }

    [Fact]
    public void Log_NamesTheCallingClassAndMethod()
    {
        var sink = new List<LogEntry>();
        using var hub = new LogHub().AddSink(new ListSink(sink));
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(hub));
        var previous = GameLog.Factory;
        GameLog.Factory = factory;
        try
        {
            Debug.Log("hello");
            Debug.LogWarning(42, category: "AI");
            Debug.LogException(new InvalidOperationException("broken"));
        }
        finally
        {
            GameLog.Factory = previous;
        }

        Assert.Equal(typeof(DebugTests).FullName, sink[0].Category);
        Assert.Equal(nameof(Log_NamesTheCallingClassAndMethod), sink[0].Source!.Method);
        Assert.True(sink[0].Source!.Line > 0);
        Assert.Equal(("AI", "42", LogLevel.Warning), (sink[1].Category, sink[1].Message, sink[1].Level));
        Assert.Equal(nameof(DebugTests), sink[1].Source!.Class);
        Assert.IsType<InvalidOperationException>(sink[2].Exception);
    }

    private sealed class ListSink(List<LogEntry> entries) : ILogSink
    {
        public void Write(LogEntry entry) => entries.Add(entry);
    }
}

public sealed class CommandAndSettingsTests
{
    [Fact]
    public void Commands_RunRegisteredHandlersOnly_WithQuotedArguments()
    {
        var registry = new DebugCommandRegistry();
        List<string>? seen = null;
        registry.Register("spawn", "Spawns an object.", "<prefab> [count]", args =>
        {
            seen = [.. args];
            return DebugCommandResult.Ok("spawned");
        });

        Assert.Equal("spawned", registry.Execute("spawn \"Big Crate\" 3").Output);
        Assert.Equal(["Big Crate", "3"], seen);
        Assert.False(registry.Execute("cmd.exe /c del").Success);
        Assert.Contains("spawn <prefab> [count] - Spawns an object.", registry.Execute("help").Output, StringComparison.Ordinal);
        Assert.Equal(["spawn"], registry.Complete("sp"));
        Assert.Throws<ArgumentException>(() => registry.Register("spawn", "", _ => DebugCommandResult.Ok()));
        Assert.Throws<ArgumentException>(() => registry.Register("Bad Name", "", _ => DebugCommandResult.Ok()));
    }

    [Fact]
    public void FailingCommand_ReturnsTheError()
    {
        var registry = new DebugCommandRegistry().Register("boom", "", _ => throw new InvalidOperationException("nope"));
        var result = registry.Execute("boom");
        Assert.False(result.Success);
        Assert.Contains("InvalidOperationException: nope", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_CreateBudgets_InMetricUnits()
    {
        var settings = new DiagnosticsSettings();
        settings.Budgets.MaxDrawCalls = null; // switched off
        var budgets = settings.CreateBudgets();
        Assert.DoesNotContain(budgets, b => b.Id == "drawCalls");
        Assert.Equal(512 * 1024.0 * 1024.0, budgets.Single(b => b.Id == "textureMemory").Limit);
        Assert.Equal(Metrics.ManagedHeap, budgets.Single(b => b.Id == "managedMemory").Metric);
    }

    [Fact]
    public void Validate_FixesInvalidValues_AndSaysWhat()
    {
        var settings = new DiagnosticsSettings { MaxDebugDrawCommands = -1, Thresholds = new BudgetThresholds(0.9, 0.5) };
        settings.Budgets.FrameTimeMs = -5;
        settings.Overlay.Scale = 10;
        var problems = settings.Validate();
        Assert.Equal(4, problems.Count);
        Assert.Equal(20_000, settings.MaxDebugDrawCommands);
        Assert.Null(settings.Budgets.FrameTimeMs);
        Assert.Equal(BudgetThresholds.Default, settings.Thresholds);
        Assert.Equal(1f, settings.Overlay.Scale);
    }

    [Fact]
    public void SessionOverrides_ChangeACopy()
    {
        var project = new DiagnosticsSettings();
        var session = project.Clone();
        session.Apply(JObject.Parse("""{ "showOverlayByDefault": true, "budgets": { "maxDrawCalls": 50 }, "overlay": { "corner": "BottomRight" } }"""));
        Assert.True(session.ShowOverlayByDefault);
        Assert.Equal(50, session.Budgets.MaxDrawCalls);
        Assert.Equal(1000.0 / 60.0, session.Budgets.FrameTimeMs!.Value, 3); // untouched values kept
        Assert.Equal(OverlayCorner.BottomRight, session.Overlay.Corner);
        Assert.False(project.ShowOverlayByDefault);
        Assert.Equal(1000, project.Budgets.MaxDrawCalls);
    }

    [Fact]
    public void History_KeepsAFixedWindow_WithGapsForUnmeasuredFrames()
    {
        var history = new MetricsHistory(capacity: 3);
        history.Track(Metrics.FrameTime);
        var metrics = new MetricsSnapshot();
        for (var i = 1; i <= 4; i++)
        {
            metrics.Frame = i;
            if (i != 3)
            {
                metrics.Set(Metrics.FrameTime, i * 10);
            }

            history.Record(metrics);
        }

        var values = new float[3];
        Assert.True(history.CopyTo(Metrics.FrameTime, values));
        Assert.Equal(20f, values[0]);
        Assert.True(float.IsNaN(values[1]));
        Assert.Equal(40f, values[2]);
    }
}
