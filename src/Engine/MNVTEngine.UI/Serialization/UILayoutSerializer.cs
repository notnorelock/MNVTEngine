using System.Globalization;
using System.Reflection;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using MNVTEngine.UI.Widgets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.UI;

/// <summary>
/// Reads and writes UI layouts (<c>.mnvtui</c>, JSON, schema version 1):
/// <code>
/// { "schemaVersion": 1, "root": { "type": "Panel", "name": "Menu", "properties": { "width": "50%", "layout": "column" },
///   "style": { "background": { "r": 0, "g": 0, "b": 0, "a": 0.8 } }, "states": { "hover": { ... } }, "children": [ ... ] } }
/// </code>
/// <list type="bullet">
/// <item><b>Widget types.</b> They come from an explicit registry (<see cref="Register{T}"/>); the built-in widgets
/// are registered.</item>
/// <item><b>Properties.</b> The public settable properties of supported types: text, numbers, booleans, enums, units
/// ("120", "50%", "auto"), thickness (a number or [left, top, right, bottom]), vectors, colors, asset references
/// ("asset:&lt;guid&gt;") and string lists.</item>
/// <item><b>Fonts.</b> They are font asset references, resolved through a callback.</item>
/// <item><b>Behaviour.</b> It is bound by <see cref="UIElement.Action"/> names, never by serialized code.</item>
/// <item><b>Errors.</b> Unknown types, unknown properties and bad values are errors with their JSON path. Saving writes
/// only values that differ from a new element of the type.</item>
/// </list>
/// </summary>
public static class UILayoutSerializer
{
    public const string Extension = ".mnvtui";
    public const int SchemaVersion = 1;
    private static readonly Dictionary<string, Func<UIElement>> Types = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { nameof(UIElement.Name), "Font", "Text", "Placeholder" };

    static UILayoutSerializer()
    {
        Register<UIElement>();
        Register<Panel>();
        Register<Label>();
        Register<Button>();
        Register<Image>();
        Register<Checkbox>();
        Register<Toggle>();
        Register<RadioButton>();
        Register<Slider>();
        Register<ProgressBar>();
        Register<ScrollView>();
        Register<Dropdown>();
        Register<TextField>();
        Register<TextArea>();
        Register<Separator>();
    }

    /// <summary>Makes a widget type loadable by its class name (game widgets too).</summary>
    public static void Register<T>()
        where T : UIElement, new() => Types[typeof(T).Name] = () => new T();

    /// <exception cref="SerializationException">Invalid JSON, unknown types or properties, bad values, newer schema.</exception>
    public static UIElement Load(string json, string sourceName, Func<AssetRef, Font?>? fonts = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var diagnostics = new List<SerializationDiagnostic>();
        JObject document;
        try
        {
            document = JObject.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SerializationException(sourceName, ex.Message, [], ex);
        }

        var version = document.Value<int?>("schemaVersion");
        if (version is null or < 1 or > SchemaVersion)
        {
            throw new SerializationException(sourceName, $"UI layout schema version {version?.ToString(CultureInfo.InvariantCulture) ?? "missing"} is not supported (this engine reads {SchemaVersion}).", []);
        }

        if (document["root"] is not JObject root)
        {
            throw new SerializationException(sourceName, "A UI layout needs a \"root\" element.", []);
        }

        var element = Read(root, "root", diagnostics, fonts);
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || element is null)
        {
            throw new SerializationException(sourceName, string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Path}: {d.Message}")), diagnostics);
        }

