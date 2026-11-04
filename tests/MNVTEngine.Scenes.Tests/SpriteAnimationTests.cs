using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Scenes.Tests;

public class SpriteAnimationTests
{
    private static readonly Guid Sheet = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly AssetRef Walk = new(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001"));
    private static readonly AssetRef Jump = new(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));

    private static string ClipJson(string frames, float rate, bool loop) =>
        $$"""{ "schemaVersion": 1, "texture": "asset:{{Sheet}}", "frameWidth": 16, "frameHeight": 8, "columns": 3, "frames": [{{frames}}], "frameRate": {{rate}}, "loop": {{(loop ? "true" : "false")}} }""";

    private sealed class Clips : ITextAssetReader
    {
        public int Reads { get; private set; }

        public string ReadText(AssetRef asset, string assetType)
        {
            Assert.Equal(AssetTypes.Animation, assetType);
            Reads++;
            return asset == Walk ? ClipJson("0, 1, 2, 3", 10, loop: true)
                : asset == Jump ? ClipJson("4, 5", 4, loop: false)
                : throw new InvalidOperationException($"{asset} is not an Animation asset in this project.");
        }
    }

    [Fact]
    public void Clip_ComputesGridRectsAndFrames()
    {
        var clip = SpriteAnimationClip.Parse(ClipJson("0, 1, 2, 3, 3", 10, loop: true), "walk.mnvtanim");
        Assert.Equal(Sheet, clip.Texture);
        Assert.Equal(new Rect(32, 0, 16, 8), clip.FrameRect(2));
        Assert.Equal(new Rect(0, 8, 16, 8), clip.FrameRect(3)); // sheet index 3 wraps to row 1
        Assert.Equal(0.5f, clip.Duration, 5);
        Assert.Equal(1, clip.FrameAt(0.15f));
        Assert.Equal(0, clip.FrameAt(0.5f)); // looped

        var once = SpriteAnimationClip.Parse(ClipJson("0, 1", 10, loop: false), "once.mnvtanim");
        Assert.Equal(1, once.FrameAt(5f));
    }

    [Fact]
    public void InvalidClip_ReportsEveryProblem()
    {
        var ex = Assert.Throws<SerializationException>(() => SpriteAnimationClip.Parse(
            """{ "schemaVersion": 1, "texture": "tex.png", "frameWidth": 0, "frameHeight": 8, "frames": [1, -2], "frameRate": 0 }""", "bad.mnvtanim"));
        Assert.Equal(["texture", "frameWidth", "frameRate", "frames[1]"], ex.Diagnostics.Select(d => d.Path));
    }

    [Fact]
    public void Animator_DrivesTheSpriteRenderer_AndSwitchesClips()
    {
        var clips = new Clips();
        var scene = new Scene { TextAssets = clips, RethrowScriptExceptions = true };
        var go = scene.CreateGameObject("Hero");
        var sprite = go.AddComponent<SpriteRenderer>();
        var animator = go.AddComponent<SpriteAnimator>();
        animator.Clip = Walk;
        var finished = 0;
        animator.Finished += _ => finished++;
        scene.Start();

        Frames(scene, 1, 0.05f); // Start + first update
        Assert.Equal(new AssetRef(Sheet), sprite.Texture);
        Assert.Equal(new Rect(0, 0, 16, 8), sprite.SourceRect);
        Frames(scene, 2, 0.05f);
        Assert.Equal(1, animator.Frame);
        Assert.Equal(new Rect(16, 0, 16, 8), sprite.SourceRect);

        animator.Play(Walk); // already playing: keeps its progress
        Assert.Equal(1, animator.Frame);

        animator.Play(Jump);
        Frames(scene, 3, 0.2f); // 0.6 s > 0.5 s clip
        Assert.False(animator.IsPlaying);
        Assert.Equal(1, finished);
        Assert.Equal(new Rect(32, 8, 16, 8), sprite.SourceRect); // last frame (sheet index 5) stays
        Frames(scene, 2, 0.2f);
        Assert.Equal(1, finished);
        Assert.Equal(2, clips.Reads); // parsed once per clip
    }

    [Fact]
    public void MissingClip_IsLogged_AndChangesNothing()
    {
        var scene = new Scene { TextAssets = new Clips() };
        var go = scene.CreateGameObject("Hero");
        var sprite = go.AddComponent<SpriteRenderer>();
        var animator = go.AddComponent<SpriteAnimator>();
        animator.Clip = new AssetRef(Guid.NewGuid());
        scene.Start();
        Frames(scene, 2, 0.1f);
        Assert.False(animator.IsPlaying);
        Assert.True(sprite.Texture.IsNone);
    }

    [Fact]
    public void SourceRectAndAnimatorSettings_SurviveSerialization()
    {
        var scene = new Scene();
        var go = scene.CreateGameObject("Hero");
        go.AddComponent<SpriteRenderer>().SourceRect = new Rect(16, 0, 16, 16);
        var animator = go.AddComponent<SpriteAnimator>();
        (animator.Clip, animator.Speed, animator.PlayOnStart) = (Walk, 2f, false);
        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());

        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Hero")!;
        Assert.Equal(new Rect(16, 0, 16, 16), copy.GetComponent<SpriteRenderer>()!.SourceRect);
        var a = copy.GetComponent<SpriteAnimator>()!;
        Assert.Equal((Walk, 2f, false), (a.Clip, a.Speed, a.PlayOnStart));
    }

    private static int _frame;

    private static void Frames(Scene scene, int count, float dt)
    {
        for (var i = 0; i < count; i++)
        {
            _frame++;
            scene.Update(new FrameTime(dt, dt, _frame * dt, _frame * dt, _frame));
        }
    }

    [Fact]
    public void MaterialDefinitions_ParseUniformsAndRejectMistakes()
    {
        var shader = Guid.NewGuid();
        var material = MaterialDefinition.Parse($$"""{ "schemaVersion": 1, "shader": "asset:{{shader}}", "blend": "Additive", "uniforms": { "uTint": [1, 0.5, 0, 1], "uSpeed": 2, "uOffset": [1, 2] } }""", "glow.mnvtmat");
        Assert.Equal((shader, "Additive"), (material.Shader!.Value, material.Blend));
        Assert.Equal([1f, 0.5f, 0f, 1f], material.Uniforms["uTint"]);
        Assert.Equal([2f], material.Uniforms["uSpeed"]);
        Assert.Null(MaterialDefinition.Parse("""{ "schemaVersion": 1 }""", "default.mnvtmat").Shader);
        Assert.Throws<SerializationException>(() => MaterialDefinition.Parse("{}", "unversioned.mnvtmat"));
        Assert.Throws<SerializationException>(() => SpriteAnimationClip.Parse("{}", "unversioned.mnvtanim"));
        Assert.Throws<SerializationException>(() => SpriteAnimationClip.Parse("[1, 2]", "array.mnvtanim"));

        var ex = Assert.Throws<SerializationException>(() => MaterialDefinition.Parse(
            """{ "schemaVersion": 1, "shader": "glow.frag", "blend": "Screen", "uniforms": { "uTime": 1, "bad name": 1, "uThree": [1, 2, 3] } }""", "bad.mnvtmat"));
        Assert.Equal(["shader", "blend", "properties.uTime", "properties.bad name", "properties.uThree"], ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Path));
    }
}
