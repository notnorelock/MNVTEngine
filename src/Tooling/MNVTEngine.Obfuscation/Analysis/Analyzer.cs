// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using Mono.Cecil.Cil;
using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Reporting;
using MNVTEngine.Obfuscation.Rules;

namespace MNVTEngine.Obfuscation.Analysis;

/// <summary>What analysis decided, for the transforms and the report.</summary>
public sealed class AnalysisOutcome
{
    public required SymbolCatalog Catalog { get; init; }

    /// <summary>Accessor method to its property or event.</summary>
    public required IReadOnlyDictionary<MethodDefinition, IMemberDefinition> AccessorOwners { get; init; }

    /// <summary>Virtual-group key per virtual method that shares its name across overrides.</summary>
    public required IReadOnlyDictionary<MethodDefinition, string> VirtualKeys { get; init; }

    /// <summary>[Obfuscation] attributes to remove (StripAfterObfuscation).</summary>
    public required IReadOnlyList<(ICustomAttributeProvider Owner, CustomAttribute Attribute)> AttributesToStrip { get; init; }

    /// <summary>Types kept because reflection or link.xml reaches them, for a generated link.xml.</summary>
    public required IReadOnlyCollection<(string Assembly, string Type)> ReflectionTypes { get; init; }

    public required IReadOnlySet<string> ExternalVirtualNames { get; init; }

    public int ReflectionMethods { get; init; }
    public int SerializerMethods { get; init; }
}

/// <summary>
/// Decides, for every type and member of the protected assemblies, whether its name can change and which
/// body transforms may touch it. Everything starts renamable; each rule below can only keep more. When the
/// analysis cannot know (a base type that does not resolve, an attribute blob that does not decode), it keeps.
/// </summary>
internal sealed class Analyzer
{
    private readonly AssemblySet _set;
    private readonly ObfuscatorConfig _config;
    private readonly IReadOnlyList<CompiledRule> _rules;
    private readonly DiagnosticBag _diag;
    private readonly SymbolCatalog _catalog = new();
    private readonly Dictionary<MethodDefinition, IMemberDefinition> _accessorOwner = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TypeDefinition> _unresolvedBase = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(string, string)> _reflectionTypes = new();
    private readonly List<(ICustomAttributeProvider, CustomAttribute)> _strip = new();
    private readonly HashSet<string> _externalVirtualNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedUnresolved = new(StringComparer.OrdinalIgnoreCase);
    private int _reflectionMethods, _serializerMethods;

    public Analyzer(AssemblySet set, ObfuscatorConfig config, IReadOnlyList<CompiledRule> rules, DiagnosticBag diagnostics)
    {
        _set = set;
        _config = config;
        _rules = rules;
        _diag = diagnostics;
    }

    public AnalysisOutcome Run()
    {
        BuildCatalog();
        KeepRuntimeContracts();
        KeepSerialization();
        if (_config.Preservation.PreserveSerializedFields) KeepMnvtSerializedMembers();
        ApplyAttributes();
        ApplyRules();
        if (_config.Preservation.PreserveReflectionTargets) ScanCodeAndAttributes();
        KeepReferencedFromOutside();
        KeepPinnedReferences();
        Dictionary<MethodDefinition, string> virtualKeys = ResolveVirtualGroups();
        ReportUnresolved();
        return new AnalysisOutcome
        {
            Catalog = _catalog,
            AccessorOwners = _accessorOwner,
            VirtualKeys = virtualKeys,
            AttributesToStrip = _strip,
            ReflectionTypes = _reflectionTypes,
            ExternalVirtualNames = _externalVirtualNames,
            ReflectionMethods = _reflectionMethods,
            SerializerMethods = _serializerMethods,
        };
    }

    // ── Catalog ────────────────────────────────────────────────────────────

    private void BuildCatalog()
    {
        foreach (LoadedAssembly a in _set.Protected)
        {
            foreach (TypeDefinition t in a.Module.GetTypes())
            {
                Add(a, t, SymbolKind.Type, t.IsNested ? null : t.Namespace);
                foreach (MethodDefinition m in t.Methods) Add(a, m, SymbolKind.Method);
                foreach (FieldDefinition f in t.Fields) Add(a, f, SymbolKind.Field);
                foreach (PropertyDefinition p in t.Properties)
                {
                    Add(a, p, SymbolKind.Property);
                    foreach (MethodDefinition acc in Accessors(p)) _accessorOwner[acc] = p;
                }
                foreach (EventDefinition e in t.Events)
                {
                    Add(a, e, SymbolKind.Event);
                    foreach (MethodDefinition acc in Accessors(e)) _accessorOwner[acc] = e;
                }
            }
        }
    }

