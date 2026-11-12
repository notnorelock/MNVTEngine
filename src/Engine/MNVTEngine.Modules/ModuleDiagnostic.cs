namespace MNVTEngine.Modules;

public enum ModuleSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A catalog or resolution problem. Codes are stable (MNVB6xx) and shared with the build's diagnostics.</summary>
/// <param name="Module">The module the problem is about, when there is one.</param>
public sealed record ModuleDiagnostic(string Code, ModuleSeverity Severity, string Message, string? Module = null)
{
    /// <summary>Two modules with the same id.</summary>
    public const string DuplicateModule = "MNVB601";

    /// <summary>A definition that breaks the catalog's rules (id format, empty profiles, self references, shared ownership).</summary>
    public const string InvalidDefinition = "MNVB602";

    /// <summary>A dependency on a module that is not in the catalog.</summary>
    public const string MissingDependency = "MNVB603";

    /// <summary>Modules that depend on each other in a cycle.</summary>
    public const string CircularDependency = "MNVB604";

    /// <summary>A module that is not available in the project's profile was requested or is required.</summary>
    public const string IncompatibleModule = "MNVB605";

    /// <summary>The project names a module the catalog does not have.</summary>
    public const string UnknownModule = "MNVB606";

    /// <summary>The project disables a module that cannot be disabled or that an enabled module requires.</summary>
    public const string RequiredModuleDisabled = "MNVB607";

    /// <summary>Scene or prefab content uses a component of a module the project does not have.</summary>
    public const string ComponentOfDisabledModule = "MNVB608";

    /// <summary>A module requires one that is not available in every profile it supports.</summary>
    public const string DependencyProfileMismatch = "MNVB609";

    /// <summary>Information: a module was enabled because an enabled module requires it.</summary>
    public const string EnabledAsDependency = "MNVB610";

    /// <summary>A module of the project has no implementation in the running executable.</summary>
    public const string ModuleNotInHost = "MNVB611";

    public static ModuleDiagnostic Error(string code, string message, string? module = null) => new(code, ModuleSeverity.Error, message, module);

    public static ModuleDiagnostic Info(string code, string message, string? module = null) => new(code, ModuleSeverity.Info, message, module);

    public override string ToString() => $"{Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}
