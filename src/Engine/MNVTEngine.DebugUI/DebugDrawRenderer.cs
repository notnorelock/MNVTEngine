using ImGuiNET;
using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Runtime;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.DebugUI;

/// <summary>
/// Turns queued <see cref="DebugDrawCommand"/> shapes into ImGui draw-list geometry: they share the UI's vertex buffer, so
/// all debug shapes cost a handful of draw calls, behind the debug windows and above the game. Text is drawn by the
/// host with the engine's text stack (<c>DebugTextRenderer</c>). World-space positions go through the scene camera; sizes of points, line thickness and
/// text stay in pixels. ImGui uses 16-bit indices, so one draw list holds at most 65 535 vertices: the background list
/// is filled first, then the foreground list; commands beyond both are skipped and counted in <see cref="Skipped"/>.
/// </summary>
internal sealed class DebugDrawRenderer
{
    private const int VertexLimit = 60_000;

    /// <summary>Commands not drawn last frame because the draw lists were full.</summary>
    public int Skipped { get; private set; }

    public int Drawn { get; private set; }

    public void Render(ImDrawListPtr background, ReadOnlySpan<DebugDrawCommand> commands, Camera2D? camera, OverlayDisplay display)
    {
        Skipped = 0;
        Drawn = 0;
        var list = background;
        var usingForeground = false;
        var toLogical = camera is null || camera.ViewportWidth <= 0 ? 1f : display.Width / (float)camera.ViewportWidth;
        foreach (ref readonly var command in commands)
        {
            if (list.VtxBuffer.Size > VertexLimit)
            {
                if (usingForeground)
                {
                    Skipped++;
                    continue;
                }

                list = ImGui.GetForegroundDrawList();
                usingForeground = true;
            }

            if (command.Shape == DebugShape.Text)
            {
                continue; // the host's DebugTextRenderer draws text
            }

            if (command.Space == DebugSpace.World && camera is null)
            {
                Skipped++;
                continue; // no camera: nowhere to put world-space draws
            }

            var projector = new Projector(command.Space, camera, toLogical);
            var color = ImGui.GetColorU32(new SysVector4(command.Color.R, command.Color.G, command.Color.B, command.Color.A));
            var thickness = MathF.Max(1f, command.Thickness);
            switch (command.Shape)
            {
                case DebugShape.Line:
                    list.AddLine(projector.Point(command.A), projector.Point(command.B), color, thickness);
                    break;
                case DebugShape.Rect:
                    var p0 = projector.Point(command.A);
                    var p1 = projector.Point(new Mathematics.Vector2(command.B.X, command.A.Y));
                    var p2 = projector.Point(command.B);
                    var p3 = projector.Point(new Mathematics.Vector2(command.A.X, command.B.Y));
                    if (command.Filled)
                    {
                        list.AddQuadFilled(p0, p1, p2, p3, color);
                    }
                    else
                    {
                        list.AddQuad(p0, p1, p2, p3, color, thickness);
                    }

                    break;
                case DebugShape.Circle:
                    var center = projector.Point(command.A);
                    var radius = command.Space == DebugSpace.World ? (projector.Point(command.A + new Mathematics.Vector2(command.Size, 0f)) - center).Length() : command.Size;
                    var segments = Math.Clamp((int)(radius * 0.5f), 12, 64);
                    if (command.Filled)
                    {
                        list.AddCircleFilled(center, radius, color, segments);
                    }
                    else
                    {
                        list.AddCircle(center, radius, color, segments, thickness);
                    }

                    break;
                case DebugShape.Point:
                    list.AddCircleFilled(projector.Point(command.A), MathF.Max(1f, command.Size * 0.5f), color, 8);
                    break;
            }

            Drawn++;
        }
    }

    /// <summary>Screen-space positions pass through; world-space ones go through the camera and into logical pixels.</summary>
    private readonly struct Projector(DebugSpace space, Camera2D? camera, float toLogical)
    {
        public SysVector2 Point(Mathematics.Vector2 p)
        {
            if (space == DebugSpace.Screen)
            {
                return new SysVector2(p.X, p.Y);
            }

            var screen = camera!.WorldToScreen(p);
            return new SysVector2(screen.X * toLogical, screen.Y * toLogical);
        }
    }
}
