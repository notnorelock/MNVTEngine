using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Scripting.VM;
using MNVTEngine.Serialization;

namespace MNVTEngine.Missions.Tests;

public class MissionTests
{
    private const string Escort = """
        var reward = 0
        var inventory = null
        mission "escort" {
            name = "Escort"
            var arrived = false
            on start {
                log("escort started")
                objective.activate("meet")
            }
            objective "meet" {
                description = "Meet the guide"
                wait for event "met_guide"
                objective.complete()
                log("after complete still runs")
            }
            objective "protect" {
                requires "meet"
                wait until arrived
                objective.complete()
            }
            objective "gather_a" { requires "meet"
                wait for event "a" }
            objective "gather_b" { requires "meet"
                wait for event "b" }
            objective "finish" {
                requires "protect"
                reward += 100
                mission.complete()
                log("unreachable")
            }
            on event "arrive" { arrived = true }
            on complete { log("escort complete, reward " + reward) }
            on fail { log("escort failed") }
        }
        on start { mission.start("escort") }
        """;

    private sealed class Host
    {
        public Host(string source)
        {
            var result = ScriptCompiler.Compile(source, "missions.mnvts", new CompileOptions { Api = new ScriptApi().Merge(StandardApi.Core).Merge(StandardApi.Missions) });
            Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));
            Manager = new MissionManager(result.Script!);
            ScriptVM? vm = null;
            Vm = vm = new ScriptVM(result.Script!, [.. CoreNatives.Create(() => vm!, Log.Add), .. Manager.CreateNatives()]);
            Vm.FiberFaulted += f => Assert.Fail(f.Error!.ToString());
            Manager.Attach(Vm);
        }

        public ScriptVM Vm { get; }

        public MissionManager Manager { get; }

        public List<string> Log { get; } = [];

        public MissionRuntime Mission(string id) => Manager.Missions[id];

        public void Tick(int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                Vm.Tick(0.1);
            }
        }
    }

    [Fact]
    public void Lifecycle_SequenceParallelDependenciesAndHooks()
    {
        var host = new Host(Escort);
        host.Vm.Initialize();
        host.Tick(2);
        var escort = host.Mission("escort");
        Assert.Equal(MissionState.Active, escort.State);
        Assert.Equal(ObjectiveState.Active, escort.Objectives["meet"].State);
        Assert.Equal(ObjectiveState.Inactive, escort.Objectives["protect"].State);

        host.Vm.RaiseEvent("met_guide");
        host.Tick();
        // 'meet' done: its three dependents activate together (parallel)
        Assert.Equal(ObjectiveState.Completed, escort.Objectives["meet"].State);
        Assert.All(["protect", "gather_a", "gather_b"], id => Assert.Equal(ObjectiveState.Active, escort.Objectives[id].State));
        Assert.Contains("after complete still runs", host.Log);

        host.Vm.RaiseEvent("a");
        host.Vm.RaiseEvent("arrive"); // mission event handler sets the mission variable
        host.Tick(3);

        Assert.Equal(MissionState.Completed, escort.State);
        Assert.Equal(100, host.Vm.GetGlobal("reward").AsNumber());
        Assert.Equal(ObjectiveState.Active, escort.Objectives["gather_b"].State); // missions end only when told to; open objectives stay as they were
        Assert.Equal(["escort started", "after complete still runs", "escort complete, reward 100"], host.Log);
        Assert.DoesNotContain(host.Vm.Fibers, f => f.IsAlive && f.Context is MissionContext { Mission: "escort" });
    }

    [Fact]
    public void Fail_StopsMissionCode_AndEventHandlersOnlyRunWhileActive()
    {
        var host = new Host(Escort);
        host.Vm.Initialize();
        host.Tick();
        host.Manager.Fail("escort", "guide lost");
        host.Tick();
        Assert.Equal(MissionState.Failed, host.Mission("escort").State);
        Assert.Equal("guide lost", host.Mission("escort").FailReason);
        Assert.Equal("escort failed", host.Log[^1]);

        host.Vm.RaiseEvent("arrive");
        host.Tick();
        Assert.False(host.Vm.GetGlobal("escort.arrived").AsBool());
    }

    [Fact]
    public void ObjectiveApiOutsideMission_IsACatchableError()
    {
        var host = new Host("""
            on start {
                try { objective.complete() } catch e { log(e) }
                try { mission.start("nope") } catch e { log(e) }
            }
            """);
        host.Vm.Initialize();
        host.Tick();
        Assert.Equal(["objective.complete() without a mission id can only be used inside a mission.", "Unknown mission 'nope'."], host.Log);
    }

    [Fact]
    public void SaveAndRestore_RoundTripsStateAndRestartsActiveObjectives()
    {
        var host = new Host(Escort);
        host.Vm.Initialize();
        host.Tick();
        host.Vm.RaiseEvent("met_guide");
        host.Tick();
        host.Vm.SetGlobal("inventory", ScriptValue.FromMap(new Dictionary<string, ScriptValue>(StringComparer.Ordinal) { ["gold"] = ScriptValue.FromNumber(5), ["items"] = ScriptValue.FromList([ScriptValue.FromString("key")]) }));
        var json = host.Manager.Save().ToJson();

        var loaded = new Host(Escort);
        loaded.Vm.Initialize();
        var diagnostics = loaded.Manager.Restore(MissionSaveData.FromJson(json));
        Assert.Empty(diagnostics);
        var escort = loaded.Mission("escort");
        Assert.Equal(ObjectiveState.Completed, escort.Objectives["meet"].State);
        Assert.Equal(ObjectiveState.Active, escort.Objectives["protect"].State);
        Assert.Equal("{gold: 5, items: [key]}", loaded.Vm.GetGlobal("inventory").ToString());

        // The top-level 'on start' calls mission.start, which is a no-op for an already-active mission.
        loaded.Vm.RaiseEvent("arrive");
        loaded.Tick(3);
        Assert.Equal(MissionState.Completed, escort.State);
    }

    [Fact]
    public void Restore_ReportsNamesTheScriptNoLongerHas_AndRejectsNewerSchemas()
    {
        var host = new Host(Escort);
        host.Vm.Initialize();
        var data = new MissionSaveData { Globals = { ["removed_var"] = 1 }, Missions = { new MissionSaveEntry { Id = "old_mission", State = MissionState.Active } } };
        Assert.Equal(["removed_var", "old_mission"], host.Manager.Restore(data).Select(d => d.Path));

        Assert.Throws<SerializationException>(() => MissionSaveData.FromJson("""{ "schemaVersion": 99, "globals": {}, "missions": [] }"""));
        Assert.Throws<SerializationException>(() => MissionSaveData.FromJson("not json"));
    }
}
