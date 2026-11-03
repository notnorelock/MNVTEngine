using MNVTEngine.Core.Timing;
using MNVTEngine.Editor.Services;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Tests;

public sealed class ScriptDebuggerTests
{
    private const string Source = """
        var total = 0
        on start {
            var a = 1
            a += 1
            total = a * 10
            log("done")
        }
        """;

    private sealed class Script(string text) : ITextAssetReader
    {
        public string ReadText(AssetRef asset, string assetType) => text;
    }

    private static readonly AssetRef ScriptAsset = new(Guid.Parse("dddddddd-0000-4000-8000-000000000001"));

    private static (Scene Scene, MissionRunner Runner) NewScene()
    {
        var scene = new Scene { TextAssets = new Script(Source), RethrowScriptExceptions = true };
        var runner = scene.CreateGameObject("Logic").AddComponent<MissionRunner>();
        runner.Script = ScriptAsset;
        return (scene, runner);
    }

    private static long _frame;

    /// <summary>One editor frame: hook first, then tick unless the debugger is showing a stop.</summary>
    private static void Frame(Scene scene, ScriptDebugSession debugger)
    {
        debugger.Attach(scene);
        if (debugger.Break is null)
        {
            _frame++;
            scene.Update(new FrameTime(0.016f, 0.016f, _frame * 0.016, _frame * 0.016, _frame));
        }
    }

    [Fact]
    public void Breakpoint_InStartCode_StopsWithStackAndLocals_ThenContinues()
    {
        var debugger = new ScriptDebugSession();
        debugger.ToggleBreakpoint(ScriptAsset.Guid, 4);
        var (scene, runner) = NewScene();
        scene.Start();

        Frame(scene, debugger);
        var stop = Assert.IsType<ScriptBreak>(debugger.Break);
        Assert.Equal((4, ScriptAsset.Guid), (stop.Line, stop.Script));
        Assert.Equal(4, stop.Fiber.GetStackTrace()[0].Line);
        Assert.Contains(stop.Fiber.GetLocals(), l => l.Name == "a" && l.Value.ToString() == "1"); // line 4 has not run yet
        Assert.Equal("0", runner.Vm!.GetGlobal("total").ToString());

        Frame(scene, debugger); // frozen while stopped
        Assert.Equal("0", runner.Vm.GetGlobal("total").ToString());

        debugger.Continue();
        Frame(scene, debugger);
        Assert.Null(debugger.Break);
        Assert.Equal("20", runner.Vm.GetGlobal("total").ToString());
    }

    [Fact]
    public void Step_StopsAtTheNextStatementOfTheSameCoroutine()
    {
        var debugger = new ScriptDebugSession();
        debugger.ToggleBreakpoint(ScriptAsset.Guid, 3);
        var (scene, runner) = NewScene();
        scene.Start();

        Frame(scene, debugger);
        Assert.Equal(3, debugger.Break!.Line);
        debugger.Step();
        Frame(scene, debugger);
        Assert.Equal(4, debugger.Break!.Line);
        debugger.Step();
        Frame(scene, debugger);
        Assert.Equal(5, debugger.Break!.Line);
        debugger.Continue();
        Frame(scene, debugger);
        Assert.Null(debugger.Break);
        Assert.Equal("20", runner.Vm!.GetGlobal("total").ToString());
    }

    [Fact]
    public void Breakpoints_Toggle_AndOnlyApplyToTheirScript()
    {
        var debugger = new ScriptDebugSession();
        debugger.ToggleBreakpoint(Guid.NewGuid(), 4); // another script
        debugger.ToggleBreakpoint(ScriptAsset.Guid, 5);
        debugger.ToggleBreakpoint(ScriptAsset.Guid, 5); // off again
        Assert.False(debugger.HasBreakpoint(ScriptAsset.Guid, 5));
        Assert.Single(debugger.Breakpoints);

        var (scene, runner) = NewScene();
        scene.Start();
        Frame(scene, debugger);
        Frame(scene, debugger);
        Assert.Null(debugger.Break);
        Assert.Equal("20", runner.Vm!.GetGlobal("total").ToString());
    }
}
