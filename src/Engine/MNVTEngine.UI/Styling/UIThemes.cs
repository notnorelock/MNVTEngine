using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>The built-in themes. Each call returns a new instance that can be modified freely.</summary>
internal static class UIThemes
{
    public static UITheme Dark() => Build("Dark", new Palette(
        Text: new Color(0.92f, 0.93f, 0.95f, 1f),
        Muted: new Color(0.55f, 0.57f, 0.62f, 1f),
        Panel: new Color(0.10f, 0.11f, 0.13f, 0.96f),
        Control: new Color(0.18f, 0.19f, 0.22f, 1f),
        ControlHover: new Color(0.24f, 0.25f, 0.29f, 1f),
        ControlPressed: new Color(0.13f, 0.14f, 0.16f, 1f),
        Field: new Color(0.07f, 0.075f, 0.09f, 1f),
        Border: new Color(0.30f, 0.32f, 0.36f, 1f),
        Accent: new Color(0.27f, 0.55f, 1f, 1f)));

    public static UITheme Light() => Build("Light", new Palette(
        Text: new Color(0.10f, 0.11f, 0.13f, 1f),
        Muted: new Color(0.42f, 0.44f, 0.48f, 1f),
        Panel: new Color(0.97f, 0.97f, 0.98f, 0.98f),
        Control: new Color(0.90f, 0.91f, 0.93f, 1f),
        ControlHover: new Color(0.84f, 0.86f, 0.89f, 1f),
        ControlPressed: new Color(0.78f, 0.80f, 0.84f, 1f),
        Field: new Color(1f, 1f, 1f, 1f),
        Border: new Color(0.70f, 0.72f, 0.76f, 1f),
        Accent: new Color(0.13f, 0.43f, 0.95f, 1f)));

    private static UITheme Build(string name, Palette p)
    {
        var theme = new UITheme(name);
        theme["UIElement"] = new UIStyle { Foreground = p.Text, Accent = p.Accent, BorderColor = p.Border, Opacity = 1f };
        theme["Panel"] = new UIStyle { Background = Color.Clear };
        theme[".panel"] = new UIStyle { Background = p.Panel, CornerRadius = 8f, Padding = 12f, BorderColor = p.Border, BorderWidth = 1f };
        theme["Label"] = new UIStyle { Background = Color.Clear };
        theme["Separator"] = new UIStyle { Background = p.Border };
        theme["Tooltip"] = new UIStyle { Background = new Color(0.05f, 0.05f, 0.06f, 0.95f), Foreground = p.Text, CornerRadius = 4f, Padding = Thickness.Symmetric(8f, 4f), FontSize = 15f };

        foreach (var control in (string[])["Button", "Dropdown", "Checkbox", "RadioButton", "Toggle"])
        {
            theme[control] = new UIStyle { Background = p.Control, BorderColor = p.Border, BorderWidth = 1f, CornerRadius = 6f, Padding = Thickness.Symmetric(12f, 6f) };
            theme[control + ":hover"] = new UIStyle { Background = p.ControlHover };
            theme[control + ":pressed"] = new UIStyle { Background = p.ControlPressed };
            theme[control + ":focused"] = new UIStyle { BorderColor = p.Accent };
            theme[control + ":disabled"] = new UIStyle { Foreground = p.Muted, Opacity = 0.6f };
        }

        theme["Checkbox"].Padding = Thickness.Zero;
        theme["RadioButton"].Padding = Thickness.Zero;
        theme["Checkbox"].Background = theme["RadioButton"].Background = Color.Clear;
        theme["Checkbox"].BorderWidth = theme["RadioButton"].BorderWidth = 0f;
        theme["Toggle"].Padding = Thickness.Zero;
        theme["Toggle"].Background = Color.Clear;
        theme["Toggle"].BorderWidth = 0f;
        theme[".primary"] = new UIStyle { Background = p.Accent, Foreground = Color.White, BorderColor = p.Accent };
        theme[".primary:hover"] = new UIStyle { Background = Lighten(p.Accent, 0.12f) };
        theme[".primary:pressed"] = new UIStyle { Background = Lighten(p.Accent, -0.15f) };

        foreach (var field in (string[])["TextField", "TextArea"])
        {
            theme[field] = new UIStyle { Background = p.Field, BorderColor = p.Border, BorderWidth = 1f, CornerRadius = 4f, Padding = Thickness.Symmetric(8f, 5f) };
            theme[field + ":hover"] = new UIStyle { BorderColor = Lighten(p.Border, 0.15f) };
            theme[field + ":focused"] = new UIStyle { BorderColor = p.Accent };
            theme[field + ":disabled"] = new UIStyle { Foreground = p.Muted, Opacity = 0.6f };
        }

        theme["Slider"] = new UIStyle { Background = p.Control, CornerRadius = 3f };
        theme["Slider:hover"] = new UIStyle { Background = p.ControlHover };
        theme["ProgressBar"] = new UIStyle { Background = p.Control, CornerRadius = 4f };
        theme["ScrollView"] = new UIStyle { Background = Color.Clear, Accent = new Color(p.Muted.R, p.Muted.G, p.Muted.B, 0.6f) };
        theme["DropdownList"] = new UIStyle { Background = p.Field, BorderColor = p.Border, BorderWidth = 1f, CornerRadius = 6f, Padding = 4f };
        theme["DropdownItem"] = new UIStyle { Background = Color.Clear, CornerRadius = 4f, Padding = Thickness.Symmetric(8f, 4f) };
        theme["DropdownItem:hover"] = new UIStyle { Background = p.ControlHover };
        return theme;
    }

    private static Color Lighten(Color c, float amount) =>
        new(Math.Clamp(c.R + amount, 0f, 1f), Math.Clamp(c.G + amount, 0f, 1f), Math.Clamp(c.B + amount, 0f, 1f), c.A);

    private sealed record Palette(Color Text, Color Muted, Color Panel, Color Control, Color ControlHover, Color ControlPressed, Color Field, Color Border, Color Accent);
}
