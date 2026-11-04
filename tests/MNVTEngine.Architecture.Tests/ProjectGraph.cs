using System.Xml.Linq;

namespace MNVTEngine.Architecture.Tests;

/// <summary>Project dependency graph built from every .csproj under src/.</summary>
public sealed class ProjectGraph
{
    private readonly Dictionary<string, Node> _nodes;

    private ProjectGraph(Dictionary<string, Node> nodes) => _nodes = nodes;

    /// <param name="DevelopmentReferences">References only present in development builds (Condition on MnvtDevTools); not part of the release graph.</param>
    public sealed record Node(string Name, string Area, IReadOnlyList<string> ProjectReferences, IReadOnlyList<string> Packages, IReadOnlyList<string> DevelopmentReferences);

    public IEnumerable<Node> Projects => _nodes.Values;

    public static string RepositoryRoot { get; } = FindRoot();

    public static ProjectGraph Load()
    {
        var src = Path.Combine(RepositoryRoot, "src");
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var area = Path.GetRelativePath(src, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];

            static bool DevelopmentOnly(XElement e) => ((string?)e.Attribute("Condition"))?.Contains("MnvtDevTools", StringComparison.Ordinal) == true;
            static string ReferenceName(XElement e) => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!.Value.Replace('\\', '/'));
            var references = xml.Descendants("ProjectReference").Where(e => !DevelopmentOnly(e)).Select(ReferenceName).ToList();
            var development = xml.Descendants("ProjectReference").Where(DevelopmentOnly).Select(ReferenceName).ToList();
            var packages = xml.Descendants("PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string)e.Attribute("Update")!)
                .ToList();

            nodes.Add(name, new Node(name, area, references, packages, development));
        }

        return new ProjectGraph(nodes);
    }

    public Node this[string name] => _nodes[name];

    public bool Contains(string name) => _nodes.ContainsKey(name);

    /// <summary>All projects reachable from <paramref name="name"/>, excluding itself.</summary>
    public IReadOnlySet<string> TransitiveReferences(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(_nodes[name].ProjectReferences);
        while (stack.TryPop(out var current))
        {
            if (seen.Add(current) && _nodes.TryGetValue(current, out var node))
            {
                foreach (var next in node.ProjectReferences)
                {
                    stack.Push(next);
                }
            }
        }

        return seen;
    }

    /// <summary>Packages referenced directly by the project or any project it depends on.</summary>
    public IReadOnlySet<string> TransitivePackages(string name) =>
        TransitiveReferences(name).Append(name)
            .Where(_nodes.ContainsKey)
            .SelectMany(n => _nodes[n].Packages)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns one cycle as a list of project names, or null.</summary>
    public IReadOnlyList<string>? FindCycle()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var path = new List<string>();

        List<string>? Visit(string name)
        {
            state[name] = 1;
            path.Add(name);
            foreach (var next in _nodes[name].ProjectReferences.Where(_nodes.ContainsKey))
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    return [.. path.SkipWhile(p => p != next), next];
                }

                if (state.GetValueOrDefault(next) == 0 && Visit(next) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            return null;
        }

        foreach (var name in _nodes.Keys)
        {
            if (state.GetValueOrDefault(name) == 0 && Visit(name) is { } cycle)
            {
                return cycle;
            }
        }

        return null;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MNVTEngine.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate MNVTEngine.sln above the test output directory.");
    }
}
