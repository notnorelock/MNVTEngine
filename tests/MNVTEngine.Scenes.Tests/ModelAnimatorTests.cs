using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Scenes.Tests;

public sealed class ModelAnimatorTests
{
    /// <summary>A 2 s clip moving node "Arm" along +X (1 unit per second) and turning the root.</summary>
    private sealed class FakeClip : ModelClip
    {
        public override string Name => "Wave";

        public override float Duration => 2f;

        public override IReadOnlyList<string?> Targets { get; } = ["Arm", null, "Missing"];

        public override void Sample(float time, Span<NodePose> poses)
        {
            poses[0] = new NodePose(new Vector3(time, 0f, 0f), null, null);
            poses[1] = new NodePose(null, Quaternion.AngleAxis(time * 10f, Vector3.Up), null);
            poses[2] = new NodePose(null, null, Vector3.One * 2f);
        }
    }

    /// <summary>A 1 s clip holding "Arm" at y = 4 and "Body" at x = 10, and walking "Hips" 3 units along +Z per cycle.</summary>
    private sealed class RunClip : ModelClip
    {
        public override string Name => "Run";

        public override float Duration => 1f;

        public override IReadOnlyList<string?> Targets { get; } = ["Arm", "Body", "Hips"];

        public override void Sample(float time, Span<NodePose> poses)
        {
            poses[0] = new NodePose(new Vector3(0f, 4f, 0f), null, null);
            poses[1] = new NodePose(new Vector3(10f, 0f, 0f), null, null);
            poses[2] = new NodePose(new Vector3(0f, 1f + (0.1f * time), 3f * time), null, null);
        }
    }

    /// <summary>
    /// A 1 s clip of "Hips" (or the root, for <paramref name="root"/>): turning 90 degrees left, optionally walking
    /// 1 unit per second along its heading (a quarter circle), or only sliding 2 units along -Z.
    /// </summary>
    private sealed class MotionClip(string name, bool turn, bool walk, bool root = false) : ModelClip
    {
        public const float Radius = 2f / MathF.PI; // 1 unit/s while turning pi/2 rad/s

        public override string Name => name;

        public override float Duration => 1f;

        public override IReadOnlyList<string?> Targets { get; } = [root ? null : "Hips"];

        public override void Sample(float time, Span<NodePose> poses)
        {
            var angle = turn ? 90f * time : 0f;
            var theta = angle * Mathf.Deg2Rad;
            var position = walk ? new Vector3(Radius * (MathF.Cos(theta) - 1f), 0f, -Radius * MathF.Sin(theta)) : new Vector3(0f, 0f, turn ? 0f : -2f * time);
            poses[0] = new NodePose(position, Quaternion.AngleAxis(angle, Vector3.Up), null);
        }
    }

    private sealed class FakeProvider : IModelAnimationProvider
    {
        public IReadOnlyList<string> ClipNames(AssetRef model) => ["Wave", "Run", "Turn", "Arc", "Slide"];

        public ModelClip? GetClip(AssetRef model, string? clip) => clip switch
        {
            null or "" or "Wave" => new FakeClip(),
            "Run" => new RunClip(),
            "Turn" => new MotionClip("Turn", turn: true, walk: false),
            "Arc" => new MotionClip("Arc", turn: true, walk: true),
            "Slide" => new MotionClip("Slide", turn: false, walk: false, root: true),
            _ => null,
        };
    }

    private static FrameTime Frame(float seconds) => new(seconds, seconds, seconds, seconds, 1);

    [Fact]
    public void CrossFade_BlendsBothClipsByWeight_ThenSettlesOnTheNewOne()
    {
        var (scene, animator, arm) = Build();
        scene.Update(Frame(0.5f)); // Wave: Arm at (0.5, 0, 0)
        animator.CrossFade("Run", 1f);
        Assert.True(animator.IsCrossFading);

        scene.Update(Frame(0.5f)); // halfway: Wave at t = 1 gives (1, 0, 0), Run gives (0, 4, 0)
        Assert.Equal(new Vector3(0.5f, 2f, 0f), Round(arm.LocalPosition));

        scene.Update(Frame(0.6f));
        Assert.False(animator.IsCrossFading);
        Assert.Equal("Run", animator.CurrentClip);
        Assert.Equal(new Vector3(0f, 4f, 0f), Round(arm.LocalPosition));
    }

