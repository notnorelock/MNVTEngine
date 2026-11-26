// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Bundles;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build.Tests;

/// <summary>Editor packages: what may be packaged, and importing, upgrading and refusing them.</summary>
public sealed class EditorPackageTests : IDisposable
{
    private const string AssetGuid = "6b1d0c2e-5a77-4c21-9a3e-0b5f1f7f0001";
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-editor-package-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void APackage_RoundTrips_AndIsNotAContentBundle()
    {
        var file = WritePackage("Kit", "1.0");

        using (var package = EditorPackageFile.Open(file))
        {
            Assert.Equal(new EditorPackageManifest("Kit", "1.0", "A kit"), package.Manifest);
            Assert.Equal(["Assets/Kit/Tile.png", "Assets/Kit/Tile.png.meta", "Editor/Kit/KitWindow.cs", "Scripts/Kit/KitComponent.cs"],
                package.Entries.Select(e => e.Area + "/" + e.Path));
            Assert.Equal("class KitComponent {}", System.Text.Encoding.UTF8.GetString(package.Read(package.Entries.Single(e => e.Area == "Scripts"))));
        }

        Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file).Dispose()); // code: never loaded by a game
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("missing.mnvtbundle")]
    [InlineData("bad|name.mnvtbundle")]
    public void OpeningNoFile_IsAPackageProblem_NotACrash(string name)
    {
        var file = name.Trim().Length == 0 ? name : Path.Combine(_root, name);

        var error = Assert.Throws<EditorPackageException>(() => EditorPackageFile.Open(file).Dispose());

        Assert.Single(error.Problems);
    }

    [Fact]
    public void Packages_KeepToTheirFolders_AndCarryNoProjects()
    {
        var source = Path.Combine(_root, "bad");
        Write(source, "Scripts/Other/Stray.cs", "class Stray {}");
        Write(source, "Scripts/Kit/Kit.csproj", "<Project />");
        Write(source, "Editor/Kit/run.ps1", "rm -r /");

        var error = Assert.Throws<EditorPackageException>(() => EditorPackageFile.Write(source, new EditorPackageManifest("Kit", "1"), Path.Combine(_root, "bad.mnvtbundle")));

        Assert.Contains(error.Problems, p => p.Contains("Scripts/Other/Stray.cs", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("Kit.csproj", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("run.ps1", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "bad.mnvtbundle")));
        Assert.Throws<EditorPackageException>(() => EditorPackageFile.Write(source, new EditorPackageManifest("../Kit", "1"), Path.Combine(_root, "x.mnvtbundle")));
    }

    [Fact]
    public void AHandMadePackage_WithPathsOutsideItsFolders_IsRefused()
    {
        var file = Path.Combine(_root, "evil.mnvtbundle");
        using (var writer = new PackageWriter(file, new PackageWriterOptions(Guid.NewGuid(), Guid.NewGuid())))
        {
            writer.Add("bundle.json", System.Text.Encoding.UTF8.GetBytes("""{ "schemaVersion": 1, "kind": "mnvt.package", "name": "Kit", "version": "1" }"""));
            writer.Add("Scripts/Elsewhere/Evil.cs", [1]);
            writer.Add("Other/Kit/x.txt", [1]);
            writer.Complete();
        }

        var error = Assert.Throws<EditorPackageException>(() => EditorPackageFile.Open(file).Dispose());
        Assert.Equal(2, error.Problems.Count);
    }

    [Fact]
    public void Import_PlacesEachArea_CreatesTheEditorProject_AndUpgradesCleanly()
    {
        var project = NewProject();
        using (var package = EditorPackageFile.Open(WritePackage("Kit", "1.0", extraScript: true)))
        {
            var plan = EditorPackageImporter.Plan(project, package);
            Assert.True(plan.CanImport, string.Join("\n", plan.Problems));
            Assert.True(plan.HasCode);
            Assert.True(plan.CreatesEditorProject);
            Assert.Null(plan.InstalledVersion);
            EditorPackageImporter.Import(project, package, _root);
        }

        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Assets", "Kit", "Tile.png.meta")));
        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Scripts", "Kit", "KitComponent.cs")));
        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Scripts", "Kit", "Old.cs")));
        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Editor", "Kit", "KitWindow.cs")));
        var editorProject = Path.Combine(project.RootDirectory, "Editor", "Game.Editor.csproj");
        Assert.Contains("../Scripts/Game.Scripts.csproj", File.ReadAllText(editorProject), StringComparison.Ordinal);
        Assert.Contains("MNVTEngine.Editor.Extensions", File.ReadAllText(editorProject), StringComparison.Ordinal);
        var reloaded = GameProject.Load(project.ProjectFile);
        Assert.Equal("Editor/Game.Editor.csproj", reloaded.Scripts!.EditorProject);
        Assert.Equal("T", JObject.Parse(File.ReadAllText(project.ProjectFile)).Value<string>("name")); // the rest of the file is kept

        // 2.0 drops a file: it is removed; the project's own files are never touched.
        File.WriteAllText(Path.Combine(project.RootDirectory, "Scripts", "Mine.cs"), "class Mine {}");
        using (var upgrade = EditorPackageFile.Open(WritePackage("Kit", "2.0")))
        {
            var plan = EditorPackageImporter.Plan(reloaded, upgrade);
            Assert.Equal("1.0", plan.InstalledVersion);
            Assert.False(plan.CreatesEditorProject);
            Assert.Equal(["Scripts/Kit/Old.cs"], plan.Removed);
            Assert.DoesNotContain(plan.Files, f => f.ReplacesOtherFile);
            EditorPackageImporter.Import(reloaded, upgrade, _root);
        }

        Assert.False(File.Exists(Path.Combine(project.RootDirectory, "Scripts", "Kit", "Old.cs")));
        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Scripts", "Mine.cs")));
        Assert.Equal("2.0", JObject.Parse(File.ReadAllText(Path.Combine(project.RootDirectory, "ProjectSettings", "Packages", "Kit.json"))).Value<string>("version"));
    }

    [Fact]
    public void Import_IsRefused_WhenAGuidBelongsToAnotherAsset()
    {
        var project = NewProject();
        Write(project.RootDirectory, "Assets/Mine.png", "png");
        Write(project.RootDirectory, "Assets/Mine.png.meta", $$"""{ "schemaVersion": 1, "guid": "{{AssetGuid}}" }""");
        using var package = EditorPackageFile.Open(WritePackage("Kit", "1.0"));

        var plan = EditorPackageImporter.Plan(project, package);
        Assert.False(plan.CanImport);
        Assert.Contains(plan.Problems, p => p.Contains("Assets/Mine.png.meta", StringComparison.Ordinal));
        Assert.Throws<EditorPackageException>(() => EditorPackageImporter.Import(project, package, _root));
        Assert.False(Directory.Exists(Path.Combine(project.RootDirectory, "Assets", "Kit"))); // nothing changed
    }

    [Fact]
    public void Import_IntoAProjectWithoutScripts_CreatesItsScriptsProject()
    {
        var project = NewProject();
        File.WriteAllText(project.ProjectFile, """{ "schemaVersion": 2, "name": "T", "profile": "2d" }""");
        project = GameProject.Load(project.ProjectFile);
        using var package = EditorPackageFile.Open(WritePackage("Kit", "1.0"));

        var plan = EditorPackageImporter.Import(project, package, _root);

        Assert.True(plan.CreatesScriptsProject);
        var reloaded = GameProject.Load(project.ProjectFile);
        Assert.Equal("Scripts/Game.Scripts.csproj", reloaded.Scripts!.Project);
        Assert.Equal("Editor/Game.Editor.csproj", reloaded.Scripts.EditorProject);
        Assert.Contains("MNVTEngine.World2D", File.ReadAllText(Path.Combine(project.RootDirectory, "Scripts", "Game.Scripts.csproj")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(project.RootDirectory, "Scripts", "Kit", "KitComponent.cs")));
    }

    private GameProject NewProject()
    {
        var directory = Path.Combine(_root, "Game" + Guid.NewGuid().ToString("N")[..6]);
        Write(directory, "Game.mnvtproj", """{ "schemaVersion": 2, "name": "T", "profile": "2d", "scripts": { "project": "Scripts/Game.Scripts.csproj" } }""");
        Write(directory, "Scripts/Game.Scripts.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Directory.CreateDirectory(Path.Combine(directory, "Assets"));
        return GameProject.Load(Path.Combine(directory, "Game.mnvtproj"));
    }

    private string WritePackage(string name, string version, bool extraScript = false)
    {
        var source = Path.Combine(_root, $"src-{name}-{version}");
        Write(source, $"Assets/{name}/Tile.png", "png");
        Write(source, $"Assets/{name}/Tile.png.meta", $$"""{ "schemaVersion": 1, "guid": "{{AssetGuid}}" }""");
        Write(source, $"Scripts/{name}/KitComponent.cs", "class KitComponent {}");
        Write(source, $"Editor/{name}/KitWindow.cs", "class KitWindow {}");
        Write(source, $"Scripts/{name}/obj/Ignored.cs", "skipped");
        if (extraScript)
        {
            Write(source, $"Scripts/{name}/Old.cs", "class Old {}");
        }

        return EditorPackageFile.Write(source, new EditorPackageManifest(name, version, "A kit"), Path.Combine(_root, $"{name}-{version}.mnvtbundle"));
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
