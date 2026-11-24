// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using MNVTEngine.Obfuscation.Analysis;
using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Naming;
using MNVTEngine.Obfuscation.Reporting;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>One renamed symbol, for the mapping file.</summary>
public sealed record RenameEntry(string Assembly, SymbolKind Kind, string OriginalId, string OriginalName, string NewName, string? OriginalDeclaringType, string? NewDeclaringType, string? OriginalFullName, string? NewFullName);

/// <summary>
/// Gives renamable symbols their new names and keeps every reference consistent: type and member references
/// in all protected modules (cross-assembly ones included), type names inside custom attribute blobs, and
/// named attribute arguments. Everything is resolved before the first name changes, because Cecil resolves
/// references by name.
/// </summary>
internal sealed class Renamer
{
    private static readonly string[] AccessorPrefixes = { "get_", "set_", "add_", "remove_", "raise_" };

    private readonly AssemblySet _set;
    private readonly AnalysisOutcome _analysis;
    private readonly ObfuscatorConfig _config;
    private readonly NameGenerator _names;
    private readonly DiagnosticBag _diag;
    private readonly SymbolCatalog _catalog;

    private readonly List<(TypeReference Ref, TypeDefinition Def)> _typeRefs = new();
    private readonly List<(MemberReference Ref, IMemberDefinition Def)> _memberRefs = new();
    private readonly List<(CustomAttribute Attribute, bool IsField, int Index, IMemberDefinition Def)> _namedArgs = new();
    private readonly Dictionary<IMemberDefinition, string> _newNames = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeDefinition, string> _newNamespaces = new(ReferenceEqualityComparer.Instance);

    public List<RenameEntry> Entries { get; } = new();
    public int ParametersRenamed { get; private set; }

    /// <summary>Every renamed symbol's final name, by <see cref="Symbol.Id"/>; persisted so a DLC can pin the game's names.</summary>
    public Dictionary<string, string> FinalNames { get; } = new(StringComparer.Ordinal);

    // The forced name for a symbol of a pinned (already-obfuscated) assembly, or null to generate one.
    private string? Pinned(Symbol s) =>
        _config.Renaming.PinnedAssemblies.Contains(s.Owner.Name) && _config.Renaming.PinnedNames.TryGetValue(s.Id, out var name) ? name : null;

    private string? PinnedGroupName(string key)
    {
        foreach (var pair in _analysis.VirtualKeys)
        {
            if (pair.Value == key && _catalog.Get(pair.Key) is { } s && Pinned(s) is { } pinned)
            {
                return pinned;
            }
        }

        return null;
    }

    public Renamer(AssemblySet set, AnalysisOutcome analysis, ObfuscatorConfig config, NameGenerator names, DiagnosticBag diagnostics)
    {
        _set = set;
        _analysis = analysis;
        _config = config;
        _names = names;
        _diag = diagnostics;
        _catalog = analysis.Catalog;
    }

    public void Run()
    {
        CollectReferences();
        AssignTypeNames();
        AssignMemberNames();
        var originalFullNames = new Dictionary<TypeDefinition, string>(ReferenceEqualityComparer.Instance);
        foreach (Symbol s in _catalog.All)
            if (s.Definition is TypeDefinition td) originalFullNames[td] = td.FullName;
        Apply();
        RecordEntries(originalFullNames);
        if (_config.Renaming.RenameParameters || _config.Renaming.RenameGenericParameters) RenameParameters();
        StripAttributes();
        foreach (LoadedAssembly a in _set.Protected) CecilCaches.RefreshTypeNameCache(a.Module, _diag);
    }

    // ── Before any name changes ────────────────────────────────────────────

