namespace MNVTEngine.Modules.Tests;

public sealed class ModuleResolverTests
{
    private static readonly ModuleCatalog Builtin = ModuleCatalog.Builtin;

    private static ModuleDefinition M(string id, string[]? requires = null, string[]? optional = null, ModuleActivation activation = ModuleActivation.Default, ProjectProfile[]? profiles = null) =>
        new(id, id, "test") { Requires = requires ?? [], Optional = optional ?? [], Activation = activation, Profiles = profiles ?? ProjectProfiles.All };

    private static string[] Ids(ModuleResolution resolution) => [.. resolution.Modules.Select(m => m.Id)];

    private static ModuleResolution Resolve(ProjectProfile profile, params (string Id, bool On)[] switches) =>
        ModuleResolver.Resolve(Builtin, profile, switches.ToDictionary(s => s.Id, s => s.On));

    [Fact]
    public void TwoDProjects_GetTheSharedAndTwoDModules_InDependencyOrder()
    {
        var resolution = Resolve(ProjectProfile.TwoD);
        Assert.True(resolution.Succeeded);
        Assert.Equal(["core", "audio", "graphics", "assets", "platform", "runtime", "scripting", "ui", "world.2d", "physics.2d"], Ids(resolution));
        Assert.DoesNotContain(resolution.Modules, m => m.ExclusiveProfile == ProjectProfile.ThreeD);
        Assert.Equal("not available in 2D", resolution.Excluded.Single(e => e.Module.Id == "world.3d").Reason);
        Assert.Equal("opt-in, not enabled", resolution.Excluded.Single(e => e.Module.Id == "devtools").Reason);
    }

    [Fact]
    public void ThreeDProjects_GetTheSharedAndThreeDModules_InDependencyOrder()
    {
        var resolution = Resolve(ProjectProfile.ThreeD);
        Assert.True(resolution.Succeeded);
        Assert.Equal(["core", "audio", "graphics", "assets", "platform", "runtime", "scripting", "ui", "world.3d", "physics.3d", "terrain"], Ids(resolution));
        Assert.DoesNotContain(resolution.Modules, m => m.ExclusiveProfile == ProjectProfile.TwoD);
    }

    [Fact]
    public void EveryModuleStartsAfterItsDependencies()
    {
        foreach (var profile in ProjectProfiles.All)
        {
            var resolution = Resolve(profile, ("devtools", true));
            var position = Ids(resolution).Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
            Assert.All(resolution.Dependencies, d => Assert.True(position[d.DependsOn] < position[d.Module], $"{d.Module} starts before {d.DependsOn}"));
        }
    }

    [Fact]
    public void Resolution_IsDeterministic_WhateverTheDefinitionOrder()
    {
        var shuffled = ModuleCatalog.Create(BuiltinModules.All.Reverse());
        foreach (var profile in ProjectProfiles.All)
        {
            var a = ModuleResolver.Resolve(Builtin, profile);
            var b = ModuleResolver.Resolve(shuffled, profile);
            Assert.Equal(Ids(a), Ids(b));
            Assert.Equal(a.Dependencies, b.Dependencies);
            Assert.Equal(a.Diagnostics, b.Diagnostics);
        }
    }

    [Fact]
    public void SwitchesDisableDefaults_AndEnableOptIns()
    {
        var resolution = Resolve(ProjectProfile.ThreeD, ("terrain", false), ("devtools", true), ("ui", false));
        Assert.True(resolution.Succeeded);
        Assert.DoesNotContain("terrain", Ids(resolution));
        Assert.DoesNotContain("ui", Ids(resolution));
        Assert.Contains("devtools", Ids(resolution));
        Assert.Equal("enabled in the project", resolution.Reasons["devtools"]);
        Assert.Equal("disabled in the project", resolution.Excluded.Single(e => e.Module.Id == "terrain").Reason);
    }

    [Fact]
    public void IncompatibleModules_AreRejected_NeverEnabled()
    {
        var resolution = Resolve(ProjectProfile.TwoD, ("physics.3d", true));
        Assert.False(resolution.Succeeded);
        var error = Assert.Single(resolution.Diagnostics, d => d.Severity == ModuleSeverity.Error);
        Assert.Equal((ModuleDiagnostic.IncompatibleModule, "physics.3d"), (error.Code, error.Module));
        Assert.DoesNotContain("physics.3d", Ids(resolution));
        Assert.DoesNotContain("world.3d", Ids(resolution));
    }