    private void Add(LoadedAssembly a, IMemberDefinition d, SymbolKind kind, string? ns = null) =>
        _catalog.Add(new Symbol { Kind = kind, Definition = d, Owner = a, Id = Symbol.MakeId(a.Name, d), OriginalName = d.Name, OriginalNamespace = ns });

    internal static IEnumerable<MethodDefinition> Accessors(PropertyDefinition p)
    {
        if (p.GetMethod != null) yield return p.GetMethod;
        if (p.SetMethod != null) yield return p.SetMethod;
        if (p.HasOtherMethods) foreach (MethodDefinition m in p.OtherMethods) yield return m;
    }

    internal static IEnumerable<MethodDefinition> Accessors(EventDefinition e)
    {
        if (e.AddMethod != null) yield return e.AddMethod;
        if (e.RemoveMethod != null) yield return e.RemoveMethod;
        if (e.InvokeMethod != null) yield return e.InvokeMethod;
        if (e.HasOtherMethods) foreach (MethodDefinition m in e.OtherMethods) yield return m;
    }

    private void Keep(IMemberDefinition? d, string reason) => _catalog.Get(d)?.Preserve(reason);

    private void KeepType(TypeDefinition t, string reason, bool members)
    {
        Keep(t, reason);
        if (!members) return;
        foreach (IMemberDefinition m in Members(t)) Keep(m, reason);
    }

    private static IEnumerable<IMemberDefinition> Members(TypeDefinition t) =>
        t.Methods.Cast<IMemberDefinition>().Concat(t.Fields).Concat(t.Properties).Concat(t.Events);

    // ── Rules every runtime imposes ────────────────────────────────────────

    private void KeepRuntimeContracts()
    {
        bool publicApi = _config.Renaming.PreservePublicApi;
        foreach (Symbol s in _catalog.All)
        {
            switch (s.Definition)
            {
                case TypeDefinition t:
                    if (t.Name == "<Module>" && t.DeclaringType == null) s.Preserve("module-type");
                    if (t.IsImport || HasAttribute(t, "System.Runtime.InteropServices.ComImportAttribute")) KeepType(t, "com-import", members: true);
                    if (IsDelegate(t)) foreach (MethodDefinition m in t.Methods) Keep(m, "delegate-runtime-method");
                    if (t.IsEnum && !_config.Renaming.RenameEnumValues) foreach (FieldDefinition f in t.Fields) Keep(f, "enum-value");
                    if (publicApi && IsExternallyVisible(t))
                    {
                        s.Preserve("public-api");
                        foreach (IMemberDefinition m in Members(t))
                            if (IsVisibleMember(m)) Keep(m, "public-api");
                    }
                    if (t.IsWindowsRuntime) KeepType(t, "windows-runtime", members: true);
                    break;
                case MethodDefinition m:
                    if (m.IsRuntimeSpecialName) s.Preserve("runtime-special-name");
                    if (m.IsPInvokeImpl || m.IsInternalCall || m.IsRuntime) s.Preserve("native-binding");
                    break;
                case FieldDefinition f:
                    if (f.IsRuntimeSpecialName) s.Preserve("runtime-special-name");
                    break;
            }
        }
    }

    private static bool IsDelegate(TypeDefinition t)
    {
        string? b = t.BaseType?.FullName;
        return b == "System.MulticastDelegate" || b == "System.Delegate";
    }

    internal static bool IsExternallyVisible(TypeDefinition? t)
    {
        while (t != null)
        {
            if (!t.IsNested) return t.IsPublic;
            if (!(t.IsNestedPublic || t.IsNestedFamily || t.IsNestedFamilyOrAssembly)) return false;
            t = t.DeclaringType;
        }
        return false;
    }

    internal static bool IsVisibleMember(IMemberDefinition? m) => m switch
    {
        MethodDefinition md => md.IsPublic || md.IsFamily || md.IsFamilyOrAssembly,
        FieldDefinition f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyDefinition p => IsVisibleMember(p.GetMethod) || IsVisibleMember(p.SetMethod),
        EventDefinition e => IsVisibleMember(e.AddMethod) || IsVisibleMember(e.RemoveMethod),
        TypeDefinition t => IsExternallyVisible(t),
        _ => false,
    };

