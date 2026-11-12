using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using MNVTEngine.Runtime;

namespace MNVTEngine.Particles3D;

/// <summary>
/// Draws every particle system of a 3D scene through the scene's camera, after the opaque meshes (which wrote the
/// depth buffer): quads oriented by <see cref="ParticleQuads"/>, depth-tested but not written, alpha or additive.
/// Systems are drawn farthest first and, with <see cref="ParticleSortMode.ByDistance"/>, their particles back to front.
/// Quads are built on the CPU into one dynamic vertex buffer, uploaded in chunks; each run of systems with the same
/// texture and blending is one draw call. Soft particles and mesh particles are not supported; a custom material is
/// not used in 3D (the quads need the particle vertex format).
/// </summary>
public sealed class ParticleScenePass3D(RenderServices services) : IScenePass
{
    public const string ParticlesDrawn = "particles.drawn", ParticlesCulled = "particles.culled", ParticleSystems = "particles.systems", ParticleDrawCalls = "particles.drawCalls";

    private const int QuadsPerChunk = 8192; // 32768 vertices: 16-bit indices

    private readonly List<(ParticleSystem System, float Distance)> _systems = [];
    private ParticleVertex[] _vertices = new ParticleVertex[256];
    private int[] _order = [];
    private float[] _keys = [];
    private VertexBuffer? _vertexBuffer;
    private IndexBuffer? _indexBuffer;
    private Shader? _shader;
    private Material? _alpha;
    private Material? _additive;
    private Texture2D? _white;

    /// <summary>After opaque meshes (100), before sprites (200).</summary>
    public int Order => 150;

    public void Draw(ScenePassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Camera3D is not { } camera)
        {
            return;
        }

        using var sample = Core.Diagnostics.Profiler.Sample("Render.Particles3D");
        var cameraPosition = camera.Transform.Position;
        _systems.Clear();
        foreach (var system in context.Scene.World.Pool<ParticleSystem>().Components)
        {
            if (system.ParticleCount > 0 && system.IsActiveAndEnabled && !context.IsHidden(system.GameObject))
            {
                _systems.Add((system, Vector3.DistanceSquared(system.Transform.Position, cameraPosition)));
            }
        }

        if (_systems.Count == 0)
        {
            return;
        }

        _systems.Sort((a, b) => b.Distance.CompareTo(a.Distance) is var c && c != 0 ? c : a.System.Settings.Renderer.SortingOrder.CompareTo(b.System.Settings.Renderer.SortingOrder));
        var device = services.Renderer.Device;
        EnsureResources(device);
        var view = new ParticleView(cameraPosition, camera.Transform.Rotation * Vector3.Right, camera.Transform.Rotation * Vector3.Up);
        var viewProjection = camera.ViewProjectionMatrix;
        var frustum = Frustum.FromViewProjection(viewProjection);
        var state = context.Target is { HasDepth: false } ? new RenderState(Cull: CullMode.None) : new RenderState(DepthTest: true, DepthWrite: false, Cull: CullMode.None);

        int drawn = 0, culled = 0, calls = 0, count = 0;
        Texture2D? runTexture = null;
        Material? runMaterial = null;
        var runStart = 0;
        foreach (var (system, _) in _systems)
        {
            var renderer = system.Simulation.Settings.Renderer;
            var texture = renderer.Texture.IsNone ? _white! : services.Resources.GetTexture(renderer.Texture);
            var material = renderer.Blend == ParticleBlendMode.Additive ? _additive! : _alpha!;
            if (count > 0 && (!ReferenceEquals(texture, runTexture) || !ReferenceEquals(material, runMaterial)))
            {
                calls += Flush(device, runStart, count - runStart, runTexture!, runMaterial!, viewProjection, state);
                runStart = count;
            }

            (runTexture, runMaterial) = (texture, material);
            count = AddSystem(system, view, frustum, count, ref drawn, ref culled);
            if (count - runStart >= QuadsPerChunk)
            {
                calls += Flush(device, runStart, count - runStart, runTexture, runMaterial, viewProjection, state);
                runStart = count;
            }
        }

        if (count > runStart)
        {
            calls += Flush(device, runStart, count - runStart, runTexture!, runMaterial!, viewProjection, state);
        }

