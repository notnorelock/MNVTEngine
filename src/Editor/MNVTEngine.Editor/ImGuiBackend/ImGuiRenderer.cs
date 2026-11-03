using System.Runtime.InteropServices;
using ImGuiNET;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.ImGuiBackend;

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

    public ImGuiRenderer(IGraphicsDevice device, Material material, string? fontPath, float fontSize)
    {
        _device = device;
        _material = material;
        Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(Context);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;
        io.NativePtr->IniFilename = null; // layout persistence is handled by EditorApp per project

        if (fontPath is not null && File.Exists(fontPath))
        {
            io.Fonts.AddFontFromFileTTF(fontPath, fontSize);
        }
        else
        {
            io.Fonts.AddFontDefault();
        }

        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
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

    public void Render(ImDrawDataPtr data, int framebufferWidth, int framebufferHeight)
    {
        if (data.CmdListsCount == 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
        {
            return;
        }

        _device.SetRenderTarget(null);
        _device.SetViewport(0, 0, framebufferWidth, framebufferHeight);

        var left = data.DisplayPos.X;
        var top = data.DisplayPos.Y;
        var projection = Matrix4x4.Ortho(left, left + data.DisplaySize.X, top + data.DisplaySize.Y, top, -1f, 1f);
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

/// <summary>Docking layout construction. ImGui.NET does not wrap the DockBuilder API, so it is bound from cimgui directly.</summary>
internal static partial class DockBuilder
{
    public const int DockSpaceFlag = 1 << 10; // ImGuiDockNodeFlags_DockSpace (internal)

    public enum Direction
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3,
    }

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderGetNode")]
    public static partial nint GetNode(uint nodeId);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderAddNode")]
    public static partial uint AddNode(uint nodeId, int flags);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderRemoveNode")]
    public static partial void RemoveNode(uint nodeId);

    // ImVec2 is passed by value; LibraryImport cannot marshal structs without disabling runtime marshalling assembly-wide.
    [DllImport("cimgui", EntryPoint = "igDockBuilderSetNodeSize")]
    public static extern void SetNodeSize(uint nodeId, System.Numerics.Vector2 size);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderSplitNode")]
    public static partial uint SplitNode(uint nodeId, Direction direction, float ratio, out uint idAtDirection, out uint idAtOpposite);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderDockWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void DockWindow(string windowName, uint nodeId);

    [LibraryImport("cimgui", EntryPoint = "igDockBuilderFinish")]
    public static partial void Finish(uint nodeId);
}
