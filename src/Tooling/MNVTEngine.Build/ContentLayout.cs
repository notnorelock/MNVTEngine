using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build;

/// <summary>
/// Decides which content package every asset goes to (Unity-style data layout):
/// <list type="bullet">
/// <item><c>levelN</c>: a scene and the assets only that scene uses. Level 0 is the startup scene; the other scenes
/// follow in path order.</item>
/// <item><c>sharedassets</c>: assets used by two or more scenes.</item>
/// <item><c>resources</c>: assets no scene references. They are loaded by path or GUID at run time: prefabs spawned
/// from code, scenes loaded by name, unused files.</item>
/// </list>
/// A scene's dependencies are followed transitively but stop at other scenes: a scene that references the next level
/// does not pull that level's assets into its own package.
/// </summary>
public sealed class ContentLayout
{
    private readonly Dictionary<Guid, string> _packageOf = [];

    private ContentLayout(IReadOnlyList<ContentPackage> levels) => Levels = levels;

    /// <summary>One level per scene, in level order.</summary>
    public IReadOnlyList<ContentPackage> Levels { get; }

    /// <param name="dependencies">Assets each asset references directly (from its importer).</param>
    /// <param name="include">The assets the game ships (see <see cref="UsageAnalyzer"/>); null for every asset.</param>
    public static ContentLayout Plan(AssetDatabase assets, string startupScene, IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> dependencies, Func<Guid, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(assets);
        include ??= _ => true;
        var startup = AssetPaths.Canonical(startupScene);
        var scenes = assets.Assets.Where(a => a.Type == AssetTypes.Scene && include(a.Guid))
            .OrderBy(a => AssetPaths.Comparer.Equals(a.Path, startup) ? 0 : 1)
            .ThenBy(a => a.Path, StringComparer.Ordinal)
            .ToList();

        var layout = new ContentLayout([.. scenes.Select((s, i) => new ContentPackage(GamePackage.LevelPackage(i), ContentPackageKinds.Level, s.Path))]);
        var users = new Dictionary<Guid, List<int>>();
        for (var level = 0; level < scenes.Count; level++)
        {
            layout._packageOf[scenes[level].Guid] = GamePackage.LevelPackage(level);
            foreach (var guid in SceneDependencies(assets, scenes[level].Guid, dependencies))
            {
                if (!users.TryGetValue(guid, out var list))
                {
                    users[guid] = list = [];
                }

                list.Add(level);
            }
        }

        foreach (var record in assets.Assets.Where(a => include(a.Guid)))
        {
            if (layout._packageOf.ContainsKey(record.Guid))
            {
                continue; // a scene: always its own level
            }

            layout._packageOf[record.Guid] = users.TryGetValue(record.Guid, out var levels)
                ? levels.Count == 1 ? GamePackage.LevelPackage(levels[0]) : GamePackage.SharedAssetsPackage
                : GamePackage.ResourcesPackage;
        }

        return layout;
    }

    /// <summary>Package file the asset is written to.</summary>
    public string PackageOf(Guid guid) => _packageOf.GetValueOrDefault(guid) ?? throw new KeyNotFoundException($"Asset {guid} is not shipped.");

    /// <summary>Everything a scene references, transitively, without crossing into other scenes; excludes the scene itself.</summary>
    private static HashSet<Guid> SceneDependencies(AssetDatabase assets, Guid scene, IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> dependencies)
    {
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>(dependencies.GetValueOrDefault(scene) ?? []);
        while (stack.TryPop(out var guid))
        {
            if (guid == scene || assets.Get(guid) is not { } record || record.Type == AssetTypes.Scene || !result.Add(guid))
            {
                continue;
            }

            foreach (var dependency in dependencies.GetValueOrDefault(guid) ?? [])
            {
                stack.Push(dependency);
            }
        }

        return result;
    }
}
