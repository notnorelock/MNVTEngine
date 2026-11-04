using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan.Shaders;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Mnvts = MNVTEngine.Scripting;

namespace MNVTEngine.Build;

/// <param name="Shaders">Shader stages precompiled into the default resources.</param>
public sealed record ContentResult(IReadOnlyList<BuildDiagnostic> Diagnostics, int AssetCount, int ScriptCount, int Shaders = 0);

/// <summary>
/// Asset compilation for exported games. Opens the project's assets read-only (call <see cref="CreateMissingMetas"/> first), runs
/// every importer, compiles <c>.mnvts</c> scripts to verified bytecode against the full runtime API, loads every scene
/// and prefab with the game's components, checks asset references, and compiles every shader the game uses to SPIR-V
/// (shader errors fail the build). With an output it writes the game content split over archives
/// (<see cref="ContentLayout"/>): globals hold the asset index (GUIDs, paths, types, import settings and archive, so
/// references stay valid) and the project file; default resources hold the precompiled shaders; every asset, with the
/// bytecode of scripts, goes to its level, shared assets or resources archive.
/// </summary>
public static class ContentCompiler
{
    /// <summary>
    /// Gives every asset file without a <c>.meta</c> one, exactly as the editor does when it sees a new file (assets
    /// added with the file explorer, a script written in a text editor). Returns a warning per created meta: it holds
    /// the asset's GUID and must be committed with it.
    /// </summary>
    public static IReadOnlyList<BuildDiagnostic> CreateMissingMetas(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Directory.Exists(project.AssetsDirectory))
        {
            return [];
        }

        var missing = Directory.EnumerateFiles(project.AssetsDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(f).StartsWith('.')
                && !File.Exists(f + AssetDatabase.MetaExtension))
            .ToList();
        if (missing.Count == 0)
        {
            return [];
        }

        AssetDatabase.OpenForEditing(project.AssetsDirectory, Path.Combine(project.RootDirectory, "Library"));
        return [.. missing.Where(f => File.Exists(f + AssetDatabase.MetaExtension)).Select(f => BuildDiagnostic.Warning(
            "MNVB209", "Created its .meta file (the asset's GUID); commit it together with the asset.", AssetPaths.ToRelative(project.AssetsDirectory, f)))];
    }

    /// <param name="registry">Built-in and game components, used to load scenes and prefabs.</param>
    /// <param name="output">Content archives to write; null to validate only.</param>
    public static ContentResult Compile(GameProject project, ComponentRegistry registry, ContentArchiveWriter? output)
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
        var dependencies = new Dictionary<Guid, IReadOnlyCollection<Guid>>();
        var bytecodes = new Dictionary<Guid, byte[]>();
        var readable = new List<AssetRecord>();
        var scripts = 0;

        // Pass 1: import, compile and check every asset; collect references for the layout.
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

            readable.Add(record);
            if (importers.GetValueOrDefault(Path.GetExtension(record.Path)) is { } importer)
            {
                dependencies[record.Guid] = Import(importer, record, data, assets, diagnostics);
            }

            if (record.Type == AssetTypes.Script)
            {
                scripts++;
                if (CompileScript(record.Path, data, diagnostics) is { } bytecode)
                {
                    bytecodes[record.Guid] = bytecode;
                }
            }
            else if (record.Type is AssetTypes.Scene or AssetTypes.Prefab)
            {
                LoadSceneOrPrefab(serializer, record, data, diagnostics);
            }
        }

        // Pass 2: every asset (and its bytecode) into its level, shared assets or resources archive.
        var layout = ContentLayout.Plan(assets, project.StartupScene, dependencies);
        if (output is not null)
        {
            output.DeclareLevels(layout.Levels);
            foreach (var record in readable)
            {
                var archive = output.For(layout.ArchiveOf(record.Guid));
                archive.Add(AssetDatabase.AssetsEntryFolder + record.Path, File.ReadAllBytes(assets.FullPath(record.Guid)));
                if (bytecodes.TryGetValue(record.Guid, out var bytecode))
                {
                    archive.Add(AssetDatabase.CompiledEntry(record.Guid), bytecode);
                }
            }
        }

        var shaders = PrecompileShaders(assets, diagnostics, output?.DefaultResources);
        if (output is not null)
        {
            output.Globals.Add(AssetDatabase.IndexEntry, Encoding.UTF8.GetBytes(assets.ExportIndex(layout.ArchiveOf).ToString(Formatting.None)));
            output.Globals.Add(Path.GetFileName(project.ProjectFile), PackagedProjectFile(project));
        }

        return new ContentResult(diagnostics, assets.Assets.Count, scripts, shaders);
    }

    /// <summary>
    /// Compiles the built-in sprite shader and every material's shader (its fragment stage with the built-in vertex stage,
    /// exactly as the runtime builds it) to SPIR-V, checking them as the device would. Returns the number of stages.
    /// </summary>
    private static int PrecompileShaders(AssetDatabase assets, List<BuildDiagnostic> diagnostics, ContentArchive.Writer? defaultResources)
    {
        using var precompiler = new VulkanShaderPrecompiler();
        precompiler.Add(BuiltinShaders.Sprite);
        precompiler.Add(Graphics.Materials.MaterialShaders.Standard);
        precompiler.Add(Graphics.Materials.MaterialShaders.Unlit);
        foreach (var material in assets.Assets.Where(a => a.Type == AssetTypes.Material).OrderBy(a => a.Path, StringComparer.Ordinal))
        {
            try
            {
                var definition = MaterialDefinition.Parse(Encoding.UTF8.GetString(assets.ReadAllBytes(material.Guid)), material.Path);
                if (definition.Shader is { } shader && assets.GetAssetType(shader) is not null)
                {
                    // Decoded exactly as ResourceCache does, so the source text (and the precompiled entry's key) match.
                    var vertex = definition.VertexShader is { } vertexShader ? Encoding.UTF8.GetString(assets.ReadAllBytes(vertexShader)) : BuiltinShaders.Sprite.VertexGlsl;
                    precompiler.Add(new ShaderSource(material.Path, vertex, Encoding.UTF8.GetString(assets.ReadAllBytes(shader))));
                }
            }
            catch (ShaderCompilationException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB210", ex.Message, material.Path));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SerializationException or ArgumentException)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB211", $"Material cannot be read: {ex.Message}", material.Path));
            }
        }

        foreach (var (entry, spirv) in precompiler.Entries)
        {
            defaultResources?.Add(entry, spirv);
        }

        return precompiler.Entries.Count;
    }

    /// <returns>The assets this one references.</returns>
    private static IReadOnlyCollection<Guid> Import(IAssetImporter importer, AssetRecord record, byte[] data, AssetDatabase assets, List<BuildDiagnostic> diagnostics)
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

        return [.. context.Dependencies.Where(d => d != record.Guid)];
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
