// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;

namespace MNVTEngine.Obfuscation.Analysis;

public enum SymbolKind
{
    Type,
    Method,
    Field,
    Property,
    Event,
}

/// <summary>
/// A type or member of a protected assembly, with what it is excluded from and why. <see cref="Id"/> is the
/// original identity (assembly plus Cecil full name), stable across runs; new names derive from it.
/// </summary>
public sealed class Symbol
{
    private readonly List<string> _reasons = new();

    public required SymbolKind Kind { get; init; }
    public required IMemberDefinition Definition { get; init; }
    public required LoadedAssembly Owner { get; init; }
    public required string Id { get; init; }
    public required string OriginalName { get; init; }
    public string? OriginalNamespace { get; init; }

    /// <summary>Transforms this symbol is excluded from. Rename covers its name; the rest cover a method's body.</summary>
    public Protection Excluded { get; private set; }

    public string? NewName { get; set; }

    /// <summary>Why the name is kept (first few reasons).</summary>
    public IReadOnlyList<string> Reasons => _reasons;

    public bool CanRename => (Excluded & Protection.Rename) == 0;
    public bool IsRenamed => NewName != null && NewName != OriginalName;

    /// <summary>Keeps the name. Returns true when this changed anything.</summary>
    public bool Preserve(string reason) => Exclude(Protection.Rename, reason);

    public bool Exclude(Protection what, string reason)
    {
        bool changed = (Excluded & what) != what;
        Excluded |= what;
        if ((what & Protection.Rename) != 0 && _reasons.Count < 6 && !_reasons.Contains(reason)) _reasons.Add(reason);
        return changed;
    }

    public override string ToString() => Id;

    internal static string MakeId(string assembly, IMemberDefinition member) => member switch
    {
        TypeDefinition t => $"[{assembly}]T:{t.FullName}",
        MethodDefinition m => $"[{assembly}]M:{m.FullName}",
        FieldDefinition f => $"[{assembly}]F:{f.FullName}",
        PropertyDefinition p => $"[{assembly}]P:{p.FullName}",
        EventDefinition e => $"[{assembly}]E:{e.FullName}",
        _ => throw new ArgumentOutOfRangeException(nameof(member)),
    };
}

/// <summary>Every symbol of the protected assemblies, by definition (reference identity).</summary>
public sealed class SymbolCatalog
{
    private readonly Dictionary<IMemberDefinition, Symbol> _byDefinition = new(ReferenceEqualityComparer.Instance);
    private readonly List<Symbol> _all = new();

    public IReadOnlyList<Symbol> All => _all;

    public void Add(Symbol s)
    {
        _byDefinition.Add(s.Definition, s);
        _all.Add(s);
    }

    public Symbol? Get(IMemberDefinition? d) => d != null && _byDefinition.TryGetValue(d, out Symbol? s) ? s : null;

    public bool Contains(IMemberDefinition? d) => d != null && _byDefinition.ContainsKey(d);

    public IEnumerable<Symbol> Of(LoadedAssembly a) => _all.Where(s => ReferenceEquals(s.Owner, a));
}

/// <summary>Cecil resolution that answers null instead of throwing when a reference is missing.</summary>
internal static class Resolve
{
    public static TypeDefinition? Type(TypeReference? t)
    {
        if (t == null) return null;
        try
        {
            return t.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static MethodDefinition? Method(MethodReference? m)
    {
        if (m == null) return null;
        try
        {
            return m.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static FieldDefinition? Field(FieldReference? f)
    {
        if (f == null) return null;
        try
        {
            return f.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static IMemberDefinition? Member(MemberReference r) => r switch
    {
        MethodReference m => Method(m),
        FieldReference f => Field(f),
        TypeReference t => Type(t),
        _ => null,
    };

    /// <summary>The assembly a type reference points to, by name, or null for definitions and odd scopes.</summary>
    public static string? ScopeAssembly(TypeReference t)
    {
        while (t is TypeSpecification spec) t = spec.ElementType;
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.Scope switch
        {
            AssemblyNameReference a => a.Name,
            ModuleDefinition m => m.Assembly?.Name.Name,
            _ => null,
        };
    }
}
