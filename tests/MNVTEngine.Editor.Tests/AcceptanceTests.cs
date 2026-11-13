using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Build;
using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Editor.Tests;

/// <summary>
/// The 3D spec's acceptance criteria, walked through as a developer would, with the services the editor uses (UI-free).
/// Rendering criteria (17-19) and the standalone run (20) are covered on the GPU by the runtime, Vulkan and build tests
/// listed in docs/graphics/3d-acceptance.md; here the export is checked down to the packed archive.
/// </summary>
public sealed class AcceptanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-acceptance-").FullName;

    public AcceptanceTests()
    {
        Directory.CreateDirectory(AssetsDir);
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt", "loadingScreen": { "enabled": false } }""");
    }

    private string AssetsDir => Path.Combine(_root, "Assets");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private void Copy(string fixture, string target)
    {
        var source = Fixture(fixture);
        foreach (var file in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [source])
        {
            var destination = Directory.Exists(source) ? Path.Combine(AssetsDir, target, Path.GetRelativePath(source, file)) : Path.Combine(AssetsDir, target);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static unsafe void AssimpConvert(string source, string destination, Silk.NET.Assimp.PostProcessSteps steps, string format)
    {
        var api = Silk.NET.Assimp.Assimp.GetApi();
        var scene = api.ImportFile(source, (uint)steps);
        Assert.True(scene is not null, api.GetErrorStringS());
        try
        {
            Assert.Equal(Silk.NET.Assimp.Return.Success, api.ExportScene(scene, format, destination, 0));
        }
        finally
        {
            api.ReleaseImport(scene);
        }
    }

    /// <summary>A glTF with one mesh of two triangles, each a primitive with its own material (buffer embedded).</summary>
    private static string TwoMaterialMesh()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0, 3, 0, 0, 2, 1, 0];
        var buffer = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(positions.AsSpan()).ToArray());
        return $$"""
            {
              "asset": { "version": "2.0" },
              "scene": 0, "scenes": [ { "nodes": [ 0 ] } ], "nodes": [ { "name": "Panel", "mesh": 0 } ],
              "materials": [ { "name": "Red", "pbrMetallicRoughness": { "baseColorFactor": [ 1, 0, 0, 1 ] } }, { "name": "Blue", "pbrMetallicRoughness": { "baseColorFactor": [ 0, 0, 1, 1 ] } } ],
              "meshes": [ { "name": "Panel", "primitives": [ { "attributes": { "POSITION": 0 }, "material": 0 }, { "attributes": { "POSITION": 1 }, "material": 1 } ] } ],
              "buffers": [ { "byteLength": 72, "uri": "data:application/octet-stream;base64,{{buffer}}" } ],
              "bufferViews": [ { "buffer": 0, "byteOffset": 0, "byteLength": 36 }, { "buffer": 0, "byteOffset": 36, "byteLength": 36 } ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [ 0, 0, 0 ], "max": [ 1, 1, 0 ] },
                { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3", "min": [ 2, 0, 0 ], "max": [ 3, 1, 0 ] } ]
            }
            """;
    }

    private static Guid Guid(AssetDatabase db, string path)
    {
        Assert.True(db.TryGetGuid(path, out var guid), path);
        Assert.True(db.Get(guid)!.Status == AssetImportStatus.Imported, $"{path}: {string.Join("; ", db.Get(guid)!.Messages)}");
        return guid;
    }

    private static ModelData Model(AssetDatabase db, Guid guid) => ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);

    [Fact]
    public void TheModelWorkflow_FromImportToExport()
    {
        Copy("WoodenCrate/fbx", "Crate");
        Copy("Khronos/CesiumMan.glb", "Characters/CesiumMan.glb");
        Directory.CreateDirectory(Path.Combine(AssetsDir, "Props"));
        // One mesh with two materials on its submeshes (two glTF primitives).
        File.WriteAllText(Path.Combine(AssetsDir, "Props", "TwoMaterials.gltf"), TwoMaterialMesh());
        var db = AssetDatabase.OpenForEditing(AssetsDir, Path.Combine(_root, "Library"), ModelImporters.WithDefaults());

        // 1, 2. FBX and GLB import.
        var crateGuid = Guid(db, "Crate/wooden_crate_01_1k.fbx");
        var characterGuid = Guid(db, "Characters/CesiumMan.glb");
        var crate = Model(db, crateGuid);

        // 3, 4. Several meshes, and the hierarchy (lid and latch are children of the root with their own transforms).
        Assert.Equal(3, crate.Meshes.Count);
        Assert.True(crate.Nodes.Count >= 4);
        Assert.All(crate.Nodes.Skip(1), n => Assert.InRange(n.Parent, 0, crate.Nodes.Count - 1));

        // 5. Several materials on the submeshes of one mesh.
        var propGuid = Guid(db, "Props/TwoMaterials.gltf");
        var prop = Model(db, propGuid);
        Assert.Contains(prop.Meshes, m => m.SubMeshMaterials.Distinct().Count() >= 2 && m.Mesh.SubMeshes.Count >= 2);

        // 6. External textures resolve to project textures (and are dependencies).
        var diffuse = crate.Textures.Single(t => t.ProjectAsset is not null);
        Assert.Contains(diffuse.ProjectAsset!.Value, db.Get(crateGuid)!.Dependencies);

        // 7, 9. Embedded textures are extracted into a project folder.
        var character = new ModelWorkflow(db, characterGuid);
        Assert.Contains(Model(db, characterGuid).Textures, t => t.Embedded is not null);
        character.ExtractTextures(character.DefaultFolder("Textures"));
        Assert.All(Model(db, characterGuid).Textures, t => Assert.Equal(AssetTypes.Texture, db.GetAssetType(t.ProjectAsset!.Value)));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(AssetsDir, "Characters", "Textures")));

        // 8. Materials are extracted into editable .mnvtmat files.
        var crateWorkflow = new ModelWorkflow(db, crateGuid);
        crateWorkflow.ExtractMaterials(crateWorkflow.DefaultFolder("Materials"), crateWorkflow.DefaultFolder("Textures"));
        var extracted = Model(db, crateGuid).Materials.Single().Override!.Value;
        Assert.Equal(AssetTypes.Material, db.GetAssetType(extracted));

        // 11. Material properties are edited (what the inspector does) and saved.
        var document = new MaterialDocument(db, extracted);
        document.Definition.Properties["_Roughness"] = MaterialValue.Number(0.2f);
        document.Commit();
        Assert.Equal(0.2f, MaterialDefinition.Parse(File.ReadAllText(db.FullPath(extracted)), "check").Properties["_Roughness"].Numbers![0]);

        // 10. An imported material is remapped to an existing material.
        var red = db.WriteText("Materials/Red.mnvtmat", """{ "schemaVersion": 2, "shader": "builtin:standard", "blend": "Opaque", "properties": { "_BaseColor": [1, 0, 0, 1] } }""");
        var propWorkflow = new ModelWorkflow(db, propGuid);
        var propMaterial = prop.Materials[0].Name;
        propWorkflow.RemapMaterial(propMaterial, red);
        Assert.Equal(red, Model(db, propGuid).FindMaterial(propMaterial)!.Override);

        // 15. Skeletal model with an animation.
        var walker = Model(db, characterGuid);
        Assert.Single(walker.Skins);
        Assert.Single(walker.Animations);

        // 16. Instantiated into a scene, which is saved with references to the models' meshes.
        var scene = new Scene { Name = "Main" };
        ModelInstantiation.Instantiate(scene, crateGuid, crate, "Crate");
        ModelInstantiation.Instantiate(scene, characterGuid, walker, "Cesium Man");
        ModelInstantiation.Instantiate(scene, propGuid, Model(db, propGuid), "Prop");
        var camera = scene.CreateGameObject("Camera").AddComponent<Camera>();
        camera.Projection = CameraProjection.Perspective;
        scene.CreateGameObject("Sun").AddComponent<Light>().Shadows = true;
        var serializer = new SceneSerializer(Build.EngineModuleSet.CreateRegistry());
        db.WriteText("Scenes/Main.mnvt", serializer.Serialize(scene));
        var references = scene.FindObjectsOfType<MeshRenderer>().Select(r => r.Mesh).ToList();
        Assert.Equal(crate.Meshes.Count + walker.Meshes.Count + Model(db, propGuid).Meshes.Count, references.Count);

        // 13, 14. Modified FBX and GLB files reimport; the scene's references, extracted textures and remaps stay valid.
        var fbxPath = Path.Combine(AssetsDir, "Crate", "wooden_crate_01_1k.fbx");
        AssimpConvert(fbxPath, fbxPath + ".tmp.fbx", 0, "fbx"); // a re-saved file: different bytes, same content
        File.Move(fbxPath + ".tmp.fbx", fbxPath, overwrite: true);
        var glbPath = Path.Combine(AssetsDir, "Characters", "CesiumMan.glb");
        File.AppendAllBytes(glbPath, new byte[8]); // padding after the GLB chunks changes the file, not the content
        var sourceHashes = (db.Get(crateGuid)!.SourceHash, db.Get(characterGuid)!.SourceHash);
        db.Refresh();
        Assert.NotEqual(sourceHashes, (db.Get(crateGuid)!.SourceHash, db.Get(characterGuid)!.SourceHash)); // both reimported
        Guid(db, "Crate/wooden_crate_01_1k.fbx");
        Guid(db, "Characters/CesiumMan.glb");
        var reloaded = serializer.Deserialize(File.ReadAllText(db.FullPath(db.TryGetGuid("Scenes/Main.mnvt", out var sceneGuid) ? sceneGuid : default))).Scene;
        foreach (var reference in reloaded.FindObjectsOfType<MeshRenderer>().Select(r => r.Mesh))
        {
            var name = reference.SubAsset![ModelCache.MeshSubAssetPrefix.Length..];
            Assert.True(Model(db, reference.Guid).FindMesh(name) is not null, $"{reference} still resolves after the reimport");
        }

        Assert.Equal(extracted, Model(db, crateGuid).Materials.Single().Override);
        Assert.All(Model(db, characterGuid).Textures, t => Assert.NotNull(t.ProjectAsset));

        // 20. The exported game's content holds the processed models, their textures and materials (not the sources).
        var project = GameProject.Load(_root);
        Assert.DoesNotContain(ContentCompiler.ImportModels(project), d => d.Severity == BuildSeverity.Error);
        var key = IO.Packages.PackageEncryptionKey.Create();
        var output = Path.Combine(_root, "Export");
        Directory.CreateDirectory(output);
        ContentResult result;
        IReadOnlyList<ContentPackage> archives;
        using (var writer = new ContentPackageWriter(output, new ContentPackageOptions(System.Guid.NewGuid(), key, Signer: null)))
        {
            result = ContentCompiler.Compile(project, Build.EngineModuleSet.CreateRegistry(), writer);
            archives = writer.Complete();
        }

        Assert.False(result.Diagnostics.HasErrors(), string.Join("\n", result.Diagnostics));
        using var archive = GameContent.Open(output, archives.Select(a => a.File), new IO.Packages.PackageOpenOptions(new IO.Packages.PackageKeyRing(key)));
        var packaged = AssetDatabase.OpenPackaged(archive.Files);
        foreach (var model in new[] { crateGuid, characterGuid, propGuid })
        {
            Assert.NotNull(packaged.ReadArtifact(model, ModelArtifact.Name));
        }

        Assert.DoesNotContain(archive.Entries, e => e.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
        Assert.True(archive.Contains(AssetDatabase.AssetsEntryFolder + db.Get(extracted)!.Path), "the extracted material ships");
        Assert.True(archive.Contains(AssetDatabase.AssetsEntryFolder + db.Get(diffuse.ProjectAsset!.Value)!.Path), "the model's texture ships");
        _ = JObject.Parse(File.ReadAllText(db.FullPath(crateGuid) + ".meta")); // the meta stays valid JSON after every change
    }
}
