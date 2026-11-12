using Microsoft.Extensions.Logging;

namespace MNVTEngine.Editor;

/// <summary>Structured log messages shared by editor panels.</summary>
internal static partial class EditorLog
{
    [LoggerMessage(EventId = 5200, Level = LogLevel.Error, Message = "{Action} failed")]
    public static partial void ActionFailed(ILogger logger, Exception exception, string action);

    [LoggerMessage(EventId = 5201, Level = LogLevel.Warning, Message = "{Message}")]
    public static partial void Warning(ILogger logger, string message);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Information, Message = "{Message}")]
    public static partial void Info(ILogger logger, string message);

    [LoggerMessage(EventId = 5204, Level = LogLevel.Information, Message = "Imported '{Path}'")]
    public static partial void Imported(ILogger logger, string? path);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Information, Message = "Created prefab '{Path}' from '{GameObject}'")]
    public static partial void PrefabCreated(ILogger logger, string path, string gameObject);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Information, Message = "Saved profile {Profile} in {Path} (the project predates profiles; previous file kept as .v1.bak). {Reason}")]
    public static partial void ProfileMigrated(ILogger logger, string profile, string path, string reason);

    [LoggerMessage(EventId = 5207, Level = LogLevel.Information, Message = "Engine assemblies loaded for this {Profile} project: {Assemblies}")]
    public static partial void EngineAssembliesLoaded(ILogger logger, string profile, string assemblies);

    [LoggerMessage(EventId = 5203, Message = "Asset {Path}: {Message}")]
    public static partial void AssetDiagnostic(ILogger logger, LogLevel level, string path, string message);
}
