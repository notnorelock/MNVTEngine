// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text.Json;
using MNVTEngine.Obfuscation.Transforms;

namespace MNVTEngine.Obfuscation.Reporting;

/// <summary>
/// The old-name → new-name map, for reading a crash from a protected build. It reveals every original name,
/// so it is written outside the output folder and must not ship. Keep it with the build that produced it.
/// </summary>
public sealed class Mapping
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = typeof(Mapping).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MappingEntry> Entries { get; set; } = new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Report.JsonOptions));
    }

    public static Mapping From(IEnumerable<RenameEntry> entries) => new()
    {
        Entries = entries.Select(e => new MappingEntry(
            e.Assembly, e.Kind.ToString(),
            e.OriginalFullName ?? (e.OriginalDeclaringType + "::" + e.OriginalName),
            e.NewFullName ?? (e.NewDeclaringType + "::" + e.NewName),
            e.OriginalName, e.NewName)).ToList(),
    };
}

public sealed record MappingEntry(string Assembly, string Kind, string Original, string Obfuscated, string OriginalName, string NewName);
