using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build;

/// <summary>
/// Every importer, for the editor and the build: the core ones, models (Assimp, glTF), mission scripts and terrain.
/// Importers are tools: exported games read imported content and never ship them.
/// </summary>
public static class EngineImporters
{
    public static IReadOnlyList<IAssetImporter> All() => [.. Assets.Models.ModelImporters.WithDefaults(), new ScriptImporter(), new TerrainImporter(), new ParticleSystemImporter()];
}

/// <summary>
/// Compiles <c>.mnvts</c> scripts on import, so errors show when a script is saved rather than when the game runs.
/// Functions outside <see cref="MNVTEngine.Scripting.Missions.StandardApi.All"/> are allowed with a warning (game code may add modules).
/// </summary>
public sealed class ScriptImporter() : SimpleImporter("Script", AssetTypes.Script, ".mnvts")
{
    public override int Version => 2;

    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data);
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
            return;
        }

        var result = MNVTEngine.Scripting.Compiler.ScriptCompiler.Compile(text, context.RelativePath, new MNVTEngine.Scripting.Compiler.CompileOptions
        {
            Api = MNVTEngine.Scripting.Missions.StandardApi.All,
            AllowUnknownFunctions = true,
        });
        foreach (var diagnostic in result.Diagnostics)
        {
            (diagnostic.Severity == MNVTEngine.Scripting.Syntax.DiagnosticSeverity.Error ? context.Errors : context.Warnings).Add(diagnostic.ToString());
        }

        if (result.Script is { } script)
        {
            context.Properties["missions"] = string.Join(", ", script.Missions.Select(m => m.Id));
            context.Properties["lines"] = (text.Count(c => c == '\n') + (text.Length > 0 ? 1 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Terrain worlds (<c>.mnvtterrain</c>, TerrainHeaven project JSON): validates the file and records the textures and prop
/// meshes it references as dependencies (paths next to the file or from the Assets root, or <c>asset:</c> references), so
/// builds include them. A reference that matches no asset is a warning: the terrain draws without it.
/// </summary>
/// <summary>Particle systems: validated with the runtime's parser; the texture and material they use are dependencies.</summary>
public sealed class ParticleSystemImporter() : SimpleImporter("ParticleSystem", AssetTypes.ParticleSystem, Particles.ParticleSystemAsset.Extension)
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var settings = Particles.ParticleSystemAsset.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data), context.RelativePath);
            foreach (var reference in Particles.ParticleSystemAsset.References(settings))
            {
                context.Dependencies.Add(reference.Guid);
            }

            context.Properties["maxParticles"] = settings.Main.MaxParticles.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Properties["modules"] = string.Join(", ", settings.EnabledModules().DefaultIfEmpty("none"));
        }
        catch (SerializationException ex)
        {
            context.Errors.AddRange(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Path}: {d.Message}").DefaultIfEmpty(ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            context.Errors.Add($"Not valid UTF-8 text: {ex.Message}");
        }
    }
}

public sealed class TerrainImporter() : SimpleImporter("Terrain", AssetTypes.Terrain, ".mnvtterrain")
{
    public override void Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        MNVTEngine.Terrain.Project.ProjectData data;
        try
        {
            data = MNVTEngine.Terrain.Project.TerrainProject.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(context.Data));
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            context.Errors.Add(ex.Message);
            return;
        }

        context.Properties["elements"] = (data.Elements?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var reference in MNVTEngine.Terrain.Project.TerrainProject.CollectReferences(data))
        {
            if (MNVTEngine.Terrain.Project.TerrainProject.TryParseAssetReference(reference, out var guid, out _))
            {
                context.Dependencies.Add(guid);
                continue;
            }

            var resolved = MNVTEngine.Terrain.Project.TerrainProject.ResolveCandidates(context.RelativePath, reference).Select(context.ResolveAsset).FirstOrDefault(g => g is not null);
            if (resolved is { } found)
            {
                context.Dependencies.Add(found);
            }
            else
            {
                context.Warnings.Add($"'{reference}' matches no asset; the terrain draws without it.");
            }
        }
    }
}
