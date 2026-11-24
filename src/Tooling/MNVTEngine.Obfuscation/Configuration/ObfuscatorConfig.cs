// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text.Json.Serialization;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Obfuscation.Configuration;

/// <summary>
/// One run's settings, as read from JSON (schema version 1, camelCase). Every field has a default; see
/// docs/configuration.md. Relative paths are resolved against <see cref="BaseDirectory"/> (the config file's
/// folder, or the working directory without one).
/// </summary>
public sealed class ObfuscatorConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>A named starting point (safe, balanced, aggressive) that the rest of the file overrides.</summary>
    public string? Preset { get; set; }

    public string? InputDirectory { get; set; }
    public string? OutputDirectory { get; set; }

    /// <summary>File-name patterns of the assemblies to protect. Other assemblies in the input are references.</summary>
    public List<string> Assemblies { get; set; } = new() { "*.dll" };

    public List<string> ExcludeAssemblies { get; set; } = new();

    /// <summary>Extra folders searched for referenced assemblies (engine DLLs, framework facades).</summary>
    public List<string> ReferenceDirectories { get; set; } = new();

    /// <summary>Also copy the assemblies that were only references into the output, for a runnable folder.</summary>
    public bool CopyUnprotectedAssemblies { get; set; }

    public FeatureOptions Features { get; set; } = new();
    public RenamingOptions Renaming { get; set; } = new();
    public StringOptions Strings { get; set; } = new();
    public ControlFlowOptions ControlFlow { get; set; } = new();
    public PreservationOptions Preservation { get; set; } = new();
    public ReportingOptions Reporting { get; set; } = new();

    [JsonIgnore]
    public string BaseDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>Resolves a configured path against <see cref="BaseDirectory"/>.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));
}

public sealed class FeatureOptions
{
    public bool RenameSymbols { get; set; } = true;
    public bool ObfuscateStrings { get; set; } = true;
    public bool ControlFlow { get; set; }
    public bool TransformConstants { get; set; }
}

public sealed class RenamingOptions
{
    /// <summary>Same input and seed give the same names and the same output bytes.</summary>
    public bool Deterministic { get; set; } = true;

    /// <summary>Secret that new names derive from. Prefer <see cref="SeedEnvironmentVariable"/> over committing it.</summary>
    public string? Seed { get; set; }

    public string? SeedEnvironmentVariable { get; set; }

    /// <summary>Keep the names of everything other assemblies could compile against (public and protected).</summary>
    public bool PreservePublicApi { get; set; } = true;

    /// <summary>Renamed top-level types also lose their namespace.</summary>
    public bool RenameNamespaces { get; set; } = true;

    public bool RenameParameters { get; set; } = true;
    public bool RenameGenericParameters { get; set; } = true;

    /// <summary>Off by default: Enum.ToString, Enum.Parse and string enum converters read value names.</summary>
    public bool RenameEnumValues { get; set; }

    /// <summary>"letters" (short ASCII names) or "unicode" (Latin and Cyrillic look-alikes).</summary>
    public string Scheme { get; set; } = NamingSchemes.Letters;
}

public static class NamingSchemes
{
    public const string Letters = "letters";
    public const string Unicode = "unicode";
    public static readonly string[] All = { Letters, Unicode };
}

public sealed class StringOptions
{
    /// <summary>Intern decoded strings, so reference equality behaves as it did with <c>ldstr</c>.</summary>
    public bool Intern { get; set; } = true;

    public int MinLength { get; set; } = 1;

    /// <summary>Patterns of literal values to leave readable (for example, values a crash reporter greps for).</summary>
    public List<string> KeepPlain { get; set; } = new();
}

public sealed class ControlFlowOptions
{
    /// <summary>Methods longer than this are left alone.</summary>
    public int MaxInstructions { get; set; } = 5000;
}

public sealed class PreservationOptions
{
    public bool PreserveSerializedFields { get; set; } = true;

    /// <summary>Keep names that string-based reflection, serializers and attribute strings may look up.</summary>
    public bool PreserveReflectionTargets { get; set; } = true;

    public string? RulesFile { get; set; }
    public List<PreservationRule> Rules { get; set; } = new();

    /// <summary>Attributes that mean "do not rename" (full names; the Attribute suffix is optional).</summary>
    public List<string> SkipRenameAttributes { get; set; } = new() { "Beebyte.Obfuscator.SkipRename" };

    /// <summary>Attributes that mean "do not touch at all".</summary>
    public List<string> SkipAttributes { get; set; } = new() { "Beebyte.Obfuscator.Skip" };
}

public sealed class ReportingOptions
{
    public bool GenerateMapping { get; set; } = true;
    public bool GenerateReport { get; set; } = true;

    /// <summary>Where the mapping and report go. Must not be inside the output directory (see AllowReportsInOutput).</summary>
    public string ReportsDirectory { get; set; } = "obfuscation-reports";

    public string? MappingPath { get; set; }
    public string? ReportPath { get; set; }

    public bool AllowReportsInOutput { get; set; }

    /// <summary>List every kept symbol and why in the report (original names only).</summary>
    public bool IncludePreservedSymbols { get; set; }
}
