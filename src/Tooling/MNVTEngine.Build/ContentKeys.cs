using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// The master key that encrypts a project's packages: <c>Settings/content.key</c> (hex). Commit it: every machine then
/// builds identical, mutually compatible content. Packages never use it directly (each derives its own data key from it,
/// see <see cref="PackageEncryptionKey"/>). It is embedded, masked, in the game executable, which must decrypt its own
/// content: it protects against casual extraction and tampering, not against reverse engineering.
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
        File.WriteAllText(path, PackageEncryptionKey.Create().ToHex() + "\n");
    }

    /// <summary>
    /// Replaces the project's key with a new one (key rotation): the old key is kept as
    /// <c>Settings/content.key.&lt;id&gt;.retired</c> for inspecting packages built with it. Builds from now on use the new key;
    /// games already shipped keep working, as each carries the key it was built with.
    /// </summary>
    /// <returns>Ids of the retired and the new key.</returns>
    public static (string? Retired, string Current) Rotate(GameProject project)
    {
        var path = PathFor(project);
        string? retired = null;
        if (File.Exists(path))
        {
            var old = PackageEncryptionKey.FromHex(File.ReadAllText(path));
            retired = old.IdText;
            File.Move(path, $"{path}.{retired}.retired", overwrite: true);
        }

        Create(project.RootDirectory);
        return (retired, PackageEncryptionKey.FromHex(File.ReadAllText(path)).IdText);
    }

    /// <summary>The project's key and its retired keys, for tools that open its packages.</summary>
    public static PackageKeyRing KeyRingFor(GameProject project)
    {
        var ring = new PackageKeyRing();
        var settings = Path.GetDirectoryName(PathFor(project))!;
        if (!Directory.Exists(settings))
        {
            return ring;
        }

        foreach (var file in Directory.EnumerateFiles(settings, "content.key*"))
        {
            try
            {
                ring.Add(PackageEncryptionKey.FromHex(File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // not a key file: ignored by tools; the build reports an invalid current key (MNVB108)
            }
        }

        return ring;
    }

    /// <summary>The project's key; creates one (with a warning) if the project has none yet.</summary>
    /// <returns>Null, with an error, if the key file is invalid.</returns>
    public static PackageEncryptionKey? LoadOrCreate(GameProject project, List<BuildDiagnostic> diagnostics)
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
            return PackageEncryptionKey.FromHex(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB108", $"{RelativePath} must hold a {PackageEncryptionKey.Size}-byte key in hex; delete it to generate a new one.", path));
            return null;
        }
    }

    /// <summary>The package key embedded in an exported game's host assembly, read without loading it.</summary>
    /// <returns>Null if the assembly has none.</returns>
    /// <exception cref="BadImageFormatException">Not a .NET assembly.</exception>
    /// <exception cref="FormatException">The embedded key is malformed.</exception>
    public static PackageEncryptionKey? ReadFromHost(string hostAssembly) =>
        ReadHostMetadata(hostAssembly, GamePackage.PackageKeyMetadata) is { } text ? EmbeddedPackageKey.Decode(text) : null;

    /// <summary>The public key an exported game verifies its packages with, read without loading the host assembly.</summary>
    /// <exception cref="BadImageFormatException">Not a .NET assembly.</exception>
    /// <exception cref="ArgumentException">The embedded key is malformed.</exception>
    public static PackageVerificationKey? ReadVerificationKeyFromHost(string hostAssembly) =>
        ReadHostMetadata(hostAssembly, GamePackage.VerificationKeyMetadata) is { } hex ? PackageVerificationKey.FromSubjectPublicKeyInfo(Convert.FromHexString(hex)) : null;

    private static string? ReadHostMetadata(string hostAssembly, string key)
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
            if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() == key && blob.ReadSerializedString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// The key that signs a build's packages and manifest (ECDSA P-256). Release builds should use the studio's release key,
/// passed with <c>--signing-key &lt;file.pem&gt;</c> or the <c>MNVT_SIGNING_KEY</c> environment variable, kept outside
/// the project and never committed. Otherwise builds are signed with this machine's development key
/// (<c>%LOCALAPPDATA%/MNVTEngine/Keys/development-signing-key.pem</c>, created on first use). Only the public key ships.
/// </summary>
public static class SigningKeys
{
    public const string EnvironmentVariable = "MNVT_SIGNING_KEY";

    /// <summary>Folder of the development key; tests point it elsewhere with <c>MNVT_KEYS_DIRECTORY</c>.</summary>
    public static string DevelopmentKeyDirectory =>
        Environment.GetEnvironmentVariable("MNVT_KEYS_DIRECTORY") is { Length: > 0 } folder ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNVTEngine", "Keys");

    public static string DevelopmentKeyPath => Path.Combine(DevelopmentKeyDirectory, "development-signing-key.pem");

    /// <summary>Writes a new signing key (PKCS#8 PEM) to <paramref name="path"/>; returns its public key id.</summary>
    public static string Create(string path)
    {
        using var key = PackageSigningKey.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, key.ExportPrivateKeyPem());
        return key.PublicKey.Id;
    }

    /// <summary>The key a build signs with: <paramref name="explicitPath"/>, then <see cref="EnvironmentVariable"/>, then the development key.</summary>
    /// <returns>Null, with an error, when the given key is unreadable or would ship with the game.</returns>
    public static PackageSigningKey? Resolve(GameProject project, BuildProfile profile, string? explicitPath, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var path = explicitPath ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment ? fromEnvironment : null);
        if (path is null)
        {
            if (profile.Configuration == "Release")
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB114",
                    $"Release build signed with this machine's development key; sign releases with your release key (--signing-key <file.pem> or {EnvironmentVariable})."));
            }

            path = DevelopmentKeyPath;
            if (!File.Exists(path))
            {
                Create(path);
            }
        }

        var full = Path.GetFullPath(path);
        if (full.StartsWith(Path.GetFullPath(project.AssetsDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB113", "The signing key is inside the project's Assets folder, whose files ship with the game; keep it outside the project.", path));
            return null;
        }

        try
        {
            return PackageSigningKey.FromPem(File.ReadAllText(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB112", $"Signing key unreadable: {ex.Message}", path));
            return null;
        }
    }
}
