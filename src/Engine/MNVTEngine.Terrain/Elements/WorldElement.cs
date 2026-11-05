using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>What a node is for, which decides its colour in the editor.</summary>
public enum NodeKind
{
    Node,
    CurveHandle,
    TerrainCut,
    River,
}

/// <summary>
/// A draggable point of an element (road ends, curve handles, intersection outlets...). Two connected elements share
/// one node. <see cref="Host"/> is the element it is attached to for moving and rotating (in TerrainHeaven: the element
/// whose mesh it is a child of; attaching it elsewhere detaches it, as three.js reparenting does).
/// </summary>
public sealed class WorldNode(Vec3 position, NodeKind kind = NodeKind.Node)
{
    public Vec3 Position { get; set; } = position;

    public NodeKind Kind { get; set; } = kind;

    /// <summary>The element this node drives.</summary>
    public WorldElement? Parent { get; set; }

    /// <summary>The element it is attached to (moves with it).</summary>
    public WorldElement? Host { get; internal set; }

    /// <summary>Moves the node and rebuilds the element it drives.</summary>
    public void Update(Vec3 position)
    {
        Position = position;
        Parent?.Update();
    }
}

/// <summary>A triangle with UVs (TerrainHeaven's Vertex.ts) and optional per-corner debug labels.</summary>
public sealed class Triangle(Vec3 a, Vec3 b, Vec3 c, Vec2 uvA = default, Vec2 uvB = default, Vec2 uvC = default)
{
    public Vec3 A { get; set; } = a;

    public Vec3 B { get; set; } = b;

    public Vec3 C { get; set; } = c;

    public Vec2 UvA { get; set; } = uvA;

    public Vec2 UvB { get; set; } = uvB;

    public Vec2 UvC { get; set; } = uvC;

    public string? LabelA { get; set; }

    public string? LabelB { get; set; }

    public string? LabelC { get; set; }
}

/// <summary>A named group of triangles: one material (texture) slot of an element's mesh.</summary>
public sealed record GeometryGroup(string Name, List<Triangle> Triangles);

public readonly record struct NodeBasis(Vec3 Forward, Vec3 Right, Vec3 Up);

public sealed record ConnectionProfile(double HalfWidth, double SidewalkWidth, double CurbHeight);

public readonly record struct Connection(WorldElement Element, int NodeIndex);

/// <summary>
/// Base of every world element (road, intersection, terrain, river...): nodes, connections between elements' nodes,
/// per-group textures and UV transforms, moving and rotating, and the geometry rebuilt by <see cref="Update"/>. The
/// element's mesh is <see cref="Geometry"/>: named groups of world-space triangles.
/// </summary>
public abstract class WorldElement
{
    private readonly List<WorldNode> _attached = [];
    private readonly Dictionary<string, string> _groupTexturePaths = new(StringComparer.Ordinal);
    private List<string> _groupNames = [];

    protected List<WorldNode> Nodes { get; set; } = [];

    public Dictionary<int, Connection> Connections { get; } = [];

    public Dictionary<string, double> TextureRotations { get; } = new(StringComparer.Ordinal);

    /// <summary>The world this element is in (set when added), for the services TerrainHeaven reached through singletons.</summary>
    public TerrainWorld? World { get; internal set; }

    /// <summary>The mesh: rebuilt by <see cref="Update"/>.</summary>
    public IReadOnlyList<GeometryGroup> Geometry { get; private set; } = [];

    /// <summary>Raised after the geometry was rebuilt.</summary>
    public Action? OnGeometryChanged { get; set; }

    /// <summary>Raised when the set of editable properties changed (a toggle showing more fields).</summary>
    public Action? OnPropertiesChanged { get; set; }

    /// <summary>Whether this element's surfaces are drawn from both sides (terrain).</summary>
    public virtual bool DoubleSided => false;

    /// <summary>Whether it casts shadows (terrain; roads and the like only receive them).</summary>
    public virtual bool CastsShadows => false;

    /// <summary>How a group is drawn when it is not just "textured, opaque": water, untextured parts. Null: the default.</summary>
    public virtual MaterialHint? GetMaterialHint(string groupName) => null;

    /// <summary>The offset of an element without nodes (TerrainHeaven moves those through their mesh position).</summary>
    public Vec3 Offset { get; protected set; }

