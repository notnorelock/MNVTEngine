namespace MNVTEngine.Runtime;

/// <summary>What a folder or file is, for opening projects (<see cref="GameProject.Classify"/>).</summary>
public enum ProjectFolderKind
{
    None,

    /// <summary>An editable development project (a <c>.mnvtproj</c> project file).</summary>
    Project,

    /// <summary>An exported game: its folder, executable, data folder, or its runtime configuration (also extracted or renamed).</summary>
    ExportedGame,

    /// <summary>A folder of <c>.mnvtpack</c> packages, or a package file.</summary>
    GamePackages,
}

/// <summary>
/// The path is a game build, not an editable project. Exported games hold packaged runtime content only: no project
/// file, no source assets, no asset metadata; the original project is what the editor opens. A
/// <see cref="FileNotFoundException"/> because no project file is there, so code that reports a missing project reports this too.
/// </summary>
public sealed class NotAProjectException(string path, ProjectFolderKind kind)
    : FileNotFoundException(kind == ProjectFolderKind.GamePackages
        ? $"'{path}' holds packaged game content (.mnvtpack), not an editable MNVTEngine project. Open the original project folder instead."
        : $"'{path}' is an exported game (a game build), not an editable MNVTEngine project. Builds contain packaged runtime content only; open the original project folder instead.", path)
{
    public ProjectFolderKind Kind { get; } = kind;
}
