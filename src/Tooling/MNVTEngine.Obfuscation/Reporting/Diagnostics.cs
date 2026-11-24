// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text.Json.Serialization;

namespace MNVTEngine.Obfuscation.Reporting;

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One finding. <see cref="Code"/> is stable (docs/diagnostics.md lists them); <see cref="Message"/> says what
/// happened and what to do. Symbol names here are original names of symbols that were kept, never new names,
/// so build logs do not leak the mapping.
/// </summary>
public sealed record Diagnostic(string Code, Severity Severity, string Message, string? Assembly = null, string? Symbol = null)
{
    public override string ToString()
    {
        string where = Assembly == null ? string.Empty : Symbol == null ? $" [{Assembly}]" : $" [{Assembly}: {Symbol}]";
        return $"{Severity.ToString().ToLowerInvariant()} {Code}{where}: {Message}";
    }
}

public static class DiagnosticCodes
{
    public const string Config = "OBF001";
    public const string InputMissing = "OBF010";
    public const string NoAssemblies = "OBF011";
    public const string NotManaged = "OBF012";
    public const string Unsupported = "OBF013";
    public const string StrongName = "OBF014";
    public const string UnsafePath = "OBF015";
    public const string OutputExists = "OBF016";
    public const string SymbolsFile = "OBF017";
    public const string UnresolvedAssembly = "OBF020";
    public const string UnresolvedType = "OBF021";
    public const string AttributeBlob = "OBF022";
    public const string ReflectionUse = "OBF030";
    public const string SerializerUse = "OBF031";
    public const string WeakSeed = "OBF032";
    public const string ControlFlowSkipped = "OBF040";
    public const string BrokenReference = "OBF050";
    public const string StackImbalance = "OBF051";
    public const string DuplicateMember = "OBF052";
    public const string OutputUnreadable = "OBF053";
    public const string Internal = "OBF090";
}

/// <summary>Collects diagnostics for one run. Not thread-safe; the pipeline is single-threaded.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);
    public int ErrorCount => _items.Count(d => d.Severity == Severity.Error);

    public void Add(Diagnostic d) => _items.Add(d);
    public void Error(string code, string message, string? assembly = null, string? symbol = null) => Add(new Diagnostic(code, Severity.Error, message, assembly, symbol));
    public void Warning(string code, string message, string? assembly = null, string? symbol = null) => Add(new Diagnostic(code, Severity.Warning, message, assembly, symbol));
    public void Info(string code, string message, string? assembly = null, string? symbol = null) => Add(new Diagnostic(code, Severity.Info, message, assembly, symbol));
    public void AddRange(IEnumerable<Diagnostic> items) => _items.AddRange(items);
}
