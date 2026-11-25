// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Versioning;

namespace MNVTEngine.Build;

/// <summary>
/// Finds the engine that tooling builds games with: <c>MNVT_ENGINE_ROOT</c>, or the first parent of the running tool
/// that is either the engine's source tree (<c>MNVTEngine.sln</c>) or an installed engine, a release without sources
/// (<c>Engine/version.json</c>; see <c>scripts/Package-Release.ps1</c> and docs/build-system/building.md). An installed
/// engine builds games from prebuilt parts: the engine as NuGet packages (<c>Engine/Packages</c>), the game host's
/// startup project, and the crash handler and browser host as finished executables.
/// </summary>
public static class EngineLocation
{
    public const string EnvironmentVariable = "MNVT_ENGINE_ROOT";
    private const string SourceMarker = "MNVTEngine.sln";
    private const string InstalledFolder = "Engine";

    /// <exception cref="DirectoryNotFoundException">No engine was found.</exception>
    public static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured)
        {
            return IsEngine(configured)
                ? Path.GetFullPath(configured)
                : throw new DirectoryNotFoundException($"{EnvironmentVariable}='{configured}' contains neither {SourceMarker} nor an installed engine ({InstalledFolder}/{VersionSession.MetadataFile}).");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (IsEngine(directory.FullName))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"MNVTEngine not found above '{AppContext.BaseDirectory}'; set {EnvironmentVariable}.");
    }

    private static bool IsEngine(string directory) => File.Exists(Path.Combine(directory, SourceMarker)) || IsInstalled(directory);

    /// <summary>True for an installed engine (prebuilt, no sources); false for the source tree.</summary>
    public static bool IsInstalled(string root) => !File.Exists(Path.Combine(root, SourceMarker)) && File.Exists(Path.Combine(root, InstalledFolder, VersionSession.MetadataFile));

    /// <summary>The version an installed engine was built as (its <c>Engine/version.json</c>); null for the source tree.</summary>
    public static EngineBuildInfo? InstalledVersion(string root) => IsInstalled(root) ? VersionSession.Read(Path.Combine(root, InstalledFolder)) : null;

    public static string GameHostProject(string root) => IsInstalled(root)
        ? Path.Combine(root, InstalledFolder, "GameHost", "MNVTEngine.GameHost.csproj")
        : Path.Combine(root, "src", "Engine", "MNVTEngine.GameHost", "MNVTEngine.GameHost.csproj");

    public static string CrashHandlerProject(string root) => Path.Combine(root, "src", "Engine", "MNVTEngine.CrashHandler", "MNVTEngine.CrashHandler.csproj");

    public static string BrowserHostProject(string root) => Path.Combine(root, "src", "Engine", "MNVTEngine.Browser.Host", "MNVTEngine.Browser.Host.csproj");

    /// <summary>An installed engine's published crash handler: framework-dependent (JIT games) or ahead-of-time compiled.</summary>
    public static string PrebuiltCrashHandler(string root, bool aot) => Path.Combine(root, InstalledFolder, "CrashHandler", aot ? "aot" : "jit");

    /// <summary>An installed engine's published browser host (with a private runtime for AOT games, which have none).</summary>
    public static string PrebuiltBrowserHost(string root) => Path.Combine(root, InstalledFolder, "BrowserHost");

    public static string EditorProject(string root) => Path.Combine(root, "src", "Editor", "MNVTEngine.Editor", "MNVTEngine.Editor.csproj");

    /// <summary>An installed engine's editor executable.</summary>
    public static string EditorExecutable(string root) => Path.Combine(root, "Editor", "MNVTEngine.Editor.exe");

    /// <summary>The local NuGet feed of an installed engine (engine packages and MNVTEngine.Sdk).</summary>
    public static string PackagesDirectory(string root) => Path.Combine(root, InstalledFolder, "Packages");

    public static string TemplatesDirectory(string root) => Path.Combine(root, "templates");
}
