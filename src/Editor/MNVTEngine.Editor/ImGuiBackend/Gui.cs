using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.ImGuiBackend;

/// <summary>Small helpers between engine types and ImGui.NET (which uses System.Numerics).</summary>
internal static class Gui
{
    public const string GameObjectPayload = "MNVT_GAMEOBJECT";
    public const string AssetPayload = "MNVT_ASSET";

    public static SysVector2 ToSys(this Vector2 v) => new(v.X, v.Y);

    public static Vector2 ToEngine(this SysVector2 v) => new(v.X, v.Y);

    public static SysVector4 ToSys(this Color c) => new(c.R, c.G, c.B, c.A);

    public static Color ToColor(this SysVector4 v) => new(v.X, v.Y, v.Z, v.W);

    /// <summary>Shows a texture; render targets are flipped so they appear upright.</summary>
    public static void Image(nint id, Texture2D texture, SysVector2 size)
    {
        if (texture.IsFlippedVertically)
        {
            ImGui.Image(id, size, new SysVector2(0f, 1f), new SysVector2(1f, 0f));
        }
        else
        {
            ImGui.Image(id, size);
        }
    }

    public static unsafe void SetGuidPayload(Guid id)
    {
        var bytes = stackalloc byte[16];
        id.TryWriteBytes(new Span<byte>(bytes, 16));
        ImGui.SetDragDropPayload(GameObjectPayload, (nint)bytes, 16);
    }

    public static unsafe Guid? AcceptGuidPayload()
    {
        var payload = ImGui.AcceptDragDropPayload(GameObjectPayload);
        return payload.NativePtr is not null && payload.DataSize == 16
            ? new Guid(new ReadOnlySpan<byte>((void*)payload.Data, 16))
            : null;
    }

    public static unsafe void SetStringPayload(string type, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* p = bytes)
        {
            ImGui.SetDragDropPayload(type, (nint)p, (uint)bytes.Length);
        }
    }

    public static unsafe string? AcceptStringPayload(string type)
    {
        var payload = ImGui.AcceptDragDropPayload(type);
        return payload.NativePtr is not null ? Marshal.PtrToStringUTF8(payload.Data, payload.DataSize) : null;
    }

    /// <summary>Right-aligned help text marker shown in muted color.</summary>
    public static void Muted(string text) => ImGui.TextColored(new SysVector4(0.55f, 0.57f, 0.62f, 1f), text);

    public static void ApplyDarkTheme()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 3f;
        style.FrameRounding = 3f;
        style.TabRounding = 3f;
        style.GrabRounding = 3f;
        style.WindowBorderSize = 1f;
        style.FramePadding = new SysVector2(6f, 4f);
        style.ItemSpacing = new SysVector2(8f, 5f);

        var c = style.Colors;
        void Set(ImGuiCol col, float r, float g, float b, float a = 1f) => c[(int)col] = new SysVector4(r, g, b, a);
        Set(ImGuiCol.Text, 0.88f, 0.89f, 0.91f);
        Set(ImGuiCol.TextDisabled, 0.48f, 0.50f, 0.54f);
        Set(ImGuiCol.WindowBg, 0.13f, 0.14f, 0.16f);
        Set(ImGuiCol.ChildBg, 0.13f, 0.14f, 0.16f);
        Set(ImGuiCol.PopupBg, 0.11f, 0.12f, 0.14f, 0.98f);
        Set(ImGuiCol.Border, 0.06f, 0.06f, 0.07f);
        Set(ImGuiCol.FrameBg, 0.20f, 0.21f, 0.24f);
        Set(ImGuiCol.FrameBgHovered, 0.25f, 0.27f, 0.31f);
        Set(ImGuiCol.FrameBgActive, 0.28f, 0.31f, 0.36f);
        Set(ImGuiCol.TitleBg, 0.09f, 0.10f, 0.11f);
        Set(ImGuiCol.TitleBgActive, 0.09f, 0.10f, 0.11f);
        Set(ImGuiCol.MenuBarBg, 0.10f, 0.11f, 0.12f);
        Set(ImGuiCol.Header, 0.22f, 0.36f, 0.56f, 0.7f);
        Set(ImGuiCol.HeaderHovered, 0.26f, 0.42f, 0.64f, 0.8f);
        Set(ImGuiCol.HeaderActive, 0.28f, 0.46f, 0.70f);
        Set(ImGuiCol.Button, 0.22f, 0.24f, 0.28f);
        Set(ImGuiCol.ButtonHovered, 0.29f, 0.32f, 0.37f);
        Set(ImGuiCol.ButtonActive, 0.26f, 0.42f, 0.64f);
        Set(ImGuiCol.Tab, 0.10f, 0.11f, 0.12f);
        Set(ImGuiCol.TabHovered, 0.26f, 0.42f, 0.64f);
        Set(ImGuiCol.TabSelected, 0.17f, 0.19f, 0.22f);
        Set(ImGuiCol.TabDimmed, 0.10f, 0.11f, 0.12f);
        Set(ImGuiCol.TabDimmedSelected, 0.14f, 0.15f, 0.17f);
        Set(ImGuiCol.DockingPreview, 0.26f, 0.42f, 0.64f, 0.6f);
        Set(ImGuiCol.CheckMark, 0.45f, 0.68f, 1f);
        Set(ImGuiCol.SliderGrab, 0.45f, 0.68f, 1f);
        Set(ImGuiCol.SeparatorHovered, 0.26f, 0.42f, 0.64f);
    }
}
