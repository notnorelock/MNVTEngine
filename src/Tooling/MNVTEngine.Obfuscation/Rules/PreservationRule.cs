// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;

namespace MNVTEngine.Obfuscation.Rules;

/// <summary>
/// "Leave these alone": a rule from the config or the rules file. Every pattern that is set must match.
/// <see cref="Type"/> matches the full name with '/' between nested types ('+' is accepted too).
/// </summary>
public sealed class PreservationRule
{
    public string? Assembly { get; set; }
    public string? Namespace { get; set; }
    public string? Type { get; set; }
    public string? Member { get; set; }

    /// <summary>any, type, method, field, property or event.</summary>
    public string Kind { get; set; } = "any";

    /// <summary>What to exclude the match from: all, rename, strings, controlFlow, constants.</summary>
    public List<string> Features { get; set; } = new() { "all" };

    /// <summary>A rule matching a type (no member pattern) also covers its members and nested types.</summary>
    public bool ApplyToMembers { get; set; } = true;

    public string? Reason { get; set; }
}

/// <summary>A separate rules file: <c>{ "schemaVersion": 1, "rules": [ ... ] }</c>.</summary>
public sealed class RuleFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PreservationRule> Rules { get; set; } = new();
}

[Flags]
internal enum SymbolKinds
{
    None = 0,
    Type = 1,
    Method = 2,
    Field = 4,
    Property = 8,
    Event = 16,
    Members = Method | Field | Property | Event,
    Any = Type | Members,
}

internal sealed class CompiledRule
{
    public required Glob? Assembly { get; init; }
    public required Glob? Namespace { get; init; }
    public required Glob? Type { get; init; }
    public required Glob? Member { get; init; }
    public required SymbolKinds Kinds { get; init; }
    public required Protection Features { get; init; }
    public required bool ApplyToMembers { get; init; }
    public required string Reason { get; init; }

    public bool MatchesAssembly(string assembly) => Assembly == null || Assembly.IsMatch(assembly);

    /// <summary>Whether the type itself (its name) is matched.</summary>
    public bool MatchesType(string assembly, TypeDefinition type) =>
        Member == null && (Kinds & SymbolKinds.Type) != 0 && MatchesTypeName(assembly, type);

    /// <summary>Whether the type matches the assembly, namespace and type patterns (ignoring kind and member).</summary>
    public bool MatchesTypeName(string assembly, TypeDefinition type)
    {
        if (!MatchesAssembly(assembly)) return false;
        if (Namespace != null && !Namespace.IsMatch(OuterNamespace(type))) return false;
        return Type == null || Type.IsMatch(type.FullName);
    }

    public bool MatchesMember(string assembly, IMemberDefinition member, SymbolKinds kind)
    {
        if ((Kinds & kind) == 0) return false;
        // Without a member pattern, a rule that can match types covers members through ApplyToMembers; a
        // member-kind rule ("kind": "method") covers every member of that kind.
        if (Member == null && (Kinds & SymbolKinds.Type) != 0) return false;
        return MatchesTypeName(assembly, member.DeclaringType) && (Member == null || Member.IsMatch(member.Name));
    }

    internal static string OuterNamespace(TypeDefinition t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Namespace;
    }
}

internal static class RuleCompiler
{
    public static List<CompiledRule> Compile(IEnumerable<PreservationRule> rules, string source, List<Configuration.ConfigError> errors)
    {
        var compiled = new List<CompiledRule>();
        int i = 0;
        foreach (PreservationRule r in rules)
        {
            string path = $"{source}[{i++}]";
            if (r == null)
            {
                errors.Add(new(path, "A rule is null."));
                continue;
            }
            bool ok = true;
            Glob? G(string? pattern, string field)
            {
                if (pattern == null) return null;
                if (field == "type") pattern = pattern.Replace('+', '/');
                if (Glob.TryCreate(pattern, out Glob? g, out string? error)) return g;
                errors.Add(new($"{path}.{field}", error!));
                ok = false;
                return null;
            }
            Glob? asm = G(r.Assembly, "assembly"), ns = G(r.Namespace, "namespace"), type = G(r.Type, "type"), member = G(r.Member, "member");
            SymbolKinds kinds = (r.Kind ?? "any").Trim().ToLowerInvariant() switch
            {
                "any" => SymbolKinds.Any,
                "type" => SymbolKinds.Type,
                "method" => SymbolKinds.Method,
                "field" => SymbolKinds.Field,
                "property" => SymbolKinds.Property,
                "event" => SymbolKinds.Event,
                _ => SymbolKinds.None,
            };
            if (kinds == SymbolKinds.None)
            {
                errors.Add(new($"{path}.kind", $"Unknown kind \"{r.Kind}\"; use any, type, method, field, property or event."));
                ok = false;
            }
            Protection features = Protection.None;
            foreach (string f in r.Features ?? new List<string> { "all" })
            {
                if (ProtectionNames.TryParse(f, out Protection p)) features |= p;
                else
                {
                    errors.Add(new($"{path}.features", $"Unknown feature \"{f}\"; use all, rename, strings, controlFlow or constants."));
                    ok = false;
                }
            }
            if (features == Protection.None) features = Protection.All;
            if (asm == null && ns == null && type == null && member == null && ok)
            {
                errors.Add(new(path, "The rule matches nothing: set at least one of assembly, namespace, type or member."));
                ok = false;
            }
            if (!ok) continue;
            compiled.Add(new CompiledRule
            {
                Assembly = asm,
                Namespace = ns,
                Type = type,
                Member = member,
                Kinds = kinds,
                Features = features,
                ApplyToMembers = r.ApplyToMembers,
                Reason = "rule:" + (string.IsNullOrWhiteSpace(r.Reason) ? path : r.Reason!.Trim()),
            });
        }
        return compiled;
    }
}
