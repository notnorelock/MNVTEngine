using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Tests;

/// <summary>A standalone context driven by a real <see cref="InputState"/>, one simulated frame at a time.</summary>
internal sealed class UIHarness : IDisposable
{
    private static readonly Lazy<Font> SharedFont = new(Font.LoadDefault);

    public UIHarness(float width = 800, float height = 600, float scale = 1f)
    {
        Size = new Vector2(width, height);
        Context = new UIContext(SharedFont.Value) { Scale = scale, Clipboard = Clipboard };
        Frame();
    }

    public UIContext Context { get; }

    public InputState Input { get; } = new();

    public MemoryClipboard Clipboard { get; } = new();

    public Vector2 Size { get; set; }

    public static Font Font => SharedFont.Value;

    /// <summary>Runs one UI frame with whatever input was set since the last one, then starts the next input frame.</summary>
    public void Frame(float dt = 1f / 60f)
    {
        Context.Update(Input.ForUI, Size, 1f, dt);
        Input.BeginFrame();
    }

    public void Move(Vector2 position)
    {
        Input.SetMousePosition(position);
        Frame();
    }

    public void Click(Vector2 position)
    {
        Input.SetMousePosition(position);
        Input.SetMouseButton(MouseButton.Left, true);
        Frame();
        Input.SetMouseButton(MouseButton.Left, false);
        Frame();
    }

    public void Click(UIElement element) => Click(element.Bounds.Center * Context.Scale);

    public void Press(Key key, bool control = false, bool shift = false)
    {
        if (control)
        {
            Input.SetKey(Key.ControlLeft, true);
        }

        if (shift)
        {
            Input.SetKey(Key.ShiftLeft, true);
        }

        Input.SetKey(key, true);
        Frame();
        Input.SetKey(key, false);
        Input.SetKey(Key.ControlLeft, false);
        Input.SetKey(Key.ShiftLeft, false);
        Frame();
    }

    public void Type(string text)
    {
        foreach (var c in text)
        {
            Input.AddText(c);
        }

        Frame();
    }

    public void Dispose() => Context.Dispose();
}

internal static class TestFonts
{
    public static string Path(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "tests", "TestAssets", "Fonts", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Test font {file} not found under tests/TestAssets/Fonts.");
    }
}