    [Fact]
    public void Layers_BlendOverTheBase_OnlyInsideTheirMask()
    {
        var (scene, animator, arm) = Build();
        scene.Update(Frame(1f)); // Wave: Arm at (1, 0, 0)
        var body = arm.Parent!;
        var bodyBefore = body.LocalPosition;

        animator.SetLayer(1, "Run", weight: 0.5f, maskRoot: "Arm"); // only the arm
        scene.Update(Frame(0.25f));
        Assert.Equal(new Vector3(0.625f, 2f, 0f), Round(arm.LocalPosition)); // halfway between Wave (1.25, 0, 0) and Run (0, 4, 0)
        Assert.Equal(bodyBefore, body.LocalPosition); // Body is outside the mask

        animator.SetLayer(1, null);
        scene.Update(Frame(0.25f));
        Assert.Equal(new Vector3(1.5f, 0f, 0f), Round(arm.LocalPosition));
    }

    [Fact]
    public void RootMotion_MovesTheGameObject_AcrossLoops_WhileTheNodeStaysInPlace()
    {
        var scene = new Scene { ModelAnimations = new FakeProvider(), RethrowScriptExceptions = true };
        var root = scene.CreateGameObject("Model");
        var hips = scene.CreateGameObject("Hips", root.Transform).Transform;
        var animator = root.AddComponent<ModelAnimator>();
        (animator.Model, animator.Clip, animator.ApplyRootMotion, animator.RootMotionNode) = (new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000002")), "Run", true, "Hips");
        scene.Start();

        scene.Update(Frame(0.5f)); // starts the clip at t = 0 and advances to 0.5
        scene.Update(Frame(1f));   // to t = 1.5: one wrap
        Assert.Equal(4.5f, root.Transform.Position.Z, 3); // 3 units per cycle, 1.5 cycles
        Assert.Equal(0f, hips.LocalPosition.Z, 3);        // the hips stay above the GameObject
        Assert.Equal(1.05f, hips.LocalPosition.Y, 3);     // but keep their vertical bob
    }

    private static (Scene Scene, Transform Root, Transform Hips, ModelAnimator Animator) RootMotion(string clip, string node = "Hips")
    {
        var scene = new Scene { ModelAnimations = new FakeProvider(), RethrowScriptExceptions = true };
        var root = scene.CreateGameObject("Model");
        var hips = scene.CreateGameObject("Hips", root.Transform).Transform;
        var animator = root.AddComponent<ModelAnimator>();
        (animator.Model, animator.Clip, animator.ApplyRootMotion, animator.RootMotionRotation, animator.RootMotionNode) = (new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000003")), clip, true, true, node);
        scene.Start();
        return (scene, root.Transform, hips, animator);
    }

    private static float Yaw(Transform t)
    {
        var forward = t.Rotation * Vector3.Forward;
        return MathF.Atan2(-forward.X, -forward.Z) * Mathf.Rad2Deg;
    }

    [Fact]
    public void RootMotionRotation_TurnsTheGameObject_AcrossLoops_WhileTheNodeKeepsItsHeading()
    {
        var (scene, root, hips, _) = RootMotion("Turn");
        scene.Update(Frame(0.5f));
        Assert.Equal(45f, Yaw(root), 1);
        Assert.Equal(0f, Yaw(hips) - Yaw(root), 1); // the hips face the GameObject's forward
        scene.Update(Frame(1f)); // to t = 1.5: one wrap
        Assert.Equal(135f, Yaw(root), 1);
        Assert.Equal(Vector3.Zero, Round(root.Position));
    }

    [Fact]
    public void RootMotionRotation_WalksAlongTheTurningHeading()
    {
        var (scene, root, hips, _) = RootMotion("Arc");
        scene.Update(Frame(0f)); // starts the clip
        for (var i = 0; i < 100; i++)
        {
            scene.Update(Frame(0.01f));
        }

        // A quarter circle to the left, ending facing -X, like the clip's own path.
        Assert.Equal(90f, Yaw(root), 0);
        Assert.Equal(-MotionClip.Radius, root.Position.X, 1);
        Assert.Equal(-MotionClip.Radius, root.Position.Z, 1);
        Assert.Equal(0f, hips.LocalPosition.Z, 3); // the hips stay over the GameObject
    }

    [Fact]
    public void RootMotion_OfTheGameObjectItself_AddsUp()
    {
        var (scene, root, _, animator) = RootMotion("Slide", node: "");
        animator.RootMotionRotation = false;
        scene.Update(Frame(0.5f));
        scene.Update(Frame(1f)); // 1.5 cycles of 2 units
        Assert.Equal(-3f, root.Position.Z, 2);
    }

    private sealed class TextReader(string json) : ITextAssetReader
    {
        public string ReadText(AssetRef asset, string assetType) =>
            assetType == AssetTypes.AnimatorController ? json : throw new InvalidOperationException("not a controller");
    }

    private const string Controller = """
        { "schemaVersion": 1,
          "parameters": { "speed": { "type": "float" }, "wave": { "type": "trigger" } },
          "defaultState": "Idle",
          "states": [ { "name": "Idle", "clip": "Wave", "speed": 0.5 }, { "name": "Run", "clip": "Run" }, { "name": "Greet", "clip": "Wave", "loop": false } ],
          "transitions": [
            { "from": "Idle", "to": "Run", "duration": 0.5, "conditions": [ { "parameter": "speed", "greater": 0.5 } ] },
            { "from": "Run", "to": "Idle", "duration": 0.5, "conditions": [ { "parameter": "speed", "less": 0.5 } ] },
            { "from": "*", "to": "Greet", "duration": 0, "conditions": [ { "parameter": "wave" } ] },
            { "from": "Greet", "to": "Idle", "duration": 0, "exitTime": 1 } ] }
        """;

    private static (Scene Scene, ModelAnimator Animator, ModelAnimatorController Controller, List<string> Changes) Controlled()
    {
        var scene = new Scene { ModelAnimations = new FakeProvider(), TextAssets = new TextReader(Controller), RethrowScriptExceptions = true };
        var root = scene.CreateGameObject("Model");
        scene.CreateGameObject("Arm", scene.CreateGameObject("Body", root.Transform).Transform);
        var animator = root.AddComponent<ModelAnimator>();
        animator.Model = new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000004"));
        var controller = root.AddComponent<ModelAnimatorController>();
        controller.Controller = new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000005"));
        var changes = new List<string>();
        controller.StateChanged += (_, from, to) => changes.Add($"{from ?? "-"}>{to}");
        scene.Start();
        return (scene, animator, controller, changes);
    }

    [Fact]
    public void Controller_PlaysTheDefaultState_AndFollowsParameters()
    {
        var (scene, animator, controller, changes) = Controlled();
        scene.Update(Frame(0.1f));
        Assert.Equal(("Idle", "Wave", 0.5f), (controller.CurrentState, animator.CurrentClip, animator.Speed));

        controller.SetFloat("speed", 2f);
        scene.Update(Frame(0.1f));
        Assert.Equal("Run", controller.CurrentState);
        Assert.True(animator.IsCrossFading);
        scene.Update(Frame(0.6f));
        Assert.Equal(("Run", false, 1f), (animator.CurrentClip, animator.IsCrossFading, animator.Speed));

        controller.SetFloat("speed", 0f);
        scene.Update(Frame(0.1f));
        Assert.Equal("Idle", controller.CurrentState);
        Assert.Equal(["->Idle", "Idle>Run", "Run>Idle"], changes);
    }

    [Fact]
    public void Controller_TriggersFireOnce_FromAnyState_AndExitTimesReturn()
    {
        var (scene, animator, controller, changes) = Controlled();
        scene.Update(Frame(0.1f));
        controller.SetTrigger("wave");
        scene.Update(Frame(0.1f));
        Assert.Equal(("Greet", false), (controller.CurrentState, animator.Loop));
        Assert.False(controller.GetBool("wave")); // consumed

        scene.Update(Frame(1.5f)); // Greet's clip is 2 s
        Assert.Equal("Greet", controller.CurrentState);
        scene.Update(Frame(0.6f)); // past its end: the exit-time transition returns
        Assert.Equal("Idle", controller.CurrentState);
        Assert.True(animator.Loop);
        Assert.Equal(["->Idle", "Idle>Greet", "Greet>Idle"], changes);
    }

    [Fact]
    public void ControllerAndRootMotionSettings_SurviveSerialization()
    {
        var scene = new Scene();
        var go = scene.CreateGameObject("Hero");
        var animator = go.AddComponent<ModelAnimator>();
        (animator.ApplyRootMotion, animator.RootMotionRotation) = (true, true);
        var asset = new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000006"));
        go.AddComponent<ModelAnimatorController>().Controller = asset;

        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Hero")!;
        Assert.Equal(asset, copy.GetComponent<ModelAnimatorController>()!.Controller);
        Assert.True(copy.GetComponent<ModelAnimator>()!.RootMotionRotation);
    }

    [Fact]
    public void Controller_RejectsUnknownParametersAndWrongTypes_AndCanBeBuiltInCode()
    {
        var (scene, animator, controller, _) = Controlled();
        scene.Update(Frame(0f)); // loads the controller
        Assert.Throws<ArgumentException>(() => controller.SetFloat("jump", 1f));
        Assert.Throws<ArgumentException>(() => controller.SetBool("speed", true));
        Assert.Throws<ArgumentException>(() => controller.Play("Fly"));

        controller.Use(AnimatorControllerDefinition.Create(
            [new AnimatorParameter("go", AnimatorParameterType.Bool, 0f)],
            [new AnimatorState("Still", "Wave", 0f, true), new AnimatorState("Moving", "Run", 1f, true)],
            [new AnimatorTransition("Still", "Moving", [new AnimatorCondition("go", AnimatorConditionMode.IsTrue, 0f)], 0f, null)]));
        Assert.Equal("Still", controller.CurrentState);
        controller.SetBool("go", true);
        scene.Update(Frame(0.1f));
        Assert.Equal(("Moving", "Run"), (controller.CurrentState, animator.CurrentClip));
    }

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));

