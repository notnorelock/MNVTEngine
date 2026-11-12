namespace MNVTEngine.Modules;

/// <summary>When a module is part of a project.</summary>
public enum ModuleActivation
{
    /// <summary>Engine infrastructure every project has; cannot be disabled.</summary>
    Always,

    /// <summary>On for the profiles it supports unless the project disables it.</summary>
    Default,

    /// <summary>Off unless the project enables it.</summary>
    OptIn,
}

/// <summary>
/// One engine module: a unit a project has or does not have. Declares what it needs (<see cref="Requires"/>), what it
/// integrates with when present (<see cref="Optional"/>), the profiles it works in, and what it brings into a build:
/// assemblies, native libraries, build assets and the component ids its assemblies register.
/// </summary>
/// <param name="Id">Stable identifier: lowercase, dot-separated (<c>physics.2d</c>).</param>
public sealed record ModuleDefinition(string Id, string DisplayName, string Description)
{
    public Version Version { get; init; } = new(1, 0, 0);

    public ModuleActivation Activation { get; init; } = ModuleActivation.Default;

    /// <summary>Profiles the module works in; a 2D-only module never enters a 3D project.</summary>
    public IReadOnlyList<ProjectProfile> Profiles { get; init; } = ProjectProfiles.All;

    /// <summary>Modules that must be enabled for this one to work; enabling this one enables them.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Modules this one integrates with when they are enabled; never enabled because of this module. When both are on, they start first.</summary>
    public IReadOnlyList<string> Optional { get; init; } = [];

    /// <summary>Assemblies (file names without extension, engine and third-party) the module ships in a game.</summary>
    public IReadOnlyList<string> RuntimeAssemblies { get; init; } = [];

    /// <summary>Editor assemblies that serve only this module.</summary>
    public IReadOnlyList<string> EditorAssemblies { get; init; } = [];

    /// <summary>Native libraries (file names) the module ships in a game.</summary>
    public IReadOnlyList<string> NativeLibraries { get; init; } = [];

    /// <summary>Built-in content the build compiles for the module, e.g. <c>shader:standard</c>.</summary>
    public IReadOnlyList<string> BuildAssets { get; init; } = [];

    /// <summary>Serialized ids of the components the module provides (scene content of a disabled module is an error).</summary>
    public IReadOnlyList<string> Components { get; init; } = [];

    public bool Supports(ProjectProfile profile) => Profiles.Contains(profile);

    /// <summary>The one profile of a profile-specific module; null for shared modules.</summary>
    public ProjectProfile? ExclusiveProfile => Profiles.Count == 1 ? Profiles[0] : null;
}
