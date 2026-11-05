using MNVTEngine.Mathematics;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Models.Tests;

/// <summary>Lights and cameras in model files, textures on UV set 1, and mesh aliases.</summary>
public sealed class LightsCamerasAndUVSetsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-models-").FullName;

    public LightsCamerasAndUVSetsTests() => Directory.CreateDirectory(AssetsDir);

    private string AssetsDir => Path.Combine(_root, "Assets");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private ModelData Import(string fixture, JObject? settings = null)
    {
        var name = Path.GetFileName(fixture);
        File.Copy(Fixture(fixture), Path.Combine(AssetsDir, name));
        if (settings is not null)
        {
            // The first open writes the meta; the settings then change it and the second open reimports.
            _ = AssetDatabase.OpenForEditing(AssetsDir, Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
            var metaPath = Path.Combine(AssetsDir, name + ".meta");
            var meta = JObject.Parse(File.ReadAllText(metaPath));
            meta["settings"] = settings;
            File.WriteAllText(metaPath, meta.ToString());
        }

        var db = AssetDatabase.OpenForEditing(AssetsDir, Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        Assert.True(db.TryGetGuid(name, out var guid));
        var record = db.Get(guid)!;
        Assert.True(record.Status == AssetImportStatus.Imported, string.Join("\n", record.Messages));
        return ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);
    }

    private static int Node(ModelData model, string name) => model.Nodes.FindIndex(n => n.Name == name);

    [Fact]
    public void GltfPunctualLights_ImportInEngineUnits_OnTheirNodes()
    {
        var model = Import("Lights/PunctualLights.gltf");

        Assert.Equal(3, model.Lights.Count);
        var sun = model.Lights.Single(l => l.Type == ModelLightType.Directional);
        Assert.Equal((Node(model, "SunNode"), 3f, Quaternion.Identity), (sun.Node, sun.Intensity, sun.Rotation));
        Assert.Equal(1f, sun.Color.R, 3); // linear (1, 0.5, 0.25) as sRGB
        Assert.Equal(0.7354f, sun.Color.G, 3);
        Assert.Equal(0.5371f, sun.Color.B, 3);

        var spot = model.Lights.Single(l => l.Type == ModelLightType.Spot);
        Assert.Equal(Node(model, "SpotNode"), spot.Node);
        Assert.Equal(1f, spot.Intensity, 3);                   // 54351.4 cd: a 1000 W lamp
        Assert.Equal(12f, spot.Range);
        Assert.Equal(2f * 0.6f * Mathf.Rad2Deg, spot.SpotAngle, 2); // half angles become full angles
        Assert.Equal(2f * 0.3f * Mathf.Rad2Deg, spot.InnerSpotAngle, 2);

        var bulb = model.Lights.Single(l => l.Type == ModelLightType.Point);
        Assert.Equal(0.1f, bulb.Intensity, 3);
        Assert.Null(bulb.Range); // infinite in glTF
    }

    [Fact]
    public void GltfCameras_Import_PerspectiveAndOrthographic()
    {
        var model = Import("Khronos/Cameras.gltf");

        Assert.Equal(2, model.Cameras.Count);
        var perspective = model.Cameras.Single(c => !c.Orthographic);
        Assert.Equal(0.7f * Mathf.Rad2Deg, perspective.FieldOfView, 2);
        Assert.Equal((0.01f, 100f), (perspective.NearClip, perspective.FarClip));
        var orthographic = model.Cameras.Single(c => c.Orthographic);
        Assert.Equal(1f, orthographic.OrthographicSize);
        Assert.All(model.Cameras, c => Assert.Equal(new Vector3(0.5f, 0.5f, 3f), model.Nodes[c.Node].Position));
    }

    [Fact]
    public void LightsAndCameras_CanBeTurnedOff()
    {
        var model = Import("Lights/PunctualLights.gltf", new JObject { ["importLights"] = false, ["importCameras"] = false });
        Assert.Empty(model.Lights);
        Assert.Empty(model.Cameras);
    }

    [Fact]
    public void AssimpLightsAndCameras_ConvertFromItsConventions()
    {
        // FBX imports go through Assimp; its glTF importer produces the same light and camera structures, so the
        // conversion is tested with the glTF fixtures read by Assimp.
        ModelData Read(string fixture)
        {
            var path = Fixture(fixture);
            var warnings = new List<string>();
            var source = new ModelSource(Path.GetFileName(path), File.ReadAllBytes(path), path, null, _ => null);
            return new AssimpModelReader().Read(source, new ModelImportSettings(), warnings, new HashSet<Guid>());
        }

        var lights = Read("Lights/PunctualLights.gltf");
        Assert.Equal(3, lights.Lights.Count);
        var sun = lights.Lights.Single(l => l.Type == ModelLightType.Directional);
        Assert.Equal("SunNode", lights.Nodes[sun.Node].Name);
        Assert.Equal(3f, sun.Intensity, 3);                      // Assimp folds intensity into the color
        Assert.Equal(new Color(1f, 0.5f, 0.25f, 1f), sun.Color); // the brightest channel is 1
        Assert.Contains(lights.Lights, l => l.Type == ModelLightType.Spot && lights.Nodes[l.Node].Name == "SpotNode");

        var cameras = Read("Khronos/Cameras.gltf");
        var perspective = cameras.Cameras.Single(c => !c.Orthographic);
        Assert.Equal(0.7f * Mathf.Rad2Deg, perspective.FieldOfView, 1); // aspect 1: vertical = horizontal
        Assert.Equal(Quaternion.Identity, perspective.Rotation);
    }

    [Fact]
    public void TexturesOnUVSet1_AreSampledWithIt()
    {
        var model = Import("Khronos/MultiUVTest.glb");

        var material = Assert.Single(model.Materials);
        Assert.Equal(1, material.UVSets[TextureRole.Emission]);
        Assert.False(material.UVSets.ContainsKey(TextureRole.BaseColor));
        Assert.NotNull(model.Meshes[0].Mesh.UV1);
        Assert.DoesNotContain(model.Warnings, w => w.Contains("UV set", StringComparison.Ordinal));
    }

    [Fact]
    public void MeshAliases_KeepOldNamesResolving_AndStaleOnesAreReported()
    {
        var model = Import("Lights/PunctualLights.gltf", new JObject
        {
            ["meshAliases"] = new JObject { ["Ground"] = "Floor", ["Gone"] = "Missing", ["Floor"] = "Floor" },
        });

        Assert.Equal("Floor", model.FindMesh("Ground")!.Name);
        Assert.Null(model.FindMesh("Gone"));
        Assert.Contains(model.Warnings, w => w.Contains("'Gone' points to 'Missing'", StringComparison.Ordinal));
        Assert.Equal("Floor", Assert.Single(model.MeshAliases).Value); // "Floor" -> "Floor" was dropped as a no-op
    }

    [Fact]
    public void ArtifactV4_RoundTripsLightsCamerasUVSetsAndAliases()
    {
        var model = Import("Lights/PunctualLights.gltf");
        model.Materials.Add(new ModelMaterial("M"));
        model.Materials[^1].UVSets[TextureRole.Occlusion] = 1;
        model.Cameras.Add(new ModelCamera("Cam", 1, false, 50f, 5f, 0.2f, 300f, Quaternion.Euler(0f, 90f, 0f)));
        model.MeshAliases["Old"] = "Floor";

        var copy = ModelArtifact.Read(ModelArtifact.Write(model));

        Assert.Equal(model.Lights, copy.Lights);
        Assert.Equal(model.Cameras, copy.Cameras);
        Assert.Equal(1, copy.Materials[^1].UVSets[TextureRole.Occlusion]);
        Assert.Equal("Floor", copy.FindMesh("Old")!.Name);

        model.Lights.Add(model.Lights[0] with { Node = 99 });
        Assert.Throws<InvalidDataException>(() => ModelArtifact.Read(ModelArtifact.Write(model)));
    }
}
