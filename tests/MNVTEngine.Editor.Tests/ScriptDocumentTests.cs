using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Scripting.Compiler;
using MNVTEngine.Scripting.Missions;

namespace MNVTEngine.Editor.Tests;

public sealed class ScriptDocumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-docs-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameProject NewProject()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Scripts"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Scripts", "Game.Scripts.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "My Game.mnvtproj"), """{ "schemaVersion": 1, "name": "My Game", "startupScene": "Scenes/Main.mnvt", "scripts": { "project": "Scripts/Game.Scripts.csproj" } }""");
        return GameProject.Load(_root);
    }

    [Fact]
    public void Editing_TracksUnsavedChanges_AndSavingWritesTheFile()
    {
        var path = Path.Combine(_root, "a.mnvts");
        File.WriteAllText(path, "on start {\r\n    log(\"hi\")\r\n}\r\n");
        var document = ScriptDocument.Open(path);
        Assert.Equal(ScriptKind.Mission, document.Kind);
        Assert.DoesNotContain('\r', document.Text);
        Assert.False(document.IsDirty);

        document.Text = document.Text.Replace("hi", "hello", StringComparison.Ordinal);
        Assert.True(document.IsDirty);
        document.Save();
        Assert.False(document.IsDirty);
        Assert.Contains("hello", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.ReadAllBytes(path).AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })); // no BOM
    }

    [Fact]
    public void ExternalChanges_ReloadCleanDocuments_AndNeverOverwriteUnsavedEdits()
    {
        var path = Path.Combine(_root, "b.cs");
        File.WriteAllText(path, "class A {}");
        var document = ScriptDocument.Open(path);

        File.WriteAllText(path, "class B {}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.False(document.CheckDisk());
        Assert.Equal("class B {}", document.Text);

        document.Text = "class Mine {}";
        File.WriteAllText(path, "class C {}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
        Assert.True(document.CheckDisk()); // conflict reported
        Assert.Equal("class Mine {}", document.Text);
    }

    [Fact]
    public void MissionDocuments_AreCheckedLive()
    {
        var path = Path.Combine(_root, "c.mnvts");
        File.WriteAllText(path, "on start { log(\"ok\") }");
        var document = ScriptDocument.Open(path);
        Assert.Empty(document.Check());

        document.Text = "on start {\n    undefined_thing()\n}";
        var error = Assert.Single(document.Check());
        Assert.Equal(2, error.Location.Line);
        Assert.Empty(ScriptDocument.Open(CreateText("d.json", "{}")).Check());
    }

    [Fact]
    public void Templates_CreateCompilingStarters_AndRefuseBadNamesOrExistingFiles()
    {
        var project = NewProject();

        var component = ScriptTemplates.CreateComponent(project, "Enemy");
        Assert.Equal(Path.Combine(_root, "Scripts", "Enemy.cs"), component);
        var code = File.ReadAllText(component);
        Assert.Contains("namespace My_Game;", code, StringComparison.Ordinal);
        Assert.Contains("public sealed class Enemy : Component", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ComponentId", code, StringComparison.Ordinal);

        var mission = ScriptTemplates.CreateMission(project, "FindTheKey");
        var source = File.ReadAllText(mission);
        Assert.Contains("mission \"find_the_key\"", source, StringComparison.Ordinal);
        Assert.True(ScriptCompiler.Compile(source, "FindTheKey.mnvts", new CompileOptions { Api = StandardApi.All }).Succeeded);

        Assert.Throws<IOException>(() => ScriptTemplates.CreateComponent(project, "Enemy"));
        Assert.Throws<ArgumentException>(() => ScriptTemplates.CreateComponent(project, "2Bad"));
        Assert.Throws<ArgumentException>(() => ScriptTemplates.CreateMission(project, "../escape"));
    }

    [Fact]
    public void SavedMissionScript_ReloadsInPlayMode_AfterAssetRefresh()
    {
        var project = NewProject();
        var session = new EditorSession(project, Build.EngineModuleSet.CreateRegistry());
        session.OpenScene("Scenes/Main.mnvt");
        var script = session.Assets.WriteText("Logic.mnvts", "var level = 1; var seen = 0; on start { seen = level }");
        session.Scene.CreateGameObject("Logic").AddComponent<MissionRunner>().Script = new AssetRef(script);
        var play = new PlayModeController(session, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        play.Play();
        var runner = play.PlayScene!.Find("Logic")!.GetComponent<MissionRunner>()!;
        play.Tick(0.1);
        Assert.Equal(1, runner.Vm!.GetGlobal("seen").AsNumber());

        var path = session.Assets.FullPath(script);
        File.WriteAllText(path, "var level = 2; var seen = 0; var added = 5; on start { seen = level }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(1, play.ReloadMissionScripts(session.Assets.Refresh().Reimported));
        Assert.Equal(1, runner.Vm!.GetGlobal("seen").AsNumber()); // variables keep their values
        Assert.Equal(5, runner.Vm.GetGlobal("added").AsNumber()); // new variables start at their initializer
        play.Stop();
    }

    private string CreateText(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }
}
