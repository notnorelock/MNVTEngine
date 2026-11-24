// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Obfuscation.Configuration;

/// <summary>A problem in a configuration or rules file. <see cref="Path"/> is a JSON path such as <c>renaming.scheme</c>.</summary>
public sealed record ConfigError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed class ConfigResult
{
    public ObfuscatorConfig? Config { get; init; }
    public List<ConfigError> Errors { get; init; } = new();
    public bool Ok => Config != null && Errors.Count == 0;
}

/// <summary>
/// Reads configuration: the defaults, then the preset (from the file or the caller), then the file. Unknown
/// settings are errors, not ignored, so a typo cannot silently turn a protection off.
/// </summary>
public static class ConfigLoader
{
    public const long MaxFileBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ConfigResult Load(string? path, string? preset = null)
    {
        if (path == null) return FromJson("{}", Environment.CurrentDirectory, preset);
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) return Fail("$", $"The configuration file {full} does not exist.");
        if (new FileInfo(full).Length > MaxFileBytes) return Fail("$", $"The configuration file is larger than {MaxFileBytes / 1024} KB.");
        return FromJson(File.ReadAllText(full), Path.GetDirectoryName(full)!, preset);
    }

    public static ConfigResult FromJson(string json, string baseDirectory, string? preset = null)
    {
        JsonObject file;
        try
        {
            JsonNode? node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 32 });
            if (node is not JsonObject o) return Fail("$", "The configuration must be a JSON object.");
            file = o;
        }
        catch (JsonException e)
        {
            return Fail(e.Path ?? "$", "Not valid JSON: " + e.Message);
        }

        string? presetName = preset ?? (file["preset"] is JsonValue v && v.TryGetValue(out string? s) ? s : null);
        JsonObject merged = (JsonObject)JsonSerializer.SerializeToNode(new ObfuscatorConfig(), Json)!;
        if (presetName != null)
        {
            if (!Presets.TryGet(presetName, out JsonObject? fragment)) return Fail("preset", $"Unknown preset \"{presetName}\"; use {string.Join(", ", Presets.Names)}.");
            Merge(merged, fragment!);
        }
        Merge(merged, file);
        if (presetName != null) merged["preset"] = presetName;

        ObfuscatorConfig? config;
        try
        {
            config = merged.Deserialize<ObfuscatorConfig>(Json);
        }
        catch (JsonException e)
        {
            string message = e.Message.Contains("could not be mapped", StringComparison.Ordinal)
                ? "Unknown setting. Check the spelling against docs/configuration.md."
                : "Wrong type: " + e.Message;
            return Fail(e.Path ?? "$", message);
        }
        if (config == null) return Fail("$", "The configuration is empty.");
        config.BaseDirectory = baseDirectory;
        var errors = Validate(config);
        return new ConfigResult { Config = config, Errors = errors };
    }

    public static string Serialize(ObfuscatorConfig config) => JsonSerializer.Serialize(config, Json);

    /// <summary>Checks values that the JSON shape cannot: enums, ranges, patterns and files that must exist.</summary>
    public static List<ConfigError> Validate(ObfuscatorConfig c)
    {
        var errors = new List<ConfigError>();
        void Err(string path, string message) => errors.Add(new ConfigError(path, message));

        if (c.SchemaVersion != ObfuscatorConfig.CurrentSchemaVersion)
            Err("schemaVersion", $"Schema version {c.SchemaVersion} is not supported; this tool reads version {ObfuscatorConfig.CurrentSchemaVersion}.");
        if (c.Assemblies == null || c.Assemblies.Count == 0) Err("assemblies", "List at least one assembly pattern (for example \"*.dll\").");
        CheckGlobs(c.Assemblies, "assemblies", errors);
        CheckGlobs(c.ExcludeAssemblies, "excludeAssemblies", errors);
        CheckGlobs(c.Strings?.KeepPlain, "strings.keepPlain", errors);
        CheckPath(c.InputDirectory, "inputDirectory", errors);
        CheckPath(c.OutputDirectory, "outputDirectory", errors);
        for (int i = 0; i < (c.ReferenceDirectories?.Count ?? 0); i++)
        {
            string p = $"referenceDirectories[{i}]";
            if (CheckPath(c.ReferenceDirectories![i], p, errors) && !Directory.Exists(c.Resolve(c.ReferenceDirectories[i])))
                Err(p, $"The folder {c.Resolve(c.ReferenceDirectories[i])} does not exist.");
        }

        if (c.Features == null) Err("features", "Must be an object.");
        if (c.Renaming == null) Err("renaming", "Must be an object.");
        else
        {
            if (!NamingSchemes.All.Contains(c.Renaming.Scheme)) Err("renaming.scheme", $"Unknown scheme \"{c.Renaming.Scheme}\"; use {string.Join(" or ", NamingSchemes.All)}.");
            if (c.Renaming.Seed != null && (c.Renaming.Seed.Length == 0 || c.Renaming.Seed.Length > 256)) Err("renaming.seed", "The seed must be 1 to 256 characters.");
            if (c.Renaming.SeedEnvironmentVariable != null && !IsIdentifier(c.Renaming.SeedEnvironmentVariable)) Err("renaming.seedEnvironmentVariable", "Not a valid environment variable name.");
        }
        if (c.Strings == null) Err("strings", "Must be an object.");
        else if (c.Strings.MinLength < 1 || c.Strings.MinLength > 4096) Err("strings.minLength", "Must be between 1 and 4096.");
        if (c.ControlFlow == null) Err("controlFlow", "Must be an object.");
        else if (c.ControlFlow.MaxInstructions < 3 || c.ControlFlow.MaxInstructions > 100_000) Err("controlFlow.maxInstructions", "Must be between 3 and 100000.");
        if (c.Preservation == null) Err("preservation", "Must be an object.");
        else
        {
            if (CheckPath(c.Preservation.RulesFile, "preservation.rulesFile", errors) && c.Preservation.RulesFile != null && !File.Exists(c.Resolve(c.Preservation.RulesFile)))
                Err("preservation.rulesFile", $"The rules file {c.Resolve(c.Preservation.RulesFile)} does not exist.");
            RuleCompiler.Compile(c.Preservation.Rules ?? new(), "preservation.rules", errors);
        }
        if (c.Reporting == null) Err("reporting", "Must be an object.");
        else
        {
            CheckPath(c.Reporting.ReportsDirectory, "reporting.reportsDirectory", errors);
            CheckPath(c.Reporting.MappingPath, "reporting.mappingPath", errors);
            CheckPath(c.Reporting.ReportPath, "reporting.reportPath", errors);
        }
        return errors;
    }

    /// <summary>Loads the rules file named in the config, plus the inline rules.</summary>
    internal static List<CompiledRule> LoadRules(ObfuscatorConfig c, List<ConfigError> errors)
    {
        var rules = RuleCompiler.Compile(c.Preservation.Rules, "preservation.rules", errors);
        if (c.Preservation.RulesFile == null) return rules;
        string path = c.Resolve(c.Preservation.RulesFile);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new IOException($"larger than {MaxFileBytes / 1024} KB");
            RuleFile? file = JsonSerializer.Deserialize<RuleFile>(File.ReadAllText(path), Json);
            if (file == null) errors.Add(new("rulesFile", "The rules file is empty."));
            else if (file.SchemaVersion != 1) errors.Add(new("rulesFile.schemaVersion", $"Rules schema version {file.SchemaVersion} is not supported; use 1."));
            else rules.AddRange(RuleCompiler.Compile(file.Rules ?? new(), "rulesFile.rules", errors));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new("rulesFile" + (e is JsonException je && je.Path != null ? "." + je.Path.TrimStart('$', '.') : string.Empty), $"Cannot read {path}: {e.Message}"));
        }
        return rules;
    }

    private static void CheckGlobs(List<string>? patterns, string path, List<ConfigError> errors)
    {
        for (int i = 0; i < (patterns?.Count ?? 0); i++)
            if (!Glob.TryCreate(patterns![i], out _, out string? error)) errors.Add(new($"{path}[{i}]", error!));
    }

    private static bool CheckPath(string? path, string at, List<ConfigError> errors)
    {
        if (path == null) return false;
        if (path.Length == 0 || path.Length > 4096 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Any(char.IsControl))
        {
            errors.Add(new(at, "Not a valid path."));
            return false;
        }
        return true;
    }

    private static bool IsIdentifier(string s) => s.Length is > 0 and <= 128 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static ConfigResult Fail(string path, string message) => new() { Errors = { new ConfigError(path, message) } };

    /// <summary>Deep merge: objects merge key by key, everything else (arrays included) is replaced.</summary>
    internal static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject so && target[key] is JsonObject to) Merge(to, so);
            else target[key] = value?.DeepClone();
        }
    }
}

