using System.Runtime.CompilerServices;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Draws UI contexts through <see cref="IGraphicsDevice"/> (the Vulkan backend in practice) with the sprite shader and
/// alpha blending, after the game scene. Each context's quads are cached and rebuilt only when it changed (layout,
/// state, text) or the atlas was reset; every frame the cached vertices are streamed and drawn in runs of one texture
/// (normally a single run: the atlas page). Owns the <see cref="GlyphAtlas"/> shared by all UI text and shapes.
/// </summary>
public sealed class UIRenderer : IDisposable
{
    private const int QuadsPerDraw = 16384; // 65 536 vertices: the 16-bit index limit
    private readonly IGraphicsDevice _device;
    private readonly Material _material;
    private readonly VertexBuffer _vertices;
    private readonly IndexBuffer _indices;
    private readonly ConditionalWeakTable<UIContext, Mesh> _meshes = [];

    public UIRenderer(IGraphicsDevice device, Material material)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(material);
        _device = device;
        _material = material;
        Atlas = new GlyphAtlas(device);
        _vertices = device.CreateVertexBuffer(SpriteVertex.Layout, QuadsPerDraw * 4, "UI.Vertices");
        var indices = new ushort[QuadsPerDraw * 6];
        for (var q = 0; q < QuadsPerDraw; q++)
        {
            var v = (ushort)(q * 4);
            indices.AsSpan(q * 6, 6).SetValues(v, (ushort)(v + 1), (ushort)(v + 2), (ushort)(v + 2), (ushort)(v + 3), v);
        }

        _indices = device.CreateIndexBuffer(indices, "UI.Indices");
    }

    public GlyphAtlas Atlas { get; }

    /// <summary>Draws one context into the current render target, whose size is <paramref name="framebufferWidth"/> x <paramref name="framebufferHeight"/>.</summary>
    public void Render(UIContext context, int framebufferWidth, int framebufferHeight)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Visible || framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        context.LayoutIfDirty();
        var mesh = _meshes.GetOrCreateValue(context);
        var pixelScale = context.Scale * context.FramebufferScale;
        if (context.VisualDirty || mesh.Generation != Atlas.Generation || mesh.PixelScale != pixelScale)
        {
            Rebuild(context, mesh, pixelScale);
        }

        Atlas.Flush();
        var stats = context.Statistics;
        stats.DrawCalls = 0;
        stats.TextureSwitches = 0;
        _device.SetViewport(0, 0, framebufferWidth, framebufferHeight);
        var projection = Matrix4x4.OrthoZeroToOne(0f, framebufferWidth, framebufferHeight, 0f, -1f, 1f);
        Texture2D? last = null;
        for (var start = 0; start < mesh.Quads; start += QuadsPerDraw)
        {
            var count = Math.Min(QuadsPerDraw, mesh.Quads - start);
            _vertices.SetData<SpriteVertex>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Vertices).Slice(start * 4, count * 4));
            foreach (var run in mesh.Runs)
            {
                var first = Math.Max(run.FirstQuad, start);
                var end = Math.Min(run.FirstQuad + run.Quads, start + count);
                if (end <= first)
                {
                    continue;
                }

                if (!ReferenceEquals(last, run.Texture))
                {
                    stats.TextureSwitches += last is null ? 0 : 1;
                    last = run.Texture;
                }

                var command = new DrawCommand(_material, run.Texture, _vertices, _indices, (first - start) * 6, (end - first) * 6, projection);
                _device.Draw(in command);
                stats.DrawCalls++;
            }
        }
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        Atlas.Dispose();
    }

    private void Rebuild(UIContext context, Mesh mesh, float pixelScale)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            mesh.Vertices.Clear();
            mesh.Runs.Clear();
            var generation = Atlas.Generation;
            var painter = new UIPainter(Atlas, pixelScale, mesh.Vertices, mesh.Runs, new Rect(0, 0, context.Size.X, context.Size.Y));
            var visible = 0;
            Paint(context.Root, painter, ref visible);
            Paint(context.Overlay, painter, ref visible);
            if (Atlas.Generation == generation)
            {
                (mesh.Generation, mesh.PixelScale) = (generation, pixelScale);
                context.Statistics.VisibleElements = visible;
                break; // otherwise the atlas was cleared while painting: quads point at old pages, paint again
            }
        }

        context.VisualDirty = false;
        context.Statistics.GeometryRebuilds++;
        context.Statistics.Quads = mesh.Quads;
    }

    private static void Paint(UIElement element, UIPainter painter, ref int visible)
    {
        if (!element.Visible)
        {
            return;
        }

        visible++;
        var style = element.ResolveStyle();
        painter.PushOpacity(style.Opacity);
        painter.PushClip(element.PaintClip);
        element.Paint(painter, style);
        painter.PopClip();
        foreach (var child in element.ChildrenInDrawOrder())
        {
            Paint(child, painter, ref visible);
        }

        painter.PushClip(element.PaintClip);
        element.PaintOver(painter, style);
        painter.PopClip();
        painter.PopOpacity();
    }

    private sealed class Mesh
    {
        public List<SpriteVertex> Vertices { get; } = [];

        public List<UIDrawRun> Runs { get; } = [];

        public int Generation { get; set; } = -1;

        public float PixelScale { get; set; }

        public int Quads => Vertices.Count / 4;
    }
}

internal static class SpanFill
{
    public static void SetValues(this Span<ushort> span, params ReadOnlySpan<ushort> values) => values.CopyTo(span);
}
