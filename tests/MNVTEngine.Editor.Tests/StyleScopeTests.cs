using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Tests;

/// <summary>
/// Runs real Dear ImGui frames headless (no GPU) to check that a button which flips its own style condition, like the
/// toolbar's Play/Stop button, leaves the style color stack balanced.
/// </summary>
[Collection(nameof(ImGuiContextGroup))]
public sealed class StyleScopeTests : IDisposable
{
    private static readonly SysVector4 Highlight = new(0.26f, 0.42f, 0.64f, 1f);
    private readonly IntPtr _context;

    public StyleScopeTests()
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        io.DisplaySize = new SysVector2(400, 300);
        io.DeltaTime = 1f / 60f;
        io.Fonts.AddFontDefault();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
    }

    public void Dispose() => ImGui.DestroyContext(_context);

    [Fact]
    public void AButtonThatTurnsOffItsOwnHighlight_StillPopsIt()
    {
        var playing = true;
        var normal = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

        Frame(mouseDown: false, ref playing); // window appears, mouse hovers the button
        Frame(mouseDown: false, ref playing);
        Frame(mouseDown: true, ref playing);  // press on "Stop"
        Frame(mouseDown: false, ref playing); // release: click → stop playing inside the scope
        Assert.False(playing);
        Frame(mouseDown: false, ref playing); // a full frame after the click: no leaked push

        Assert.Equal(normal, ImGui.GetStyle().Colors[(int)ImGuiCol.Button]);
    }

    private static void Frame(bool mouseDown, ref bool playing)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(30, 30);
        io.AddMouseButtonEvent(0, mouseDown);
        ImGui.NewFrame();
        ImGui.SetNextWindowPos(SysVector2.Zero);
        ImGui.Begin("Toolbar");
        using (Gui.StyleColor(ImGuiCol.Button, Highlight, playing))
        {
            if (ImGui.Button(playing ? "Stop" : "Play", new SysVector2(80, 40)))
            {
                playing = !playing; // the push condition changes before the scope ends
            }
        }

        ImGui.End();
        ImGui.Render(); // ends the frame; Dear ImGui asserts here on an unbalanced style stack
    }
}

/// <summary>Dear ImGui keeps one current context per process: tests that create one never run in parallel.</summary>
[CollectionDefinition(nameof(ImGuiContextGroup), DisableParallelization = true)]
public sealed class ImGuiContextGroup;
