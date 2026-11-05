using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Tests;

public sealed class AnimatorControllerDocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-animator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AnimatorControllerDocument New()
    {
        var path = Path.Combine(_root, "Hero.mnvtanimator");
        AnimatorControllerDocument.CreateNew(path);
        return new AnimatorControllerDocument(path);
    }

    [Fact]
    public void ANewController_IsValid_AndEditsKeepNamesConsistent()
    {
        var document = New();
        Assert.Empty(document.Problems);
        var idle = document.FindState("Idle")!;
        var run = document.AddState(new Vector2(400f, 60f), "Run");
        var speed = document.AddParameter(AnimatorParameterType.Float);
        var toRun = document.AddTransition("Idle", "Run");
        toRun.ExitTime = null;
        var condition = document.AddCondition(toRun);
        (condition.Mode, condition.Threshold) = (AnimatorConditionMode.Greater, 0.1f);
        document.AddTransition(null, "Idle").Conditions.Add(new AnimatorGraphCondition { Parameter = speed.Name, Mode = AnimatorConditionMode.Less, Threshold = 0.1f });
        Assert.Empty(document.Problems);

        Assert.Equal("Walk", document.RenameState(run, "Walk"));
        Assert.Equal("Walk", toRun.To);
        Assert.Equal("Idle 2", document.RenameState(document.AddState(Vector2.Zero, "Jump"), "Idle")); // names stay unique
        Assert.Equal("speed", document.RenameParameter(speed, "speed"));
        Assert.Equal("speed", condition.Parameter);
        Assert.Equal(AnimatorConditionMode.Greater, AnimatorControllerDocument.DefaultMode(AnimatorParameterType.Float));
        Assert.Equal([AnimatorConditionMode.IsTrue], AnimatorControllerDocument.ModesFor(AnimatorParameterType.Trigger));

        document.RemoveState(idle);
        Assert.DoesNotContain(document.Transitions, t => t.From == "Idle" || t.To == "Idle");
        Assert.Equal("Walk", document.DefaultState); // another state took over
    }

    [Fact]
    public void Saving_WritesTheRuntimeFormat_WithPositions_AndUndoRedoWork()
    {
        var document = New();
        var run = document.AddState(new Vector2(420f, 90f), "Run");
        run.Clip = "Run";
        run.Speed = 1.5f;
        var jump = document.AddParameter(AnimatorParameterType.Trigger);
        var toRun = document.AddTransition(null, "Run");
        toRun.Conditions.Add(new AnimatorGraphCondition { Parameter = jump.Name, Mode = AnimatorConditionMode.IsTrue });
        document.Commit();
        document.Save();
        Assert.False(document.IsDirty);

        var definition = AnimatorControllerDefinition.Parse(File.ReadAllText(document.FullPath), "Hero.mnvtanimator"); // the runtime reads it
        Assert.Equal(new AnimatorState("Run", "Run", 1.5f, true), definition.FindState("Run"));
        Assert.Equal(new AnimatorCondition("Trigger", AnimatorConditionMode.IsTrue, 0f), Assert.Single(definition.Transitions.Single(t => t.From is null).Conditions));

        var reopened = new AnimatorControllerDocument(document.FullPath);
        Assert.Equal(new Vector2(420f, 90f), reopened.FindState("Run")!.Position);
        Assert.Equal(document.Serialize(), reopened.Serialize());

        reopened.RemoveState(reopened.FindState("Run")!);
        reopened.Commit();
        Assert.True(reopened.IsDirty);
        reopened.Undo();
        Assert.NotNull(reopened.FindState("Run"));
        Assert.False(reopened.IsDirty);
        reopened.Redo();
        Assert.Null(reopened.FindState("Run"));
    }

    [Fact]
    public void Mistakes_AreListed_AndABrokenFileStillOpens()
    {
        var path = Path.Combine(_root, "Broken.mnvtanimator");
        File.WriteAllText(path, """{ "schemaVersion": 1, "states": [ { "name": "A", "clip": "A" } ], "transitions": [ { "from": "A", "to": "Nowhere" } ] }""");
        var document = new AnimatorControllerDocument(path);

        Assert.Single(document.States);
        Assert.Contains(document.Problems, p => p.Contains("'Nowhere' is not a state", StringComparison.Ordinal));
        Assert.Contains(document.Problems, p => p.Contains("needs a condition or an exit time", StringComparison.Ordinal));
        File.WriteAllText(path, "not json");
        Assert.Throws<SerializationException>(() => new AnimatorControllerDocument(path));
    }
}
