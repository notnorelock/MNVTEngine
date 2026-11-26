// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.Bundles;
using MNVTEngine.IO;
using MNVTEngine.Runtime;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>What <see cref="BundleBuilder.Build"/> wrote.</summary>
public sealed record BundleResult(string? File, int Assets, IReadOnlyList<BuildDiagnostic> Diagnostics);

/// <summary>What to package: the root prefab, what the bundle is for, and which assets the game already has.</summary>
/// <param name="Root">The root prefab, relative to the project (<c>Assets/Worlds/Lounge.prefab</c>) or to Assets.</param>
/// <param name="Kind">What the bundle is for, as the game that loads it expects (<c>borealise.world</c>).</param>
/// <param name="Provided">
/// Folders (relative to Assets) whose assets the game already ships, such as an SDK's shared tilesets: referenced, never
/// packaged (a bundle cannot carry an asset the game has).
/// </param>
/// <param name="Properties">Short strings for the manifest (SDK version, author).</param>
/// <param name="Policy">What the game accepts; the build refuses what the game would.</param>
public sealed record BundleRequest(
    string Root, string Kind, string Name, string? Output = null, IReadOnlyList<string>? Provided = null,
    IReadOnlyDictionary<string, string>? Properties = null, ContentBundlePolicy? Policy = null);

/// <summary>
/// Builds a content bundle (<c>mnvt package bundle</c>, docs/architecture/content-bundles.md): the root prefab and every
/// asset it references, except those in provided folders, as an unencrypted, unsigned <c>.mnvtbundle</c> with its
/// manifest and index. Bundles never carry code: a referenced script fails the build. The written file is opened again
/// with the policy, so what the build accepts the game accepts too.
/// </summary>
public static class BundleBuilder
{
    public static BundleResult Build(GameProject project, BundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = new List<BuildDiagnostic>();
        var policy = request.Policy ?? new ContentBundlePolicy();
        if (request.Kind.Length is 0 or > 64 || request.Name.Length is 0 or > 128)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB122", "A bundle needs a kind of 1-64 characters and a name of 1-128."));
            return new BundleResult(null, 0, diagnostics);
        }

        string rootPath;
        IReadOnlyList<string> provided;
        try
        {
            rootPath = WithoutAssets(request.Root);
            provided = [.. (request.Provided ?? []).Select(WithoutAssets)];
        }
        catch (ArgumentException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB122", ex.Message, request.Root));
            return new BundleResult(null, 0, diagnostics);
        }

        diagnostics.AddRange(ContentCompiler.CreateMissingMetas(project));
        var assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory, EngineImporters.All(), artifactsLibrary: ContentCompiler.LibraryOf(project));
        if (!assets.TryGetGuid(rootPath, out var root) || assets.Get(root) is not { Type: AssetTypes.Prefab })
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB122", $"Assets/{rootPath} is not a prefab.", rootPath));
            return new BundleResult(null, 0, diagnostics);
        }

        var records = new List<AssetRecord>();
        foreach (var guid in References(assets, root, provided, diagnostics))
        {
            if (assets.Get(guid) is not { } record)
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB124", $"The bundle references asset {guid}, which is not in the project; it will be missing.", rootPath));
            }
            else if (!provided.Any(folder => VirtualPath.IsUnder(record.Path, folder)))
            {
                if (record.Type == AssetTypes.Script || !policy.AllowedAssetTypes.Contains(record.Type))
                {
                    diagnostics.Add(BuildDiagnostic.Error("MNVB123", $"Assets/{record.Path}: assets of type {record.Type} cannot go in a bundle (bundles carry no code).", record.Path));
                }

                records.Add(record);
            }
        }

        if (diagnostics.HasErrors())
        {
            return new BundleResult(null, 0, diagnostics);
        }

        records.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var directory = Path.GetFullPath(request.Output ?? Path.Combine(project.RootDirectory, "Builds", "bundles"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, FileName(request.Name) + ContentBundleFile.Extension);

        // Identity from the content, so rebuilding an unchanged bundle gives the same file.
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData(Encoding.UTF8.GetBytes($"MNVTEngine bundle v{ContentBundleFile.SchemaVersion}\n{request.Kind}\n{request.Name}\n{root:D}\n"));
        var data = new Dictionary<Guid, byte[]>();
        foreach (var record in records)
        {
            data[record.Guid] = assets.ReadAllBytes(record.Guid);
            identity.AppendData(Encoding.UTF8.GetBytes(record.Path + "\n"));
            identity.AppendData(SHA256.HashData(data[record.Guid]));
        }

        var digest = identity.GetHashAndReset();
        var manifest = new ContentBundleManifest(request.Kind, request.Name, root, request.Properties ?? new Dictionary<string, string>());
        var contents = records.Select(r => (new ContentBundleAsset(r.Guid, r.Path, r.Type, r.Importer, assets.GetSettings(r.Guid)), data[r.Guid])).ToList();
        ContentBundleFile.Write(file, manifest, contents, new Guid(digest.AsSpan(0, 16)), new Guid(digest.AsSpan(16, 16)),
            new Dictionary<string, string> { ["kind"] = "bundle", ["bundleKind"] = request.Kind, ["game"] = project.Name });

        try
        {
            using var check = ContentBundleFile.Open(file, policy with { Kind = request.Kind });
        }
        catch (ContentBundleException ex)
        {
            File.Delete(file); // never leave a bundle the game would refuse
            diagnostics.AddRange(ex.Problems.Select(p => BuildDiagnostic.Error("MNVB123", p, file)));
            return new BundleResult(null, 0, diagnostics);
        }

        return new BundleResult(file, records.Count, diagnostics);
    }

    private static readonly HashSet<string> Documents = new(StringComparer.Ordinal)
    {
        AssetTypes.Prefab, AssetTypes.Scene, AssetTypes.Animation, AssetTypes.Material, AssetTypes.AnimatorController, AssetTypes.ParticleSystem, AssetTypes.Json,
    };

    /// <summary>
    /// The root and every asset it reaches. A read-only database does not import, so JSON documents (prefabs, clips,
    /// materials) are scanned for <c>asset:</c> references here; assets in provided folders are not followed.
    /// </summary>
    private static HashSet<Guid> References(AssetDatabase assets, Guid root, IReadOnlyList<string> provided, List<BuildDiagnostic> diagnostics)
    {
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>([root]);
        while (stack.TryPop(out var guid))
        {
            if (!result.Add(guid) || assets.Get(guid) is not { } record || provided.Any(folder => VirtualPath.IsUnder(record.Path, folder)))
            {
                continue;
            }

            var references = new HashSet<Guid>(record.Dependencies);
            if (Documents.Contains(record.Type))
            {
                try
                {
                    AssetImporters.CollectReferences(JToken.Parse(Encoding.UTF8.GetString(assets.ReadAllBytes(guid))), references);
                }
                catch (JsonException ex)
                {
                    diagnostics.Add(BuildDiagnostic.Error("MNVB123", $"Assets/{record.Path}: not valid JSON ({ex.Message}).", record.Path));
                }
            }

            foreach (var reference in references.Where(r => r != guid))
            {
                stack.Push(reference);
            }
        }

        return result;
    }

    private static string WithoutAssets(string path) =>
        AssetPaths.Canonical(path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase) ? path[7..] : path);

    /// <summary>The bundle's name as a file name: letters, digits, '-' and '_' kept, the rest '-'.</summary>
    private static string FileName(string name) => new([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
}
