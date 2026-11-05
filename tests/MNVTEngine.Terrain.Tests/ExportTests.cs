using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Export;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Project;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's ObjWriter.test.ts, plus Unity export layout checks.</summary>
public sealed class ExportTests
{
    private static readonly double[] SquarePositions = [0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 0, 1];
    private static readonly double[] SquareUvs = [0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1];

    private static ObjGroup Square(string material, string name = "road") => new(name, material, SquarePositions, SquareUvs);

    private static string[] Lines(string text, string prefix) => [.. text.Split('\n').Where(l => l.StartsWith(prefix, StringComparison.Ordinal))];

    private static double[] Vector(string line) => [.. line.Split(' ').Skip(1).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public void EmitsAVVtVnTriplePerVertexAndOneFacePerTriangle()
    {
        var obj = ObjWriter.BuildObj("road_lod0.mtl", "road_lod0", [Square("road_lod0_road")]);
        var lines = obj.Split('\n').Where(l => l.Length > 0).ToList();
        Assert.Equal(["mtllib road_lod0.mtl", "o road_lod0", "g road", "usemtl road_lod0_road"], lines.Take(4));
        Assert.Equal((6, 6, 6), (Lines(obj, "v ").Length, Lines(obj, "vt ").Length, Lines(obj, "vn ").Length));
        Assert.Equal(["f 1/1/1 2/2/2 3/3/3", "f 4/4/4 5/5/5 6/6/6"], Lines(obj, "f "));
    }

    [Fact]
    public void KeepsARunningVertexCountAcrossGroups()
    {
        var obj = ObjWriter.BuildObj("mixed.mtl", "mixed", [Square("mat_a"), Square("mat_b", "sidewalk")]);
        Assert.Equal(["f 1/1/1 2/2/2 3/3/3", "f 4/4/4 5/5/5 6/6/6", "f 7/7/7 8/8/8 9/9/9", "f 10/10/10 11/11/11 12/12/12"], Lines(obj, "f "));
    }

    [Fact]
    public void GivesAFlatSurfaceOneNormal()
    {
        var normals = Lines(ObjWriter.BuildObj("flat.mtl", "flat", [Square("flat_mat")]), "vn ");
        Assert.All(normals, n => Assert.Equal(normals[0], n));
        Assert.Equal(1, Math.Abs(Vector(normals[0])[1]), 5);
    }

    [Fact]
    public void KeepsASharpCornerCrisp()
    {
        var floor = new ObjGroup("floor", "floor_mat", SquarePositions, SquareUvs);
        var wall = new ObjGroup("wall", "wall_mat", [1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 1], SquareUvs);
        var normals = Lines(ObjWriter.BuildObj("corner.mtl", "corner", [floor, wall]), "vn ").Select(Vector).ToList();
        static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);
        Assert.True(Math.Abs(Dot(normals[0], normals[6])) < 0.1);
        Assert.All(normals.Take(6), n => Assert.True(Dot(n, normals[0]) > 0.99));
        Assert.All(normals.Skip(6), n => Assert.True(Dot(n, normals[6]) > 0.99));
    }

