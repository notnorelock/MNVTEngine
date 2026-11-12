using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using MNVTEngine.Runtime;

namespace MNVTEngine.Particles2D;

/// <summary>
/// Draws every particle system of a 2D scene as sprites in the view's sprite batch: textured, colored, rotated and
/// sized per particle, with sprite-sheet frames, alpha or additive blending or a material, and stretched along the
/// velocity on request. Particles share the batch with the sprites, so they sort with them (sorting order, then
/// submission order) and batch by texture and material: one draw call per run of particles, never one per particle.
/// </summary>
public sealed class ParticleScenePass2D(RenderServices services) : IScenePass
{
    /// <summary>Statistics this pass adds to <see cref="ScenePassContext.Count"/>.</summary>
    public const string ParticlesDrawn = "particles.drawn", ParticlesCulled = "particles.culled", ParticleSystems = "particles.systems";

    private int[] _order = [];
    private float[] _keys = [];
    private Material? _additive;

    /// <summary>After the sprites (200), so a system with the same sorting order draws over them.</summary>
    public int Order => 210;

    public void Draw(ScenePassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var sample = Core.Diagnostics.Profiler.Sample("Render.Particles2D");
        int drawn = 0, culled = 0, systems = 0;
        foreach (var system in context.Scene.World.Pool<ParticleSystem>().Components)
        {
            if (system.ParticleCount == 0 || !system.IsActiveAndEnabled || context.IsHidden(system.GameObject))
            {
                continue;
            }

            systems++;
            DrawSystem(context, system, ref drawn, ref culled);
        }

        context.Count(ParticleSystems, systems);
        context.Count(ParticlesDrawn, drawn);
        context.Count(ParticlesCulled, culled);
    }

    private void DrawSystem(ScenePassContext context, ParticleSystem system, ref int drawn, ref int culled)
    {
        var simulation = system.Simulation;
        var particles = simulation.Particles;
        var settings = simulation.Settings;
        var renderer = settings.Renderer;
        var batch = context.Batch;
        var resources = services.Resources;
        var texture = renderer.Texture.IsNone ? batch.WhitePixel : resources.GetTexture(renderer.Texture);
        var material = !renderer.Material.IsNone ? resources.GetMaterial(renderer.Material)
            : renderer.Blend == ParticleBlendMode.Additive ? _additive ??= new Material(services.Renderer.DefaultMaterial.Shader, BlendMode.Additive, "Particles/Additive")
            : null;

        var sheet = settings.TextureSheetAnimation.Enabled ? settings.TextureSheetAnimation : null;
        var columns = Math.Max(1, sheet?.Columns ?? 1);
        var frameSize = new Vector2(texture.Width / (float)columns, texture.Height / (float)Math.Max(1, sheet?.Rows ?? 1));

        var local = settings.Main.SimulationSpace == ParticleSimulationSpace.Local;
        var toWorld = system.Transform.LocalToWorldMatrix;
        var scale = local ? Vector2.Abs((Vector2)system.Transform.LossyScale) : Vector2.One;
        var stretched = renderer.RenderMode == ParticleRenderMode.Stretched;
        var depth = -renderer.SortingOrder;
        var visible = context.View.VisibleWorldBounds;

        var count = particles.Count;
        var order = DrawOrder(particles, renderer.SortMode);
        for (var n = 0; n < count; n++)
        {
            var i = order is null ? n : order[n];
            var position = (Vector2)(local ? toWorld.MultiplyPoint3x4(particles.Position[i]) : particles.Position[i]);
            var size = Vector2.Scale(new Vector2(particles.Size[i].X, particles.Size[i].Y), scale);
            var rotation = particles.Rotation[i].Z;
            if (stretched)
            {
                var velocity = (Vector2)(local ? toWorld.MultiplyVector(particles.Velocity[i]) : particles.Velocity[i]);
                var speed = velocity.Magnitude;
                if (speed > 1e-4f)
                {
                    rotation = MathF.Atan2(velocity.Y, velocity.X) * Mathf.Rad2Deg;
                    size = new Vector2((size.X * renderer.LengthScale) + (speed * renderer.SpeedScale), size.Y);
                }
            }

            var radius = Math.Max(Math.Abs(size.X), Math.Abs(size.Y));
            if (position.X + radius < visible.Min.X || position.X - radius > visible.Max.X || position.Y + radius < visible.Min.Y || position.Y - radius > visible.Max.Y)
            {
                culled++;
                continue;
            }

            Rect? source = null;
            if (sheet is not null)
            {
                var frame = particles.Frame[i];
                source = new Rect(frame % columns * frameSize.X, frame / columns * frameSize.Y, frameSize.X, frameSize.Y);
            }

            batch.Draw(texture, position, size, particles.Color[i], rotation, null, source, SpriteFlip.None, depth, material);
            drawn++;
        }
    }

    /// <summary>Draw order for the sort mode (later is on top); null keeps the buffer's order.</summary>
    private int[]? DrawOrder(ParticleBuffer particles, ParticleSortMode mode)
    {
        if (mode is not (ParticleSortMode.OldestInFront or ParticleSortMode.YoungestInFront))
        {
            return null;
        }

        var count = particles.Count;
        if (_order.Length < count)
        {
            _order = new int[Math.Max(count, _order.Length * 2)];
            _keys = new float[_order.Length];
        }

        for (var i = 0; i < count; i++)
        {
            _order[i] = i;
            _keys[i] = mode == ParticleSortMode.OldestInFront ? particles.Age[i] : -particles.Age[i]; // ascending: drawn first, so below
        }

        Array.Sort(_keys, _order, 0, count);
        return _order;
    }

    public void Dispose()
    {
        // The additive material only references the renderer's shader, which the renderer owns.
    }
}
