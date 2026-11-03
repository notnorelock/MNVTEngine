using MNVTEngine.GameAssemblies;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build;

public enum BuildSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One problem found while validating, building or packaging a game. Codes: MNVB1xx project and profile, MNVB2xx
/// assets and content, MNVB3xx C# scripts, MNVB4xx packaging, MNVB5xx package validation.
/// </summary>
public sealed record BuildDiagnostic(BuildSeverity Severity, string Code, string Message, string? File = null, int Line = 0, int Column = 0)
{
    public static BuildDiagnostic Error(string code, string message, string? file = null) => new(BuildSeverity.Error, code, message, file);

    public static BuildDiagnostic Warning(string code, string message, string? file = null) => new(BuildSeverity.Warning, code, message, file);

    public static BuildDiagnostic FromScript(ScriptDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(diagnostic.Severity == ScriptDiagnosticSeverity.Error ? BuildSeverity.Error : BuildSeverity.Warning, diagnostic.Code, diagnostic.Message, diagnostic.File, diagnostic.Line, diagnostic.Column);
    }

    /// <param name="file">The file the diagnostic belongs to; null when <see cref="SerializationDiagnostic.Path"/> is the file.</param>
    public static BuildDiagnostic FromSerialization(string code, SerializationDiagnostic diagnostic, string? file = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => BuildSeverity.Error,
            DiagnosticSeverity.Warning => BuildSeverity.Warning,
            _ => BuildSeverity.Info,
        };
        return file is null
            ? new(severity, code, diagnostic.Message, diagnostic.Path)
            : new(severity, code, diagnostic.Path.Length > 0 ? $"{diagnostic.Path}: {diagnostic.Message}" : diagnostic.Message, file);
    }

    public override string ToString()
    {
        var severity = Severity.ToString().ToLowerInvariant();
        var location = File is null ? "" : Line > 0 ? $"{File}({Line},{Column}): " : $"{File}: ";
        return $"{location}{severity} {Code}: {Message}";
    }
}

public static class BuildDiagnostics
{
    public static bool HasErrors(this IEnumerable<BuildDiagnostic> diagnostics) => diagnostics.Any(d => d.Severity == BuildSeverity.Error);
}
