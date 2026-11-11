using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Project;
using TerrainVec3 = MNVTEngine.Terrain.Geometry.Vec3;

namespace MNVTEngine.TerrainComponentsTests;

public sealed class WorldTerrainTests
{
    private static readonly AssetRef WorldAsset = new(Guid.Parse("7e5a0000-0000-4000-8000-0000000000aa"));

    private sealed class Reader(string text) : ITextAssetReader
    {
        public int Reads { get; private set; }

        public string ReadText(AssetRef asset, string assetType)
        {
            Reads++;
            return asset == WorldAsset && assetType == AssetTypes.Terrain ? text : throw new InvalidOperationException($"{asset} is not a {assetType} asset.");
        }
    }

    private static string FlatWorldWithRiver()
    {
        var project = new TerrainProject();
        project.World.Add(new Terrain.Elements.Terrain(TerrainVec3.Zero, 20, 20));
        project.World.Add(new RiverSpline(new TerrainVec3(-10, 0, 6), new TerrainVec3(10, 0, 6)));
        return project.Save();
    }

    [Fact]
    public void RoundTripsItsSettings()
    {
        var registry = TerrainComponents.Register(Physics3DComponents.Register(ComponentRegistry.CreateDefault()));
        var scene = new Scene();
        var terrain = scene.CreateGameObject("Terrain").AddComponent<WorldTerrain>();
        (terrain.Project, terrain.LodLevel, terrain.DrawFoliage, terrain.DrawProps) = (WorldAsset, 2, false, false);
        var serializer = new SceneSerializer(registry);
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.FindObjectsOfType<WorldTerrain>().Single();
        Assert.Equal((WorldAsset, 2, false, false), (copy.Project, copy.LodLevel, copy.DrawFoliage, copy.DrawProps));
    }

    [Fact]
    public void LoadsTheAssetOnFirstUse_AndReportsBadAssets()
    {
        var reader = new Reader(FlatWorldWithRiver());
        var scene = new Scene { TextAssets = reader };
        var terrain = scene.CreateGameObject("Terrain").AddComponent<WorldTerrain>();
        Assert.Null(terrain.Data); // no asset yet
        terrain.Project = WorldAsset;
        var version = terrain.Version;
        Assert.Equal(2, terrain.Data!.World.Elements.Count);
        Assert.Equal(2, terrain.Data.World.Elements.Count);
        Assert.Equal(1, reader.Reads);
        Assert.True(terrain.Version > version);

        var broken = new Scene { TextAssets = new Reader("{ \"version\": 7 }") }.CreateGameObject("Broken").AddComponent<WorldTerrain>();
        broken.Project = WorldAsset;
        Assert.Null(broken.Data);
        Assert.Contains("version 7", broken.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void CollidesWithOpaqueSurfaces_ButNotWater()
    {
        var scene = new Scene { TextAssets = new Reader(FlatWorldWithRiver()) };
        var terrain = scene.CreateGameObject("Terrain").AddComponent<WorldTerrain>();
        terrain.Project = WorldAsset;
        var mesh = terrain.GetMeshData()!;
        var tiles = terrain.Data!.World.Elements.OfType<Terrain.Elements.Terrain>().Sum(t => t.Geometry.Sum(g => g.Triangles.Count));
        Assert.Equal(tiles * 3, mesh.Indices.Length); // the river's water surface adds nothing
        Assert.Same(mesh, terrain.GetMeshData()); // cached until the world changes
        terrain.MarkChanged();
        Assert.NotSame(mesh, terrain.GetMeshData());
    }

    [Fact]
    public void ABallRestsOnTheTerrain()
    {
        var project = new TerrainProject();
        project.World.Add(new Terrain.Elements.Terrain(TerrainVec3.Zero, 20, 20));
        project.World.Update();
        var scene = new Scene { RethrowScriptExceptions = true };
        var ground = scene.CreateGameObject("Terrain");
        ground.AddComponent<WorldTerrain>().SetData(project);
        ground.AddComponent<MeshCollider>();
        var ball = scene.CreateGameObject("Ball");
        ball.Transform.Position = new Vector3(1f, 3f, 1f);
        ball.AddComponent<SphereCollider>();
        ball.AddComponent<Rigidbody>();
        scene.Start();
        for (var i = 0; i < 180; i++)
        {
            scene.FixedUpdate(1f / 60f);
        }

        Assert.Equal(0.5f, ball.Transform.Position.Y, 1);
    }

    [Fact]
    public void LodLevelIsClampedAndRebuildsTheWorld()
    {
        var terrain = new Scene().CreateGameObject("Terrain").AddComponent<WorldTerrain>();
        var project = new TerrainProject();
        terrain.SetData(project);
        terrain.LodLevel = 9;
        Assert.Equal((3, 3), (terrain.LodLevel, project.World.LodPreviewLevel));
    }
}
