using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MNVTEngine.Assets;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// The key that encrypts a project's exported content: <c>Settings/content.key</c> (hex). Commit it: every machine then
/// builds identical, mutually compatible content. It is embedded in the game executable, which must decrypt its own
/// content, so it protects against casual extraction and tampering, not against reverse engineering.
/// </summary>
public static class ContentKeys
{
    public const string RelativePath = "Settings/content.key";

    public static string PathFor(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(project.RootDirectory, RelativePath);
    }

    /// <summary>Writes a new random key for a project.</summary>
    public static void Create(string projectRoot)
    {
        var path = Path.Combine(projectRoot, RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Convert.ToHexString(ContentArchive.CreateKey()) + "\n");
    }

    /// <summary>The project's key; creates one (with a warning) if the project has none yet.</summary>
    /// <returns>Null, with an error, if the key file is invalid.</returns>
    public static byte[]? LoadOrCreate(GameProject project, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var path = PathFor(project);
        if (!File.Exists(path))
        {
            Create(project.RootDirectory);
            diagnostics.Add(BuildDiagnostic.Warning("MNVB107", $"Created a content encryption key in {RelativePath}; commit it so every machine builds the same content.", path));
        }

        try
        {
            var key = Convert.FromHexString(File.ReadAllText(path).Trim());
            if (key.Length == ContentArchive.KeySize)
            {
                return key;
            }
        }
        catch (FormatException)
        {
            // reported below
        }

        diagnostics.Add(BuildDiagnostic.Error("MNVB108", $"{RelativePath} must hold a {ContentArchive.KeySize}-byte key in hex; delete it to generate a new one.", path));
        return null;
    }

    /// <summary>Reads the key embedded in an exported game's host assembly, without loading it.</summary>
    /// <returns>Null if the assembly has no key.</returns>
    /// <exception cref="BadImageFormatException">Not a .NET assembly.</exception>
    public static byte[]? ReadFromHost(string hostAssembly)
    {
        using var pe = new PEReader(File.OpenRead(hostAssembly));
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference
                || metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) != "AssemblyMetadataAttribute")
            {
                continue;
            }

            // Blob: prolog 0x0001, then the two string arguments (key, value).
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() == GamePackage.ContentKeyMetadata && blob.ReadSerializedString() is { } hex)
            {
                return Convert.FromHexString(hex);
            }
        }

        return null;
    }
}
