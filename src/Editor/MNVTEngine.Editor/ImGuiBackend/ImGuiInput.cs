using ImGuiNET;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;

namespace MNVTEngine.Editor.ImGuiBackend;

/// <summary>Feeds the engine's per-frame input edges into ImGui's event queue.</summary>
internal static class ImGuiInput
{
    private static readonly (Key Key, ImGuiKey ImGui)[] KeyMap = BuildKeyMap();

    /// <summary>Engine keys with no ImGui equivalent; expected to be empty.</summary>
    public static IEnumerable<Key> UnmappedKeys => Enum.GetValues<Key>().Where(k => k != Key.Unknown && KeyMap.All(m => m.Key != k));

    public static void NewFrame(GameWindow window, float deltaTime)
    {
        var io = ImGui.GetIO();
        var input = window.Input;
        io.DisplaySize = new System.Numerics.Vector2(window.WindowWidth, window.WindowHeight);
        io.DisplayFramebufferScale = window.WindowWidth > 0
            ? new System.Numerics.Vector2((float)window.FramebufferWidth / window.WindowWidth, (float)window.FramebufferHeight / window.WindowHeight)
            : System.Numerics.Vector2.One;
        io.DeltaTime = MathF.Max(deltaTime, 1e-4f);

        io.AddMousePosEvent(input.MousePosition.X, input.MousePosition.Y);
        for (var i = 0; i < 3; i++)
        {
            var button = (MouseButton)i;
            if (input.WasMouseButtonPressed(button))
            {
                io.AddMouseButtonEvent(i, true);
            }

            if (input.WasMouseButtonReleased(button))
            {
                io.AddMouseButtonEvent(i, false);
            }
        }

        if (input.ScrollDelta != Vector2.Zero)
        {
            io.AddMouseWheelEvent(input.ScrollDelta.X, input.ScrollDelta.Y);
        }

        io.AddKeyEvent(ImGuiKey.ModCtrl, input.IsKeyDown(Key.ControlLeft) || input.IsKeyDown(Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, input.IsKeyDown(Key.ShiftLeft) || input.IsKeyDown(Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, input.IsKeyDown(Key.AltLeft) || input.IsKeyDown(Key.AltRight));

        foreach (var (key, imguiKey) in KeyMap)
        {
            if (input.WasKeyPressed(key))
            {
                io.AddKeyEvent(imguiKey, true);
            }

            if (input.WasKeyReleased(key))
            {
                io.AddKeyEvent(imguiKey, false);
            }
        }

        foreach (var c in input.TextInput)
        {
            io.AddInputCharacter(c);
        }
    }

    private static (Key, ImGuiKey)[] BuildKeyMap()
    {
        var renames = new Dictionary<string, string>
        {
            ["Left"] = "LeftArrow",
            ["Right"] = "RightArrow",
            ["Up"] = "UpArrow",
            ["Down"] = "DownArrow",
            ["ShiftLeft"] = "LeftShift",
            ["ShiftRight"] = "RightShift",
            ["ControlLeft"] = "LeftCtrl",
            ["ControlRight"] = "RightCtrl",
            ["AltLeft"] = "LeftAlt",
            ["AltRight"] = "RightAlt",
            ["BackSlash"] = "Backslash",
        };

        var map = new List<(Key, ImGuiKey)>();
        foreach (var key in Enum.GetValues<Key>())
        {
            var name = key.ToString();
            name = renames.GetValueOrDefault(name) ?? (name.StartsWith("Number", StringComparison.Ordinal) ? "_" + name[^1] : name);
            if (key != Key.Unknown && Enum.TryParse<ImGuiKey>(name, out var imguiKey))
            {
                map.Add((key, imguiKey));
            }
        }

        return [.. map];
    }
}
