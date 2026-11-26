// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Build;
using MNVTEngine.Editor.Extensions;
using MNVTEngine.Editor.Services;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

[EditorMenu("Tests/Sample Window")]
public sealed class SampleExtensionWindow : EditorWindow
{
    public override void OnGui(IEditorGui gui) => gui.Text("sample");
}

[EditorMenu("Tests//Broken")]
public sealed class BadPathExtensionWindow : EditorWindow
{
    public override void OnGui(IEditorGui gui) => gui.Text("bad path");
}

public sealed class NoConstructorExtensionWindow(string title) : EditorWindow
{
    public override string Title => title;

    public override void OnGui(IEditorGui gui) => gui.Text(title);
}

public sealed class SampleTransformInspector : ComponentInspector<Transform>
{
    protected override void OnInspectorGui(IEditorGui gui, Transform component) => gui.Text(component.GameObject.Name);
}

/// <summary>Editor extensions: discovery, their settings and secrets, and a project's editor code built and loaded with its scripts.</summary>
public sealed class EditorExtensionTests : IDisposable
{
    private static readonly string[] EngineAssemblies = ["Core", "Mathematics", "ECS", "Input", "Serialization", "Scenes", "Bundles", "Editor.Extensions"];

    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-extensions-").FullName;
    private DateTime _clock = DateTime.UtcNow;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Build servers may briefly hold files in obj/.
        }
    }

    [Fact]
    public void TheCatalog_FindsWindowsAndInspectors_AndReportsWhatItCannotUse()
    {
        var catalog = ExtensionCatalog.From(typeof(EditorExtensionTests).Assembly);

        Assert.Contains((typeof(SampleExtensionWindow), "Tests/Sample Window"), catalog.Windows);
        Assert.Contains((typeof(BadPathExtensionWindow), (string?)null), catalog.Windows); // kept, without a menu item
        Assert.DoesNotContain(catalog.Windows, w => w.Type == typeof(NoConstructorExtensionWindow));
        Assert.Contains(typeof(SampleTransformInspector), catalog.Inspectors);
        Assert.Contains(catalog.Problems, p => p.Contains(nameof(NoConstructorExtensionWindow), StringComparison.Ordinal));
        Assert.Contains(catalog.Problems, p => p.Contains("Tests//Broken", StringComparison.Ordinal));
        Assert.Equal("MNVTEngine.Editor.Tests", catalog.Name);
    }

    [Fact]
    public void Settings_AreKeptInTheProject_AndABrokenFileIsReplaced()
    {
        var settings = new ProjectExtensionSettings(_root, "My/Tools");
        settings.Write("item", "42");
        settings.Write("gone", "x");
        settings.Write("gone", null);
        var file = Path.Combine(_root, "ProjectSettings", "Extensions", "My_Tools.json");
        Assert.True(File.Exists(file));
        Assert.Equal("42", new ProjectExtensionSettings(_root, "My/Tools").Read("item"));
        Assert.Null(new ProjectExtensionSettings(_root, "My/Tools").Read("gone"));

        File.WriteAllText(file, "{ not json");
        var broken = new ProjectExtensionSettings(_root, "My/Tools");
        Assert.NotNull(broken.Problem);
        broken.Write("item", "7");
        Assert.Equal("7", new ProjectExtensionSettings(_root, "My/Tools").Read("item"));
    }

    [Fact]
    public void Secrets_AreEncryptedForThisUser()
    {
        var store = Path.Combine(_root, "secrets");
        var secrets = new EditorSecretStore(_root, "Tools", store);
        Assert.Equal(OperatingSystem.IsWindows(), secrets.Available);
        if (!secrets.Available)
        {
            return;
        }

        secrets.Write("token", "super-secret-token");
        Assert.Equal("super-secret-token", new EditorSecretStore(_root, "Tools", store).Read("token"));
        var file = Assert.Single(Directory.GetFiles(store, "*.bin", SearchOption.AllDirectories));
        Assert.DoesNotContain("super-secret-token", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        Assert.Null(new EditorSecretStore(Path.Combine(_root, "other"), "Tools", store).Read("token")); // per project

        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.NotNull(new EditorSecretStore(_root, "Tools", store).Problem);
    }

    [Fact]
    public async Task AProjectsEditorCode_BuildsAndReloadsWithItsScripts_ButNeverInTheGamesBuild()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "Editor"));
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"),
            """{ "schemaVersion": 1, "name": "T", "scripts": { "project": "Scripts/Game.csproj", "editorProject": "Editor/Game.Editor.csproj" } }""");
        var references = string.Join("\n", EngineAssemblies.Select(n => $"""<Reference Include="{Path.Combine(AppContext.BaseDirectory, $"MNVTEngine.{n}.dll")}" Private="false" />"""));
        File.WriteAllText(Path.Combine(_root, "Scripts", "Game.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>{references}</ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "Scripts", "Health.cs"), """
            namespace Game;
            [MNVTEngine.ComponentId("Game.Health")]
            public sealed class Health : MNVTEngine.Component { public int Points = 10; }
            """);
        File.WriteAllText(Path.Combine(_root, "Editor", "Game.Editor.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>{references}<ProjectReference Include="../Scripts/Game.csproj" /></ItemGroup>
            </Project>
            """);
        WriteEditorSource("Health Tool");

        var project = GameProject.Load(_root);
        Assert.Null(ScriptProject.For(project)!.EditorAssemblyName); // what the game's build and the Player compile
        var session = new EditorSession(project, ComponentRegistry.CreateDefault());
        var play = new PlayModeController(session, NullLoggerFactory.Instance);
        using var scripts = new ScriptReloadController(session, play, NullLogger.Instance);
        var reloads = 0;
        scripts.Reloaded += () => reloads++;

        await WaitFor(scripts, () => reloads == 1);
        Assert.Equal("Game.Editor", scripts.Extensions.Name);
        Assert.Equal("Game/Health Tool", Assert.Single(scripts.Extensions.Windows).MenuPath);
        Assert.Single(scripts.Extensions.Inspectors);
        Assert.Contains(session.Registry.Types, t => t.Value.Name == "Health"); // the game's components are registered, the editor code adds none

        WriteEditorSource("Health Doctor"); // editing only the editor code reloads too
        await WaitFor(scripts, () => reloads == 2);
        Assert.Equal("Game/Health Doctor", Assert.Single(scripts.Extensions.Windows).MenuPath);
        await WaitFor(scripts, () => scripts.LastUnloadCollected is not null);
        Assert.True(scripts.LastUnloadCollected);
    }

    private void WriteEditorSource(string menu)
    {
        var path = Path.Combine(_root, "Editor", "HealthTool.cs");
        File.WriteAllText(path, $$"""
            using MNVTEngine.Editor.Extensions;
            namespace Game.Editor;
            [EditorMenu("Game/{{menu}}")]
            public sealed class HealthTool : EditorWindow { public override void OnGui(IEditorGui gui) => gui.Text("hp"); }
            public sealed class HealthInspector : ComponentInspector<Game.Health>
            {
                protected override void OnInspectorGui(IEditorGui gui, Game.Health component) => gui.Text(component.Points.ToString());
            }
            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(10, 10_000)));
    }

    private async Task WaitFor(ScriptReloadController scripts, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out; state {scripts.State}: {string.Join("\n", scripts.Diagnostics)}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
            _clock = _clock.AddSeconds(5); // skip poll and verification delays
            scripts.Update(_clock);
        }
    }
}
