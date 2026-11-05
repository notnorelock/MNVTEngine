using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>A curved fence: a double-sided plane or box panel along the path, and posts at a spacing and at sharp turns.</summary>
public sealed class Fence(Vec3 positionA, Vec3 positionB) : SplineElement(positionA, positionB, NodeKind.Node)
{
    private readonly Dictionary<string, UVTransform> _uvTransforms = new(StringComparer.Ordinal) { ["fence"] = new(), ["posts"] = new() };

    /// <summary>plane or box.</summary>
    public string Style { get; set; } = "plane";

    public double Height { get; set; } = 1.5;

    public double Thickness { get; set; } = 0.08;

    public double PostSpacing { get; set; } = 2;

    public double PostHeight { get; set; } = 1.8;

    public double PostWidth { get; set; } = 0.14;

    /// <summary>box or circle.</summary>
    public string PostShape { get; set; } = "box";

    public int PostSides { get; set; } = 12;

    /// <summary>A post is added wherever the path has turned this many degrees since the last one.</summary>
    public double MaxAngleStep { get; set; } = 30;

    public override bool CastsShadows => true;

    public override bool CutsTerrainSurface() => false;

    /// <summary>The editor draws the plane once, double-sided (exports add explicit back faces instead).</summary>
    public override MaterialHint? GetMaterialHint(string groupName) => groupName == "fence" && Style == "plane" ? new MaterialHint(DoubleSided: true) : null;

    public override IReadOnlyList<GuideLine> GetGuideLines() =>
        Divisions <= 0 ? [] : [new GuideLine(GetBezierCurvePoints(), NodeKind.CurveHandle), .. base.GetGuideLines()];

    public List<Vec3> GetPostPositions(int? divisionsOverride = null)
    {
        var points = GetBezierCurvePoints(divisionsOverride);
        if (points.Count < 2)
        {
            return [.. points];
        }

        var spacing = Math.Max(0.1, PostSpacing);
        var threshold = MathUtil.DegToRad(MathUtil.Clamp(MaxAngleStep, 1, 180));
        var cumulative = new List<double> { 0 };
        for (var i = 1; i < points.Count; i++)
        {
            cumulative.Add(cumulative[i - 1] + points[i].DistanceTo(points[i - 1]));
        }

        var total = cumulative[^1];
        var distances = new SortedSet<double> { 0, total };
        for (var distance = spacing; distance < total - 1e-6; distance += spacing)
        {
            distances.Add(distance);
        }

        var accumulatedTurn = 0.0;
        for (var i = 1; i < points.Count - 1; i++)
        {
            var before = (points[i] - points[i - 1]).Normalized();
            var after = (points[i + 1] - points[i]).Normalized();
            accumulatedTurn += before.AngleTo(after);
            if (accumulatedTurn + 1e-6 >= threshold)
            {
                distances.Add(cumulative[i]);
                accumulatedTurn = 0;
            }
        }

        return distances.Select(d => SamplePolyline(points, cumulative, d)).ToList();
    }

    private static Vec3 SamplePolyline(List<Vec3> points, List<double> cumulative, double distance)
    {
        for (var i = 1; i < cumulative.Count; i++)
        {
            if (distance > cumulative[i])
            {
                continue;
            }

            var span = cumulative[i] - cumulative[i - 1];
            return points[i - 1].Lerp(points[i], span > 1e-8 ? (distance - cumulative[i - 1]) / span : 0);
        }

        return points[^1];
    }

    protected override List<GeometryGroup> GetGeometry()
    {
        var lod = World?.LodPreviewLevel ?? 0;
        return BuildGeometry(lod > 0 ? LodLevels.Divisions(Divisions, lod) : null, false);
    }

    /// <summary>
    /// Export geometry: engines may cull back faces, so the exported plane gets explicit reverse triangles (the editor
    /// draws one double-sided quad to avoid coplanar transparent triangles fighting).
    /// </summary>
    public List<GeometryGroup> GetExportGeometry(int lodIndex) => BuildGeometry(LodLevels.Divisions(Divisions, lodIndex), true);

    private List<GeometryGroup> BuildGeometry(int? divisionsOverride, bool includePlaneBackFaces)
    {
        var path = GetBezierCurvePoints(divisionsOverride);
        var panel = new List<Triangle>();
        var distance = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var (start, end) = (path[i - 1], path[i]);
            var length = start.DistanceTo(end);
            if (length < 1e-6)
            {
                continue;
            }

            if (Style == "plane")
            {
                AddPlanePanel(panel, start, end, distance, distance + length, includePlaneBackFaces);
            }
            else
            {
                AddBox(panel, start, end, 0, Math.Max(0.05, Height), Math.Max(0.01, Thickness));
            }

            distance += length;
        }

        var posts = new List<Triangle>();
        foreach (var position in GetPostPositions(divisionsOverride))
        {
            AddPost(posts, position);
        }

