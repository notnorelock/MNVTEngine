using MNVTEngine.Modules;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime.Tests;

public sealed class ProjectProfileMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mnvt-profile-" + Guid.NewGuid().ToString("N"));

    public ProjectProfileMigrationTests() => Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string ProjectFile => Path.Combine(_root, "Game.mnvtproj");

    private void WriteProject(string profileProperty, int schemaVersion = 2) =>
        File.WriteAllText(ProjectFile, $$"""{ "schemaVersion": {{schemaVersion}}, "name": "Game", {{profileProperty}} "startupScene": "Scenes/Main.mnvt", "window": { "width": 640, "height": 360 } }""");

    private void WriteScene(string file, params string[] components) =>
        File.WriteAllText(Path.Combine(_root, "Assets", file),
            "{ \"schemaVersion\": 3, \"name\": \"S\", \"gameObjects\": [ { \"id\": \"" + Guid.NewGuid() + "\", \"name\": \"O\", \"components\": ["
            + string.Join(", ", components.Select(c => $"{{ \"type\": \"{c}\", \"data\": {{}} }}")) + "] } ] }");

    [Theory]
    [InlineData("\"profile\": \"2d\",", ProjectProfile.TwoD)]
    [InlineData("\"profile\": \"3D\",", ProjectProfile.ThreeD)]
    public void CurrentProjects_DeclareTheirProfile(string property, ProjectProfile expected)
    {
        WriteScene("Scenes/Main.mnvt");
        WriteProject(property);
        var project = GameProject.Load(_root);
        Assert.Equal(expected, project.Profile);
        Assert.False(project.NeedsProfileMigration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"profile\": \"hybrid\",")]
    [InlineData("\"profile\": 2,")]
    public void CurrentProjects_WithoutAValidProfile_DoNotLoad(string property)
    {
        WriteScene("Scenes/Main.mnvt");
        WriteProject(property);
        var ex = Assert.Throws<SerializationException>(() => GameProject.Load(_root));
        Assert.Contains("profile", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyProject_Loads_AndIsMigratedToItsInferredProfile_WithABackup()
    {
        WriteScene("Scenes/Main.mnvt", "MNVTEngine.Camera", "MNVTEngine.MeshRenderer");
        WriteProject("", schemaVersion: 1);
        var original = File.ReadAllText(ProjectFile);
        var project = GameProject.Load(_root);
        Assert.True(project.NeedsProfileMigration);

        // Resolving (build, run) infers without touching the file.
        var resolved = ProjectProfileMigration.Resolve(project);
        Assert.Equal((ProjectProfile.ThreeD, true), (resolved.Profile, resolved.IsInferred));
        Assert.Equal(original, File.ReadAllText(ProjectFile));

        var migrated = ProjectProfileMigration.Migrate(project);
        Assert.Equal(ProjectProfile.ThreeD, migrated.Profile);
        Assert.Equal(original, File.ReadAllText(ProjectFile + ".v1.bak"));
        var saved = JObject.Parse(File.ReadAllText(ProjectFile));
        Assert.Equal(("3d", 2, 640), (saved.Value<string>("profile"), saved.Value<int>("schemaVersion"), saved["window"]!.Value<int>("width")));
        Assert.False(File.Exists(ProjectFile + ".tmp"));

        var reloaded = GameProject.Load(_root);
        Assert.Equal(ProjectProfile.ThreeD, reloaded.Profile);
        Assert.Null(ProjectProfileMigration.Migrate(reloaded).Inference); // already migrated: nothing to do
        Assert.Equal(original, File.ReadAllText(ProjectFile + ".v1.bak")); // the backup is never overwritten
    }

    [Fact]
    public void LegacyProject_WithMixedContent_NeedsAnExplicitChoice()
    {
        WriteScene("Scenes/Main.mnvt", "MNVTEngine.SpriteRenderer");
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Prefabs"));
        WriteScene("Prefabs/Crate.mnvtprefab", "MNVTEngine.Rigidbody");
        WriteProject("", schemaVersion: 1);
        var original = File.ReadAllText(ProjectFile);
        var project = GameProject.Load(_root);

        var conflict = Assert.Throws<ProfileConflictException>(() => ProjectProfileMigration.Resolve(project));
        Assert.Contains("Prefabs/Crate.mnvtprefab", conflict.Message, StringComparison.Ordinal);
        Assert.Throws<ProfileConflictException>(() => ProjectProfileMigration.Migrate(project));
        Assert.Equal(original, File.ReadAllText(ProjectFile)); // nothing written without a decision

        Assert.Equal(ProjectProfile.TwoD, ProjectProfileMigration.Migrate(project, ProjectProfile.TwoD).Profile);
        Assert.Equal(ProjectProfile.TwoD, GameProject.Load(_root).Profile);
    }

    [Fact]
    public void AChoiceThatContradictsTheContent_IsRejected()
    {
        WriteScene("Scenes/Main.mnvt", "MNVTEngine.Tilemap");
        WriteProject("", schemaVersion: 1);
        var project = GameProject.Load(_root);
        Assert.Throws<InvalidOperationException>(() => ProjectProfileMigration.Migrate(project, ProjectProfile.ThreeD));
        Assert.True(GameProject.Load(_root).NeedsProfileMigration);

        // A project without profile-specific content can be either.
        WriteScene("Scenes/Main.mnvt", "MNVTEngine.Camera");
        Assert.Equal(ProjectProfile.ThreeD, ProjectProfileMigration.Migrate(GameProject.Load(_root), ProjectProfile.ThreeD).Profile);
        Assert.Throws<InvalidOperationException>(() => ProjectProfileMigration.Migrate(GameProject.Load(_root), ProjectProfile.TwoD));
    }

    [Fact]
    public void BrokenScenes_AreSkippedAndListed()
    {
        WriteScene("Scenes/Main.mnvt", "MNVTEngine.MeshRenderer");
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Broken.mnvt"), "{ not json");
        WriteProject("", schemaVersion: 1);
        var unreadable = new List<string>();
        Assert.Equal(ProjectProfile.ThreeD, ProjectProfileMigration.Infer(GameProject.Load(_root), unreadable).Profile);
        Assert.Equal(["Scenes/Broken.mnvt"], unreadable);
    }

    [Fact]
    public void EveryProfileSpecificComponentId_IsARegisteredEngineComponent()
    {
        // Guards the inference table against renamed or removed components.
        var registry = EngineComponents.CreateRegistry();
        var ids = typeof(ProfileInference).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Select(f => f.GetValue(null)).OfType<HashSet<string>>().SelectMany(s => s).ToList();
        Assert.True(ids.Count > 20);
        Assert.All(ids, id => Assert.True(registry.TryGetType(id, out _), $"{id} is not registered."));
    }
}
