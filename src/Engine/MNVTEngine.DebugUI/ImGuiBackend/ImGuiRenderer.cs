using System.Runtime.InteropServices;
using ImGuiNET;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.DebugUI.ImGuiBackend;

/// <summary>
/// Draws ImGui through <see cref="IGraphicsDevice"/>. ImDrawVert (pos, uv, packed RGBA) has exactly the
/// <see cref="SpriteVertex"/> layout, so the built-in sprite shader and alpha blending are reused; clip rects
/// become scissor rects. Textures shown in the UI are registered to get an ImGui texture id.
/// </summary>
public sealed unsafe class ImGuiRenderer : IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly Material _material;
    private readonly Dictionary<nint, Texture2D> _textures = [];
    private readonly Dictionary<Texture2D, nint> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Texture2D _fontTexture;
    private VertexBuffer _vertices;
    private IndexBuffer _indices;
    private nint _nextId = 1;

    /// <param name="fontScale">Framebuffer pixels per logical pixel: the font is rasterized that much larger and drawn scaled down, so text stays sharp on high-DPI displays.</param>
    /// <param name="iconFont">
    /// An icon font merged into the text font (its glyphs in <paramref name="iconFont"/>.First to Last, e.g. a private use
    /// area), so labels can mix text and icons. The TrueType data is copied: the caller keeps its array.
    /// </param>
    /// <param name="codeFontPath">A monospaced font for code (<see cref="CodeFont"/>); without it code uses the text font.</param>
    public ImGuiRenderer(IGraphicsDevice device, Material material, string? fontPath, float fontSize, float fontScale = 1f, (byte[] Ttf, char First, char Last)? iconFont = null, string? codeFontPath = null)
    {
        _device = device;
        _material = material;
        Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(Context);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.NativePtr->IniFilename = null; // layout persistence is handled by EditorApp per project

        fontScale = Math.Clamp(fontScale, 1f, 4f);
        if (fontPath is not null && File.Exists(fontPath))
        {
            io.Fonts.AddFontFromFileTTF(fontPath, fontSize * fontScale);
        }
        else
        {
            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            config->SizePixels = 13f * fontScale;
            io.Fonts.AddFontDefault(config);
            ImGuiNative.ImFontConfig_destroy(config);
        }

        io.FontGlobalScale = 1f / fontScale;

        // The glyph ranges must stay alive until the atlas is built (GetTexDataAsRGBA32).
        var ranges = (ushort*)NativeMemory.Alloc(3, sizeof(ushort));
        if (iconFont is { Ttf.Length: > 0 } icons)
        {
            (ranges[0], ranges[1], ranges[2]) = (icons.First, icons.Last, 0);
            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            config->MergeMode = 1;
            config->PixelSnapH = 1;
            config->GlyphMinAdvanceX = fontSize * fontScale; // monospaced icons line up in toolbars and trees
            config->GlyphOffset = new System.Numerics.Vector2(0f, fontSize * fontScale * 0.12f); // onto the text baseline
            // The atlas owns and frees the data (FontDataOwnedByAtlas), so it must come from ImGui's allocator.
            var data = ImGui.MemAlloc((uint)icons.Ttf.Length);
            Marshal.Copy(icons.Ttf, 0, data, icons.Ttf.Length);
            io.Fonts.AddFontFromMemoryTTF(data, icons.Ttf.Length, fontSize * fontScale * 0.95f, config, (nint)ranges);
            ImGuiNative.ImFontConfig_destroy(config);
        }

        if (codeFontPath is not null && File.Exists(codeFontPath))
        {
            CodeFont = io.Fonts.AddFontFromFileTTF(codeFontPath, (fontSize - 1f) * fontScale); // after the icons: they merge into the text font only
        }

        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
        NativeMemory.Free(ranges);
        _fontTexture = device.CreateTexture(width, height, new ReadOnlySpan<byte>(pixels, width * height * 4), SamplerState.LinearClamp, "ImGui.Font");
        io.Fonts.SetTexID(Register(_fontTexture));
        io.Fonts.ClearTexData();

        _vertices = device.CreateVertexBuffer(SpriteVertex.Layout, 16384, "ImGui.Vertices");
        _indices = device.CreateDynamicIndexBuffer(32768, "ImGui.Indices");
    }

    public nint Context { get; }

    /// <summary>Stable id for a texture, for use with <c>ImGui.Image</c>.</summary>
    public nint Register(Texture2D texture)
    {
        if (_ids.TryGetValue(texture, out var id))
        {
            return id;
        }

        id = _nextId++;
        _ids.Add(texture, id);
        _textures.Add(id, texture);
        return id;
    }

    /// <summary>Points an existing id at a new texture (e.g. a render target recreated at a new size).</summary>
    public void Rebind(nint id, Texture2D texture)
    {
        if (_textures.Remove(id, out var old))
        {
            _ids.Remove(old);
        }

        _textures[id] = texture;
        _ids[texture] = id;
    }

    public void Unregister(Texture2D texture)
    {
        if (_ids.Remove(texture, out var id))
        {
            _textures.Remove(id);
        }
    }

    /// <summary>Forgets textures that were disposed elsewhere (e.g. evicted after reimport).</summary>
    public void PruneDisposed()
    {
        foreach (var texture in _ids.Keys.Where(t => t.IsDisposed).ToList())
        {
            Unregister(texture);
        }
    }

    /// <param name="target">Where to draw; null for the back buffer.</param>
    public void Render(ImDrawDataPtr data, int framebufferWidth, int framebufferHeight, RenderTarget2D? target = null)
    {
        if (data.CmdListsCount == 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        _device.SetRenderTarget(target);
        _device.SetViewport(0, 0, framebufferWidth, framebufferHeight);

        var left = data.DisplayPos.X;
        var top = data.DisplayPos.Y;
        var projection = Matrix4x4.OrthoZeroToOne(left, left + data.DisplaySize.X, top + data.DisplaySize.Y, top, -1f, 1f); // z = 0 at depth 0.5, inside Vulkan's range
        var scale = data.FramebufferScale;

        for (var n = 0; n < data.CmdListsCount; n++)
        {
            var list = data.CmdLists[n];
            EnsureCapacity(list.VtxBuffer.Size, list.IdxBuffer.Size);
            _vertices.SetData(new ReadOnlySpan<SpriteVertex>((void*)list.VtxBuffer.Data, list.VtxBuffer.Size));
            _indices.SetData(new ReadOnlySpan<ushort>((void*)list.IdxBuffer.Data, list.IdxBuffer.Size));

            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var cmd = list.CmdBuffer[c];
                if (cmd.UserCallback != 0 || cmd.ElemCount == 0)
                {
                    continue;
                }

                // Clip rect is (x1, y1, x2, y2) in display space, y down; scissor is framebuffer pixels, y up.
                var x1 = (cmd.ClipRect.X - left) * scale.X;
                var y1 = (cmd.ClipRect.Y - top) * scale.Y;
                var x2 = (cmd.ClipRect.Z - left) * scale.X;
                var y2 = (cmd.ClipRect.W - top) * scale.Y;
                if (x2 <= x1 || y2 <= y1)
                {
                    continue;
                }

                if (!_textures.TryGetValue(cmd.TextureId, out var texture) || texture.IsDisposed)
                {
                    continue; // texture released this frame (e.g. resized view); skip rather than crash
                }

                var command = new DrawCommand(_material, texture, _vertices, _indices, (int)cmd.IdxOffset, (int)cmd.ElemCount, projection,
                    Scissor: new Rect(x1, framebufferHeight - y2, x2 - x1, y2 - y1));
                _device.Draw(in command);
            }
        }
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _fontTexture.Dispose();
        ImGui.DestroyContext(Context);
    }

    /// <summary>The monospaced code font, when one was given; push it with <c>ImGui.PushFont</c>.</summary>
    public ImFontPtr? CodeFont { get; }

    private void EnsureCapacity(int vertexCount, int indexCount)
    {
        if (vertexCount > _vertices.Capacity)
        {
            _vertices.Dispose();
            _vertices = _device.CreateVertexBuffer(SpriteVertex.Layout, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)vertexCount), "ImGui.Vertices");
        }

        if (indexCount > _indices.Count)
        {
            _indices.Dispose();
            _indices = _device.CreateDynamicIndexBuffer((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)indexCount), "ImGui.Indices");
        }
    }
}

