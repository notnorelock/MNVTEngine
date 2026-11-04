using MNVTEngine.Assets;
using MNVTEngine.Editor.Services;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Tests;

public sealed class MaterialDocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-matdoc-").FullName;
    private readonly AssetDatabase _assets;
    private readonly Guid _material;

    public MaterialDocumentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        _assets = AssetDatabase.OpenForEditing(Path.Combine(_root, "Assets"), Path.Combine(_root, "Library"));
        _material = _assets.WriteText("Materials/Body.mnvtmat", MaterialDocument.NewStandardMaterialJson());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private MaterialDefinition OnDisk() => MaterialDefinition.Parse(File.ReadAllText(Path.Combine(_root, "Assets", "Materials", "Body.mnvtmat")), "Body.mnvtmat");

    [Fact]
    public void NewMaterials_AreOpaqueWhiteStandard()
    {
        var definition = OnDisk();
        Assert.Equal((MaterialDefinition.BuiltinStandard, "Opaque"), (definition.Builtin, definition.Blend));
        Assert.Equal(MaterialValue.Vector(1f, 1f, 1f, 1f), definition.Properties["_BaseColor"]);
        Assert.Equal(AssetTypes.Material, _assets.GetAssetType(_material));
    }

    [Fact]
    public void Commits_WriteTheFile_AndUndoRedoStepThroughThem()
    {
        var document = new MaterialDocument(_assets, _material);
        Assert.False(document.Commit()); // nothing changed

        document.Definition.Properties["_Metallic"] = MaterialValue.Number(1f);
        Assert.True(document.Commit());
        Assert.Equal(1f, OnDisk().Properties["_Metallic"].Numbers![0]);

        document.Definition.Blend = "Alpha";
        document.Definition.RenderQueue = 3100;
        document.Commit();
        Assert.Equal(("Alpha", (int?)3100), (OnDisk().Blend, OnDisk().RenderQueue));

        document.Undo();
        Assert.Equal(("Opaque", (int?)null), (OnDisk().Blend, OnDisk().RenderQueue));
        Assert.Equal("Opaque", document.Definition.Blend);
        document.Undo();
        Assert.Equal(0f, OnDisk().Properties["_Metallic"].Numbers![0]);
        Assert.False(document.CanUndo);
        document.Redo();
        document.Redo();
        Assert.Equal("Alpha", OnDisk().Blend);
    }

    [Fact]
    public void ShaderSwitches_AndPropertyResets_AreSaved()
    {
        var shader = _assets.WriteText("Shaders/glow.frag", "#version 450 core\nout vec4 fragColor;\nvoid main() { fragColor = vec4(1.0); }\n");
        var document = new MaterialDocument(_assets, _material);
        document.SetShader(null, shader);
        Assert.True(document.ResetProperty("_Roughness"));
        document.Commit();
        Assert.Equal((null, (Guid?)shader), (OnDisk().Builtin, OnDisk().Shader));
        Assert.False(OnDisk().Properties.ContainsKey("_Roughness"));
        Assert.Contains(shader, _assets.Get(_material)!.Dependencies);

        document.SetShader(MaterialDefinition.BuiltinUnlit, null);
        document.Commit();
        Assert.Equal(MaterialDefinition.BuiltinUnlit, OnDisk().Builtin);
        Assert.Throws<ArgumentException>(() => document.SetShader(null, null));
    }
}
