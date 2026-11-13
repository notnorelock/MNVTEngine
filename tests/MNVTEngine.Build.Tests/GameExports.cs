namespace MNVTEngine.Build.Tests;

/// <summary>
/// Test classes that publish a real game host (dotnet publish of GameHost). They share the engine's build outputs, so
/// two publishes at once can collide on locked files: these classes run one after another. Builds sign with a
/// development key of their own (<see cref="ExportSigningKeys"/>), not the user's.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameExports : ICollectionFixture<ExportSigningKeys>
{
    public const string Name = "Game exports";
}

/// <summary>Points the development signing key (SigningKeys.DevelopmentKeyDirectory) at a temporary folder for the export tests.</summary>
public sealed class ExportSigningKeys : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mnvt-keys-").FullName;

    public ExportSigningKeys() => Environment.SetEnvironmentVariable("MNVT_KEYS_DIRECTORY", _directory);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MNVT_KEYS_DIRECTORY", null);
        Directory.Delete(_directory, recursive: true);
    }
}
