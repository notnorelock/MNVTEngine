namespace MNVTEngine.Serialization.Tests;

public sealed class AnimatorControllerDefinitionTests
{
    private const string Locomotion = """
        { "schemaVersion": 1,
          "parameters": { "speed": { "type": "float" }, "grounded": { "type": "bool", "default": true }, "jump": { "type": "trigger" }, "combo": { "type": "int", "default": 2 } },
          "defaultState": "Idle",
          "states": [ { "name": "Idle", "clip": "Idle" }, { "name": "Run", "clip": "Run", "speed": 1.5 }, { "name": "Jump", "clip": "Jump", "loop": false } ],
          "transitions": [
            { "from": "Idle", "to": "Run", "duration": 0.2, "conditions": [ { "parameter": "speed", "greater": 0.1 }, { "parameter": "grounded", "is": true } ] },
            { "from": "*", "to": "Jump", "conditions": [ { "parameter": "jump" } ] },
            { "from": "Jump", "to": "Idle", "duration": 0, "exitTime": 1 } ] }
        """;

    [Fact]
    public void Parse_ReadsParametersStatesAndTransitions()
    {
        var controller = AnimatorControllerDefinition.Parse(Locomotion, "Locomotion.mnvtanimator");

        Assert.Equal("Idle", controller.DefaultState);
        Assert.Equal([new AnimatorParameter("speed", AnimatorParameterType.Float, 0f), new("grounded", AnimatorParameterType.Bool, 1f), new("jump", AnimatorParameterType.Trigger, 0f), new("combo", AnimatorParameterType.Int, 2f)], controller.Parameters);
        Assert.Equal(new AnimatorState("Run", "Run", 1.5f, true), controller.FindState("Run"));
        Assert.False(controller.FindState("Jump")!.Loop);
        var toRun = controller.Transitions[0];
        Assert.Equal(("Idle", "Run", 0.2f, (float?)null), (toRun.From, toRun.To, toRun.Duration, toRun.ExitTime));
        Assert.Equal([new AnimatorCondition("speed", AnimatorConditionMode.Greater, 0.1f), new("grounded", AnimatorConditionMode.IsTrue, 0f)], toRun.Conditions);
        Assert.Null(controller.Transitions[1].From); // any state
        Assert.Equal(new AnimatorCondition("jump", AnimatorConditionMode.IsTrue, 0f), Assert.Single(controller.Transitions[1].Conditions));
        Assert.Equal(1f, controller.Transitions[2].ExitTime);
    }

    [Theory]
    [InlineData("""{ "from": "Idle", "to": "Nowhere", "conditions": [ { "parameter": "jump" } ] }""", "'Nowhere' is not a state")]
    [InlineData("""{ "from": "Idle", "to": "Run", "conditions": [ { "parameter": "speed", "is": true } ] }""", "'speed' is a float parameter")]
    [InlineData("""{ "from": "Idle", "to": "Run", "conditions": [ { "parameter": "grounded", "greater": 1 } ] }""", "'grounded' is a bool parameter")]
    [InlineData("""{ "from": "Idle", "to": "Run", "conditions": [ { "parameter": "missing" } ] }""", "'missing', which is not a parameter")]
    [InlineData("""{ "from": "Idle", "to": "Run" }""", "needs a condition or an exit time")]
    [InlineData("""{ "from": "*", "to": "Run", "exitTime": 1 }""", "\"any state\" transition to 'Run' needs a condition")]
    [InlineData("""{ "from": "Idle", "to": "Run", "duration": -1, "conditions": [ { "parameter": "jump" } ] }""", "'duration' must be 0 or more")]
    public void Parse_ReportsMistakes(string transition, string expected)
    {
        var json = $$"""
            { "schemaVersion": 1, "parameters": { "speed": { "type": "float" }, "grounded": { "type": "bool" }, "jump": { "type": "trigger" } },
              "states": [ { "name": "Idle", "clip": "Idle" }, { "name": "Run", "clip": "Run" } ], "transitions": [ {{transition}} ] }
            """;
        var error = Assert.Throws<SerializationException>(() => AnimatorControllerDefinition.Parse(json, "Bad.mnvtanimator"));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsBadStatesAndParameters_AndCreateValidatesCodeBuiltControllers()
    {
        var error = Assert.Throws<SerializationException>(() => AnimatorControllerDefinition.Parse(
            """{ "schemaVersion": 1, "parameters": { "x": { "type": "vector" } }, "states": [ { "name": "A", "clip": "A" }, { "name": "A", "clip": "B" } ], "defaultState": "C" }""", "Bad.mnvtanimator"));
        Assert.Contains(error.Diagnostics, d => d.Path == "parameters.x");
        Assert.Throws<SerializationException>(() => AnimatorControllerDefinition.Parse("""{ "schemaVersion": 1, "states": [] }""", "Empty.mnvtanimator"));

        var duplicate = Assert.Throws<ArgumentException>(() => AnimatorControllerDefinition.Create([], [new("A", "A", 1f, true), new("A", "B", 1f, true)], []));
        Assert.Contains("Two states are named 'A'", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal("A", AnimatorControllerDefinition.Create([], [new("A", "A", 1f, true)], []).DefaultState);
    }
}
