using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class SpriteBatchTests : IDisposable
{
    private readonly FakeGraphicsDevice _device = new();
    private readonly Renderer2D _renderer;
    private readonly Texture2D _a;
    private readonly Texture2D _b;

    public SpriteBatchTests()
    {
        _renderer = new Renderer2D(_device);
        _a = _device.CreateTexture(4, 4, new byte[64], SamplerState.PointClamp, "A");
        _b = _device.CreateTexture(4, 4, new byte[64], SamplerState.PointClamp, "B");
    }

    private SpriteBatch Batch => _renderer.Batch;

    public void Dispose() => _device.Dispose();

    [Fact]
    public void SameTexture_IsOneDrawCall()
    {
        Batch.Begin(Matrix4x4.Identity);
        for (var i = 0; i < 100; i++)
        {
            Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        }

        Batch.End();
        var draw = Assert.Single(_device.Draws);
        Assert.Equal(600, draw.IndexCount);
        Assert.Equal(0, draw.IndexOffset);
        Assert.Same(_a, draw.Texture);
    }

    [Fact]
    public void Deferred_BreaksBatchOnTextureChange_PreservingOrder()
    {
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.Draw(_b, Vector2.Zero, Vector2.One, Color.White);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.End();

        Assert.Equal([_a, _b, _a], _device.Draws.Select(d => d.Texture));
        Assert.Equal([0, 6, 12], _device.Draws.Select(d => d.IndexOffset));
    }

    [Fact]
    public void TextureMode_GroupsByFirstUse()
    {
        Batch.Begin(Matrix4x4.Identity, SpriteSortMode.Texture);
        Batch.Draw(_b, new Vector2(1f, 0f), Vector2.One, Color.White);
        Batch.Draw(_a, new Vector2(2f, 0f), Vector2.One, Color.White);
        Batch.Draw(_b, new Vector2(3f, 0f), Vector2.One, Color.White);
        Batch.End();

        Assert.Equal([_b, _a], _device.Draws.Select(d => d.Texture));
        Assert.Equal(12, _device.Draws[0].IndexCount);

        // Within the group, submission order is kept (stable sort).
        var vertices = _device.VerticesAtDraw[0];
        Assert.Equal(1f, (vertices[0].Position.X + vertices[2].Position.X) / 2f);
        Assert.Equal(3f, (vertices[4].Position.X + vertices[6].Position.X) / 2f);
    }

    [Fact]
    public void BackToFront_DrawsHighestDepthFirst_TiesKeepOrder()
    {
        Batch.Begin(Matrix4x4.Identity, SpriteSortMode.BackToFront);
        Batch.Draw(_a, new Vector2(1f, 0f), Vector2.One, Color.White, depth: 0f);
        Batch.Draw(_a, new Vector2(2f, 0f), Vector2.One, Color.White, depth: 5f);
        Batch.Draw(_a, new Vector2(3f, 0f), Vector2.One, Color.White, depth: 0f);
        Batch.End();

        var v = _device.VerticesAtDraw.Single();
        Assert.Equal([2f, 1f, 3f], [v[0].Position.X + 0.5f, v[4].Position.X + 0.5f, v[8].Position.X + 0.5f]);
    }

    [Fact]
    public void ManySprites_SplitIntoChunks()
    {
        var total = SpriteBatch.MaxSpritesPerChunk + 10;
        Batch.Begin(Matrix4x4.Identity);
        for (var i = 0; i < total; i++)
        {
            Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        }

        Batch.End();
        Assert.Equal(2, _device.Draws.Count);
        Assert.Equal(SpriteBatch.MaxSpritesPerChunk * 6, _device.Draws[0].IndexCount);
        Assert.Equal(60, _device.Draws[1].IndexCount);
        Assert.Equal(total, Batch.SpriteCount);
        Assert.Equal(2, Batch.DrawCallCount);
    }

    [Fact]
    public void QuadGeometry_PivotRotationAndUv()
    {
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(_a, new Vector2(10f, 20f), new Vector2(4f, 2f), Color.Red, pivot: Vector2.Zero);
        Batch.Draw(_a, Vector2.Zero, new Vector2(2f, 2f), Color.White, rotationDegrees: 90f);
        Batch.End();

        var v = _device.VerticesAtDraw.Single();
        // Order: bottom-left, bottom-right, top-right, top-left.
        Assert.Equal(new Vector2(10f, 20f), v[0].Position);
        Assert.Equal(new Vector2(14f, 22f), v[2].Position);
        Assert.Equal(new Vector2(0f, 1f), v[0].TexCoord); // bottom of image = v 1 (row 0 is top)
        Assert.Equal(new Vector2(1f, 0f), v[2].TexCoord);
        Assert.Equal(Color.Red.ToRgba32(), v[0].Color);

        // 90 degrees CCW around the center: bottom-left corner (-1, -1) moves to (1, -1).
        Assert.True(Vector2.Approximately(new Vector2(1f, -1f), v[4].Position));
    }

    [Fact]
    public void SourceRectAndFlip_ProduceExpectedUv()
    {
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White, source: new Rect(0f, 0f, 2f, 2f), flip: SpriteFlip.Horizontal);
        Batch.End();

        var v = _device.VerticesAtDraw.Single();
        Assert.Equal(new Vector2(0.5f, 0.5f), v[0].TexCoord);
        Assert.Equal(new Vector2(0f, 0f), v[2].TexCoord);
    }

    [Fact]
    public void RenderTargets_AreSampledUpright()
    {
        using var target = _device.CreateRenderTarget(4, 4, SamplerState.PointClamp);
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(target, Vector2.Zero, Vector2.One, Color.White);
        Batch.End();

        var v = _device.VerticesAtDraw.Single();
        Assert.Equal(0f, v[0].TexCoord.Y); // flipped texture: bottom of the quad samples row 0
        Assert.Equal(1f, v[2].TexCoord.Y);
    }

    [Fact]
    public void Material_IsPassedThrough_DefaultOtherwise()
    {
        var additive = _renderer.CreateSpriteMaterial(BlendMode.Additive, "Add");
        Batch.Begin(Matrix4x4.Identity, material: additive);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.End();
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.End();

        Assert.Same(additive, _device.Draws[0].Material);
        Assert.Same(_renderer.DefaultMaterial, _device.Draws[1].Material);
    }

    [Fact]
    public void Renderer_UsesCameraMatrixAndTarget()
    {
        var camera = new Camera2D(800, 600) { Position = new Vector2(5f, 5f) };
        using var target = _device.CreateRenderTarget(800, 600, SamplerState.PointClamp);
        _renderer.Begin(camera, target: target).Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        _renderer.End();

        Assert.Same(target, _device.CurrentTarget);
        Assert.Equal(camera.ViewProjection, _device.Draws.Single().ViewProjection);
    }

    [Fact]
    public void Misuse_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White));
        Assert.Throws<InvalidOperationException>(Batch.End);
        Batch.Begin(Matrix4x4.Identity);
        Assert.Throws<InvalidOperationException>(() => Batch.Begin(Matrix4x4.Identity));
        _b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Batch.Draw(_b, Vector2.Zero, Vector2.One, Color.White));
        Batch.End();
    }

    [Fact]
    public void EmptyBatch_IssuesNoDraws()
    {
        Batch.Begin(Matrix4x4.Identity);
        Batch.End();
        Assert.Empty(_device.Draws);
    }

    [Fact]
    public void Shapes_UseWhitePixelAndBatchTogether()
    {
        Batch.Begin(Matrix4x4.Identity);
        Batch.DrawLine(Vector2.Zero, new Vector2(10f, 0f), Color.White, 2f);
        Batch.DrawRectangle(new Rect(0f, 0f, 10f, 10f), Color.White);
        Batch.DrawCircle(Vector2.Zero, 5f, Color.White, segments: 8);
        Batch.End();

        var draw = Assert.Single(_device.Draws);
        Assert.Same(Batch.WhitePixel, draw.Texture);
        Assert.Equal((1 + 4 + 8) * 6, draw.IndexCount);

        var line = _device.VerticesAtDraw[0];
        Assert.True(Vector2.Approximately(new Vector2(0f, -1f), line[0].Position));
        Assert.True(Vector2.Approximately(new Vector2(10f, 1f), line[2].Position));
    }

    [Fact]
    public void Dispose_ReleasesEverything()
    {
        _a.Dispose();
        _b.Dispose();
        _renderer.Dispose();
        Assert.Equal(0, _device.Resources.LiveCount);
    }

    [Fact]
    public void PerSpriteMaterials_SplitDrawsOnlyWhereTheyChange()
    {
        var glow = new Material(_renderer.DefaultMaterial.Shader, BlendMode.Additive, "Glow");
        Batch.Begin(Matrix4x4.Identity);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White, material: glow);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White, material: glow);
        Batch.Draw(_a, Vector2.Zero, Vector2.One, Color.White);
        Batch.End();

        Assert.Equal([_renderer.DefaultMaterial, glow, _renderer.DefaultMaterial], _device.Draws.Select(d => d.Material));
        Assert.Equal([6, 12, 6], _device.Draws.Select(d => d.IndexCount));
    }
}