        ApplyUV("fence", panel);
        ApplyUV("posts", posts);
        return [new GeometryGroup("fence", panel), new GeometryGroup("posts", posts)];
    }

    private void AddPlanePanel(List<Triangle> output, Vec3 start, Vec3 end, double u0, double u1, bool includeBackFace)
    {
        var up = new Vec3(0, Math.Max(0.05, Height), 0);
        AddQuad(output, start, end, end + up, start + up, new(u0, 0), new(u1, 0), new(u1, 1), new(u0, 1));
        if (includeBackFace)
        {
            AddQuad(output, end, start, start + up, end + up, new(u1, 0), new(u0, 0), new(u0, 1), new(u1, 1));
        }
    }

    private static void AddBox(List<Triangle> output, Vec3 start, Vec3 end, double bottom, double top, double width)
    {
        var direction = end - start;
        var length = direction.Length;
        if (length < 1e-6)
        {
            return;
        }

        var right = (direction / length).Cross(Vec3.UnitY).Normalized() * (width / 2);
        var (b, t) = (new Vec3(0, bottom, 0), new Vec3(0, top, 0));
        Vec3[] c =
        [
            start - right + b, start + right + b, end + right + b, end - right + b,
            start - right + t, start + right + t, end + right + t, end - right + t,
        ];

        // Vertical faces span V 0..1 whatever the height: a fence texture fits once bottom to top, U repeats along the length.
        AddQuadSimple(output, c[3], c[0], c[4], c[7], length, 1);
        AddQuadSimple(output, c[1], c[2], c[6], c[5], length, 1);
        AddQuadSimple(output, c[2], c[3], c[7], c[6], width, 1);
        AddQuadSimple(output, c[0], c[1], c[5], c[4], width, 1);
        AddQuadSimple(output, c[7], c[4], c[5], c[6], length, width);
        AddQuadSimple(output, c[1], c[0], c[3], c[2], length, width);
    }

    private void AddPost(List<Triangle> output, Vec3 center)
    {
        var sides = PostShape == "box" ? 4 : Math.Max(3, PostSides);
        var radius = Math.Max(0.02, PostWidth) / 2;
        var up = new Vec3(0, Math.Max(0.05, PostHeight), 0);
        var bottom = new List<Vec3>();
        var top = new List<Vec3>();
        for (var i = 0; i < sides; i++)
        {
            var angle = (i / (double)sides * Math.PI * 2) + (PostShape == "box" ? Math.PI / 4 : 0);
            var offset = new Vec3(Math.Cos(angle) * radius, 0, Math.Sin(angle) * radius);
            bottom.Add(center + offset);
            top.Add(center + offset + up);
        }

        for (var i = 0; i < sides; i++)
        {
            var next = (i + 1) % sides;

            // Ring points run counter-clockwise from above: reversed on the walls so their normals face out, the cap
            // winds upward and the base downward.
            AddQuadSimple(output, bottom[next], bottom[i], top[i], top[next], Math.PI * 2 * radius / sides, up.Y);
            output.Add(new Triangle(top[next], top[i], center + up));
            output.Add(new Triangle(bottom[i], bottom[next], center));
        }
    }

    private static void AddQuadSimple(List<Triangle> output, Vec3 a, Vec3 b, Vec3 c, Vec3 d, double width, double height) =>
        AddQuad(output, a, b, c, d, new(0, 0), new(width, 0), new(width, height), new(0, height));

    private static void AddQuad(List<Triangle> output, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec2 uvA, Vec2 uvB, Vec2 uvC, Vec2 uvD)
    {
        output.Add(new Triangle(a, b, c, uvA, uvB, uvC));
        output.Add(new Triangle(a, c, d, uvA, uvC, uvD));
    }

    private void ApplyUV(string group, List<Triangle> triangles)
    {
        var t = _uvTransforms[group];
        Vec2 Map(Vec2 uv) => new((uv.X * t.ScaleX) + t.OffsetX, (uv.Y * t.ScaleY) + t.OffsetY);
        foreach (var tri in triangles)
        {
            tri.UvA = Map(tri.UvA);
            tri.UvB = Map(tri.UvB);
            tri.UvC = Map(tri.UvC);
        }
    }

    public override IReadOnlyList<string> GetUVGroups() => ["fence", "posts"];

    public override UVTransform GetUVTransform(string group) => _uvTransforms.TryGetValue(group, out var t) ? t.Clone() : base.GetUVTransform(group);

    public override void SetUVTransform(string group, UVTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!_uvTransforms.ContainsKey(group))
        {
            return;
        }

        _uvTransforms[group] = new UVTransform
        {
            OffsetX = double.IsFinite(transform.OffsetX) ? transform.OffsetX : 0,
            OffsetY = double.IsFinite(transform.OffsetY) ? transform.OffsetY : 0,
            ScaleX = double.IsFinite(transform.ScaleX) ? Math.Max(0.1, transform.ScaleX) : 1,
            ScaleY = double.IsFinite(transform.ScaleY) ? Math.Max(0.1, transform.ScaleY) : 1,
        };
        Update();
    }

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea() => [];

    public override PropertyDefinition GetProperties()
    {
        Vector3Property NodeProperty(string label, WorldNode node) => new(label, () => node.Position, v =>
        {
            node.Update(v);
            Update();
        });
        void Set(Action change, bool refresh = false)
        {
            change();
            Update();
            if (refresh)
            {
                OnPropertiesChanged?.Invoke();
            }
        }

        var fence = new List<PropertyItem>
        {
            new NumberProperty("Divisions", () => Divisions, v => Set(() => Divisions = (int)MathUtil.JsRound(v)), 0, 64, 1),
            new SelectProperty("Style", [new("Double-Sided Plane", "plane"), new("Box", "box")], () => Style, v => Set(() => Style = v == "box" ? "box" : "plane", true)),
            new NumberProperty("Height", () => Height, v => Set(() => Height = Math.Max(0.05, v)), 0.05, null, 0.05),
            new NumberProperty("Texture Stretch", () => _uvTransforms["fence"].ScaleX, v =>
            {
                var uv = _uvTransforms["fence"].Clone();
                uv.ScaleX = Math.Max(0.01, v);
                SetUVTransform("fence", uv);
            }, 0.01, null, 0.1),
        };
        if (Style == "box")
        {
            fence.Add(new NumberProperty("Thickness", () => Thickness, v => Set(() => Thickness = Math.Max(0.01, v)), 0.01, null, 0.01));
        }

        var posts = new List<PropertyItem>
        {
            new NumberProperty("Spacing", () => PostSpacing, v => Set(() => PostSpacing = Math.Max(0.1, v)), 0.1, null, 0.1),
            new NumberProperty("Max Angle Step", () => MaxAngleStep, v => Set(() => MaxAngleStep = MathUtil.Clamp(v, 1, 180)), 1, 180, 1),
            new NumberProperty("Post Height", () => PostHeight, v => Set(() => PostHeight = Math.Max(0.05, v)), 0.05, null, 0.05),
            new NumberProperty("Post Width", () => PostWidth, v => Set(() => PostWidth = Math.Max(0.04, v)), 0.04, null, 0.01),
            new SelectProperty("Post Shape", [new("Box", "box"), new("Circle", "circle")], () => PostShape, v => Set(() => PostShape = v == "circle" ? "circle" : "box", true)),
        };
        if (PostShape == "circle")
        {
            posts.Add(new NumberProperty("Circle Sides", () => PostSides, v => Set(() => PostSides = Math.Max(3, (int)MathUtil.JsRound(v))), 3, 64, 1));
        }

        return new PropertyDefinition("Fence", "☷",
        [
            new PropertySection("Nodes", [NodeProperty("Node A", NodeA), NodeProperty("Node B", NodeB)]),
            new PropertySection("Fence", fence),
            new PropertySection("Posts", posts),
        ]);
    }

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        var data = new ElementData
        {
            Type = "fence",
            Id = id,
            Textures = textures,
            TextureRotations = rotations,
            UvTransforms = _uvTransforms.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
            FenceStyle = Style,
            FenceHeight = Height,
            FenceThickness = Thickness,
            FencePostSpacing = PostSpacing,
            FencePostHeight = PostHeight,
            FencePostWidth = PostWidth,
            FencePostShape = PostShape,
            FencePostSides = PostSides,
            FenceMaxAngleStep = MaxAngleStep,
        };
        SerializeCurve(data);
        return data;
    }

    public static Fence Deserialize(ElementData data)
    {
        var (a, b) = EndPositions(data, new Vec3(1, 0, 0));
        var fence = new Fence(a, b)
        {
            Style = data.FenceStyle == "box" ? "box" : "plane",
            Height = Math.Max(0.05, data.FenceHeight ?? 1.5),
            Thickness = Math.Max(0.01, data.FenceThickness ?? 0.08),
            PostSpacing = Math.Max(0.1, data.FencePostSpacing ?? 2),
            PostHeight = Math.Max(0.05, data.FencePostHeight ?? 1.8),
            PostWidth = Math.Max(0.04, data.FencePostWidth ?? 0.14),
            PostShape = data.FencePostShape == "circle" ? "circle" : "box",
            PostSides = Math.Max(3, data.FencePostSides ?? 12),
            MaxAngleStep = MathUtil.Clamp(data.FenceMaxAngleStep ?? 30, 1, 180),
        };
        foreach (var (group, transform) in data.UvTransforms ?? [])
        {
            if (fence._uvTransforms.ContainsKey(group))
            {
                fence._uvTransforms[group] = transform.Clone();
            }
        }

        fence.DeserializeCurve(data);
        return fence;
    }
}