    [Fact]
    public void SkipsEmptyGroupsAndSanitizesNames()
    {
        Assert.DoesNotContain("bridgePillars", ObjWriter.BuildObj("r.mtl", "r", [Square("mat_road"), new ObjGroup("bridgePillars", "mat_empty", [], [])]), StringComparison.Ordinal);
        var obj = ObjWriter.BuildObj("x.mtl", "x", [Square("weird mat/name", "weird group name!")]);
        Assert.Contains("g weird_group_name\n", obj, StringComparison.Ordinal);
        Assert.Contains("usemtl weird_matname\n", obj, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesOneMaterialBlockWithRelativeTexturesWhenTextured()
    {
        var mtl = ObjWriter.BuildMtl([new ObjMaterial("road_lod0_road", "../Textures/road.png"), new ObjMaterial("road_lod0_sidewalk", null)]);
        Assert.Contains("newmtl road_lod0_road", mtl, StringComparison.Ordinal);
        Assert.Contains("map_Kd ../Textures/road.png", mtl, StringComparison.Ordinal);
        Assert.DoesNotContain("map_Kd", mtl.Split("newmtl road_lod0_sidewalk")[1], StringComparison.Ordinal);
    }

    private sealed class Source : ITerrainExportSource
    {
        public byte[]? ReadTexture(string projectPath) => projectPath.Contains("missing", StringComparison.Ordinal) ? null : [1, 2, 3];

        public IReadOnlyList<MeshGroup>? LoadMesh(MeshAssetData asset) =>
            [new MeshGroup("body", [0, 0, 0, 1, 0, 0, 0, 1, 0], [0, 0, 1, 0, 0, 1], Vec3.One, "props/rock.jpg")];
    }

    [Fact]
    public void ExportsTheUnityFolderLayout()
    {
        var project = new TerrainProject();
        var terrain = new Elements.Terrain(Vec3.Zero, 10, 10);
        terrain.SetGroupTextureReference("terrain", "textures/grass.png");
        terrain.TextureLayers[1].TexturePath = "textures/missing.png";
        project.World.Add(terrain);
        project.World.Add(new Road(new Vec3(-4, 0, 0), new Vec3(4, 0, 0)));
        project.World.Add(new TerrainCutSpline(new Vec3(0, 0, -3), new Vec3(0, 0, 3)));
        var rock = project.Meshes.AddAsset(MeshAssetData.Create("Rock", "rock.obj"));
        project.Meshes.AddInstance(rock, new MeshInstanceData { Position = PointData.From(new Vec3(1, 0, 2)), RotationY = 30 });
        project.Meshes.AddAsset(MeshAssetData.Create("Unused", "unused.obj"));
        project.Foliage.AddType(new FoliageTypeData { DisplayName = "Grass", TexturePath = "textures/blade.png" });

        var directory = Directory.CreateTempSubdirectory("mnvt-terrain-export").FullName;
        try
        {
            var progress = new List<ExportProgress>();
            var manifest = new UnityExporter().Export(project, directory, new UnityExportOptions(LodLevels: 2, Scale: 2), new Source(), progress.Add);
            Assert.Equal(["terrain_0", "road_0"], manifest.Objects.Select(o => o.Name)); // the cut spline has no geometry
            Assert.Equal(["Meshes/terrain_0_lod0.obj", "Meshes/terrain_0_lod1.obj"], manifest.Objects[0].Lods.Select(l => l.ObjFile));
            Assert.Equal("grass.png", manifest.Objects[0].TerrainPaint!.Layers[0].Texture); // layer 0 falls back to the tile texture
            Assert.Equal(["grass.png", "blade.png"], manifest.Textures); // the missing layer texture is skipped
            Assert.True(File.Exists(Path.Combine(directory, "ControlMaps", "terrain_0_control.png")));
            Assert.Equal("Done", progress[^1].Message);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
            Assert.Equal(2, (double)json["scale"]!);
            Assert.Null(json["objects"]![1]!["terrainPaint"]);
            var meshes = JObject.Parse(File.ReadAllText(Path.Combine(directory, "meshes.json")));
            Assert.Single(meshes["assets"]!);
            Assert.Equal(30, (double)meshes["placements"]![0]!["rotation"]!["y"]!);
            Assert.Equal(1, (double)meshes["placements"]![0]!["position"]!["x"]!); // placements stay unscaled
            Assert.StartsWith("Meshes/prop_", (string)meshes["assets"]![0]!["lods"]![0]!["objFile"]!, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(Path.Combine(directory, "Textures"), "prop_*.jpg"));
            Assert.Equal("Grass", (string)JObject.Parse(File.ReadAllText(Path.Combine(directory, "foliage.json")))["Types"]![0]!["DisplayName"]!);

            var maxX = Lines(File.ReadAllText(Path.Combine(directory, "Meshes", "road_0_lod0.obj")), "v ").Select(Vector).Max(v => v[0]);
            Assert.Equal(8, maxX, 6); // the scale is baked into element vertices
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("textures\\grass.png", "grass.png")]
    [InlineData("..", "texture.png")]
    [InlineData("a/b/", "texture.png")]
    public void ExportsTexturesUnderAPlainFileName(string path, string expected) => Assert.Equal(expected, UnityExporter.TextureFileName(path));
}