    [Fact]
    public void UnknownModules_AreReported()
    {
        var error = Assert.Single(Resolve(ProjectProfile.TwoD, ("particles.2d", true)).Diagnostics);
        Assert.Equal(ModuleDiagnostic.UnknownModule, error.Code);
        Assert.Contains("Known modules:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingARequiredModule_IsAnError_NotSilentlyOverridden()
    {
        var always = Resolve(ProjectProfile.TwoD, ("graphics", false));
        Assert.Equal([ModuleDiagnostic.RequiredModuleDisabled], always.Diagnostics.Select(d => d.Code));
        Assert.Contains("graphics", Ids(always)); // reported, still listed as the infrastructure it is

        var needed = Resolve(ProjectProfile.TwoD, ("world.2d", false));
        var error = Assert.Single(needed.Diagnostics, d => d.Severity == ModuleSeverity.Error);
        Assert.Equal((ModuleDiagnostic.RequiredModuleDisabled, "world.2d"), (error.Code, error.Module));
        Assert.Contains("'physics.2d' requires 'world.2d'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("world.2d", Ids(needed));
        Assert.False(needed.Succeeded);

        // Disabling the dependent too is the consistent fix.
        Assert.True(Resolve(ProjectProfile.TwoD, ("world.2d", false), ("physics.2d", false)).Succeeded);
    }

    [Fact]
    public void RequiredDependencies_AreEnabledAutomatically_AndReported()
    {
        var catalog = ModuleCatalog.Create([M("base"), M("lib", activation: ModuleActivation.OptIn), M("feature", requires: ["lib"], activation: ModuleActivation.OptIn)]);
        var resolution = ModuleResolver.Resolve(catalog, ProjectProfile.TwoD, new Dictionary<string, bool> { ["feature"] = true });
        Assert.Equal(["base", "lib", "feature"], Ids(resolution));
        Assert.Equal("required by feature", resolution.Reasons["lib"]);
        var info = Assert.Single(resolution.Diagnostics);
        Assert.Equal((ModuleDiagnostic.EnabledAsDependency, ModuleSeverity.Info), (info.Code, info.Severity));
        Assert.True(resolution.Succeeded);
    }

    [Fact]
    public void OptionalDependencies_AreNeverEnabledByThis_ButStartFirstWhenPresent()
    {
        var catalog = ModuleCatalog.Create([M("a", optional: ["z"]), M("z", activation: ModuleActivation.OptIn)]);
        var without = ModuleResolver.Resolve(catalog, ProjectProfile.TwoD);
        Assert.Equal(["a"], Ids(without));
        Assert.Empty(without.Dependencies);

        var with = ModuleResolver.Resolve(catalog, ProjectProfile.TwoD, new Dictionary<string, bool> { ["z"] = true });
        Assert.Equal(["z", "a"], Ids(with)); // by id "a" would come first; the optional edge orders it after "z"
        Assert.Equal([new ModuleDependency("a", "z", IsOptional: true)], with.Dependencies);
    }

    [Fact]
    public void ContentOfModulesTheProjectDoesNotHave_IsReported()
    {
        var resolution = Resolve(ProjectProfile.TwoD, ("physics.2d", false));
        var problems = resolution.CheckContent(
            [new("MNVTEngine.Rigidbody2D", "Scenes/Level.mnvt"), new("MNVTEngine.MeshRenderer", "Prefabs/Crate.mnvtprefab"), new("MNVTEngine.SpriteRenderer", "Scenes/Level.mnvt"), new("Game.Player", "Scenes/Level.mnvt")],
            Builtin);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(ModuleDiagnostic.ComponentOfDisabledModule, p.Code));
        Assert.Contains("MNVTEngine.Rigidbody2D (module 'physics.2d', disabled in the project) is used in Scenes/Level.mnvt.", problems.Select(p => p.Message));
        Assert.Contains("MNVTEngine.MeshRenderer (module 'world.3d', not available in 2D) is used in Prefabs/Crate.mnvtprefab.", problems.Select(p => p.Message));
    }
}
