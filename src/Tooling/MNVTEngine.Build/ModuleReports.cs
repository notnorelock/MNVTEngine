using System.Text;
using MNVTEngine.Modules;

namespace MNVTEngine.Build;

/// <summary>Plain-text module reports for <c>mnvt modules list | validate | graph</c>.</summary>
public static class ModuleReports
{
    /// <summary>Every catalog module; with a resolution, also its status in that project.</summary>
    public static string List(ModuleCatalog catalog, ModuleResolution? project = null, string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var rows = new List<string[]> { project is null
            ? ["Module", "Profiles", "Activation", "Requires", "Optional"]
            : ["Module", "Profiles", "Activation", "Requires", "Optional", $"In {projectName} ({project.Profile.DisplayName()})"] };
        foreach (var module in catalog.Modules)
        {
            string[] row = [module.Id, Profiles(module), Activation(module), Join(module.Requires), Join(module.Optional)];
            rows.Add(project is null ? row : [.. row, Status(project, module)]);
        }

        var text = new StringBuilder($"Engine modules ({catalog.Modules.Count})\n\n");
        Table(text, rows);
        return text.ToString();
    }

    /// <summary>The project's modules in startup order with their dependencies, then the modules it does not have.</summary>
    public static string Graph(ModuleResolution resolution, string projectName)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var text = new StringBuilder($"{projectName} ({resolution.Profile.DisplayName()}): {resolution.Modules.Count} modules, in startup order (shutdown is the reverse)\n\n");
        List<string[]> rows = [["#", "Module", "Why", "Depends on"]];
        for (var i = 0; i < resolution.Modules.Count; i++)
        {
            var id = resolution.Modules[i].Id;
            var edges = resolution.Dependencies.Where(d => d.Module == id).Select(d => d.IsOptional ? d.DependsOn + " (optional)" : d.DependsOn);
            rows.Add([(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), id, resolution.Reasons[id], Join([.. edges])]);
        }

        Table(text, rows);
        text.Append("\nDependency tree (- required, ~ optional)\n");
        foreach (var root in resolution.Modules.Where(m => !resolution.Dependencies.Any(d => d.DependsOn == m.Id)))
        {
            Tree(text, resolution, root.Id, 0, "", []);
        }

        if (resolution.Excluded.Count > 0)
        {
            text.Append("\nNot in this project\n");
            Table(text, [.. resolution.Excluded.Select(e => new[] { e.Module.Id, e.Reason })]);
        }

        AppendProblems(text, resolution.Diagnostics);
        return text.ToString();
    }

    /// <summary>Validation result: the problems and a one-line verdict.</summary>
    public static string Validate(ModuleResolution resolution, IReadOnlyList<ModuleDiagnostic> diagnostics, string projectName)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var text = new StringBuilder($"{projectName} ({resolution.Profile.DisplayName()}): {Join([.. resolution.Modules.Select(m => m.Id)])}\n");
        AppendProblems(text, diagnostics);
        var errors = diagnostics.Count(d => d.Severity == ModuleSeverity.Error);
        text.Append(errors == 0 ? "\nModules OK.\n" : $"\nModules: {errors} error(s).\n");
        return text.ToString();
    }

    private static void AppendProblems(StringBuilder text, IReadOnlyList<ModuleDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        text.Append('\n');
        foreach (var diagnostic in diagnostics.OrderByDescending(d => d.Severity).ThenBy(d => d.Code, StringComparer.Ordinal))
        {
            text.Append("  ").Append(diagnostic).Append('\n');
        }
    }

    // Children are the module's dependencies; a module reached again under the same root is shown as "(see above)".
    private static void Tree(StringBuilder text, ModuleResolution resolution, string id, int depth, string marker, HashSet<string> shown)
    {
        var again = !shown.Add(id);
        text.Append(' ', 2 + (depth * 4)).Append(marker).Append(id).Append(again ? " (see above)" : "").Append('\n');
        if (again)
        {
            return;
        }

        foreach (var edge in resolution.Dependencies.Where(d => d.Module == id))
        {
            Tree(text, resolution, edge.DependsOn, depth + 1, edge.IsOptional ? "~ " : "- ", shown);
        }
    }

    private static string Status(ModuleResolution project, ModuleDefinition module) =>
        project.Reasons.TryGetValue(module.Id, out var why) ? "on: " + why : "off: " + project.Excluded.First(e => e.Module.Id == module.Id).Reason;

    private static string Profiles(ModuleDefinition module) => string.Join(", ", module.Profiles.Select(p => p.DisplayName()));

    private static string Activation(ModuleDefinition module) => module.Activation switch
    {
        ModuleActivation.Always => "always",
        ModuleActivation.Auto => "auto",
        _ => "opt-in",
    };

    private static string Join(IReadOnlyList<string> items) => items.Count == 0 ? "-" : string.Join(", ", items);

    private static void Table(StringBuilder text, IReadOnlyList<string[]> rows)
    {
        var widths = Enumerable.Range(0, rows.Max(r => r.Length)).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
        foreach (var row in rows)
        {
            text.Append("  ").Append(string.Join("  ", row.Select((cell, c) => c == row.Length - 1 ? cell : cell.PadRight(widths[c])))).Append('\n');
        }
    }
}
