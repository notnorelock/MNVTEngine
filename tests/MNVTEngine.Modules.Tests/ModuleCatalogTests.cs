namespace MNVTEngine.Modules.Tests;

public sealed class ModuleCatalogTests
{
    private static ModuleDefinition M(string id, string[]? requires = null, string[]? optional = null, ProjectProfile[]? profiles = null) =>
        new(id, id, "test") { Requires = requires ?? [], Optional = optional ?? [], Profiles = profiles ?? ProjectProfiles.All };

    private static string[] Codes(params ModuleDefinition[] modules) => [.. ModuleCatalog.Validate(modules).Select(d => d.Code)];

    [Fact]
    public void TheBuiltinCatalog_IsValid_AndOrderedById()
    {
        Assert.Empty(ModuleCatalog.Validate(BuiltinModules.All));
        var ids = ModuleCatalog.Builtin.Modules.Select(m => m.Id).ToList();
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(21, ids.Count);
    }

    [Fact]
    public void BuiltinProfileModules_StayInTheirProfile()
    {
        var catalog = ModuleCatalog.Builtin;
        Assert.All(["world.2d", "physics.2d"], id => Assert.Equal(ProjectProfile.TwoD, catalog[id].ExclusiveProfile));
        Assert.All(["world.3d", "physics.3d", "terrain"], id => Assert.Equal(ProjectProfile.ThreeD, catalog[id].ExclusiveProfile));
        Assert.All(["core", "audio", "ui", "scripting"], id => Assert.Null(catalog[id].ExclusiveProfile)); // shared, also screen-space UI
        Assert.Equal("physics.3d", catalog.ModuleOfComponent("MNVTEngine.Rigidbody")!.Id);
        Assert.Null(catalog.ModuleOfComponent("Game.Player"));
    }

    [Fact]
    public void DuplicateModules_AreRejected_AndNothingIsRegistered()
    {
        Assert.Equal([ModuleDiagnostic.DuplicateModule], Codes(M("a"), M("a")));
        var ex = Assert.Throws<ModuleCatalogException>(() => ModuleCatalog.Create([M("a"), M("a")]));
        Assert.Contains("'a' is defined more than once", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Physics")]
    [InlineData("physics..2d")]
    [InlineData("2d.physics")]
    [InlineData("physics-2d")]
    public void ModuleIds_AreLowercaseDottedNames(string id) => Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M(id)));

    [Fact]
    public void InvalidDefinitions_AreReported()
    {
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a", requires: ["a"])));
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a", requires: ["b"], optional: ["b"]), M("b")));
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a", profiles: [])));
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a", profiles: [ProjectProfile.TwoD]) with { Activation = ModuleActivation.Always }));
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a") with { DisplayName = " " }));
    }

    [Fact]
    public void EachAssemblyLibraryAssetAndComponent_HasOneOwner()
    {
        var problems = ModuleCatalog.Validate([M("a") with { RuntimeAssemblies = ["Shared"] }, M("b") with { RuntimeAssemblies = ["shared"] }]);
        Assert.Contains("'Shared'", Assert.Single(problems).Message, StringComparison.Ordinal);
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a") with { Components = ["X"] }, M("b") with { Components = ["X"] }));
        Assert.Equal([ModuleDiagnostic.InvalidDefinition], Codes(M("a") with { NativeLibraries = ["x.dll"] }, M("b") with { NativeLibraries = ["x.dll"] }));
    }

    [Fact]
    public void MissingDependencies_AreReported_ForRequiredAndOptional()
    {
        Assert.Equal([ModuleDiagnostic.MissingDependency], Codes(M("a", requires: ["ghost"])));
        Assert.Equal([ModuleDiagnostic.MissingDependency], Codes(M("a", optional: ["ghost"])));
    }

    [Fact]
    public void Cycles_AreReported_ThroughOptionalDependenciesToo()
    {
        var cycle = Assert.Single(ModuleCatalog.Validate([M("a", requires: ["b"]), M("b", requires: ["c"]), M("c", requires: ["a"])]));
        Assert.Equal((ModuleDiagnostic.CircularDependency, "Circular module dependency: a -> b -> c -> a."), (cycle.Code, cycle.Message));
        Assert.Equal([ModuleDiagnostic.CircularDependency], Codes(M("a", requires: ["b"]), M("b", optional: ["a"])));
    }

    [Fact]
    public void ARequiredDependency_MustExistInEveryProfileOfTheModule()
    {
        var problem = Assert.Single(ModuleCatalog.Validate([M("shared", requires: ["flat"]), M("flat", profiles: [ProjectProfile.TwoD])]));
        Assert.Equal(ModuleDiagnostic.DependencyProfileMismatch, problem.Code);
        Assert.Contains("not available in 3D", problem.Message, StringComparison.Ordinal);

        // Optional integrations may be profile-specific: they simply are not there in the other profile.
        Assert.Empty(Codes(M("shared", optional: ["flat"]), M("flat", profiles: [ProjectProfile.TwoD])));
    }
}
