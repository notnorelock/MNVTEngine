using System.Collections.Concurrent;
using System.Reflection;

namespace MNVTEngine.Scenes;

/// <summary>Per-type facts computed once by reflection: which update callbacks are overridden and which members persist.</summary>
public sealed class ComponentTypeInfo
{
    private static readonly ConcurrentDictionary<Type, ComponentTypeInfo> Cache = new();

    private ComponentTypeInfo(Type type)
    {
        Type = type;
        HasUpdate = Overrides(type, nameof(Component.Update));
        HasFixedUpdate = Overrides(type, nameof(Component.FixedUpdate));
        HasLateUpdate = Overrides(type, nameof(Component.LateUpdate));
        SerializableMembers = FindSerializableMembers(type);
    }

    public Type Type { get; }

    public bool HasUpdate { get; }

    public bool HasFixedUpdate { get; }

    public bool HasLateUpdate { get; }

    /// <summary>
    /// Public instance fields (unless <see cref="DontSerializeAttribute"/>), plus non-public fields and read-write
    /// properties marked <see cref="SerializeFieldAttribute"/>. Members of <see cref="Component"/> itself are excluded.
    /// </summary>
    public IReadOnlyList<MemberInfo> SerializableMembers { get; }

    // shortcut: types are cached for the process lifetime; collectible game assemblies (Milestone 5) must evict on unload.
    public static ComponentTypeInfo Get(Type type) => Cache.GetOrAdd(type, static t => new ComponentTypeInfo(t));

    /// <summary>Name used in JSON: camelCase, without a leading underscore.</summary>
    public static string JsonName(MemberInfo member)
    {
        var name = member.Name.TrimStart('_');
        return name.Length == 0 ? member.Name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static bool Overrides(Type type, string method) =>
        type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)?.DeclaringType != typeof(Component);

    private static List<MemberInfo> FindSerializableMembers(Type type)
    {
        var members = new List<MemberInfo>();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // Walk base-first so inherited members precede derived ones in the file.
        var chain = new Stack<Type>();
        for (var t = type; t is not null && t != typeof(Component); t = t.BaseType)
        {
            chain.Push(t);
        }

        foreach (var t in chain)
        {
            foreach (var field in t.GetFields(Flags))
            {
                var marked = field.IsDefined(typeof(SerializeFieldAttribute));
                if (field.IsInitOnly || field.IsDefined(typeof(DontSerializeAttribute)) || field.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
                {
                    continue;
                }

                if (field.IsPublic || marked)
                {
                    members.Add(field);
                }
            }

            foreach (var property in t.GetProperties(Flags))
            {
                if (property.IsDefined(typeof(SerializeFieldAttribute)) && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    members.Add(property);
                }
            }
        }

        return members;
    }
}