    // ── Serialization ──────────────────────────────────────────────────────

    private void KeepSerialization()
    {
        if (!_config.Preservation.PreserveSerializedFields) return;
        foreach (Symbol s in _catalog.All)
        {
            if (s.Definition is not TypeDefinition t || !t.IsSerializable || t.IsEnum) continue;
            // BinaryFormatter and other [Serializable] serializers read these fields by name.
            foreach (FieldDefinition f in t.Fields)
                if (!f.IsStatic && !f.IsLiteral && !f.IsNotSerialized) Keep(f, "serializable-field");
        }
    }

    /// <summary>
    /// Keeps the names of members MNVTEngine serializes into scenes and prefabs by name (<c>ComponentTypeInfo</c>):
    /// public instance fields unless <c>[DontSerialize]</c>, and non-public fields or read-write properties marked
    /// <c>[SerializeField]</c>. The component type itself is still renamed; export remaps the type id in scene data.
    /// </summary>
    // A pinned assembly (an already-obfuscated game, referenced while obfuscating its DLC) keeps every name except the
    // ones the earlier run renamed: those are forced to their recorded new names by the renamer, so references resolve.
    private void KeepPinnedReferences()
    {
        var pinnedAssemblies = _config.Renaming.PinnedAssemblies;
        if (pinnedAssemblies.Count == 0)
        {
            return;
        }

        foreach (Symbol s in _catalog.All)
        {
            if (pinnedAssemblies.Contains(s.Owner.Name) && !_config.Renaming.PinnedNames.ContainsKey(s.Id))
            {
                s.Preserve("pinned-reference");
            }
        }
    }

    private void KeepMnvtSerializedMembers()
    {
        // Types whose serialized members the export will remap in scene data (components and the data types nested in
        // them): their members may be renamed. Every other type keeps its names, for other serializers.
        var renameTypes = _config.Preservation.RenameMemberTypes;
        foreach (Symbol s in _catalog.All)
        {
            if (renameTypes.Count > 0 && s.Definition is IMemberDefinition { DeclaringType: { } declaring } && renameTypes.Contains(declaring.FullName))
            {
                continue;
            }

            switch (s.Definition)
            {
                case FieldDefinition f when !f.IsStatic && !f.IsLiteral &&
                    ((f.IsPublic && !HasAttribute(f, EngineKnowledge.DontSerialize)) || HasAttribute(f, EngineKnowledge.SerializeField)):
                    s.Preserve("mnvt-serialized-field");
                    break;
                case PropertyDefinition p when HasAttribute(p, EngineKnowledge.SerializeField) && p.GetMethod != null && p.SetMethod != null:
                    s.Preserve("mnvt-serialized-field");
                    break;
            }
        }
    }

    internal static bool HasAttribute(ICustomAttributeProvider p, string fullName) =>
        p.HasCustomAttributes && p.CustomAttributes.Any(a => a.AttributeType.FullName == fullName);

    // ── Attributes and rules ───────────────────────────────────────────────

    private void ApplyAttributes()
    {
        var skipRename = AttributeNames(_config.Preservation.SkipRenameAttributes);
        var skipAll = AttributeNames(_config.Preservation.SkipAttributes);
        foreach (LoadedAssembly a in _set.Protected)
        {
            foreach (CustomAttribute ca in a.Definition.CustomAttributes.ToList())
            {
                if (ca.AttributeType.FullName != "System.Reflection.ObfuscationAttribute") continue;
                var (exclude, features, _, strip) = ReadObfuscation(ca, a.Name, "assembly");
                if (exclude)
                    foreach (Symbol s in _catalog.Of(a)) s.Exclude(features, "obfuscation-attribute");
                if (strip) _strip.Add((a.Definition, ca));
            }
        }
        foreach (Symbol s in _catalog.All.ToList())
        {
            IMemberDefinition d = s.Definition;
            if (!((ICustomAttributeProvider)d).HasCustomAttributes) continue;
            foreach (CustomAttribute ca in ((ICustomAttributeProvider)d).CustomAttributes)
            {
                string name = ca.AttributeType.FullName;
                if (name == "System.Reflection.ObfuscationAttribute")
                {
                    var (exclude, features, toMembers, strip) = ReadObfuscation(ca, s.Owner.Name, d.FullName);
                    if (exclude) Apply(s, features, "obfuscation-attribute", toMembers);
                    if (strip) _strip.Add(((ICustomAttributeProvider)d, ca));
                }
                else if (skipAll.Contains(name)) Apply(s, Protection.All, "skip-attribute", true);
                else if (skipRename.Contains(name)) Apply(s, Protection.Rename, "skip-rename-attribute", true);
                else if (name == EngineKnowledge.Preserve || ca.AttributeType.Name == "PreserveAttribute") s.Preserve("preserve-attribute");
                else if (EngineKnowledge.EntryPointAttributes.Contains(name))
                {
                    s.Preserve("unity-entry-point");
                    if (d.DeclaringType != null) Keep(d.DeclaringType, "unity-entry-point");
                }
            }
        }
    }

