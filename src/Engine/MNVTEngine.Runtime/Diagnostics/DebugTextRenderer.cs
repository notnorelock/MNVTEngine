using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime;

/// <summary>
/// Draws the text of <c>Debug.DrawText</c>, <c>DrawWorldText</c> and <c>DrawLabel</c> with the engine's text stack (the
/// built-in font through a <see cref="GlyphAtlas"/> and the sprite batch), above the game and its UI and below the debug
/// windows. Hosts call it after the UI; the debug overlay draws the other debug shapes.
/// </summary>
public sealed class DebugTextRenderer(Renderer2D renderer) : IDisposable
{
    private readonly GlyphAtlas _atlas = new(renderer.Device);
    private Font? _font;
    private Texture2D? _white;

    /// <summary>Texts drawn by the last <see cref="Render"/>.</summary>
    public int Drawn { get; private set; }

    /// <summary>Draws the text commands into <paramref name="target"/> (null: the back buffer) of <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
    /// <param name="sceneCamera">The scene's view, for world-space text; without it world-space text is skipped.</param>
    public void Render(ReadOnlySpan<DebugDrawCommand> commands, Camera2D? sceneCamera, int width, int height, RenderTarget2D? target = null)
    {
        Drawn = 0;
        if (width <= 0 || height <= 0 || !Contains(commands))
        {
            return;
        }

        _font ??= Font.LoadDefault();
        _white ??= renderer.Device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp, "DebugText.White");
        var camera = new Camera2D(width, height) { Position = new Vector2(width / 2f, height / 2f) };
        camera.UsePixelUnits(); // world units are pixels, origin bottom-left
        var batch = renderer.Begin(camera, target: target);
        foreach (ref readonly var command in commands)
        {
            if (command.Shape != DebugShape.Text || command.Text is not { Length: > 0 } text)
            {
                continue;
            }

            Vector2 anchor;
            if (command.Space == DebugSpace.Screen)
            {
                anchor = command.A;
            }
            else if (sceneCamera is not null)
            {
                anchor = sceneCamera.WorldToScreen(command.A);
            }
            else
            {
                continue; // no camera: nowhere to put world-space text
            }

            var layout = TextLayout.Create(text, new TextStyle(_font, MathF.Max(1f, command.Size)));
            var x = command.Alignment switch
            {
                TextAlignment.Center => anchor.X - (layout.Size.X * 0.5f),
                TextAlignment.Right => anchor.X - layout.Size.X,
                _ => anchor.X,
            };
            var top = command.Space == DebugSpace.World ? anchor.Y - layout.Size.Y : anchor.Y; // world text sits above its point
            if (command.Background.A > 0f)
            {
                batch.Draw(_white, new Vector2(x - 3f, height - (top + layout.Size.Y + 2f)), layout.Size + new Vector2(6f, 4f), command.Background, 0f, Vector2.Zero);
            }

            batch.DrawText(_atlas, layout, new Vector2(MathF.Round(x), height - MathF.Round(top)), command.Color);
            Drawn++;
        }

        _atlas.Flush(); // glyphs rasterized above reach their pages before the batch draws
        renderer.End();
    }

    private static bool Contains(ReadOnlySpan<DebugDrawCommand> commands)
    {
        foreach (ref readonly var command in commands)
        {
            if (command.Shape == DebugShape.Text)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _atlas.Dispose();
        _white?.Dispose();
        _font?.Dispose();
    }
}
