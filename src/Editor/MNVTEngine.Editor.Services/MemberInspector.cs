using System.Reflection;
using System.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Services;

public enum MemberKind
{
    Unsupported,
    Float,
    Int,
    Bool,
    String,
    AssetPath,
    Vector2,
    Color,
    Enum,
    GameObjectReference,
    ComponentReference,
}

/// <summary>A persisted component member as the inspector sees it: display name, kind, and safe get/set.</summary>
public sealed class InspectableMember
{
    private readonly MemberInfo _member;

    internal InspectableMember(MemberInfo member)
    {
        _member = member;
        Type = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
        DisplayName = Nicify(member.Name);
        AssetExtensions = member.GetCustomAttribute<AssetPathAttribute>()?.Extensions;
        Kind = Classify(Type, AssetExtensions is not null);
    }

    public string DisplayName { get; }

    public Type Type { get; }

    public MemberKind Kind { get; }

    /// <summary>Accepted extensions for <see cref="MemberKind.AssetPath"/>; empty means any file.</summary>
    public IReadOnlyList<string>? AssetExtensions { get; }

    public object? GetValue(Component component) =>
        _member is FieldInfo f ? f.GetValue(component) : ((PropertyInfo)_member).GetValue(component);

    /// <summary>Sets the value; property validation failures are returned as <paramref name="error"/> instead of thrown.</summary>
    public bool TrySetValue(Component component, object? value, out string? error)
    {
        error = null;
        try
        {
            if (_member is FieldInfo field)
            {
                field.SetValue(component, value);
            }
            else
            {
                ((PropertyInfo)_member).SetValue(component, value);
            }

            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException inner)
        {
            error = $"{DisplayName}: {inner.Message}";
            return false;
        }
        catch (ArgumentException ex)
        {
            error = $"{DisplayName}: {ex.Message}";
            return false;
        }
    }

    public bool AcceptsAsset(string relativePath) =>
        Kind == MemberKind.AssetPath && (AssetExtensions!.Count == 0 || AssetExtensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase));

    /// <summary>"orthographicSize" / "_secret" -> "Orthographic Size" / "Secret".</summary>
    public static string Nicify(string name)
    {
        var trimmed = name.TrimStart('_');
        var builder = new StringBuilder(trimmed.Length + 4);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (i == 0)
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else
            {
                if (char.IsUpper(c) && !char.IsUpper(trimmed[i - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static MemberKind Classify(Type type, bool isAssetPath) => type switch
    {
        _ when type == typeof(float) => MemberKind.Float,
        _ when type == typeof(int) => MemberKind.Int,
        _ when type == typeof(bool) => MemberKind.Bool,
        _ when type == typeof(string) => isAssetPath ? MemberKind.AssetPath : MemberKind.String,
        _ when type == typeof(Vector2) => MemberKind.Vector2,
        _ when type == typeof(Color) => MemberKind.Color,
        _ when type.IsEnum => MemberKind.Enum,
        _ when type == typeof(GameObject) => MemberKind.GameObjectReference,
        _ when typeof(Component).IsAssignableFrom(type) => MemberKind.ComponentReference,
        _ => MemberKind.Unsupported,
    };
}

public static class MemberInspector
{
    public static IReadOnlyList<InspectableMember> GetMembers(Type componentType) =>
        [.. ComponentTypeInfo.Get(componentType).SerializableMembers.Select(m => new InspectableMember(m))];

    /// <summary>
    /// Resolves a dragged GameObject into a value for a reference member: the object itself, or its component of the
    /// member's type. Null when the object has no such component.
    /// </summary>
    public static object? ResolveReference(InspectableMember member, GameObject dropped)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(dropped);
        return member.Kind switch
        {
            MemberKind.GameObjectReference => dropped,
            MemberKind.ComponentReference => dropped.Components.FirstOrDefault(c => !c.IsDestroyed && member.Type.IsInstanceOfType(c)),
            _ => null,
        };
    }
}
