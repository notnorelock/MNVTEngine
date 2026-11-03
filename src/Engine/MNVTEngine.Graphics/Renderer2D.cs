namespace MNVTEngine.Graphics;

/// <summary>
/// Owns the shared 2D rendering resources (sprite shader, default material, sprite batch) and ties
/// a <see cref="Camera2D"/> and an optional render target to each pass.
/// </summary>
public sealed class Renderer2D : IDisposable
{
    public Renderer2D(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        SpriteShader = device.CreateShader(BuiltinShaders.Sprite);
        try
        {
            DefaultMaterial = new Material(SpriteShader, BlendMode.Alpha, "Sprite/Default");
            Batch = new SpriteBatch(device, DefaultMaterial);
        }
        catch
        {
            SpriteShader.Dispose();
            throw;
        }
    }

    public IGraphicsDevice Device { get; }

    public Shader SpriteShader { get; }

    /// <summary>Alpha-blended sprite material. Create more with <see cref="CreateSpriteMaterial"/>.</summary>
    public Material DefaultMaterial { get; }

    public SpriteBatch Batch { get; }

    public Material CreateSpriteMaterial(BlendMode blendMode, string name) => new(SpriteShader, blendMode, name);

    /// <summary>
    /// Starts a pass. Rendering goes to <paramref name="target"/> (or the back buffer) with a viewport matching the camera.
    /// </summary>
    public SpriteBatch Begin(Camera2D camera, SpriteSortMode sortMode = SpriteSortMode.Deferred, Material? material = null, RenderTarget2D? target = null)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Device.SetRenderTarget(target);
        Device.SetViewport(0, 0, camera.ViewportWidth, camera.ViewportHeight);
        Batch.Begin(camera.ViewProjection, sortMode, material);
        return Batch;
    }

    public void End() => Batch.End();

    public void Dispose()
    {
        Batch.Dispose();
        SpriteShader.Dispose();
    }
}