    private static HashSet<string> AttributeNames(IEnumerable<string> names)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string n in names)
        {
            set.Add(n);
            set.Add(n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^"Attribute".Length] : n + "Attribute");
        }
        return set;
    }

    private (bool Exclude, Protection Features, bool ApplyToMembers, bool Strip) ReadObfuscation(CustomAttribute ca, string assembly, string where)
    {
        bool exclude = true, toMembers = true, strip = true;
        Protection features = Protection.All;
        try
        {
            foreach (CustomAttributeNamedArgument p in ca.Properties)
            {
                switch (p.Name)
                {
                    case "Exclude" when p.Argument.Value is bool b:
                        exclude = b;
                        break;
                    case "ApplyToMembers" when p.Argument.Value is bool b:
                        toMembers = b;
                        break;
                    case "StripAfterObfuscation" when p.Argument.Value is bool b:
                        strip = b;
                        break;
                    case "Feature" when p.Argument.Value is string f:
                        if (!ProtectionNames.TryParse(f, out features))
                        {
                            _diag.Warning(DiagnosticCodes.Config, $"[Obfuscation(Feature = \"{f}\")] is not a known feature; treating it as \"all\".", assembly, where);
                            features = Protection.All;
                        }
                        break;
                }
            }
        }
        catch (Exception e) when (e is AssemblyResolutionException or InvalidOperationException or NotSupportedException)
        {
            _diag.Warning(DiagnosticCodes.AttributeBlob, "Cannot read an [Obfuscation] attribute; excluding the symbol from everything.", assembly, where);
        }
        return (exclude, features, toMembers, strip);
    }

    /// <summary>Excludes a symbol and, for a type with <paramref name="toMembers"/>, its members and nested types.</summary>
    private void Apply(Symbol s, Protection features, string reason, bool toMembers)
    {
        s.Exclude(features, reason);
        if (!toMembers || s.Definition is not TypeDefinition t) return;
        foreach (IMemberDefinition m in Members(t)) _catalog.Get(m)?.Exclude(features, reason);
        foreach (TypeDefinition n in t.NestedTypes)
            if (_catalog.Get(n) is { } ns) Apply(ns, features, reason, true);
    }

    private void ApplyRules()
    {
        if (_rules.Count == 0) return;
        foreach (Symbol s in _catalog.All)
        {
            string asm = s.Owner.Name;
            foreach (CompiledRule r in _rules)
            {
                if (s.Definition is TypeDefinition t)
                {
                    if (r.MatchesType(asm, t)) Apply(s, r.Features, r.Reason, r.ApplyToMembers);
                }
                else if (r.MatchesMember(asm, s.Definition, KindOf(s.Kind))) s.Exclude(r.Features, r.Reason);
            }
        }
    }

    private static SymbolKinds KindOf(SymbolKind k) => k switch
    {
        SymbolKind.Type => SymbolKinds.Type,
        SymbolKind.Method => SymbolKinds.Method,
        SymbolKind.Field => SymbolKinds.Field,
        SymbolKind.Property => SymbolKinds.Property,
        _ => SymbolKinds.Event,
    };

    // ── Reflection, serializers and attribute strings ──────────────────────

    private void ScanCodeAndAttributes()
    {
        var reflectionNames = new HashSet<string>(StringComparer.Ordinal);
        var attributeStrings = new HashSet<string>(StringComparer.Ordinal);
        var serializerTypes = new List<TypeReference>();
        foreach (LoadedAssembly a in _set.All)
        {
            int lookups = 0, serializers = 0;
            foreach (TypeDefinition t in SafeTypes(a))
            {
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    MethodBody body;
                    try
                    {
                        body = m.Body;
                    }
                    catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or NullReferenceException)
                    {
                        if (a.IsProtected) _diag.Error(DiagnosticCodes.NotManaged, $"A method body does not decode ({e.Message}).", a.Name, m.FullName);
                        continue;
                    }
                    bool lookup = false, serializer = false, takesTypeArgument = false;
                    List<string>? strings = null;
                    List<TypeReference>? tokens = null;
                    foreach (Instruction ins in body.Instructions)
                    {
                        switch (ins.Operand)
                        {
                            case string s:
                                (strings ??= new()).Add(s);
                                break;
                            case TypeReference tr when ins.OpCode.Code == Code.Ldtoken:
                                (tokens ??= new()).Add(tr);
                                break;
                            case MethodReference mr when ins.OpCode.FlowControl == FlowControl.Call:
                                TypeReference declaring = mr.DeclaringType is GenericInstanceType git ? git.ElementType : mr.DeclaringType;
                                if (EngineKnowledge.StringLookups.TryGetValue(declaring.FullName, out HashSet<string>? names) && names.Contains(mr.Name)
                                    && mr.Parameters.Any(p => p.ParameterType.MetadataType == MetadataType.String))
                                    lookup = true;
                                if (EngineKnowledge.IsSerializerType(declaring))
                                {
                                    serializer = true;
                                    // The type being (de)serialized: a generic argument on the method or its
                                    // declaring type (DeserializeObject<T>, JsonSerializer<T>), or a Type the
                                    // call itself takes (DeserializeObject(json, typeof(T))). Keep its members.
                                    if (mr is GenericInstanceMethod gim) serializerTypes.AddRange(gim.GenericArguments);
                                    if (mr.DeclaringType is GenericInstanceType dgit) serializerTypes.AddRange(dgit.GenericArguments);
                                    takesTypeArgument |= mr.Parameters.Any(p => p.ParameterType.FullName == "System.Type");
                                }
                                break;
                        }
                    }
                    if (lookup)
                    {
                        lookups++;
                        if (strings != null) reflectionNames.UnionWith(strings);
                    }
                    if (serializer)
                    {
                        serializers++;
                        // Only sweep ldtoken types in when the serializer takes a Type; otherwise an unrelated
                        // typeof() in the same method (reflection, nameof) would be mistaken for its subject.
                        if (tokens != null && takesTypeArgument) serializerTypes.AddRange(tokens);
                    }
                }
            }
            if (a.IsProtected) CollectAttributeStrings(a, attributeStrings, serializerTypes);
            if (lookups > 0)
                _diag.Info(DiagnosticCodes.ReflectionUse, $"{lookups} method(s) look members or types up by string; the names in their string literals are kept.", a.Name);
            if (serializers > 0 && a.IsProtected)
                _diag.Info(DiagnosticCodes.SerializerUse, $"{serializers} method(s) call a serializer; the member names of the types they pass are kept.", a.Name);
            _reflectionMethods += lookups;
            _serializerMethods += serializers;
        }

        var visited = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
        foreach (TypeReference t in serializerTypes) KeepSerializerType(t, visited);
        KeepNames(reflectionNames, "reflection-name");
        KeepNames(attributeStrings, "attribute-string");
    }

    private IEnumerable<TypeDefinition> SafeTypes(LoadedAssembly a)
    {
        try
        {
            return a.Module.GetTypes().ToList();
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            _diag.Warning(DiagnosticCodes.NotManaged, $"Cannot list the types ({e.Message}); its code is not scanned for reflection.", a.Name);
            return Array.Empty<TypeDefinition>();
        }
    }

    private void CollectAttributeStrings(LoadedAssembly a, HashSet<string> strings, List<TypeReference> serializerTypes)
    {
        foreach (ICustomAttributeProvider p in AttributeWalker.Providers(a.Module))
        {
            if (!p.HasCustomAttributes) continue;
            foreach (CustomAttribute ca in p.CustomAttributes)
            {
                string ns = ca.AttributeType.Namespace;
                if (ns.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) || ns.StartsWith("System.Diagnostics", StringComparison.Ordinal)) continue;
                if (EngineKnowledge.IsSerializerType(ca.AttributeType))
                {
                    // [JsonObject], [DataContract], [JsonProperty] and friends: the type's member names are its keys.
                    TypeDefinition? owner = p switch
                    {
                        TypeDefinition td => td,
                        IMemberDefinition md => md.DeclaringType,
                        _ => null,
                    };
                    if (owner != null) serializerTypes.Add(owner);
                    if (p is IMemberDefinition member) Keep(member, "serializer-member");
                }
                try
                {
                    foreach (CustomAttributeArgument arg in ca.ConstructorArguments) CollectStrings(arg, strings);
                    foreach (CustomAttributeNamedArgument n in ca.Fields.Concat(ca.Properties)) CollectStrings(n.Argument, strings);
                }
                catch (Exception e) when (e is AssemblyResolutionException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    // Reported when the renamer re-reads the blob.
                }
            }
        }
    }

    private static void CollectStrings(CustomAttributeArgument arg, HashSet<string> strings)
    {
        switch (arg.Value)
        {
            case string s:
                strings.Add(s);
                break;
            case CustomAttributeArgument inner:
                CollectStrings(inner, strings);
                break;
            case CustomAttributeArgument[] many:
                foreach (CustomAttributeArgument x in many) CollectStrings(x, strings);
                break;
        }
    }

    private void KeepSerializerType(TypeReference t, HashSet<TypeDefinition> visited)
    {
        switch (t)
        {
            case GenericInstanceType git:
                KeepSerializerType(git.ElementType, visited);
                foreach (TypeReference arg in git.GenericArguments) KeepSerializerType(arg, visited);
                return;
            case TypeSpecification spec:
                KeepSerializerType(spec.ElementType, visited);
                return;
            case GenericParameter:
                return;
        }
        TypeDefinition? d = Resolve.Type(t);
        if (d == null || !_catalog.Contains(d) || !visited.Add(d)) return;
        Keep(d, "serializer-type");
        foreach (FieldDefinition f in d.Fields)
        {
            if (f.IsStatic) continue;
            Keep(f, "serializer-member");
            KeepSerializerType(f.FieldType, visited);
        }
        foreach (PropertyDefinition p in d.Properties)
        {
            if (p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic ?? false) continue;
            Keep(p, "serializer-member");
            KeepSerializerType(p.PropertyType, visited);
        }
        if (d.BaseType != null) KeepSerializerType(d.BaseType, visited);
        if (d.DeclaringType != null) Keep(d.DeclaringType, "serializer-type");
    }

    /// <summary>
    /// Keeps types and members whose name equals one of <paramref name="names"/>. A dotted string also keeps
    /// the type with that full name; an assembly-qualified one ("Ns.Type, Assembly") is cut at the comma.
    /// </summary>
    private void KeepNames(HashSet<string> names, string reason)
    {
        if (names.Count == 0) return;
        var bySimple = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
        var typesByFull = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (Symbol s in _catalog.All)
        {
            Index(bySimple, s.OriginalName, s);
            if (s.Definition is TypeDefinition t)
            {
                typesByFull[t.FullName] = s;
                int tick = s.OriginalName.IndexOf('`');
                if (tick > 0) Index(bySimple, s.OriginalName[..tick], s);
            }
        }
        foreach (string raw in names)
        {
            if (raw.Length == 0 || raw.Length > 1024) continue;
            string s = raw.Trim();
            int comma = s.IndexOf(',');
            if (comma > 0) s = s[..comma].Trim();
            string normalized = s.Replace('+', '/');
            if (typesByFull.TryGetValue(normalized, out Symbol? type))
            {
                type.Preserve(reason);
                _reflectionTypes.Add((type.Owner.Name, ((TypeDefinition)type.Definition).FullName));
            }
            int cut = normalized.LastIndexOfAny(new[] { '.', '/' });
            string simple = cut >= 0 && cut < normalized.Length - 1 ? normalized[(cut + 1)..] : normalized;
            if (bySimple.TryGetValue(simple, out List<Symbol>? hits))
            {
                foreach (Symbol h in hits)
                {
                    h.Preserve(reason);
                    if (h.Definition is TypeDefinition ht) _reflectionTypes.Add((h.Owner.Name, ht.FullName));
                }
            }
        }
    }

    private static void Index(Dictionary<string, List<Symbol>> index, string key, Symbol s)
    {
        if (!index.TryGetValue(key, out List<Symbol>? list)) index[key] = list = new List<Symbol>();
        list.Add(s);
    }

    // ── Other assemblies that use the protected ones ───────────────────────

    /// <summary>
    /// An assembly that is not rewritten still refers to protected symbols by name, and its types may derive
    /// from or implement protected types (binding overrides by name). All of that keeps its name.
    /// </summary>
    private void KeepReferencedFromOutside()
    {
        var protectedNames = new HashSet<string>(_set.Protected.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        foreach (LoadedAssembly r in _set.References)
        {
            string reason = "referenced-by:" + r.Name;
            if (!r.Module.AssemblyReferences.Any(n => protectedNames.Contains(n.Name))) continue;
            try
            {
                foreach (TypeReference tr in r.Module.GetTypeReferences())
                {
                    if (Resolve.ScopeAssembly(tr) is { } scope && protectedNames.Contains(scope))
                    {
                        TypeDefinition? d = Resolve.Type(tr);
                        for (TypeDefinition? x = d; x != null; x = x.DeclaringType) Keep(x, reason);
                    }
                }
                foreach (MemberReference mr in r.Module.GetMemberReferences())
                {
                    if (mr.DeclaringType == null || Resolve.ScopeAssembly(mr.DeclaringType) is not { } scope || !protectedNames.Contains(scope)) continue;
                    IMemberDefinition? d = Resolve.Member(mr);
                    Keep(d, reason);
                    if (d is MethodDefinition md && _accessorOwner.TryGetValue(md, out IMemberDefinition? owner)) Keep(owner, reason);
                }
                foreach (TypeDefinition t in r.Module.GetTypes())
                {
                    IEnumerable<TypeReference> bases = t.Interfaces.Select(i => i.InterfaceType);
                    if (t.BaseType != null) bases = bases.Append(t.BaseType);
                    foreach (TypeReference b in bases)
                    {
                        if (Resolve.ScopeAssembly(b) is not { } scope || !protectedNames.Contains(scope)) continue;
                        TypeDefinition? bd = Resolve.Type(b);
                        if (bd != null) KeepOverridable(bd, "implemented-by:" + r.Name, new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance));
                    }
                }
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                _diag.Warning(DiagnosticCodes.NotManaged, $"Cannot read its references to the protected assemblies ({e.Message}); keeping every protected public name it could use.", r.Name);
                foreach (Symbol s in _catalog.All.Where(s => IsVisibleMember(s.Definition))) s.Preserve(reason);
            }
        }
    }

    private void KeepOverridable(TypeDefinition t, string reason, HashSet<TypeDefinition> seen)
    {
        if (!_catalog.Contains(t) || !seen.Add(t)) return;
        foreach (MethodDefinition m in t.Methods)
        {
            if (!m.IsVirtual) continue;
            Keep(m, reason);
            if (_accessorOwner.TryGetValue(m, out IMemberDefinition? owner)) Keep(owner, reason);
        }
        if (Resolve.Type(t.BaseType) is { } b) KeepOverridable(b, reason, seen);
        foreach (InterfaceImplementation i in t.Interfaces)
            if (Resolve.Type(i.InterfaceType) is { } id) KeepOverridable(id, reason, seen);
    }

    // ── Virtual methods ────────────────────────────────────────────────────

    /// <summary>
    /// Overrides and implicit interface implementations bind by name, so every virtual method with the same
    /// name (accessors: the same property or event name) gets the same new name, across all protected
    /// assemblies. A group keeps its name when any member must: kept for another reason, or the name exists
    /// on a base type or interface outside the protected set (an override of ToString, IDisposable.Dispose,
    /// MonoBehaviour messages declared virtual...). Explicit implementations (bound by .override) rename alone.
    /// </summary>
    private Dictionary<MethodDefinition, string> ResolveVirtualGroups()
    {
        CollectExternalVirtualNames();
        var keys = new Dictionary<MethodDefinition, string>(ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
        foreach (Symbol s in _catalog.All)
        {
            if (s.Definition is not MethodDefinition m || !m.IsVirtual) continue;
            if (m.HasOverrides && m.IsPrivate) continue; // explicit implementation
            string key = _accessorOwner.TryGetValue(m, out IMemberDefinition? owner)
                ? (owner is PropertyDefinition ? "prop:" : "event:") + owner.Name
                : m.Name;
            keys[m] = key;
            Index(groups, key, s);
            if (_externalVirtualNames.Contains(m.Name)) s.Preserve("overrides-external");
            if (HasUnresolvedAncestry(m.DeclaringType)) s.Preserve("unresolved-base");
        }

        // Accessors and their property must agree, and a kept member keeps its whole group: repeat to a fixpoint.
        bool changed = true;
        for (int pass = 0; changed && pass < 64; pass++)
        {
            changed = SyncAccessors();
            foreach (List<Symbol> group in groups.Values)
            {
                Symbol? kept = group.FirstOrDefault(g => !g.CanRename);
                if (kept == null) continue;
                foreach (Symbol g in group) changed |= g.Preserve("virtual-group");
            }
        }
        return keys;
    }

    private bool SyncAccessors()
    {
        bool changed = false;
        foreach (Symbol s in _catalog.All)
        {
            IEnumerable<MethodDefinition> accessors = s.Definition switch
            {
                PropertyDefinition p => Accessors(p),
                EventDefinition e => Accessors(e),
                _ => Enumerable.Empty<MethodDefinition>(),
            };
            var syms = accessors.Select(_catalog.Get).Where(x => x != null).Select(x => x!).ToList();
            if (syms.Count == 0) continue;
            if (!s.CanRename || syms.Any(x => !x.CanRename))
            {
                changed |= s.Preserve("accessor");
                foreach (Symbol x in syms) changed |= x.Preserve("accessor");
            }
        }
        return changed;
    }

    private void CollectExternalVirtualNames()
    {
        var seen = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
        foreach (Symbol s in _catalog.All)
        {
            if (s.Definition is not TypeDefinition t) continue;
            for (TypeDefinition? c = t; c != null;)
            {
                foreach (InterfaceImplementation i in c.Interfaces) AddInterface(i.InterfaceType, t, seen);
                if (c.BaseType == null) break;
                TypeDefinition? b = Resolve.Type(c.BaseType);
                if (b == null)
                {
                    NoteUnresolved(c.BaseType, t);
                    _unresolvedBase.Add(t);
                    break;
                }
                if (!_catalog.Contains(b) && seen.Add(b))
                    foreach (MethodDefinition m in b.Methods)
                        if (m.IsVirtual) _externalVirtualNames.Add(m.Name);
                c = b;
            }
        }
    }

    private void AddInterface(TypeReference iface, TypeDefinition user, HashSet<TypeDefinition> seen)
    {
        TypeDefinition? d = Resolve.Type(iface);
        if (d == null)
        {
            NoteUnresolved(iface, user);
            _unresolvedBase.Add(user);
            return;
        }
        if (!seen.Add(d)) return;
        if (!_catalog.Contains(d))
            foreach (MethodDefinition m in d.Methods) _externalVirtualNames.Add(m.Name);
        foreach (InterfaceImplementation i in d.Interfaces) AddInterface(i.InterfaceType, user, seen);
    }

    // Every protected type whose base chain or interfaces do not resolve is in the set itself.
    private bool HasUnresolvedAncestry(TypeDefinition t) => _unresolvedBase.Contains(t);

    // ── Unresolved references ──────────────────────────────────────────────

    private void NoteUnresolved(TypeReference missing, TypeDefinition user)
    {
        string? asm = Resolve.ScopeAssembly(missing);
        string key = asm ?? missing.FullName;
        if (!_warnedUnresolved.Add(key)) return;
        _diag.Warning(DiagnosticCodes.UnresolvedType,
            $"Cannot resolve {missing.FullName}" + (asm != null ? $" from {asm}" : string.Empty)
            + ". Types built on it keep their names and virtual methods. Add the folder holding that assembly to referenceDirectories.",
            user.Module.Assembly.Name.Name);
    }

    private void ReportUnresolved()
    {
        foreach (string name in _set.UnresolvedAssemblies.OrderBy(n => n, StringComparer.Ordinal))
            if (!_warnedUnresolved.Contains(name))
                _diag.Info(DiagnosticCodes.UnresolvedAssembly, $"The referenced assembly {name} was not found; nothing protected depended on it for a decision.");
    }
}
