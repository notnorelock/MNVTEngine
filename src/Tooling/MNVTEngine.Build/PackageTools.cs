using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Developer tooling for <c>.mnvtpack</c> files (<c>mnvt package ...</c>): inspect, verify, extract and analyze. These are
/// the engine's own authorized tools; they open encrypted packages with the project's keys, or with the key of the game
/// the package belongs to. Extraction is a tool, never a runtime feature, and its output is not an editable project.
/// </summary>
public static class PackageTools
{
    /// <summary>
    /// Keys and trust for opening a package outside the game: the project's key ring (current and retired keys), and the
    /// encryption and verification keys of the game build the package sits in, when it sits in one.
    /// </summary>
    /// <param name="project">A project whose keys to use; null for none.</param>
    /// <param name="trustedKeyFile">A public key (PEM) to trust; null trusts the key of the game the package belongs to.</param>
    public static PackageOpenOptions OptionsFor(string packageOrFolder, GameProject? project = null, string? trustedKeyFile = null)
    {
        var keys = project is null ? new PackageKeyRing() : ContentKeys.KeyRingFor(project);
        var trusted = new List<PackageVerificationKey>();
        if (trustedKeyFile is not null)
        {
            var pem = PemEncoding.Find(File.ReadAllText(trustedKeyFile));
            trusted.Add(PackageVerificationKey.FromSubjectPublicKeyInfo(Convert.FromBase64String(File.ReadAllText(trustedKeyFile)[pem.Base64Data])));
        }

        if (FindHost(packageOrFolder) is { } host)
        {
            try
            {
                if (ContentKeys.ReadFromHost(host) is { } embedded)
                {
                    keys.Add(embedded);
                }

                if (trustedKeyFile is null && ContentKeys.ReadVerificationKeyFromHost(host) is { } verification)
                {
                    trusted.Add(verification);
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FormatException or ArgumentException)
            {
                // a native (ahead-of-time) host: its keys are compiled in; use the project's keys
            }
        }

        return new PackageOpenOptions(keys, trusted);
    }

    /// <summary>A readable description of a package: public header, then (with its key) contents and sizes.</summary>
    public static string Inspect(string file, PackageOpenOptions options, bool listEntries = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = new StringBuilder();
        var info = MnvtPackage.ReadInfo(file);
        Line($"Package      {Path.GetFileName(file)}  ({Size(info.FileLength)})");
        Line($"Format       {info.FormatVersion}");
        Line($"Package GUID {info.PackageGuid}");
        Line($"Build id     {info.BuildId}");
        Line($"Chunk size   {Size(info.ChunkSize)}");
        Line($"Encrypted    {(info.IsEncrypted ? $"yes (AES-256-GCM, key {PackageEncryptionKey.FormatId(info.KeyId)})" : "no")}");
        Line($"Signed       {(info.IsSigned ? $"yes (ECDSA P-256, key {info.SignerId})" : "no")}");
        foreach (var (key, value) in info.Properties)
        {
            Line($"  {key,-10} {value}");
        }

        try
        {
            using var package = MnvtPackage.Open(file, options);
            var entries = package.Entries.ToList();
            var original = entries.Sum(e => e.Length);
            Line($"Signature    {package.Signature}");
            Line($"Files        {entries.Count}, {Size(original)} in a {Size(info.FileLength)} package ({Ratio(info.FileLength, original)}), {entries.Count - entries.Select(e => e.ContentSha256).Distinct().Count()} duplicates stored once");
            foreach (var group in entries.GroupBy(e => TopFolder(e.Path)).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Line($"  {group.Key + "/",-12} {group.Count(),5} files  {Size(group.Sum(e => e.Length)),10} -> {Size(group.Sum(e => e.StoredLength)),10}");
            }

            if (listEntries)
            {
                foreach (var entry in entries)
                {
                    Line($"  {entry.Path}  {Size(entry.Length)} -> {Size(entry.StoredLength)}{(entry.AssetGuid != Guid.Empty ? $"  asset {entry.AssetGuid}" : "")}{(entry.Dependencies.Count > 0 ? $"  {entry.Dependencies.Count} dependencies" : "")}");
                }
            }
        }
        catch (PackageException ex) when (ex.Error is PackageError.MissingKey)
        {
            Line($"Contents     not shown: {ex.Message} Pass --project <project> to use the project's keys.");
        }

        return text.ToString();

        void Line(string line) => text.Append(line).Append('\n');
    }

    /// <summary>Opens a package with full checks and reads every chunk.</summary>
    /// <returns>Problems found; empty when the package is intact (and, with trusted keys, authentic).</returns>
    public static IReadOnlyList<string> Verify(string file, PackageOpenOptions options, bool requireTrustedSignature)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using var package = MnvtPackage.Open(file, options with { RequireSignature = requireTrustedSignature });
            var result = package.Verify();
            var problems = result.Problems.ToList();
            if (package.Signature == PackageSignatureStatus.UnknownSigner)
            {
                problems.Add($"signed by key {package.Info.SignerId}, which is not trusted here (pass --trust <public key.pem>); integrity was checked, authenticity was not");
            }

            return problems;
        }
        catch (PackageException ex)
        {
            return [$"{ex.Error}: {ex.Message}"];
        }
    }

    /// <summary>Writes every file of a package below <paramref name="outputDirectory"/>. The output is inspection material, not a project.</summary>
    /// <returns>Number of files written.</returns>
    /// <exception cref="PackageException">The package cannot be opened or a file is damaged.</exception>
    public static int Extract(string file, string outputDirectory, PackageOpenOptions options)
    {
        using var package = MnvtPackage.Open(file, options);
        var root = Path.GetFullPath(outputDirectory);
        var count = 0;
        foreach (var entry in package.Entries)
        {
            // Entry paths are canonical virtual paths (checked when the index was read): they cannot leave the root.
            var target = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException(PackageError.Malformed, $"'{entry.Path}' would be written outside the output folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = package.OpenRead(entry.Path);
            using var output = File.Create(target);
            input.CopyTo(output);
            count++;
        }

        return count;
    }

    /// <summary>
    /// What packages a build of the project would contain (<c>mnvt package analyze</c>): per package the files and their
    /// size, duplicate content, and the assets stripped as unused; from <see cref="ContentCompiler"/> without writing anything.
    /// </summary>
    public static string Analyze(GameProject project, ContentResult content)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(content);
        var text = new StringBuilder();
        if (content.Usage is not { } usage || content.Layout is not { } layout)
        {
            return "The content did not compile; see the diagnostics.\n";
        }

        var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var packages = new SortedDictionary<string, (int Files, long Bytes)>(StringComparer.Ordinal);
        foreach (var asset in usage.Included)
        {
            var file = project.ResolveAsset(asset.Path);
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            var package = layout.PackageOf(asset.Guid);
            packages[package] = (packages.GetValueOrDefault(package).Files + 1, packages.GetValueOrDefault(package).Bytes + size);
            if (File.Exists(file))
            {
                using var stream = File.OpenRead(file);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                (byHash.TryGetValue(hash, out var same) ? same : byHash[hash] = []).Add(asset.Path);
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"{project.Name}: {usage.Included.Count} assets shipped, {usage.Stripped.Count} stripped as unused\n");
        foreach (var (package, (files, bytes)) in packages)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {package,-26} {files,5} assets  {Size(bytes),10} (before compression)\n");
        }

        foreach (var duplicates in byHash.Values.Where(v => v.Count > 1))
        {
            text.Append(CultureInfo.InvariantCulture, $"  duplicate content (stored once per package): {string.Join(", ", duplicates)}\n");
        }

        foreach (var stripped in usage.Stripped)
        {
            text.Append(CultureInfo.InvariantCulture, $"  stripped: {stripped.Path} ({stripped.Reason})\n");
        }

        return text.ToString();
    }

    /// <summary>The game host assembly of the build a package or data folder belongs to; null when it is not in one.</summary>
    private static string? FindHost(string packageOrFolder)
    {
        var full = Path.GetFullPath(packageOrFolder);
        var data = File.Exists(full) ? Path.GetDirectoryName(full)! : full;
        if (!data.EndsWith(GamePackage.DataFolderSuffix, StringComparison.OrdinalIgnoreCase))
        {
            data = GamePackage.FindDataDirectory(data) ?? "";
        }

        var name = Path.GetFileName(data);
        if (name.Length <= GamePackage.DataFolderSuffix.Length)
        {
            return null;
        }

        var host = Path.Combine(Path.GetDirectoryName(data)!, name[..^GamePackage.DataFolderSuffix.Length] + ".dll");
        return File.Exists(host) ? host : null;
    }

    private static string TopFolder(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.IndexOf('/', StringComparison.Ordinal)] : "(root)";

    private static string Ratio(long stored, long original) => original == 0 ? "-" : ((double)stored / original).ToString("P0", CultureInfo.InvariantCulture);

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB"),
    };
}
