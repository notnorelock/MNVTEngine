using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Project;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Terrain.Tests;

public sealed class TerrainProjectTests
{
    private sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static TerrainProject BuildSample()
    {
        var project = new TerrainProject();
        var intersection = new Intersection(Vec3.Zero, 4);
        var road = new Road(new Vec3(6, 0, 0), new Vec3(20, 0, 0)) { Width = 4 };
        var terrain = new Elements.Terrain(new Vec3(0, -1, 0), 40, 40);
        road.SetGroupTextureReference("road", "textures/asphalt.png");
        road.SetTextureRotation("road", 90);
        terrain.TextureLayers[0].TexturePath = "textures/grass.png";
        project.World.Add(intersection);
        project.World.Add(road);
        project.World.Add(terrain);
        road.Connect(0, intersection, 1);
        project.World.CutPoints.SetRadius(project.World.CutPoints.AddPoint(new Vec3(10, 0, 10)), 6);
        project.Settings.Hour = 18;
        project.Settings.SetLodDistance(1, 120);
        var grass = project.Foliage.AddType(new FoliageTypeData { DisplayName = "Grass", TexturePath = "textures/grass_blade.png" });
        project.Foliage.AddInstance(grass, new FoliageInstanceData { Position = PointData.From(new Vec3(3, 0, 3)), ScaleT = 0.5 });
        var rock = project.Meshes.AddAsset(MeshAssetData.Create("Rock", "rock.obj"));
        project.Meshes.AddInstance(rock, new MeshInstanceData { Position = PointData.From(new Vec3(1, 0, 1)), RotationY = 45 });
        return project;
    }

    [Fact]
    public void SaveWritesTerrainHeavenFieldNames()
    {
        var json = JObject.Parse(BuildSample().Save());
        Assert.Equal(1, (int)json["version"]!);
        Assert.Equal(["intersection", "road", "terrain"], json["elements"]!.Select(e => (string)e["type"]!));
        Assert.Equal(90, (double)json["elements"]![1]!["textureRotations"]!["road"]!); // dictionary keys stay as written
        Assert.Single(json["connections"]!);
        Assert.Equal(6, (double)json["terrainCutPoints"]![0]!["radius"]!);
        Assert.Equal(["textures/asphalt.png", "textures/grass.png", "textures/grass_blade.png"], json["texturePaths"]!.Select(t => (string)t!));
        Assert.Equal("Grass", (string)json["foliage"]!["Types"]![0]!["DisplayName"]!);
        Assert.Equal(3, (double)json["foliage"]!["Layers"]![0]!["Instances"]![0]!["Position"]!["x"]!);
        Assert.Equal(45, (double)json["meshes"]!["Layers"]![0]!["Instances"]![0]!["RotationY"]!);
        Assert.Equal(120, (double)json["settings"]!["lodDistances"]![1]!);
    }

    [Fact]
    public void LoadRestoresElementsConnectionsAndSideData()
    {
        var restored = new TerrainProject();
        restored.Load(BuildSample().Save());
        Assert.Equal(3, restored.World.Elements.Count);
        var road = Assert.IsType<Road>(restored.World.Elements[1]);
        var intersection = Assert.IsType<Intersection>(restored.World.Elements[0]);
        Assert.True(road.IsConnected(0));
        Assert.Same(intersection.GetNode(1), road.GetNode(0));
        Assert.Equal("textures/asphalt.png", road.GetGroupTextureReferences()["road"]);
        Assert.Equal(90, road.TextureRotations["road"]);
        Assert.Equal("textures/grass.png", Assert.IsType<Elements.Terrain>(restored.World.Elements[2]).TextureLayers[0].TexturePath);
        Assert.Equal(6, restored.World.CutPoints.GetRadius(restored.World.CutPoints.Points[0]));
        Assert.Equal((18.0, 120.0), (restored.Settings.Hour, restored.Settings.LodDistances[1]));
        Assert.Equal(0.5, restored.Foliage.GetInstances(0)[0].ScaleT);
        Assert.Equal("rock.obj", restored.Meshes.Assets[0].LibraryPath);
        Assert.Equal(BuildSample().Save().Length, restored.Save().Length);
    }

    [Fact]
    public void LoadSkipsBuildingsAndBrokenConnectionsWithWarnings()
    {
        var logger = new ListLogger();
        var project = new TerrainProject(logger);
        project.Load("""
        {
          "version": 1,
          "settings": { "skyColor": "#ffffff", "dayNightCycle": false, "hour": 99 },
          "elements": [
            { "type": "building", "id": 0, "nodes": [{ "x": 0, "y": 0, "z": 0 }], "textures": {}, "textureRotations": {} },
            { "type": "road", "id": 1, "nodes": [{ "x": 0, "y": 0, "z": 0 }, { "x": 10, "y": 0, "z": 0 }], "textures": {}, "textureRotations": {} }
          ],
          "connections": [{ "elementA": 0, "nodeA": 0, "elementB": 1, "nodeB": 0 }, { "elementA": 1, "nodeA": 9, "elementB": 7, "nodeB": 0 }]
        }
        """);
        Assert.IsType<Road>(Assert.Single(project.World.Elements));
        Assert.Equal(24, project.Settings.Hour);
        Assert.Contains(logger.Messages, m => m.Contains("'building'", StringComparison.Ordinal));
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("connection", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("{ \"version\": 2, \"elements\": [], \"connections\": [] }")]
    [InlineData("not json")]
    [InlineData("")]
    public void LoadRejectsInvalidFiles(string json) => Assert.Throws<InvalidDataException>(() => new TerrainProject().Load(json));

    [Theory]
    [InlineData("./textures\\grass.png", "textures/grass.png")]
    [InlineData("https://example.com/assets/my%20rock.png?v=2", "my rock.png")]
    [InlineData("C:\\textures\\a.png", "C:/textures/a.png")]
    [InlineData("data:image/png;base64,AAAA", "legacy-texture-ce4918c.png")]
    public void NormalizesTexturePathsLikeTerrainHeaven(string source, string expected) =>
        Assert.Equal(expected, TerrainProject.NormalizeTexturePath(source));

    [Fact]
    public void SettingsEvaluateTheSkyForEachMode()
    {
        var settings = new ProjectSettings();
        settings.LoadData(new ProjectSettingsData { SkyColor = "#ffffff" });
        var flat = settings.Evaluate();
        Assert.Equal((Vec3.One, false, (Vec3?)null), (flat.Background, flat.SkyDomeVisible, flat.FogColor));

        settings.EnhancedVisuals = true;
        settings.Hour = 12;
        var noon = settings.Evaluate();
        Assert.True(noon.SunVisible && noon.SunLightDirection.Y > 0.9 && noon.SunCastsShadows);
        Assert.Equal(ProjectSettings.Hex(0x7298ba).X, ProjectSettings.GetSkyPalette(10).Zenith.X, 12);
        Assert.Equal(0, noon.StarOpacity);

        settings.Hour = 0;
        var midnight = settings.Evaluate();
        Assert.True(midnight.MoonVisible && !midnight.SunVisible && midnight.StarOpacity > 0.7);
        Assert.True(midnight.SunLightDirection.Y > 0.9); // the moon lights from above

        settings.DayNightCycle = true;
        settings.DayLength = 240;
        Assert.False(settings.Advance(0.05));
        Assert.True(settings.Advance(0.1));
        Assert.Equal(0.15 * 24 / 240, settings.Hour, 9);
    }
}
