// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text.Json;
using System.Text.Json.Serialization;
using MNVTEngine.Obfuscation.Analysis;
using MNVTEngine.Obfuscation.Transforms;

namespace MNVTEngine.Obfuscation.Reporting;

/// <summary>Per-assembly counts for the report.</summary>
public sealed record AssemblyReport(
    string Name,
    long InputBytes,
    long OutputBytes,
    int Types,
    int Methods,
    int Fields,
    int RenamedTypes,
    int RenamedMembers,
    int PreservedTypes,
    int PreservedMembers,
    int StringsEncrypted,
    int ConstantsHidden,
    int MethodsFlowObfuscated);

/// <summary>What one run did, written next to the mapping. Holds no new names, so it is safe to keep in a build log.</summary>
public sealed class Report
{
    public string Tool { get; set; } = "MNVTEngine Obfuscator";
    public string Version { get; set; } = typeof(Report).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Deterministic { get; set; }
    public string? Preset { get; set; }
    public List<string> Features { get; set; } = new();
    public List<AssemblyReport> Assemblies { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
    public Severity Worst { get; set; }

    public int Warnings { get; set; }
    public int Errors { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();

    /// <summary>Reasons symbols were kept, with a count each (original-name-free), to explain coverage.</summary>
    public Dictionary<string, int> PreservationReasons { get; set; } = new();

    public List<PreservedSymbol> PreservedSymbols { get; set; } = new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record PreservedSymbol(string Assembly, string Kind, string Name, string DeclaringType, IReadOnlyList<string> Reasons);

/// <summary>Builds the report and the mapping from the catalog and transform results.</summary>
internal static class ReportBuilder
{
    public static Report Build(IEnumerable<AssemblyReport> assemblies, SymbolCatalog catalog, DiagnosticBag diag, bool includePreserved)
    {
        var report = new Report
        {
            Assemblies = assemblies.ToList(),
            Diagnostics = diag.Items.ToList(),
            Warnings = diag.Items.Count(d => d.Severity == Severity.Warning),
            Errors = diag.ErrorCount,
            Worst = diag.Items.Count == 0 ? Severity.Info : diag.Items.Max(d => d.Severity),
        };
        foreach (Symbol s in catalog.All)
        {
            if (s.IsRenamed || s.Reasons.Count == 0) continue;
            foreach (string reason in s.Reasons)
            {
                string key = reason.Contains(':') ? reason[..reason.IndexOf(':')] : reason;
                report.PreservationReasons[key] = report.PreservationReasons.GetValueOrDefault(key) + 1;
            }
            if (includePreserved)
                report.PreservedSymbols.Add(new PreservedSymbol(s.Owner.Name, s.Kind.ToString(), s.OriginalName,
                    s.Definition.DeclaringType?.FullName ?? "(type)", s.Reasons.ToList()));
        }
        return report;
    }
}
