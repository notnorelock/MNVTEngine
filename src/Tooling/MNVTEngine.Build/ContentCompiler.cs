using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Mnvts = MNVTEngine.Scripting;

namespace MNVTEngine.Build;

public sealed record ContentResult(IReadOnlyList<BuildDiagnostic> Diagnostics, int AssetCount, int ScriptCount);

/// <summary>
/// Asset compilation for exported games. Opens the project's assets read-only (the project is never modified), runs
/// every importer, compiles <c>.mnvts</c> scripts to verified bytecode against the full runtime API, loads every scene
/// and prefab with the game's components, and checks asset references. With an archive it writes the game content:
/// the asset index (GUIDs, paths, types, import settings, so references stay valid), every asset, bytecode per script
/// GUID, and the project file.
/// </summary>
public static class ContentCompiler
{
    /// <param name="registry">Built-in and game components, used to load scenes and prefabs.</param>
    /// <param name="archive">Content archive to write; null to validate only.</param>
    public static ContentResult Compile(GameProject project, ComponentRegistry registry, ContentArchive.Writer? archive)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(registry);
        var diagnostics = new List<BuildDiagnostic>();
        var assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory);
        diagnostics.AddRange(assets.Diagnostics.Select(d => BuildDiagnostic.FromSerialization("MNVB201", d)));

        var importers = AssetImporters.CreateDefault()
            .SelectMany(i => i.Extensions.Select(e => (Extension: e, Importer: i)))
            .ToDictionary(p => p.Extension, p => p.Importer, StringComparer.OrdinalIgnoreCase);
        var serializer = new SceneSerializer(registry, assets);
        var scripts = 0;
        foreach (var record in assets.Assets.OrderBy(r => r.Path, StringComparer.Ordinal))
        {
            var fullPath = assets.FullPath(record.Guid);
            byte[] data;
            try
            {
                data = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB202", $"Cannot read asset: {ex.Message}", record.Path));
                continue;
            }

            if (importers.GetValueOrDefault(Path.GetExtension(record.Path)) is { } importer)
            {
                Import(importer, record, data, assets, diagnostics);
            }

            byte[]? bytecode = null;
            if (record.Type == AssetTypes.Script)
            {
                bytecode = CompileScript(record.Path, data, diagnostics);
                scripts++;
            }
            else if (record.Type is AssetTypes.Scene or AssetTypes.Prefab)
            {
                LoadSceneOrPrefab(serializer, record, data, diagnostics);
            }

            if (archive is not null)
            {
                archive.Add(AssetDatabase.AssetsEntryFolder + record.Path, data);
                if (bytecode is not null)
                {
                    archive.Add(AssetDatabase.CompiledEntry(record.Guid), bytecode);
                }
            }
        }

        if (archive is not null)
        {
            archive.Add(AssetDatabase.IndexEntry, Encoding.UTF8.GetBytes(assets.ExportIndex().ToString(Formatting.None)));
            archive.Add(Path.GetFileName(project.ProjectFile), PackagedProjectFile(project));
        }

        return new ContentResult(diagnostics, assets.Assets.Count, scripts);
    }

    private static void Import(IAssetImporter importer, AssetRecord record, byte[] data, AssetDatabase assets, List<BuildDiagnostic> diagnostics)
    {
        var problems = new List<string>();
        var context = new AssetImportContext(record.Path, data, importer.NormalizeSettings(assets.GetSettings(record.Guid), problems));
        importer.Import(context);
        diagnostics.AddRange(problems.Select(p => BuildDiagnostic.Warning("MNVB203", $"Import settings: {p}", record.Path)));

        // The strict script compile below reports script diagnostics with line numbers.
        if (record.Type != AssetTypes.Script)
        {
            diagnostics.AddRange(context.Errors.Select(e => BuildDiagnostic.Error("MNVB204", e, record.Path)));
            diagnostics.AddRange(context.Warnings.Select(w => BuildDiagnostic.Warning("MNVB205", w, record.Path)));
        }

        foreach (var missing in context.Dependencies.Where(d => assets.GetAssetType(d) is null).Order())
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB206", $"References asset {missing}, which is not in the project.", record.Path));
        }
    }

    /// <summary>Strict compile: unlike the editor's import check, unknown API functions are errors because the game would fail to link.</summary>
    private static byte[]? CompileScript(string path, byte[] data, List<BuildDiagnostic> diagnostics)
    {
        string source;
        try
        {
            source = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB204", $"Not valid UTF-8 text: {ex.Message}", path));
            return null;
        }

        var result = Mnvts.Compiler.ScriptCompiler.Compile(source, path, new Mnvts.Compiler.CompileOptions { Api = StandardApi.All });
        diagnostics.AddRange(result.Diagnostics.Select(d => new BuildDiagnostic(
            d.Severity == Mnvts.Syntax.DiagnosticSeverity.Error ? BuildSeverity.Error : BuildSeverity.Warning,
            d.Code,
            d.Message,
            path,
            d.Location.Line,
            d.Location.Column)));
        return result.Succeeded ? BytecodeSerializer.Write(result.Script!) : null;
    }

    private static void LoadSceneOrPrefab(SceneSerializer serializer, AssetRecord record, byte[] data, List<BuildDiagnostic> diagnostics)
    {
        var json = Encoding.UTF8.GetString(data);
        try
        {
            var loaded = record.Type == AssetTypes.Scene
                ? serializer.Deserialize(json, record.Path).Diagnostics
                : serializer.InstantiatePrefab(json, new Scene(), null, record.Path).Diagnostics;
            diagnostics.AddRange(loaded.Select(d => BuildDiagnostic.FromSerialization("MNVB207", d, record.Path)));
        }
        catch (SerializationException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB208", ex.Message, record.Path));
        }
    }

    /// <summary>The project file without development-only settings (C# project path, build profiles).</summary>
    private static byte[] PackagedProjectFile(GameProject project)
    {
        var document = JObject.Parse(File.ReadAllText(project.ProjectFile));
        document.Remove("scripts");
        document.Remove("buildProfiles");
        return Encoding.UTF8.GetBytes(document.ToString(Formatting.Indented));
    }
}
