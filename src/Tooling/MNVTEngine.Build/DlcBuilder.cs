using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Graphics;
using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;
using Newtonsoft.Json;

namespace MNVTEngine.Build;

/// <summary>What <see cref="DlcBuilder.Build"/> wrote.</summary>
/// <param name="KeyFile">With a separate key: where it was written (for the game's server; never ship it).</param>
public sealed record DlcResult(string? PackageFile, string? KeyFile, int Assets, IReadOnlyList<BuildDiagnostic> Diagnostics);

/// <summary>
/// Builds a DLC package (<c>mnvt package dlc</c>) from the assets in one folder of the project (e.g. <c>Assets/DLC/Forest</c>):
/// models as their artifacts, scripts with their bytecode, textures with their streaming proxy, and an asset index
/// (<c>Index/&lt;name&gt;.json</c>) the game merges over its own. Signed with the game's signing key, so games accept it;
/// encrypted with the project key, or with a key of its own whose file goes to the game's server
/// (<see cref="Runtime.GameContent.MountDlc"/>). C# code is not packaged: DLC carries content, not assemblies.
/// Copy the package into <c>&lt;Game&gt;_Data/DLC</c>.
/// </summary>
public static class DlcBuilder
{
    /// <param name="folder">Path relative to Assets whose assets the DLC holds.</param>
    /// <param name="output">Folder for the package (and key file); default <c>Builds/dlc</c>.</param>
    /// <param name="separateKey">Encrypt with a new key of its own instead of the project's, written next to the package.</param>
    public static DlcResult Build(GameProject project, BuildProfile profile, string folder, string name, string? output, string? signingKeyPath, bool separateKey)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        var diagnostics = new List<BuildDiagnostic>();
        if (!GamePackage.IsValidExecutableName(name))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB115", $"'{name}' is not a valid DLC name (letters, digits, '_', '-', '.'; starting with a letter)."));
            return new DlcResult(null, null, 0, diagnostics);
        }

        string root;
        try
        {
            root = AssetPaths.Canonical(folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) ? folder[7..] : folder);
        }
        catch (ArgumentException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB115", ex.Message, folder));
            return new DlcResult(null, null, 0, diagnostics);
        }

        diagnostics.AddRange(ContentCompiler.CreateMissingMetas(project));
        diagnostics.AddRange(ContentCompiler.ImportModels(project));
        var assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory, EngineImporters.All(), artifactsLibrary: ContentCompiler.LibraryOf(project));
        var records = assets.Assets.Where(r => VirtualPath.IsUnder(r.Path, root)).OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
        if (records.Count == 0)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB115", $"No assets in Assets/{root}.", root));
            return new DlcResult(null, null, 0, diagnostics);
        }

        var key = separateKey ? PackageEncryptionKey.Create() : ContentKeys.LoadOrCreate(project, diagnostics);
        using var signer = SigningKeys.Resolve(project, profile, signingKeyPath, diagnostics);
        if (key is null || signer is null || diagnostics.HasErrors())
        {
            return new DlcResult(null, null, 0, diagnostics);
        }

        var directory = Path.GetFullPath(output ?? Path.Combine(project.RootDirectory, "Builds", "dlc"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name + GamePackage.PackageExtension);
        var included = records.Select(r => r.Guid).ToHashSet();

        // Identity from the content, so rebuilding unchanged DLC gives the same package.
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData(Encoding.UTF8.GetBytes($"MNVTEngine DLC v1\n{name}\n{GamePackager.EngineVersion}\n{key.IdText}\n{signer.PublicKey.Id}\n"));
        foreach (var record in records)
        {
            identity.AppendData(Encoding.UTF8.GetBytes(record.Path + "\n"));
            identity.AppendData(SHA256.HashData(assets.ReadAllBytes(record.Guid)));
        }

        var digest = identity.GetHashAndReset();
        var options = new PackageWriterOptions(new Guid(digest.AsSpan(0, 16)), new Guid(digest.AsSpan(16, 16)), profile.Compression,
            EncryptionKey: profile.Encrypt || separateKey ? key : null, SigningKey: signer,
            Properties: new Dictionary<string, string> { ["kind"] = "dlc", ["game"] = project.Name, ["engine"] = GamePackager.EngineVersion });
        using (var writer = new PackageWriter(file, options))
        {
            foreach (var record in records)
            {
                if (record.Type == AssetTypes.Model)
                {
                    if (ContentCompiler.ModelDependencies(assets, record, diagnostics) is { } model)
                    {
                        writer.Add(AssetDatabase.ArtifactEntry(record.Guid, ModelArtifact.Name), model.Artifacts[0].Data, record.Guid);
                        foreach (var (artifactName, bytes) in model.Artifacts.Skip(1))
                        {
                            writer.Add(AssetDatabase.ArtifactEntry(record.Guid, artifactName), bytes); // streamed mesh geometry
                        }
                    }

                    continue; // a missing or unreadable artifact was reported
                }

                var data = assets.ReadAllBytes(record.Guid);
                writer.Add(AssetDatabase.AssetsEntryFolder + record.Path, data, record.Guid);
                if (record.Type == AssetTypes.Script && ContentCompiler.CompileScript(record.Path, data, diagnostics) is { } bytecode)
                {
                    writer.Add(AssetDatabase.CompiledEntry(record.Guid), bytecode);
                }

                if (record.Type == AssetTypes.Texture && project.Streaming.Textures)
                {
                    foreach (var (artifact, bytes) in ContentCompiler.StreamingArtifacts(assets, record, diagnostics))
                    {
                        writer.Add(AssetDatabase.ArtifactEntry(record.Guid, artifact), bytes);
                    }
                }
            }

            var index = assets.ExportIndex(_ => Path.GetFileName(file), included.Contains);
            writer.Add($"{AssetDatabase.ExtraIndexFolder}/{name}.json", Encoding.UTF8.GetBytes(index.ToString(Formatting.None)));
            if (!diagnostics.HasErrors())
            {
                writer.Complete();
            }
        }

        if (diagnostics.HasErrors())
        {
            File.Delete(file); // never leave an incomplete package behind
            return new DlcResult(null, null, 0, diagnostics);
        }

        string? keyFile = null;
        if (separateKey)
        {
            keyFile = Path.Combine(directory, name + ".key");
            File.WriteAllText(keyFile, key.ToHex() + "\n");
            diagnostics.Add(BuildDiagnostic.Warning("MNVB116", $"The DLC has its own key in {Path.GetFileName(keyFile)}: give it to your server, which hands it to games that may play the DLC (DownloadableContent.Mount). Never ship it.", keyFile));
        }

        return new DlcResult(file, keyFile, records.Count, diagnostics);
    }
}
