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

    private sealed class FakeProvider : IModelAnimationProvider
    {
        public IReadOnlyList<string> ClipNames(AssetRef model) => ["Wave"];

        public ModelClip? GetClip(AssetRef model, string? clip) => string.IsNullOrEmpty(clip) || clip == "Wave" ? new FakeClip() : null;
    }

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