        context.Count(ParticleSystems, _systems.Count);
        context.Count(ParticlesDrawn, drawn);
        context.Count(ParticlesCulled, culled);
        context.Count(ParticleDrawCalls, calls);
    }

    /// <summary>Adds the system's visible particles as quads from quad <paramref name="first"/>; returns the new quad count.</summary>
    private int AddSystem(ParticleSystem system, in ParticleView view, Frustum frustum, int first, ref int drawn, ref int culled)
    {
        var simulation = system.Simulation;
        var particles = simulation.Particles;
        var settings = simulation.Settings;
        var renderer = settings.Renderer;
        var local = settings.Main.SimulationSpace == ParticleSimulationSpace.Local;
        var toWorld = system.Transform.LocalToWorldMatrix;
        var scale = local ? system.Transform.LossyScale : Vector3.One;
        var sheet = settings.TextureSheetAnimation.Enabled ? settings.TextureSheetAnimation : null;
        var columns = Math.Max(1, sheet?.Columns ?? 1);
        var rows = Math.Max(1, sheet?.Rows ?? 1);
        var order = DrawOrder(particles, renderer.SortMode, view.Position, local, toWorld);
        var quad = first;
        EnsureVertices((first + particles.Count) * 4);
        for (var n = 0; n < particles.Count; n++)
        {
            var i = order is null ? n : order[n];
            var position = local ? toWorld.MultiplyPoint3x4(particles.Position[i]) : particles.Position[i];
            var size = new Vector2(particles.Size[i].X * Math.Abs(scale.X), particles.Size[i].Y * Math.Abs(scale.Y));
            if (!frustum.Intersects(new BoundingSphere(position, Math.Max(size.X, size.Y))))
            {
                culled++;
                continue;
            }

            var velocity = local ? toWorld.MultiplyVector(particles.Velocity[i]) : particles.Velocity[i];
            var (right, up) = ParticleQuads.Axes(renderer.RenderMode, view, position, size, particles.Rotation[i], velocity, renderer.LengthScale, renderer.SpeedScale);
            var frame = sheet is null ? 0 : particles.Frame[i];
            var u0 = frame % columns / (float)columns;
            var v0 = frame / columns % rows / (float)rows;
            var (u1, v1) = (u0 + (1f / columns), v0 + (1f / rows));
            var color = particles.Color[i].ToRgba32();
            var v = quad * 4;
            _vertices[v] = new ParticleVertex(position - right - up, new Vector2(u0, v1), color);
            _vertices[v + 1] = new ParticleVertex(position + right - up, new Vector2(u1, v1), color);
            _vertices[v + 2] = new ParticleVertex(position + right + up, new Vector2(u1, v0), color);
            _vertices[v + 3] = new ParticleVertex(position - right + up, new Vector2(u0, v0), color);
            quad++;
            drawn++;
        }

        return quad;
    }

    /// <summary>Uploads quads [start, start + count) and draws them, in chunks the 16-bit index buffer can address.</summary>
    private int Flush(IGraphicsDevice device, int start, int count, Texture2D texture, Material material, Matrix4x4 viewProjection, RenderState state)
    {
        var calls = 0;
        for (var offset = 0; offset < count; offset += QuadsPerChunk)
        {
            var quads = Math.Min(QuadsPerChunk, count - offset);
            _vertexBuffer!.SetData<ParticleVertex>(_vertices.AsSpan((start + offset) * 4, quads * 4));
            device.Draw(new DrawCommand(material, texture, _vertexBuffer, _indexBuffer!, 0, quads * 6, viewProjection, State: state));
            calls++;
        }

        return calls;
    }

    /// <summary>Back to front (by distance), or by age; null keeps the buffer's order.</summary>
    private int[]? DrawOrder(ParticleBuffer particles, ParticleSortMode mode, Vector3 camera, bool local, Matrix4x4 toWorld)
    {
        if (mode == ParticleSortMode.None)
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
            _keys[i] = mode switch
            {
                ParticleSortMode.ByDistance => -Vector3.DistanceSquared(local ? toWorld.MultiplyPoint3x4(particles.Position[i]) : particles.Position[i], camera), // farthest first
                ParticleSortMode.OldestInFront => particles.Age[i],
                _ => -particles.Age[i],
            };
        }

        Array.Sort(_keys, _order, 0, count);
        return _order;
    }

    private void EnsureVertices(int vertices)
    {
        if (_vertices.Length < vertices)
        {
            Array.Resize(ref _vertices, Math.Max(vertices, _vertices.Length * 2));
        }
    }

    private void EnsureResources(IGraphicsDevice device)
    {
        if (_vertexBuffer is not null)
        {
            return;
        }

        _vertexBuffer = device.CreateVertexBuffer(ParticleVertex.Layout, QuadsPerChunk * 4, "Particles3D.Vertices");
        var indices = new ushort[QuadsPerChunk * 6];
        for (var q = 0; q < QuadsPerChunk; q++)
        {
            var v = (ushort)(q * 4);
            var i = q * 6;
            (indices[i], indices[i + 1], indices[i + 2], indices[i + 3], indices[i + 4], indices[i + 5]) = (v, (ushort)(v + 1), (ushort)(v + 2), (ushort)(v + 2), (ushort)(v + 3), v);
        }

        _indexBuffer = device.CreateIndexBuffer(indices, "Particles3D.Indices");
        _shader = device.CreateShader(ParticleVertex.Shader);
        _alpha = new Material(_shader, BlendMode.Alpha, "Particles3D/Alpha");
        _additive = new Material(_shader, BlendMode.Additive, "Particles3D/Additive");
        _white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.LinearClamp, "Particles3D.White");
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _shader?.Dispose();
        _white?.Dispose();
    }
}
