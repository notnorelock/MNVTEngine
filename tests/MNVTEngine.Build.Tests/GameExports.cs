namespace MNVTEngine.Build.Tests;

/// <summary>
/// Test classes that publish a real game host (dotnet publish of GameHost). They share the engine's build outputs, so
/// two publishes at once can collide on locked files: these classes run one after another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GameExports
{
    public const string Name = "Game exports";
}