    private static (Scene Scene, ModelAnimator Animator, Transform Arm) Build(bool loop = true)
    {
        var scene = new Scene { ModelAnimations = new FakeProvider(), RethrowScriptExceptions = true };
        var root = scene.CreateGameObject("Model");
        var arm = scene.CreateGameObject("Arm", scene.CreateGameObject("Body", root.Transform).Transform);
        var animator = root.AddComponent<ModelAnimator>();
        (animator.Model, animator.Loop) = (new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000001")), loop);
        scene.Start();
        return (scene, animator, arm.Transform);
    }

    [Fact]
    public void Plays_OnStart_MovingNamedDescendants_AndTheRoot()
    {
        var (scene, animator, arm) = Build();
        scene.Update(new FrameTime(0.5f, 0.5f, 0.5, 0.5, 1)); // Start, then the first Update
        Assert.True(animator.IsPlaying);
        Assert.Equal("Wave", animator.CurrentClip);
        Assert.Equal(0.5f, arm.LocalPosition.X, 4);
        Assert.True(Quaternion.Approximately(Quaternion.AngleAxis(5f, Vector3.Up), animator.Transform.LocalRotation, 0.01f));
    }

    [Fact]
    public void Loops_OrStopsAtTheEnd_RaisingFinished()
    {
        var (loopScene, looping, loopArm) = Build();
        loopScene.Update(new FrameTime(2.5f, 2.5f, 2.5, 2.5, 1));
        Assert.Equal(0.5f, loopArm.LocalPosition.X, 4);
        Assert.True(looping.IsPlaying);

        var (scene, once, arm) = Build(loop: false);
        var finished = 0;
        once.Finished += _ => finished++;
        scene.Update(new FrameTime(3f, 3f, 3, 3, 1));
        Assert.Equal(2f, arm.LocalPosition.X, 4); // clamped to the last frame
        Assert.False(once.IsPlaying);
        Assert.Equal(1, finished);
    }

    [Fact]
    public void Sample_ShowsATimeWithoutPlaying_AndUnknownClipsDoNothing()
    {
        var (scene, animator, arm) = Build();
        scene.Update(new FrameTime(0.1f, 0.1f, 0.1, 0.1, 1));
        animator.Pause();
        animator.Sample(1.25f);
        Assert.Equal(1.25f, arm.LocalPosition.X, 4);

        animator.Play("Nope");
        Assert.Equal("Wave", animator.CurrentClip); // still the old clip
    }

    [Fact]
    public void Settings_SurviveSerialization()
    {
        var scene = new Scene();
        var animator = scene.CreateGameObject("Model").AddComponent<ModelAnimator>();
        (animator.Model, animator.Clip, animator.Speed, animator.Loop, animator.PlayOnStart) = (new AssetRef(Guid.Parse("e7000000-0000-4000-8000-000000000002")), "Run", 1.5f, false, false);

        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Model")!.GetComponent<ModelAnimator>()!;
        Assert.Equal((animator.Model, "Run", 1.5f, false, false), (copy.Model, copy.Clip, copy.Speed, copy.Loop, copy.PlayOnStart));
    }
}
