using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;

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

    private sealed class FakeProvider : IModelAnimationProvider
    {
        public IReadOnlyList<string> ClipNames(AssetRef model) => ["Wave", "Run"];

        public ModelClip? GetClip(AssetRef model, string? clip) => clip switch
        {
            null or "" or "Wave" => new FakeClip(),
            "Run" => new RunClip(),
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
