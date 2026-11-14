using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// The build id: a hash of everything a build is made from (engine source revision, profile settings, keys' ids, the project
/// file, every file of Assets with its .meta, the imported artifacts and the compiled C# scripts). Identical inputs give
/// the same id, so identical builds are byte-identical; any change gives a new id, and with it new package GUIDs and
/// new per-package encryption keys.
/// </summary>
public static class BuildIdentity
{
    /// <param name="keyIds">Ids of the encryption and signing keys (they change the output too).</param>
    /// <param name="scriptsOutput">Compiled C# scripts folder; null without scripts.</param>
    /// <param name="engineIdentity">The engine's source revision (<see cref="PipelineVersion.SourceIdentity"/>): not its build number, which changes on every build.</param>
    public static Guid Compute(GameProject project, BuildProfile profile, string? scriptsOutput, string engineIdentity, params IEnumerable<string> keyIds)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Text(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));

        Text("MNVTEngine build v1");
        Text(engineIdentity);
        Text($"{profile.Name}|{profile.Configuration}|{profile.Target}|{profile.ScriptingBackend}|{profile.Compression}|{profile.Encrypt}");
        foreach (var (extension, compression) in profile.CompressionByExtension.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Text($"{extension}={compression}");
        }

        foreach (var id in keyIds)
        {
            Text(id);
        }

        hash.AppendData(File.ReadAllBytes(project.ProjectFile));
        AddFolder(hash, project.AssetsDirectory);
        AddFolder(hash, Path.Combine(project.RootDirectory, "Library", "Artifacts"));
        if (scriptsOutput is not null)
        {
            AddFolder(hash, scriptsOutput);
        }

        return new Guid(hash.GetHashAndReset().AsSpan(0, 16));
    }

    private static void AddFolder(IncrementalHash hash, string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace('\\', '/'))).OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\n"));
            using var stream = File.OpenRead(file.Full);
            hash.AppendData(SHA256.HashData(stream));
        }
    }
}
