// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Editor.Panels;
using MNVTEngine.Editor.Services;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Tests;

/// <summary>The Hierarchy's create menu offers only what the project's profile can save.</summary>
public sealed class CreateMenuTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-create-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private EditorSession Session(ProjectProfile profile)
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "name": "Main", "gameObjects": [] }""");
        File.WriteAllText(Path.Combine(_root, "Game.mnvtproj"), """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt" }""");
        var session = new EditorSession(GameProject.Load(_root), Build.EngineModuleSet.CreateRegistry(profile));
        session.OpenScene("Scenes/Main.mnvt");
        return session;
    }

    [Fact]
    public void ACubeInATwoDProject_CannotBeSaved_SoTheMenuDoesNotOfferIt()
    {
        // The crash: the menu added a MeshRenderer that a 2D registry does not know, and the next undo snapshot threw.
        var session = Session(ProjectProfile.TwoD);
        var cube = session.Scene.CreateGameObject("Cube");
        cube.AddComponent<MeshRenderer>();
        cube.AddComponent<BoxCollider>();
        session.Edited("Create");
        Assert.Contains("is not registered", Assert.Throws<InvalidOperationException>(() => session.History.Commit()).Message, StringComparison.Ordinal);

        var registry = session.Registry;
        Assert.False(HierarchyPanel.CanCreate(registry, HierarchyPanel.CreateRequirements["3D Object"]));
        Assert.False(HierarchyPanel.CanCreate(registry, HierarchyPanel.CreateRequirements["Light"]));
        Assert.True(HierarchyPanel.CanCreate(registry, HierarchyPanel.CreateRequirements["Create Sprite"]));
    }

    [Fact]
    public void ACubeInAThreeDProject_IsCreatedAndUndoable()
    {
        var session = Session(ProjectProfile.ThreeD);
        Assert.True(HierarchyPanel.CanCreate(session.Registry, HierarchyPanel.CreateRequirements["3D Object"]));
        Assert.True(HierarchyPanel.CanCreate(session.Registry, HierarchyPanel.CreateRequirements["Light"]));

        var cube = session.Scene.CreateGameObject("Cube");
        cube.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
        cube.AddComponent<BoxCollider>();
        session.Edited("Create");
        Assert.True(session.History.Commit());
        session.History.Undo();
        Assert.Null(session.Scene.Find("Cube"));
        session.History.Redo();
        Assert.Equal(PrimitiveMesh.Cube, session.Scene.Find("Cube")!.GetComponent<MeshRenderer>()!.Primitive);
    }
}
