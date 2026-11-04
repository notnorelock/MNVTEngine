using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build;

/// <summary>
/// Decides which content archive every asset goes to (Unity-style data layout):
/// <list type="bullet">
/// <item><c>levelN</c>: a scene and the assets only that scene uses. Level 0 is the startup scene; the other scenes
/// follow in path order.</item>
/// <item><c>sharedassets</c>: assets used by two or more scenes.</item>
/// <item><c>resources</c>: assets no scene references. They are loaded by path or GUID at run time: prefabs spawned
/// from code, scenes loaded by name, unused files.</item>
/// </list>
/// A scene's dependencies are followed transitively but stop at other scenes: a scene that references the next level
/// does not pull that level's assets into its own archive.
/// </summary>
public sealed class ContentLayout
{
    private readonly Dictionary<Guid, string> _archiveOf = [];

    private ContentLayout(IReadOnlyList<PackagedArchive> levels) => Levels = levels;

    /// <summary>One level per scene, in level order.</summary>
    public IReadOnlyList<PackagedArchive> Levels { get; }

    /// <param name="dependencies">Assets each asset references directly (from its importer).</param>
    public static ContentLayout Plan(AssetDatabase assets, string startupScene, IReadOnlyDictionary<Guid, IReadOnlyCollection<Guid>> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(assets);
        var startup = AssetPaths.Canonical(startupScene);
        var scenes = assets.Assets.Where(a => a.Type == AssetTypes.Scene)
            .OrderBy(a => AssetPaths.Comparer.Equals(a.Path, startup) ? 0 : 1)
            .ThenBy(a => a.Path, StringComparer.Ordinal)
            .ToList();

        var layout = new ContentLayout([.. scenes.Select((s, i) => new PackagedArchive(GamePackage.LevelArchive(i), ContentArchiveKinds.Level, s.Path))]);
        var users = new Dictionary<Guid, List<int>>();
        for (var level = 0; level < scenes.Count; level++)
        {
            layout._archiveOf[scenes[level].Guid] = GamePackage.LevelArchive(level);
            foreach (var guid in SceneDependencies(assets, scenes[level].Guid, dependencies))
            {
                if (!users.TryGetValue(guid, out var list))
                {
                    users[guid] = list = [];
                }

                list.Add(level);
            }
        }

        foreach (var record in assets.Assets)
        {
            if (layout._archiveOf.ContainsKey(record.Guid))
            {
                continue; // a scene: always its own level
            }

            layout._archiveOf[record.Guid] = users.TryGetValue(record.Guid, out var levels)
                ? levels.Count == 1 ? GamePackage.LevelArchive(levels[0]) : GamePackage.SharedAssetsArchive
                : GamePackage.ResourcesArchive;
        }

        return layout;
    }

    /// <summary>Archive file the asset is written to.</summary>
    public string ArchiveOf(Guid guid) => _archiveOf[guid];

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

/// <summary>
/// Writes a game's content archives into one folder: opens each archive on first use, and always creates the globals,
/// default resources and level archives (shared assets and resources only when they have content), so the archive list
/// is stable for a given project.
/// </summary>
public sealed class ContentArchiveWriter : IDisposable
{
    private readonly string _directory;
    private readonly byte[] _key;
    private readonly Dictionary<string, (ContentArchive.Writer Writer, PackagedArchive Archive)> _open = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public ContentArchiveWriter(string directory, byte[] key)
    {
        _directory = directory;
        _key = key;
        Open(new PackagedArchive(GamePackage.GlobalsArchive, ContentArchiveKinds.Globals));
        Open(new PackagedArchive(GamePackage.DefaultResourcesArchive, ContentArchiveKinds.DefaultResources));
    }

    /// <summary>
    /// Archives in a fixed order (globals, default resources, levels, shared assets, resources) with their entry counts;
    /// final after <see cref="Dispose"/>.
    /// </summary>
    public List<PackagedArchive> Archives => [.. _order
        .Select(f => _open[f].Archive with { Entries = _open[f].Writer.Count })
        .OrderBy(a => Array.IndexOf(KindOrder, a.Kind))];

    private static readonly string[] KindOrder = [ContentArchiveKinds.Globals, ContentArchiveKinds.DefaultResources, ContentArchiveKinds.Level, ContentArchiveKinds.SharedAssets, ContentArchiveKinds.Resources];

    /// <summary>Creates one archive per level (from <see cref="ContentLayout.Levels"/>), even for a scene with no own assets.</summary>
    public void DeclareLevels(IEnumerable<PackagedArchive> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        foreach (var level in levels)
        {
            Open(level);
        }
    }

    /// <summary>File paths of the archives written so far.</summary>
    public IEnumerable<string> Files => _order.Select(f => Path.Combine(_directory, f));

    public ContentArchive.Writer Globals => _open[GamePackage.GlobalsArchive].Writer;

    public ContentArchive.Writer DefaultResources => _open[GamePackage.DefaultResourcesArchive].Writer;

    /// <summary>The writer of an archive named by <see cref="ContentLayout.ArchiveOf"/>; shared assets and resources open on demand.</summary>
    public ContentArchive.Writer For(string file)
    {
        if (_open.TryGetValue(file, out var open))
        {
            return open.Writer;
        }

        var kind = file == GamePackage.SharedAssetsArchive ? ContentArchiveKinds.SharedAssets
            : file == GamePackage.ResourcesArchive ? ContentArchiveKinds.Resources
            : throw new ArgumentException($"'{file}' is not an archive of this layout.", nameof(file));
        return Open(new PackagedArchive(file, kind));
    }

    public void Dispose()
    {
        foreach (var file in _order)
        {
            _open[file].Writer.Dispose();
        }
    }

    private ContentArchive.Writer Open(PackagedArchive archive)
    {
        var writer = new ContentArchive.Writer(Path.Combine(_directory, archive.File), _key);
        _open.Add(archive.File, (writer, archive));
        _order.Add(archive.File);
        return writer;
    }
}
