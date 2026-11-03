using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace MNVTEngine.GameAssemblies;

/// <summary>
/// Releases process-wide reflection caches that would otherwise keep unloaded game types (and so their whole load
/// context) alive. Newtonsoft.Json caches reflection results in static <c>ThreadSafeStore</c> instances keyed by
/// <see cref="Type"/>/<see cref="MemberInfo"/> and offers no API to clear them, so they are cleared via reflection;
/// they repopulate on demand. If a future Newtonsoft version renames these internals, clearing reports zero stores and
/// unload verification (see <see cref="GameAssemblyHost.IsCollected"/>) shows the leak instead of failing silently.
/// </summary>
public static class ReflectionCacheCleaner
{
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Lazy<IReadOnlyList<FieldInfo>> NewtonsoftStores = new(FindNewtonsoftStores);

    /// <summary>Number of Newtonsoft cache stores found; zero means this Newtonsoft version is not supported by the cleaner.</summary>
    public static int KnownStoreCount => NewtonsoftStores.Value.Count;

    /// <summary>Clears serializer caches and component-model metadata for <paramref name="assemblies"/>. Returns the number of stores cleared.</summary>
    public static int Release(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            TypeDescriptor.Refresh(assembly);
        }

        var cleared = 0;
        foreach (var field in NewtonsoftStores.Value)
        {
            var store = field.GetValue(null);
            if (store?.GetType().GetField("_concurrentStore", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(store) is IDictionary dictionary)
            {
                dictionary.Clear();
                cleared++;
            }
        }

        return cleared;
    }

    private static List<FieldInfo> FindNewtonsoftStores()
    {
        var assembly = typeof(JsonConvert).Assembly;
        Type[] attributeCandidates =
        [
            .. assembly.GetTypes().Where(t => typeof(Attribute).IsAssignableFrom(t)), // abstract too: JsonContainerAttribute is queried
            typeof(DataContractAttribute), typeof(DataMemberAttribute), typeof(EnumMemberAttribute),
            typeof(IgnoreDataMemberAttribute), typeof(NonSerializedAttribute), typeof(SerializableAttribute),
        ];

        var fields = new List<FieldInfo>();
        foreach (var type in assembly.GetTypes())
        {
            // Generic caches such as CachedAttributeGetter<T> have one static store per closed type.
            var closedTypes = type.IsGenericTypeDefinition && type.GetGenericArguments().Length == 1
                ? attributeCandidates.Select(a => TryClose(type, a)).OfType<Type>()
                : type.IsGenericTypeDefinition ? [] : [type];

            foreach (var closed in closedTypes)
            {
                fields.AddRange(closed.GetFields(StaticFields).Where(f => f.FieldType.IsGenericType && f.FieldType.Name.StartsWith("ThreadSafeStore", StringComparison.Ordinal)));
            }
        }

        return fields;
    }

    private static Type? TryClose(Type definition, Type argument)
    {
        try
        {
            return definition.MakeGenericType(argument);
        }
        catch (ArgumentException)
        {
            return null; // constraint not satisfied
        }
    }
}