    private void CollectReferences()
    {
        foreach (LoadedAssembly a in _set.Protected)
        {
            ModuleDefinition module = a.Module;
            foreach (TypeReference tr in module.GetTypeReferences()) Track(tr);
            foreach (MemberReference mr in module.GetMemberReferences()) TrackMember(mr);
            // References to the module's own members through a generic instance (Counter<T>::_items,
            // List<Counter<int>>::Add) and generic method instances live inline in method bodies, not in
            // GetMemberReferences(); walk the bodies to catch them.
            foreach (TypeDefinition t in module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                    TrackBody(m, a);
            foreach (ICustomAttributeProvider p in AttributeWalker.Providers(module))
            {
                if (!p.HasCustomAttributes) continue;
                foreach (CustomAttribute ca in p.CustomAttributes) CollectAttribute(ca, a);
            }
        }
    }

    private void TrackMember(MemberReference mr)
    {
        if (mr is MethodReference method)
        {
            Track(method.DeclaringType);
            Track(method.ReturnType);
            foreach (ParameterDefinition p in method.Parameters) Track(p.ParameterType);
        }
        else if (mr is FieldReference field)
        {
            Track(field.DeclaringType);
            Track(field.FieldType);
        }
        IMemberDefinition? d = Resolve.Member(mr);
        if (d != null && _catalog.Contains(d)) _memberRefs.Add((mr, d));
    }

    private void TrackBody(MethodDefinition method, LoadedAssembly owner)
    {
        if (!method.HasBody) return;
        Mono.Cecil.Cil.MethodBody body;
        try
        {
            body = method.Body;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            _diag.Warning(DiagnosticCodes.NotManaged, $"A method body did not decode during renaming ({e.Message}); references in it keep their old names.", owner.Name, method.FullName);
            return;
        }
        foreach (Mono.Cecil.Cil.VariableDefinition v in body.Variables) Track(v.VariableType);
        foreach (Mono.Cecil.Cil.Instruction ins in body.Instructions)
        {
            switch (ins.Operand)
            {
                case GenericInstanceMethod gim:
                    TrackMember(gim.ElementMethod);
                    foreach (TypeReference arg in gim.GenericArguments) Track(arg);
                    break;
                case MethodReference mr:
                    TrackMember(mr);
                    break;
                case FieldReference fr:
                    TrackMember(fr);
                    break;
                case TypeReference tr:
                    Track(tr);
                    break;
            }
        }
    }

    private void CollectAttribute(CustomAttribute ca, LoadedAssembly owner)
    {
        try
        {
            // Reading the arguments makes Cecil re-encode the blob on write, with the new type names.
            foreach (CustomAttributeArgument arg in ca.ConstructorArguments) TrackArgument(arg);
            for (int i = 0; i < ca.Fields.Count; i++) TrackArgument(ca.Fields[i].Argument);
            for (int i = 0; i < ca.Properties.Count; i++) TrackArgument(ca.Properties[i].Argument);
        }
        catch (Exception e) when (e is AssemblyResolutionException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            _diag.Warning(DiagnosticCodes.AttributeBlob,
                $"An attribute of type {ca.AttributeType.FullName} could not be decoded ({e.Message}); it is written back unchanged, so any type name inside it keeps its old spelling.", owner.Name);
            return;
        }
        if (Resolve.Type(ca.AttributeType) is not { } attrType || !_catalog.Contains(attrType)) return;
        for (int i = 0; i < ca.Fields.Count; i++)
            if (FindMember(attrType, ca.Fields[i].Name, field: true) is { } f) _namedArgs.Add((ca, true, i, f));
        for (int i = 0; i < ca.Properties.Count; i++)
            if (FindMember(attrType, ca.Properties[i].Name, field: false) is { } p) _namedArgs.Add((ca, false, i, p));
    }

    private static IMemberDefinition? FindMember(TypeDefinition t, string name, bool field)
    {
        for (TypeDefinition? c = t; c != null; c = Resolve.Type(c.BaseType))
        {
            IMemberDefinition? m = field ? c.Fields.FirstOrDefault(f => f.Name == name) : c.Properties.FirstOrDefault(p => p.Name == name);
            if (m != null) return m;
        }
        return null;
    }

    private void TrackArgument(CustomAttributeArgument arg)
    {
        Track(arg.Type);
        switch (arg.Value)
        {
            case TypeReference t:
                Track(t);
                break;
            case CustomAttributeArgument inner:
                TrackArgument(inner);
                break;
            case CustomAttributeArgument[] many:
                foreach (CustomAttributeArgument x in many) TrackArgument(x);
                break;
        }
    }

    private void Track(TypeReference? t)
    {
        switch (t)
        {
            case null:
            case TypeDefinition:
            case GenericParameter:
                return;
            case GenericInstanceType git:
                Track(git.ElementType);
                foreach (TypeReference a in git.GenericArguments) Track(a);
                return;
            case TypeSpecification spec:
                Track(spec.ElementType);
                return;
        }
        Track(t.DeclaringType);
        TypeDefinition? d = Resolve.Type(t);
        if (d != null && _catalog.Contains(d)) _typeRefs.Add((t, d));
    }

    // ── Names ──────────────────────────────────────────────────────────────

    private void AssignTypeNames()
    {
        bool stripNamespaces = _config.Renaming.RenameNamespaces;
        foreach (LoadedAssembly a in _set.Protected)
        {
            var topLevel = new HashSet<string>(StringComparer.Ordinal);
            var nested = new Dictionary<TypeDefinition, HashSet<string>>(ReferenceEqualityComparer.Instance);
            foreach (TypeDefinition t in a.Module.GetTypes())
            {
                if (t.DeclaringType == null) topLevel.Add(t.Namespace + "\u0000" + t.Name);
                else NestedNames(nested, t.DeclaringType).Add(t.Name);
            }
            foreach (Symbol s in _catalog.Of(a).Where(s => s.Kind == SymbolKind.Type && s.CanRename).OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var t = (TypeDefinition)s.Definition;
                string arity = Arity(t.Name);
                int hint = NameLength(t.Name); // original length, without the `n arity suffix
                if (t.DeclaringType == null)
                {
                    string ns = stripNamespaces ? string.Empty : t.Namespace;
                    string name = Pinned(s) ?? _names.Create(s.Id, n => topLevel.Contains(ns + "\u0000" + n + arity), hint) + arity;
                    topLevel.Add(ns + "\u0000" + name);
                    _newNames[t] = name;
                    _newNamespaces[t] = ns;
                }
                else
                {
                    HashSet<string> taken = NestedNames(nested, t.DeclaringType);
                    string name = Pinned(s) ?? _names.Create(s.Id, n => taken.Contains(n + arity), hint) + arity;
                    taken.Add(name);
                    _newNames[t] = name;
                }
            }
        }
    }

    private static HashSet<string> NestedNames(Dictionary<TypeDefinition, HashSet<string>> map, TypeDefinition owner)
    {
        if (!map.TryGetValue(owner, out HashSet<string>? set)) map[owner] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    private static string Arity(string name)
    {
        int tick = name.LastIndexOf('`');
        return tick > 0 && int.TryParse(name.AsSpan(tick + 1), out _) ? name[tick..] : string.Empty;
    }

    /// <summary>
    /// The length to base the new name on: the original name without its generic-arity suffix. Compiler names
    /// like &lt;Foo&gt;d__4 are much longer than the readable name they came from, so the bracketed part is
    /// dropped to a rough estimate.
    /// </summary>
    private static int NameLength(string name)
    {
        int tick = name.LastIndexOf('`');
        if (tick > 0 && int.TryParse(name.AsSpan(tick + 1), out _)) name = name[..tick];
        int angle = name.IndexOf('<');
        if (angle >= 0)
        {
            int close = name.IndexOf('>', angle);
            if (close > angle) return Math.Max(4, close - angle - 1); // the method name inside <...>
        }
        return name.Length;
    }

    /// <summary>Length of the readable name a virtual-group key carries (prop:Name, event:Name, or a method name).</summary>
    private static int VirtualKeyLength(string key)
    {
        int colon = key.IndexOf(':');
        return NameLength(colon >= 0 ? key[(colon + 1)..] : key);
    }

    private void AssignMemberNames()
    {
        // Virtual groups first: one name per group, unique across everything a method could meet.
        var globalMethodNames = new HashSet<string>(_analysis.ExternalVirtualNames, StringComparer.Ordinal);
        foreach (LoadedAssembly a in _set.Protected)
            foreach (TypeDefinition t in a.Module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                    globalMethodNames.Add(m.Name);

        var groupNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in _analysis.VirtualKeys.Values.Distinct().OrderBy(k => k, StringComparer.Ordinal))
        {
            bool renamable = _analysis.VirtualKeys.Where(kv => kv.Value == key).All(kv => _catalog.Get(kv.Key)?.CanRename ?? false);
            if (!renamable) continue;
            string name = PinnedGroupName(key) ?? _names.Create("V:" + key, n => globalMethodNames.Contains(n) || AccessorPrefixes.Any(p => globalMethodNames.Contains(p + n)), VirtualKeyLength(key));
            globalMethodNames.Add(name);
            groupNames[key] = name;
        }

        foreach (LoadedAssembly a in _set.Protected)
        {
            foreach (TypeDefinition t in a.Module.GetTypes())
            {
                var methodNames = new HashSet<string>(t.Methods.Select(m => m.Name), StringComparer.Ordinal);
                var fieldNames = new HashSet<string>(t.Fields.Select(f => f.Name), StringComparer.Ordinal);
                var propertyNames = new HashSet<string>(t.Properties.Select(p => p.Name).Concat(t.Events.Select(e => e.Name)), StringComparer.Ordinal);

                foreach (MethodDefinition m in t.Methods.OrderBy(m => _catalog.Get(m)!.Id, StringComparer.Ordinal))
                {
                    Symbol s = _catalog.Get(m)!;
                    if (!s.CanRename || _analysis.AccessorOwners.ContainsKey(m)) continue;
                    if (_analysis.VirtualKeys.TryGetValue(m, out string? key))
                    {
                        if (groupNames.TryGetValue(key, out string? g)) _newNames[m] = g;
                        continue;
                    }
                    string name = Pinned(s) ?? _names.Create(s.Id, n => methodNames.Contains(n) || globalMethodNames.Contains(n), NameLength(s.OriginalName));
                    methodNames.Add(name);
                    _newNames[m] = name;
                }
                foreach (FieldDefinition f in t.Fields.OrderBy(f => _catalog.Get(f)!.Id, StringComparer.Ordinal))
                {
                    Symbol s = _catalog.Get(f)!;
                    if (!s.CanRename) continue;
                    string name = Pinned(s) ?? _names.Create(s.Id, fieldNames.Contains, NameLength(s.OriginalName));
                    fieldNames.Add(name);
                    _newNames[f] = name;
                }
                var owners = t.Properties.Cast<IMemberDefinition>().Concat(t.Events).OrderBy(o => _catalog.Get(o)!.Id, StringComparer.Ordinal);
                foreach (IMemberDefinition owner in owners)
                {
                    Symbol s = _catalog.Get(owner)!;
                    if (!s.CanRename) continue;
                    List<MethodDefinition> accessors = owner is PropertyDefinition p ? Analyzer.Accessors(p).ToList() : Analyzer.Accessors((EventDefinition)owner).ToList();
                    string? name = null;
                    foreach (MethodDefinition acc in accessors)
                        if (_analysis.VirtualKeys.TryGetValue(acc, out string? key) && groupNames.TryGetValue(key, out string? g)) name = g;
                    if (name == null && accessors.Any(acc => _analysis.VirtualKeys.ContainsKey(acc))) continue; // the group kept its name
                    name ??= Pinned(s) ?? _names.Create(s.Id, n => propertyNames.Contains(n) || AccessorPrefixes.Any(pre => methodNames.Contains(pre + n)), NameLength(s.OriginalName));
                    propertyNames.Add(name);
                    _newNames[owner] = name;
                    foreach (MethodDefinition acc in accessors)
                    {
                        string? prefix = AccessorPrefixes.FirstOrDefault(pre => acc.Name == pre + owner.Name);
                        if (prefix == null || !(_catalog.Get(acc)?.CanRename ?? false)) continue;
                        _newNames[acc] = prefix + name;
                        methodNames.Add(prefix + name);
                    }
                }
            }
        }
    }

    // ── Applying ───────────────────────────────────────────────────────────

    private void Apply()
    {
        foreach (var (def, name) in _newNames)
        {
            switch (def)
            {
                case TypeDefinition t:
                    t.Name = name;
                    if (_newNamespaces.TryGetValue(t, out string? ns)) t.Namespace = ns;
                    break;
                case MethodDefinition m:
                    m.Name = name;
                    break;
                case FieldDefinition f:
                    f.Name = name;
                    break;
                case PropertyDefinition p:
                    p.Name = name;
                    break;
                case EventDefinition e:
                    e.Name = name;
                    break;
            }
        }
        foreach (var (r, d) in _typeRefs)
        {
            r.Name = d.Name;
            if (d.DeclaringType == null) r.Namespace = d.Namespace;
        }
        foreach (var (r, d) in _memberRefs) r.Name = d.Name;
        foreach (var (ca, isField, index, d) in _namedArgs)
        {
            if (isField) ca.Fields[index] = new CustomAttributeNamedArgument(d.Name, ca.Fields[index].Argument);
            else ca.Properties[index] = new CustomAttributeNamedArgument(d.Name, ca.Properties[index].Argument);
        }
        foreach (Symbol s in _catalog.All)
            if (_newNames.TryGetValue(s.Definition, out string? n))
            {
                s.NewName = n;
                if (n != s.OriginalName) FinalNames[s.Id] = n;
            }
    }

    private void RecordEntries(Dictionary<TypeDefinition, string> originalTypeNames)
    {
        foreach (Symbol s in _catalog.All.Where(s => s.IsRenamed || (s.Definition is TypeDefinition && _newNamespaces.ContainsKey((TypeDefinition)s.Definition))).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (s.Definition is TypeDefinition t)
            {
                Entries.Add(new RenameEntry(s.Owner.Name, s.Kind, s.Id, s.OriginalName, t.Name, null, null, originalTypeNames[t], t.FullName));
            }
            else
            {
                TypeDefinition dt = s.Definition.DeclaringType;
                Entries.Add(new RenameEntry(s.Owner.Name, s.Kind, s.Id, s.OriginalName, s.Definition.Name, originalTypeNames[dt], dt.FullName, null, null));
            }
        }
    }

    private void RenameParameters()
    {
        foreach (Symbol s in _catalog.All)
        {
            bool keep = s.Reasons.Any(r => r == "public-api" || r.StartsWith("referenced-by:", StringComparison.Ordinal) || r == "com-import");
            if (keep) continue;
            if (s.Definition is MethodDefinition m)
            {
                if (_config.Renaming.RenameParameters)
                {
                    for (int i = 0; i < m.Parameters.Count; i++)
                    {
                        m.Parameters[i].Name = _names.Positional(i);
                        ParametersRenamed++;
                    }
                }
                if (_config.Renaming.RenameGenericParameters)
                    for (int i = 0; i < m.GenericParameters.Count; i++) m.GenericParameters[i].Name = _names.Positional(i);
            }
            else if (s.Definition is TypeDefinition t && _config.Renaming.RenameGenericParameters)
            {
                for (int i = 0; i < t.GenericParameters.Count; i++) t.GenericParameters[i].Name = _names.Positional(i).ToUpperInvariant();
            }
        }
    }

    private void StripAttributes()
    {
        foreach (var (owner, attribute) in _analysis.AttributesToStrip) owner.CustomAttributes.Remove(attribute);
    }
}
