using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.VM;

namespace MNVTEngine.Scripting.Tests;

public class VirtualMachineTests
{
    [Fact]
    public void WaitSeconds_SuspendsWithoutBlocking()
    {
        var host = new ScriptTestHost("""
            on start {
                log("a")
                wait 1.0
                log("b")
            }
            """);
        host.Tick(0.5); // the fiber reaches 'wait 1.0' at t = 0.5, so it wakes at t = 1.5
        Assert.Equal(["a"], host.Log);
        host.Tick(0.9);
        Assert.Equal(["a"], host.Log);
        host.Tick(0.2);
        Assert.Equal(["a", "b"], host.Log);
    }

    [Fact]
    public void WaitUntil_ReevaluatesEachTick()
    {
        var host = new ScriptTestHost("""
            var ready = false
            on start {
                wait until ready
                log("go")
            }
            """);
        host.Tick(times: 3);
        Assert.Empty(host.Log);
        host.Vm.SetGlobal("ready", ScriptValue.True);
        host.Tick();
        Assert.Equal(["go"], host.Log);
    }

    [Fact]
    public void WaitForEvent_ReceivesPayload_AndHandlersRun()
    {
        var host = new ScriptTestHost("""
            on start {
                wait for event "door_opened" as door
                log("waiter: " + door)
            }
            on event "door_opened"(name) { log("handler: " + name) }
            """);
        host.Tick(times: 2);
        host.Vm.RaiseEvent("door_opened", ScriptValue.FromString("north"));
        host.Tick();
        Assert.Equal(["handler: north", "waiter: north"], host.Log.Order().ToList());
    }

    [Fact]
    public void SpawnAfterAndEvery_RunConcurrently()
    {
        var host = new ScriptTestHost("""
            var ticks = 0
            func worker(name) {
                wait 0.25
                log(name)
            }
            on start {
                spawn worker("spawned")
                after 0.5 { log("after") }
                every 0.3 { ticks += 1 }
                log("start done")
            }
            """);
        host.Tick(0.1, 12); // timers start at t = 0.1: every-0.3 fires at 0.4, 0.7, 1.0
        Assert.Equal(["start done", "spawned", "after"], host.Log);
        Assert.Equal(3, host.Vm.GetGlobal("ticks").AsNumber());
    }

    [Fact]
    public void InfiniteLoop_IsStoppedByTheInstructionBudget_OthersKeepRunning()
    {
        var host = new ScriptTestHost("""
            func spin() { while true { } }
            on start {
                spawn spin()
                wait 0.1
                log("still alive")
            }
            """, new ScriptLimits { InstructionsPerSlice = 10_000 });
        host.Tick(0.1, 3);
        var fault = Assert.Single(host.Faults);
        Assert.Contains("Execution limit exceeded", fault.Message, StringComparison.Ordinal);
        Assert.Equal("spin", fault.StackTrace[0].Function);
        Assert.Equal(["still alive"], host.Log);
    }

    [Fact]
    public void RunawayRecursion_HitsTheCallDepthLimit()
    {
        var host = new ScriptTestHost("func f() { f() }\non start { f() }", new ScriptLimits { MaxCallDepth = 50 });
        host.Tick();
        Assert.Contains("Call depth limit", Assert.Single(host.Faults).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UncaughtErrors_HaveScriptStackTraces()
    {
        var host = new ScriptTestHost("""
            func inner() {
                var list = [1]
                return list[5]
            }
            func outer() { return inner() }
            on start { outer() }
            """);
        host.Tick();
        var fault = Assert.Single(host.Faults);
        Assert.Contains("out of range", fault.Message, StringComparison.Ordinal);
        Assert.Equal([("inner", 3), ("outer", 5), ("on start", 6)], fault.StackTrace.Select(f => (f.Function, f.Line)));
    }

    [Fact]
    public void HostFunctionExceptions_AreCatchableAndNotSwallowed()
    {
        var host = new ScriptTestHost("""
            on start {
                try { broken() } catch e { log(e) }
                broken()
            }
            """,
            natives: [new NativeFunction("broken", _ => throw new InvalidOperationException("disk on fire"))],
            api: new ScriptApi().Add("broken", 0, 0, ""));
        host.Tick();
        Assert.Equal(["Internal error: disk on fire"], host.Log);
        Assert.IsType<InvalidOperationException>(Assert.Single(host.Faults).HostException);
    }

    [Fact]
    public void GlobalInitializers_RunInOrder_AndCannotWait()
    {
        var host = new ScriptTestHost("var a = 2\nvar b = a * 10\non start { log(b) }");
        host.Tick();
        Assert.Equal(["20"], host.Log);
    }

    [Fact]
    public void Debugger_BreakpointsPauseAndExposeLocals()
    {
        var host = new ScriptTestHost("""
            on start {
                var hp = 10
                hp -= 3
                log(hp)
            }
            """);
        var debugger = new BreakpointDebugger { Trace = [] };
        debugger.Breakpoints.Add(4);
        host.Vm.Debugger = debugger;
        host.Tick();

        var (fiber, line) = Assert.Single(debugger.Hits);
        Assert.Equal(4, line);
        Assert.Equal(FiberState.Paused, fiber.State);
        Assert.Equal(("hp", ScriptValue.FromNumber(7)), fiber.GetLocals().Single());
        Assert.Empty(host.Log);

        host.Vm.Resume(fiber);
        host.Tick();
        Assert.Equal(["7"], host.Log);
        Assert.Equal([2, 3, 4], debugger.Trace);
    }

    [Fact]
    public void Bytecode_RoundTripsAndRuns()
    {
        var source = """
            var greeting = "hi"
            mission "m" {
                name = "Mission M"
                var count = 3
                on start { log(greeting + count) }
                objective "a" { description = "Do A"
                    wait 1 }
                objective "b" { requires "a"
                    optional = true }
            }
            on start { log("top") }
            """;
        var compiled = ScriptCompiler.Compile(source, "rt.mnvts", new CompileOptions { Api = new ScriptApi().Add("log", 1, 1, "") }).Script!;
        var bytes = BytecodeSerializer.Write(compiled);
        var restored = BytecodeSerializer.Read(bytes);

        Assert.Equal(bytes, BytecodeSerializer.Write(restored));
        var mission = Assert.Single(restored.Missions);
        Assert.Equal("Mission M", mission.Name);
        Assert.Equal(["a"], mission.Objectives[1].Requires);
        Assert.True(mission.Objectives[1].Optional);
        Assert.Equal("Do A", mission.Objectives[0].Description);

        var log = new List<string>();
        var vm = new ScriptVM(restored, [new NativeFunction("log", c => { log.Add(c[0].ToString()); return ScriptValue.Null; })]);
        vm.Initialize();
        vm.Tick(0.1);
        Assert.Equal(["top"], log);
    }

    [Fact]
    public void Bytecode_VerifierRejectsCorruptData()
    {
        var compiled = ScriptCompiler.Compile("func f() { return 1 }", "c.mnvts").Script!;
        var bytes = BytecodeSerializer.Write(compiled);
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(bytes[..^5]));
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read([.. "XXXX"u8, .. bytes[4..]]));

        var function = compiled.Functions.Single(fn => fn.Name == "f");
        function.Code[0] = 999; // invalid opcode
        Assert.Throws<InvalidDataException>(() => BytecodeSerializer.Read(BytecodeSerializer.Write(compiled)));
    }
}