/// <summary>Named starting points. Each is a partial configuration laid over the defaults.</summary>
public static class Presets
{
    public const string Safe = "safe";
    public const string Balanced = "balanced";
    public const string Aggressive = "aggressive";
    public static readonly string[] Names = { Safe, Balanced, Aggressive };

    public static bool TryGet(string name, out JsonObject? fragment)
    {
        fragment = name switch
        {
            // Private and internal names only, plus strings; the public API (what the engine calls) is kept.
            Safe => new JsonObject
            {
                ["features"] = new JsonObject { ["renameSymbols"] = true, ["obfuscateStrings"] = true, ["controlFlow"] = false, ["transformConstants"] = false },
                ["renaming"] = new JsonObject { ["preservePublicApi"] = true, ["renameParameters"] = false },
            },
            Balanced => new JsonObject
            {
                ["features"] = new JsonObject { ["renameSymbols"] = true, ["obfuscateStrings"] = true, ["controlFlow"] = false, ["transformConstants"] = true },
                ["renaming"] = new JsonObject { ["preservePublicApi"] = true },
            },
            // For a closed set of assemblies that nothing else compiles against.
            Aggressive => new JsonObject
            {
                ["features"] = new JsonObject { ["renameSymbols"] = true, ["obfuscateStrings"] = true, ["controlFlow"] = true, ["transformConstants"] = true },
                ["renaming"] = new JsonObject { ["preservePublicApi"] = false },
            },
            _ => null,
        };
        return fragment != null;
    }
}