    public int NodeCount => Nodes.Count;

    public WorldNode GetNode(int index) => Nodes[index];

    /// <summary>Nodes attached to this element that are not its own nodes (curve handles) plus shared nodes it hosts.</summary>
    public IReadOnlyList<WorldNode> GetChildWorldNodes() => _attached;

    public IReadOnlyList<WorldNode> GetTransformWorldNodes()
    {
        var result = new List<WorldNode>();
        var seen = new HashSet<WorldNode>(ReferenceEqualityComparer.Instance);
        foreach (var node in Nodes.Concat(_attached))
        {
            if (node is not null && seen.Add(node))
            {
                result.Add(node);
            }
        }

        return result;
    }

    public IReadOnlyList<WorldElement> GetAffectedElementsForNode(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var affected = new List<WorldElement>();
        void Add(WorldElement element)
        {
            if (!affected.Contains(element))
            {
                affected.Add(element);
            }
        }

        if (node.Parent is { } parent)
        {
            Add(parent);
        }

        var referencesNode = false;
        for (var index = 0; index < Nodes.Count; index++)
        {
            if (Nodes[index] != node)
            {
                continue;
            }

            referencesNode = true;
            Add(this);
            if (Connections.TryGetValue(index, out var connection))
            {
                Add(connection.Element);
            }
        }

        if (!referencesNode && node.Parent == this)
        {
            Add(this);
        }

        return affected;
    }

    public int GetNodeIndex(WorldNode node) => Nodes.IndexOf(node);

    /// <summary>Attaches a node so it moves with this element (three.js mesh.add): it leaves its previous host.</summary>
    protected void Attach(WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Host?._attached.Remove(node);
        node.Host = this;
        _attached.Add(node);
    }

    protected void Detach(WorldNode node)
    {
        if (node.Host == this)
        {
            _attached.Remove(node);
            node.Host = null;
        }
    }

    public void SetNode(int index, WorldNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (index < Nodes.Count && Nodes[index] is { } old)
        {
            Detach(old);
            if (old.Parent == this)
            {
                old.Parent = null;
            }
        }

        while (Nodes.Count <= index)
        {
            Nodes.Add(null!);
        }

        Nodes[index] = node;
        node.Parent = this;
        Attach(node);
    }

    public bool IsConnected(int nodeIndex) => Connections.ContainsKey(nodeIndex);

