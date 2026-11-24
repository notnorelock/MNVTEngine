// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Obfuscation;
using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Reporting;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Build;

/// <summary>How much to obfuscate a game's compiled assembly. Renaming is always on; the rest is opt-in.</summary>
public sealed record ObfuscationSettings
{
    /// <summary>Encrypt string literals in method bodies.</summary>
    public bool Strings { get; init; } = true;

    /// <summary>Reorder each method's control flow behind a dispatcher where it can be proven safe. Slower builds.</summary>
    public bool ControlFlow { get; init; }

    /// <summary>"letters" (short ASCII) or "unicode" (look-alike glyphs).</summary>
    public string Scheme { get; init; } = NamingSchemes.Letters;

    /// <summary>Secret the new names derive from. Prefer the environment variable over a stored value.</summary>
    public string? Seed { get; init; }

    public string SeedEnvironmentVariable { get; init; } = "MNVT_OBFUSCATION_SEED";
}

/// <summary>What obfuscating one assembly produced.</summary>
public sealed record ObfuscationOutcome(
    bool Success,
    string ObfuscatedFile,
    IReadOnlyDictionary<string, string> TypeRenames,
    IReadOnlyDictionary<string, string> MemberRenames,
    IReadOnlyDictionary<string, string> PinMap,
    string? MappingPath,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Obfuscates a game's compiled script assembly for a JIT export: renames its types, namespaces and non-serialized
/// members, and (optionally) encrypts strings, keeping everything the engine reaches by name — serialized member
/// names, component ids and the public surface the engine's virtual calls bind to. The component type names it does
/// rename are returned so the export can remap them in the shipped scene and prefab data (<see cref="SceneRemapper"/>).
/// The rename map is written next to the build's reports, never into the game, so crash logs can still be read.
/// </summary>
public static class GameObfuscation
{
    /// <summary>
    /// Protects <paramref name="assemblyFile"/>, writing the obfuscated copy into <paramref name="outputDirectory"/>
    /// (which must differ from the assembly's folder) and the rename map into <paramref name="reportsDirectory"/>.
    /// <paramref name="referenceDirectories"/> hold the engine assemblies it is compiled against.
    /// </summary>
    /// <param name="preserveTypeNames">
    /// Keep every type's name (rename only members, and encrypt strings). Used for ahead-of-time exports, whose
    /// generated bootstrap references the component types by name and whose scenes are then not remapped. When
    /// false, type names are renamed too and <see cref="ObfuscationOutcome.TypeRenames"/> drives the scene remap.
    /// </param>
    /// <param name="renameMemberTypes">
    /// Full names of the types whose serialized members may be renamed (components and the data types nested in their
    /// scene data). The caller remaps the scene keys of exactly these from <see cref="ObfuscationOutcome.MemberRenames"/>;
    /// null or empty keeps every serialized member name. Ignored when <paramref name="preserveTypeNames"/> is set.
    /// </param>
    public static ObfuscationOutcome Protect(
        string assemblyFile,
        IReadOnlyList<string> referenceDirectories,
        string outputDirectory,
        string reportsDirectory,
        ObfuscationSettings settings,
        bool preserveTypeNames = false,
        IReadOnlySet<string>? renameMemberTypes = null,
        IReadOnlyList<string>? pinnedCompanions = null,
        IReadOnlyDictionary<string, string>? pinnedNames = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(assemblyFile);
        ArgumentNullException.ThrowIfNull(settings);
        var inputDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyFile))
            ?? throw new ArgumentException("The assembly file has no directory.", nameof(assemblyFile));
        var fileName = Path.GetFileName(assemblyFile);
        var primaryName = Path.GetFileNameWithoutExtension(fileName);
        var companions = pinnedCompanions ?? [];

        var config = new ObfuscatorConfig
        {
            InputDirectory = inputDirectory,
            OutputDirectory = Path.GetFullPath(outputDirectory),
            // The pinned companions (an already-obfuscated game) are protected too, so the primary's references into
            // them are rewritten to the pinned names; only the primary's output ships.
            Assemblies = [fileName, .. companions],
            ReferenceDirectories = referenceDirectories.Select(Path.GetFullPath).ToList(),
            // Control-flow flattening and integer-literal math are always on: obfuscation always hides the logic, not just the names.
            Features = new FeatureOptions { RenameSymbols = true, ObfuscateStrings = settings.Strings, ControlFlow = true, TransformConstants = true },
            // A game's assembly is closed: nothing else compiles against it, so names can go. The engine binds to it
            // through virtual overrides of Component (kept automatically) and serialized members (kept by name below).
            // Ahead-of-time builds keep type names (preserveTypeNames): their generated bootstrap names the types.
            Renaming = new RenamingOptions
            {
                PreservePublicApi = preserveTypeNames,
                Scheme = settings.Scheme,
                Seed = settings.Seed,
                SeedEnvironmentVariable = settings.SeedEnvironmentVariable,
                PinnedAssemblies = companions.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal),
                PinnedNames = pinnedNames is null ? new(StringComparer.Ordinal) : new(pinnedNames, StringComparer.Ordinal),
            },
            Preservation = new PreservationOptions
            {
                PreserveSerializedFields = true,
                // JIT renames the scene-data types' members and remaps the keys; AOT keeps names (its bootstrap scenes are not remapped).
                RenameMemberTypes = preserveTypeNames || renameMemberTypes is null ? [] : new HashSet<string>(renameMemberTypes, StringComparer.Ordinal),
                Rules = preserveTypeNames ? [new() { Type = "*", Kind = "type", Features = ["rename"], Reason = "aot-type-name" }] : [],
            },
            Reporting = new ReportingOptions { ReportsDirectory = Path.GetFullPath(reportsDirectory), AllowReportsInOutput = false, GenerateMapping = true },
        };

        var result = new ObfuscationEngine(config).Protect();
        var entries = (result.Mapping?.Entries ?? []).Where(e => e.Assembly == primaryName).ToList();
        var typeRenames = entries
            .Where(e => e.Kind == "Type")
            .ToDictionary(e => e.Original, e => e.Obfuscated, StringComparer.Ordinal);

        // Member renames keyed by "DeclaringTypeFullName::originalMemberName" -> new member name; the caller turns
        // these into per-component scene JSON key maps (see GameBuilder).
        var memberRenames = entries
            .Where(e => e.Kind is "Field" or "Property")
            .GroupBy(e => e.Original, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().NewName, StringComparer.Ordinal);

        return new ObfuscationOutcome(
            result.Success,
            Path.Combine(config.OutputDirectory!, fileName),
            typeRenames,
            memberRenames,
            result.FinalNames,
            result.MappingPath,
            result.Diagnostics.Items.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList(),
            result.Diagnostics.Items.Where(d => d.Severity == Severity.Warning).Select(d => d.ToString()).ToList());
    }
}