        return element;
    }

    /// <param name="fontRefs">The asset reference of a font, for writing; null writes no fonts.</param>
    public static string Save(UIElement root, Func<Font, AssetRef?>? fontRefs = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new JObject { ["schemaVersion"] = SchemaVersion, ["root"] = Write(root, fontRefs) }.ToString(Formatting.Indented);
    }

    private static UIElement? Read(JObject item, string path, List<SerializationDiagnostic> diagnostics, Func<AssetRef, Font?>? fonts)
    {
        void Error(string at, string message) => diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, at, message));
        var typeName = item.Value<string>("type") ?? "Panel";
        if (!Types.TryGetValue(typeName, out var create))
        {
            Error(path + ".type", $"Unknown widget type '{typeName}'. Known: {string.Join(", ", Types.Keys.Order(StringComparer.Ordinal))}.");
            return null;
        }

        var element = create();
        element.Name = item.Value<string>("name") ?? "";
        foreach (var property in item.Properties())
        {
            if (property.Name is not ("type" or "name" or "properties" or "style" or "states" or "children" or "text" or "placeholder" or "font"))
            {
                Error($"{path}.{property.Name}", "Unknown key (expected type, name, text, placeholder, font, properties, style, states, children).");
            }
        }

        SetText(element, item, path, Error);
        if (item.Value<string>("font") is { } font)
        {
            if (fonts is null || !AssetRefText.TryParse(font, out var asset) || fonts(asset) is not { } loaded)
            {
                Error(path + ".font", $"Font '{font}' could not be loaded.");
            }
            else
            {
                element.Font = loaded;
            }
        }

        if (item["properties"] is JObject properties)
        {
            foreach (var property in properties.Properties())
            {
                var info = FindProperty(element.GetType(), property.Name);
                if (info is null)
                {
                    Error($"{path}.properties.{property.Name}", $"{typeName} has no settable property '{property.Name}'.");
                    continue;
                }

                if (info.PropertyType == typeof(List<string>) && info.GetValue(element) is List<string> list && property.Value is JArray strings)
                {
                    list.AddRange(strings.Values<string>().OfType<string>());
                    continue;
                }

                if (TryConvert(property.Value, info.PropertyType, out var value, out var problem))
                {
                    info.SetValue(element, value);
                }
                else
                {
                    Error($"{path}.properties.{property.Name}", problem);
                }
            }
        }

        if (item["style"] is JObject style)
        {
            ReadStyle(element.Style, style, path + ".style", Error);
        }

        if (item["states"] is JObject states)
        {
            foreach (var state in states.Properties())
            {
                if (!Enum.TryParse<UIState>(state.Value is JObject ? state.Name : "", ignoreCase: true, out var flag) || flag == UIState.Normal)
                {
                    Error($"{path}.states.{state.Name}", "Unknown state (hover, pressed, focused, disabled, checked).");
                    continue;
                }

                var own = new UIStyle();
                ReadStyle(own, (JObject)state.Value, $"{path}.states.{state.Name}", Error);
                element.StateStyles[flag] = own;
            }
        }

        if (item["children"] is JArray children)
        {
            var parent = element is ScrollView scroll ? scroll.Content : element;
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is JObject child && Read(child, $"{path}.children[{i}]", diagnostics, fonts) is { } loaded)
                {
                    parent.AddChild(loaded);
                }
            }
        }

        return element;
    }

    private static JObject Write(UIElement element, Func<Font, AssetRef?>? fontRefs)
    {
        var type = element.GetType();
        var defaults = Types.TryGetValue(type.Name, out var create) ? create() : throw new InvalidOperationException($"Widget type {type.Name} is not registered for serialization.");
        var item = new JObject { ["type"] = type.Name };
        if (element.Name.Length > 0)
        {
            item["name"] = element.Name;
        }

        if (TextProperty(type) is { } text && text.GetValue(element) is string { Length: > 0 } value)
        {
            item["text"] = value;
        }

        if (type.GetProperty("Placeholder") is { } placeholder && placeholder.GetValue(element) is string { Length: > 0 } hint && hint != placeholder.GetValue(defaults) as string)
        {
            item["placeholder"] = hint;
        }

        if (element.Style.Font is { } font && fontRefs?.Invoke(font) is { } fontRef)
        {
            item["font"] = AssetRefText.Format(fontRef);
        }

        var properties = new JObject();
        foreach (var info in SerializableProperties(type))
        {
            var current = info.GetValue(element);
            if (info.PropertyType == typeof(List<string>))
            {
                if (current is List<string> { Count: > 0 } list)
                {
                    properties[Camel(info.Name)] = new JArray(list);
                }

                continue;
            }

            if (!Equals(current, info.GetValue(defaults)))
            {
                properties[Camel(info.Name)] = ToJson(current);
            }
        }

        if (properties.Count > 0)
        {
            item["properties"] = properties;
        }

        if (WriteStyle(element.Style) is { Count: > 0 } style)
        {
            item["style"] = style;
        }

        if (element.StateStyles.Count > 0)
        {
            item["states"] = new JObject(element.StateStyles.Select(p => new JProperty(Camel(p.Key.ToString()), WriteStyle(p.Value))));
        }

        var children = (element is ScrollView scroll ? scroll.Content.Children : element.Children).Select(c => Write(c, fontRefs)).ToList();
        if (children.Count > 0)
        {
            item["children"] = new JArray(children);
        }

        return item;
    }

    private static void SetText(UIElement element, JObject item, string path, Action<string, string> error)
    {
        foreach (var key in (string[])["text", "placeholder"])
        {
            if (item.Value<string>(key) is not { } value)
            {
                continue;
            }

            var info = key == "text" ? TextProperty(element.GetType()) : element.GetType().GetProperty("Placeholder");
            if (info is null || !info.CanWrite)
            {
                error($"{path}.{key}", $"{element.GetType().Name} has no {key}.");
            }
            else
            {
                info.SetValue(element, value);
            }
        }
    }

    private static PropertyInfo? TextProperty(Type type) => type.GetProperty("Text", BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true, PropertyType: var t } p && t == typeof(string) ? p : null;

    private static IEnumerable<PropertyInfo> SerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !Skipped.Contains(p.Name) && p.DeclaringType != typeof(object) && IsSupported(p))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    private static bool IsSupported(PropertyInfo p) =>
        (p.CanWrite && p.SetMethod!.IsPublic && IsSupportedType(p.PropertyType) && !IsStyleShortcut(p)) || (p.PropertyType == typeof(List<string>) && p.CanRead);

    /// <summary>BackgroundColor, Padding... live in "style"; they are written there, not twice.</summary>
    private static bool IsStyleShortcut(PropertyInfo p) =>
        p.DeclaringType == typeof(UIElement) && p.Name is nameof(UIElement.BackgroundColor) or nameof(UIElement.TextColor) or nameof(UIElement.BorderColor)
            or nameof(UIElement.BorderWidth) or nameof(UIElement.CornerRadius) or nameof(UIElement.Padding) or nameof(UIElement.FontSize) or nameof(UIElement.Opacity) or nameof(UIElement.Font);

    private static bool IsSupportedType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) || t == typeof(bool) || t == typeof(float) || t == typeof(int) || t.IsEnum || t == typeof(UIUnit) || t == typeof(Thickness)
            || t == typeof(Vector2) || t == typeof(Color) || t == typeof(AssetRef);
    }

    private static PropertyInfo? FindProperty(Type type, string name) =>
        SerializableProperties(type).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryConvert(JToken token, Type type, out object? value, out string problem)
    {
        problem = "";
        value = null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (token.Type == JTokenType.Null && (Nullable.GetUnderlyingType(type) is not null || !type.IsValueType))
        {
            return true;
        }

        try
        {
            value = target switch
            {
                _ when target == typeof(string) => token.Value<string>(),
                _ when target == typeof(bool) => token.Value<bool>(),
                _ when target == typeof(float) => token.Value<float>(),
                _ when target == typeof(int) => token.Value<int>(),
                _ when target.IsEnum => Enum.Parse(target, token.Value<string>()!, ignoreCase: true),
                _ when target == typeof(UIUnit) => token.Type is JTokenType.Integer or JTokenType.Float ? UIUnit.Pixels(token.Value<float>()) : UIUnit.Parse(token.Value<string>()!),
                _ when target == typeof(Thickness) => token is JArray a && a.Count == 4 ? new Thickness(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), a[3].Value<float>()) : Thickness.All(token.Value<float>()),
                _ when target == typeof(Vector2) => new Vector2(token.Value<float>("x"), token.Value<float>("y")),
                _ when target == typeof(Color) => new Color(token.Value<float>("r"), token.Value<float>("g"), token.Value<float>("b"), token["a"]?.Value<float>() ?? 1f),
                _ when target == typeof(AssetRef) => AssetRefText.TryParse(token.Value<string>(), out var asset) ? asset : throw new FormatException($"'{token}' is not an asset reference (asset:<guid>)."),
                _ => throw new FormatException($"Type {target.Name} is not serializable."),
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or JsonException or OverflowException or NullReferenceException)
        {
            problem = $"Expected {target.Name}: {ex.Message}";
            return false;
        }
    }

    private static JToken ToJson(object? value) => value switch
    {
        null => JValue.CreateNull(),
        UIUnit unit => unit.ToString(),
        Thickness t => new JArray(t.Left, t.Top, t.Right, t.Bottom),
        Vector2 v => new JObject { ["x"] = v.X, ["y"] = v.Y },
        Color c => new JObject { ["r"] = c.R, ["g"] = c.G, ["b"] = c.B, ["a"] = c.A },
        AssetRef a => AssetRefText.Format(a),
        Enum e => Camel(e.ToString()),
        _ => JToken.FromObject(value),
    };

    private static readonly (string Name, Type Type, Func<UIStyle, object?> Get, Action<UIStyle, object?> Set)[] StyleFields =
    [
        ("background", typeof(Color?), s => s.Background, (s, v) => s.Background = (Color?)v),
        ("foreground", typeof(Color?), s => s.Foreground, (s, v) => s.Foreground = (Color?)v),
        ("borderColor", typeof(Color?), s => s.BorderColor, (s, v) => s.BorderColor = (Color?)v),
        ("borderWidth", typeof(float?), s => s.BorderWidth, (s, v) => s.BorderWidth = (float?)v),
        ("cornerRadius", typeof(float?), s => s.CornerRadius, (s, v) => s.CornerRadius = (float?)v),
        ("accent", typeof(Color?), s => s.Accent, (s, v) => s.Accent = (Color?)v),
        ("fontSize", typeof(float?), s => s.FontSize, (s, v) => s.FontSize = (float?)v),
        ("padding", typeof(Thickness?), s => s.Padding, (s, v) => s.Padding = (Thickness?)v),
        ("opacity", typeof(float?), s => s.Opacity, (s, v) => s.Opacity = (float?)v),
        ("shadow", typeof(Color?), s => s.Shadow, (s, v) => s.Shadow = (Color?)v),
        ("shadowOffset", typeof(Vector2?), s => s.ShadowOffset, (s, v) => s.ShadowOffset = (Vector2?)v),
    ];

    private static void ReadStyle(UIStyle style, JObject json, string path, Action<string, string> error)
    {
        foreach (var property in json.Properties())
        {
            var field = StyleFields.FirstOrDefault(f => f.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
            if (field.Name is null)
            {
                error($"{path}.{property.Name}", $"Unknown style property (known: {string.Join(", ", StyleFields.Select(f => f.Name))}).");
            }
            else if (TryConvert(property.Value, field.Type, out var value, out var problem))
            {
                field.Set(style, value);
            }
            else
            {
                error($"{path}.{property.Name}", problem);
            }
        }
    }

    private static JObject WriteStyle(UIStyle style) =>
        new(StyleFields.Where(f => f.Get(style) is not null).Select(f => new JProperty(f.Name, ToJson(f.Get(style)))));

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>"asset:&lt;guid&gt;" text form of <see cref="AssetRef"/>.</summary>
internal static class AssetRefText
{
    public static string Format(AssetRef asset) => AssetRef.Prefix + asset.Guid.ToString("D");

    public static bool TryParse(string? text, out AssetRef asset)
    {
        asset = default;
        if (text is null || !text.StartsWith(AssetRef.Prefix, StringComparison.Ordinal) || !Guid.TryParse(text.AsSpan(AssetRef.Prefix.Length), out var guid))
        {
            return false;
        }

        asset = new AssetRef(guid);
        return true;
    }
}