    public bool Connect(int thisNodeIndex, WorldElement other, int otherNodeIndex)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Connections.ContainsKey(thisNodeIndex) || other.Connections.ContainsKey(otherNodeIndex))
        {
            return false;
        }

        SetNode(thisNodeIndex, other.GetNode(otherNodeIndex));
        Connections[thisNodeIndex] = new Connection(other, otherNodeIndex);
        other.Connections[otherNodeIndex] = new Connection(this, thisNodeIndex);

        // A connection changes the resolved width, curb and endpoint basis on both sides: rebuild both meshes now so a
        // caller cannot leave the neighbour stale.
        Update();
        other.Update();
        return true;
    }

    public void Disconnect(int nodeIndex)
    {
        if (!Connections.Remove(nodeIndex, out var connection))
        {
            return;
        }

        connection.Element.Connections.Remove(connection.NodeIndex);

        // The shared node: SetNode orphans it, so keep it, give this element its own new node, and hand the shared one
        // back to the other element.
        var sharedNode = Nodes[nodeIndex];
        SetNode(nodeIndex, new WorldNode(sharedNode.Position, NodeKind.Node));
        sharedNode.Parent = connection.Element;
        connection.Element.Attach(sharedNode);
        Update();
        connection.Element.Update();
    }

    public void DisconnectAll()
    {
        foreach (var connection in Connections.Values)
        {
            foreach (var (otherIndex, otherConnection) in connection.Element.Connections.ToList())
            {
                if (otherConnection.Element == this)
                {
                    connection.Element.Connections.Remove(otherIndex);
                }
            }
        }

        Connections.Clear();
    }

    public abstract NodeBasis GetNodeBasis(int index);

    public abstract PropertyDefinition GetProperties();

    public abstract IReadOnlyList<OccupiedTriangle> GetOccupiedArea();

    public abstract ElementData Serialize(int id);

    protected abstract List<GeometryGroup> GetGeometry();

    protected (Dictionary<string, string> Textures, Dictionary<string, double> TextureRotations) CollectTextureMaps()
    {
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        var rotations = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var groupName in GetGroupNames())
        {
            if (_groupTexturePaths.TryGetValue(groupName, out var path) && path.Length > 0)
            {
                textures[groupName] = path;
            }

            if (TextureRotations.TryGetValue(groupName, out var rotation) && rotation != 0)
            {
                rotations[groupName] = rotation;
            }
        }

        return (textures, rotations);
    }

    /// <summary>Group names that support UV editing.</summary>
    public virtual IReadOnlyList<string> GetUVGroups() => [];

    public virtual UVTransform GetUVTransform(string group) => new();

    public virtual void SetUVTransform(string group, UVTransform transform)
    {
    }

    public virtual double GetWidth() => 0;

    public virtual double GetSidewalkWidth() => 0;

    public virtual double GetCurbHeight() => 0;

    /// <summary>A fixed endpoint profile wins over width blending on both sides of a connection.</summary>
    public virtual ConnectionProfile? GetFixedConnectionProfile(int index) => null;

    public virtual bool IsTerrainSurface() => false;

    public virtual bool CutsTerrainSurface() => true;

    public virtual bool DependsOnTerrainSurface() => false;

    private ConnectionProfile? FixedProfile(int index, Connection connection) =>
        GetFixedConnectionProfile(index) ?? connection.Element.GetFixedConnectionProfile(connection.NodeIndex);

    public double GetResolvedHalfWidth(int index)
    {
        if (!Connections.TryGetValue(index, out var connection))
        {
            return GetWidth() / 2;
        }

        return FixedProfile(index, connection) is { } fixedProfile ? fixedProfile.HalfWidth : (GetWidth() + connection.Element.GetWidth()) / 2 / 2;
    }

    public double GetResolvedSidewalkWidth(int index)
    {
        if (!Connections.TryGetValue(index, out var connection))
        {
            return GetSidewalkWidth();
        }

        if (FixedProfile(index, connection) is { } fixedProfile)
        {
            return fixedProfile.SidewalkWidth;
        }

        var own = GetSidewalkWidth();
        var other = connection.Element.GetSidewalkWidth();
        return own <= 0 || other <= 0 ? 0 : (own + other) / 2;
    }

    public double GetResolvedCurbHeight(int index)
    {
        if (!Connections.TryGetValue(index, out var connection))
        {
            return GetCurbHeight();
        }

        if (FixedProfile(index, connection) is { } fixedProfile)
        {
            return fixedProfile.CurbHeight;
        }

        return GetSidewalkWidth() <= 0 || connection.Element.GetSidewalkWidth() <= 0 ? 0 : (GetCurbHeight() + connection.Element.GetCurbHeight()) / 2;
    }

    public virtual void Translate(Vec3 delta)
    {
        var childNodes = GetTransformWorldNodes();
        if (childNodes.Count == 0)
        {
            Offset += delta;
            Update();
            return;
        }

        var touched = new List<WorldElement>();
        foreach (var node in childNodes)
        {
            node.Position += delta;
            foreach (var element in GetAffectedElementsForNode(node))
            {
                if (!touched.Contains(element))
                {
                    touched.Add(element);
                }
            }
        }

        if (touched.Count == 0)
        {
            Update();
            return;
        }

        foreach (var element in touched)
        {
            element.Update();
        }
    }

    public virtual bool CanRotate() => GetTransformWorldNodes().Count > 0;

    public virtual void RotateAround(Vec3 pivot, Quat deltaRotation)
    {
        var childNodes = GetTransformWorldNodes();
        if (childNodes.Count == 0)
        {
            return;
        }

        var touched = new List<WorldElement>();
        foreach (var node in childNodes)
        {
            node.Position = (node.Position - pivot).ApplyQuaternion(deltaRotation) + pivot;
            foreach (var element in GetAffectedElementsForNode(node))
            {
                if (!touched.Contains(element))
                {
                    touched.Add(element);
                }
            }
        }

        foreach (var element in touched)
        {
            element.Update();
        }
    }

    public NodeBasis GetResolvedNodeBasis(int index)
    {
        var basis = GetNodeBasis(index);
        if (!Connections.TryGetValue(index, out var connection))
        {
            return basis;
        }

        var connectionBasis = connection.Element.GetNodeBasis(connection.NodeIndex);
        var ownFixed = GetFixedConnectionProfile(index);
        var connectedFixed = connection.Element.GetFixedConnectionProfile(connection.NodeIndex);

        // A fixed connection profile describes a rigid mouth, such as an intersection outlet: its cross-section must
        // stay exactly on that mouth's plane. Averaging its basis with an angled road tangent would rotate the road's
        // endpoint, making one corner overlap the intersection while the other leaves a visible gap.
        if (ownFixed is not null || connectedFixed is not null)
        {
            var fixedBasis = ownFixed is not null ? basis : connectionBasis;
            var fixedRight = fixedBasis.Right;
            if (basis.Forward.Dot(fixedBasis.Forward) < 0)
            {
                fixedRight = -fixedRight;
            }

            var fixedForward = basis.Up.Cross(fixedRight).Normalized();
            return new NodeBasis(fixedForward, fixedRight.Normalized(), basis.Up);
        }

        // If the forwards point opposite (end to end), the connected right is flipped: negate it to keep sides consistent.
        var averageRight = (basis.Forward.Dot(connectionBasis.Forward) < 0 ? basis.Right - connectionBasis.Right : basis.Right + connectionBasis.Right).Normalized();
        var forward = basis.Up.Cross(averageRight).Normalized();
        var right = forward.Cross(basis.Up).Normalized();
        return new NodeBasis(forward, right, basis.Up);
    }

    /// <summary>Rebuilds the geometry and raises <see cref="OnGeometryChanged"/>.</summary>
    public virtual void Update()
    {
        var groups = GetGeometry();
        Geometry = groups;
        _groupNames = groups.Select(g => g.Name).ToList();
        OnGeometryChanged?.Invoke();
    }

    /// <summary>Detaches every node and connection (the element is being removed).</summary>
    public virtual void Dispose()
    {
        foreach (var node in _attached.ToList())
        {
            Detach(node);
        }

        Nodes = [];
        Connections.Clear();
        OnPropertiesChanged = null;
        OnGeometryChanged = null;
    }

    public string? GetGroupNameAtFace(int faceIndex)
    {
        var start = 0;
        foreach (var group in Geometry)
        {
            if (faceIndex < start + group.Triangles.Count)
            {
                return group.Name;
            }

            start += group.Triangles.Count;
        }

        return _groupNames.FirstOrDefault();
    }

    public IReadOnlyList<string> GetGroupNames() => _groupNames;

    /// <summary>The texture file each group uses (project-relative paths).</summary>
    public IReadOnlyDictionary<string, string> GetGroupTextureReferences() => _groupTexturePaths;

    public void SetGroupTextureReference(string groupName, string sourcePath)
    {
        if (!string.IsNullOrEmpty(sourcePath))
        {
            _groupTexturePaths[groupName] = sourcePath;
        }
    }

    public void ClearGroupTextureReference(string groupName) => _groupTexturePaths.Remove(groupName);

    public void SetTextureRotation(string groupName, double degrees) => TextureRotations[groupName] = degrees;

    /// <summary>One entry per raw vertex (three per triangle) with its group and debug label (the vertex-debug overlay).</summary>
    public IReadOnlyList<(int Index, string Label, string Group, Vec3 Position)> GetVertexDebugInfo()
    {
        var result = new List<(int, string, string, Vec3)>();
        var index = 0;
        foreach (var group in Geometry)
        {
            foreach (var triangle in group.Triangles)
            {
                result.Add((index++, triangle.LabelA ?? "", group.Name, triangle.A));
                result.Add((index++, triangle.LabelB ?? "", group.Name, triangle.B));
                result.Add((index++, triangle.LabelC ?? "", group.Name, triangle.C));
            }
        }

        return result;
    }

    /// <summary>Reads textures, rotations and node positions shared by every element type from saved data.</summary>
    protected internal void LoadCommon(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var (group, path) in data.Textures)
        {
            SetGroupTextureReference(group, path);
        }

        foreach (var (group, degrees) in data.TextureRotations)
        {
            TextureRotations[group] = degrees;
        }
    }
}
