using MNVTEngine.Assets;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// A material asset open in the inspector. The panel edits <see cref="Definition"/> directly; <see cref="Commit"/>
/// (called when an edit ends) writes the file, so the scene shows the change at once, as in Unity, and records an undo
/// step. UI-free and testable.
/// </summary>
public sealed class MaterialDocument
{
    private readonly AssetDatabase _assets;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string _committed;

    /// <exception cref="SerializationException">The file is not a valid material.</exception>
    public MaterialDocument(AssetDatabase assets, Guid guid)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _assets = assets;
        Guid = guid;
        Path = assets.TryGetPath(guid, out var path) ? path : throw new KeyNotFoundException($"Asset {guid} does not exist.");
        Definition = MaterialDefinition.Parse(System.Text.Encoding.UTF8.GetString(assets.ReadAllBytes(guid)), Path);
        _committed = Definition.ToJson();
    }

    public Guid Guid { get; }

    /// <summary>Project-relative path of the material.</summary>
    public string Path { get; }

    /// <summary>The material being edited; change it, then call <see cref="Commit"/>.</summary>
    public MaterialDefinition Definition { get; private set; }

    /// <summary>The JSON last written (or loaded); compare to detect changes.</summary>
    public string Committed => _committed;

    public bool CanUndo => _undo.Count > 0 || Definition.ToJson() != _committed;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Writes the material if it changed since the last commit, and records an undo step. Returns true when written.</summary>
    public bool Commit()
    {
        var json = Definition.ToJson();
        if (json == _committed)
        {
            return false;
        }

        _undo.Add(_committed);
        _redo.Clear();
        Write(json);
        return true;
    }

    public void Undo()
    {
        Commit();
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Add(_committed);
        var json = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Write(json);
        Definition = MaterialDefinition.Parse(json, Path);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Add(_committed);
        var json = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Write(json);
        Definition = MaterialDefinition.Parse(json, Path);
    }

    /// <summary>Changes the shader; properties that the new shader does not document are kept (custom shaders may use them).</summary>
    public void SetShader(string? builtin, Guid? custom)
    {
        if ((builtin is null) == (custom is null))
        {
            throw new ArgumentException("Give either a built-in shader id or a custom shader asset.");
        }

        Definition.Builtin = builtin;
        Definition.Shader = custom;
        if (custom is null)
        {
            Definition.VertexShader = null;
        }
    }

    /// <summary>Removes a property so it takes the shader's default again.</summary>
    public bool ResetProperty(string name) => Definition.Properties.Remove(name);

    /// <summary>A new Standard material: opaque, white, medium roughness.</summary>
    public static string NewStandardMaterialJson()
    {
        var definition = new MaterialDefinition { Builtin = MaterialDefinition.BuiltinStandard, Blend = "Opaque" };
        definition.Properties["_BaseColor"] = MaterialValue.Vector(1f, 1f, 1f, 1f);
        definition.Properties["_Metallic"] = MaterialValue.Number(0f);
        definition.Properties["_Roughness"] = MaterialValue.Number(0.5f);
        return definition.ToJson();
    }

    private void Write(string json)
    {
        _assets.WriteText(Path, json);
        _committed = json;
    }
}
