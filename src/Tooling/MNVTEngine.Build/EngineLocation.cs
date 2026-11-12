namespace MNVTEngine.Build;

/// <summary>
/// Finds the engine source tree that tooling builds games from: <c>MNVT_ENGINE_ROOT</c>, or the first parent of the
/// running tool that contains <c>MNVTEngine.sln</c>.
/// </summary>
public static class EngineLocation
{
    public const string EnvironmentVariable = "MNVT_ENGINE_ROOT";
    private const string Marker = "MNVTEngine.sln";

    /// <exception cref="DirectoryNotFoundException">No engine source tree was found.</exception>
    public static string FindRoot()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured)
        {
            return File.Exists(Path.Combine(configured, Marker))
                ? Path.GetFullPath(configured)
                : throw new DirectoryNotFoundException($"{EnvironmentVariable}='{configured}' does not contain {Marker}.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, Marker)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"MNVTEngine source tree not found above '{AppContext.BaseDirectory}'; set {EnvironmentVariable}.");
    }

    public static string GameHostProject(string root) => Path.Combine(root, "src", "Engine", "MNVTEngine.GameHost", "MNVTEngine.GameHost.csproj");

    public static string CrashHandlerProject(string root) => Path.Combine(root, "src", "Engine", "MNVTEngine.CrashHandler", "MNVTEngine.CrashHandler.csproj");

    public static string EditorProject(string root) => Path.Combine(root, "src", "Editor", "MNVTEngine.Editor", "MNVTEngine.Editor.csproj");

    public static string TemplatesDirectory(string root) => Path.Combine(root, "templates");
}
