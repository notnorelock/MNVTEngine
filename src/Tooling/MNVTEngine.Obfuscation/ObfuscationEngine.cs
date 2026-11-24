// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Diagnostics;
using Mono.Cecil;
using MNVTEngine.Obfuscation.Analysis;
using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Naming;
using MNVTEngine.Obfuscation.Reporting;
using MNVTEngine.Obfuscation.Rules;
using MNVTEngine.Obfuscation.Transforms;
using MNVTEngine.Obfuscation.Validation;

namespace MNVTEngine.Obfuscation;

/// <summary>What a run produced: diagnostics, the report, the mapping and where files were written.</summary>
public sealed class ObfuscationResult
{
    public required bool Success { get; init; }
    public required DiagnosticBag Diagnostics { get; init; }
    public Report? Report { get; init; }
    public Mapping? Mapping { get; init; }
    public string? MappingPath { get; init; }
    public string? ReportPath { get; init; }
    public List<string> WrittenFiles { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Runs a configuration: load, analyze, (for protect) transform, write and validate. Analysis never changes a
/// file, so <see cref="Analyze"/> is a safe dry run. Writing goes to a temp file and moves into place, so a
/// failure never leaves a half-written assembly, and originals are never overwritten unless input and output
/// are the same folder (which <see cref="Protect"/> refuses).
/// </summary>
public sealed class ObfuscationEngine
{
    private readonly ObfuscatorConfig _config;
    private readonly DiagnosticBag _diag = new();

    public ObfuscationEngine(ObfuscatorConfig config) => _config = config;

    public DiagnosticBag Diagnostics => _diag;

    /// <summary>Loads and analyzes without writing. Returns null when input cannot be loaded (see diagnostics).</summary>
    public ObfuscationResult Analyze() => Run(write: false);

    public ObfuscationResult Protect() => Run(write: true);

    private ObfuscationResult Run(bool write)
    {
        var sw = Stopwatch.StartNew();
        string input = _config.InputDirectory != null ? _config.Resolve(_config.InputDirectory) : _config.BaseDirectory;
        string? output = _config.OutputDirectory != null ? _config.Resolve(_config.OutputDirectory) : null;

        if (write)
        {
            if (output == null)
            {
                _diag.Error(DiagnosticCodes.Config, "outputDirectory is required to protect assemblies.");
                return Failed(sw);
            }
            if (PathsEqual(input, output))
            {
                _diag.Error(DiagnosticCodes.UnsafePath, "The output folder is the input folder; choose a different output so the originals are never overwritten.");
                return Failed(sw);
            }
        }

        var include = Compile(_config.Assemblies);
        var exclude = Compile(_config.ExcludeAssemblies);
        var referenceDirs = _config.ReferenceDirectories.Select(_config.Resolve).ToList();
        using AssemblySet? set = AssemblySet.Load(input, referenceDirs, include, exclude, _diag);
        if (set == null || !set.Protected.Any()) return Failed(sw);

        var ruleErrors = new List<ConfigError>();
        List<CompiledRule> rules = ConfigLoader.LoadRules(_config, ruleErrors);
        foreach (ConfigError e in ruleErrors) _diag.Error(DiagnosticCodes.Config, e.Message, symbol: e.Path);
        if (_diag.HasErrors) return Failed(sw);

        AnalysisOutcome analysis = new Analyzer(set, _config, rules, _diag).Run();
        if (_diag.HasErrors) return Failed(sw);

        byte[] seed = ResolveSeed();
        var scheme = _config.Renaming.Scheme;
        var reports = new List<AssemblyReport>();
        Renamer? renamer = null;
        var stringStats = new Dictionary<string, (int replaced, int bytes)>();
        var constantStats = new Dictionary<string, int>();
        var flowStats = new Dictionary<string, int>();

        if (_config.Features.RenameSymbols)
        {
            renamer = new Renamer(set, analysis, _config, new NameGenerator(seed, scheme), _diag);
            renamer.Run();
        }
        if (_config.Features.ObfuscateStrings) EncryptStrings(set, analysis, seed, stringStats);
        if (_config.Features.TransformConstants) HideConstants(set, analysis, seed, constantStats);
        if (_config.Features.ControlFlow) ObfuscateFlow(set, analysis, seed, flowStats);

        var result = new ObfuscationResult { Success = !_diag.HasErrors, Diagnostics = _diag };
        if (write && !_diag.HasErrors)
            WriteAssemblies(set, output!, reports, stringStats, constantStats, flowStats, analysis, result);
        else
            foreach (LoadedAssembly a in set.Protected)
                reports.Add(MakeReport(a, a.InputBytes, analysis, stringStats, constantStats, flowStats));

        Report report = ReportBuilder.Build(reports, analysis.Catalog, _diag, _config.Reporting.IncludePreservedSymbols);
        report.Deterministic = _config.Renaming.Deterministic;
        report.Preset = _config.Preset;
        report.Features = EnabledFeatures();
        Mapping? mapping = renamer != null ? Mapping.From(renamer.Entries) : null;

        sw.Stop();
        report.GeneratedAt = DateTimeOffset.UtcNow;
        var final = new ObfuscationResult
        {
            Success = !_diag.HasErrors,
            Diagnostics = _diag,
            Report = report,
            Mapping = mapping,
            Elapsed = sw.Elapsed,
        };
        final.WrittenFiles.AddRange(result.WrittenFiles);
        return WriteReports(final, output, write);
    }

    private void EncryptStrings(AssemblySet set, AnalysisOutcome analysis, byte[] seed, Dictionary<string, (int, int)> stats)
    {
        var keepPlain = Compile(_config.Strings.KeepPlain);
        foreach (LoadedAssembly a in set.Protected)
        {
            uint key = _config.Renaming.Deterministic ? DeterministicKey(seed, a.Name, "strings") : StringEncryption.RandomKey();
            var enc = new StringEncryption(a.Module, _config.Strings.Intern, _config.Strings.MinLength, keepPlain, label => MemberName(seed, a.Name, label), key);
            foreach (TypeDefinition t in a.Module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                    if (!IsExcluded(analysis, m, Protection.Strings)) enc.Rewrite(m);
            enc.Finish();
            stats[a.Name] = (enc.Replaced, enc.BlobBytes);
        }
    }

    private void HideConstants(AssemblySet set, AnalysisOutcome analysis, byte[] seed, Dictionary<string, int> stats)
    {
        foreach (LoadedAssembly a in set.Protected)
        {
            var ct = new ConstantTransform(DeterministicSeed(seed, a.Name, "constants"));
            foreach (TypeDefinition t in a.Module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                    if (!IsExcluded(analysis, m, Protection.Constants)) ct.Rewrite(m);
            stats[a.Name] = ct.Replaced;
        }
    }

    private void ObfuscateFlow(AssemblySet set, AnalysisOutcome analysis, byte[] seed, Dictionary<string, int> stats)
    {
        foreach (LoadedAssembly a in set.Protected)
        {
            var cf = new ControlFlowTransform(_config.ControlFlow.MaxInstructions, DeterministicSeed(seed, a.Name, "flow"));
            foreach (TypeDefinition t in a.Module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                    if (!IsExcluded(analysis, m, Protection.ControlFlow)) cf.Rewrite(m);
            stats[a.Name] = cf.Transformed;
            if (cf.Skipped > 0)
                _diag.Info(DiagnosticCodes.ControlFlowSkipped, $"{cf.Transformed} method(s) had their control flow obfuscated; {cf.Skipped} were left unchanged because the transform could not be proven safe for them.", a.Name);
        }
    }

    private bool IsExcluded(AnalysisOutcome analysis, MethodDefinition m, Protection what)
    {
        Symbol? s = analysis.Catalog.Get(m);
        return s != null && (s.Excluded & what) != 0;
    }

    private void WriteAssemblies(AssemblySet set, string output, List<AssemblyReport> reports, Dictionary<string, (int, int)> strings, Dictionary<string, int> constants, Dictionary<string, int> flow, AnalysisOutcome analysis, ObfuscationResult result)
    {
        Directory.CreateDirectory(output);
        foreach (LoadedAssembly a in set.Protected)
        {
            string target = Path.Combine(output, a.FileName);
            long size;
            try
            {
                size = WriteOne(a, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _diag.Error(DiagnosticCodes.OutputUnreadable, $"Could not write the protected assembly: {e.Message}", a.FileName);
                continue;
            }
            result.WrittenFiles.Add(target);
            OutputValidator.Validate(target, set.Resolver, _diag);
            reports.Add(MakeReport(a, size, analysis, strings, constants, flow));
        }
        if (_config.CopyUnprotectedAssemblies)
            foreach (LoadedAssembly r in set.References)
            {
                string target = Path.Combine(output, r.FileName);
                if (File.Exists(target)) continue;
                try
                {
                    File.Copy(r.Path, target, overwrite: false);
                    result.WrittenFiles.Add(target);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _diag.Warning(DiagnosticCodes.OutputUnreadable, $"Could not copy the reference assembly {r.FileName}: {e.Message}");
                }
            }
    }

    /// <summary>Writes to a temp file next to the target and moves it into place, so a partial write is never seen.</summary>
    private static long WriteOne(LoadedAssembly a, string target)
    {
        string temp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            a.Definition.Write(temp, new WriterParameters { WriteSymbols = false, DeterministicMvid = true });
            long size = new FileInfo(temp).Length;
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            return size;
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private AssemblyReport MakeReport(LoadedAssembly a, long outputBytes, AnalysisOutcome analysis, Dictionary<string, (int replaced, int bytes)> strings, Dictionary<string, int> constants, Dictionary<string, int> flow)
    {
        var syms = analysis.Catalog.Of(a).ToList();
        int types = syms.Count(s => s.Kind == SymbolKind.Type);
        int renamedTypes = syms.Count(s => s.Kind == SymbolKind.Type && s.IsRenamed);
        int renamedMembers = syms.Count(s => s.Kind != SymbolKind.Type && s.IsRenamed);
        int preservedTypes = syms.Count(s => s.Kind == SymbolKind.Type && !s.IsRenamed);
        int preservedMembers = syms.Count(s => s.Kind != SymbolKind.Type && !s.CanRename);
        return new AssemblyReport(a.Name, a.InputBytes, outputBytes, types,
            syms.Count(s => s.Kind == SymbolKind.Method), syms.Count(s => s.Kind == SymbolKind.Field),
            renamedTypes, renamedMembers, preservedTypes, preservedMembers,
            strings.GetValueOrDefault(a.Name).replaced, constants.GetValueOrDefault(a.Name), flow.GetValueOrDefault(a.Name));
    }

    private ObfuscationResult WriteReports(ObfuscationResult result, string? output, bool write)
    {
        if (!write || result.Report == null) return result;
        var reporting = _config.Reporting;
        string reportsDir = reporting.ReportsDirectory != null ? _config.Resolve(reporting.ReportsDirectory) : _config.BaseDirectory;
        if (output != null && !reporting.AllowReportsInOutput && IsInside(reportsDir, output))
        {
            _diag.Warning(DiagnosticCodes.UnsafePath, "The reports folder is inside the output folder; the mapping would ship with the build. Writing reports next to the output folder instead. Set reporting.allowReportsInOutput to override.");
            reportsDir = Path.Combine(Path.GetDirectoryName(output.TrimEnd(Path.DirectorySeparatorChar))!, "obfuscation-reports");
        }

        string? mappingPath = null, reportPath = null;
        try
        {
            if (reporting.GenerateMapping && result.Mapping != null)
            {
                mappingPath = reporting.MappingPath != null ? _config.Resolve(reporting.MappingPath) : Path.Combine(reportsDir, "mapping.json");
                result.Mapping.Save(mappingPath);
            }
            if (reporting.GenerateReport)
            {
                reportPath = reporting.ReportPath != null ? _config.Resolve(reporting.ReportPath) : Path.Combine(reportsDir, "report.json");
                result.Report.Save(reportPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _diag.Warning(DiagnosticCodes.OutputUnreadable, $"Could not write a report file: {e.Message}");
        }

        var final = new ObfuscationResult
        {
            Success = result.Success && !_diag.HasErrors,
            Diagnostics = _diag,
            Report = result.Report,
            Mapping = result.Mapping,
            MappingPath = mappingPath,
            ReportPath = reportPath,
            Elapsed = result.Elapsed,
        };
        final.WrittenFiles.AddRange(result.WrittenFiles);
        return final;
    }

    // ── Seeds and names ────────────────────────────────────────────────────

    private byte[] ResolveSeed()
    {
        string? seed = _config.Renaming.Seed;
        if (_config.Renaming.SeedEnvironmentVariable != null)
        {
            string? fromEnv = Environment.GetEnvironmentVariable(_config.Renaming.SeedEnvironmentVariable);
            if (!string.IsNullOrEmpty(fromEnv)) seed = fromEnv;
        }
        if (_config.Renaming.Deterministic)
        {
            if (seed == null)
            {
                _diag.Warning(DiagnosticCodes.WeakSeed, "Deterministic renaming with no seed uses a fixed built-in seed, so the names are the same in every project. Set renaming.seed (or seedEnvironmentVariable) to a private value.");
                seed = "mnvtengine-obfuscation-default-seed";
            }
            return NameGenerator.SeedBytes(seed);
        }
        return seed != null ? NameGenerator.SeedBytes(seed) : NameGenerator.RandomSeed();
    }

    private uint DeterministicKey(byte[] seed, string assembly, string purpose) => unchecked((uint)DeterministicSeed(seed, assembly, purpose));

    private static int DeterministicSeed(byte[] seed, string assembly, string purpose)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(seed);
        byte[] b = h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(purpose + "\u0000" + assembly));
        return BitConverter.ToInt32(b, 0);
    }

    private string MemberName(byte[] seed, string assembly, string label)
    {
        var g = new NameGenerator(seed, _config.Renaming.Scheme);
        return g.Create($"[{assembly}]inject:{label}", _ => false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private List<Glob> Compile(IEnumerable<string> patterns) => patterns.Select(Glob.Create).ToList();

    private List<string> EnabledFeatures()
    {
        var list = new List<string>();
        if (_config.Features.RenameSymbols) list.Add("renameSymbols");
        if (_config.Features.ObfuscateStrings) list.Add("obfuscateStrings");
        if (_config.Features.TransformConstants) list.Add("transformConstants");
        if (_config.Features.ControlFlow) list.Add("controlFlow");
        return list;
    }

    private ObfuscationResult Failed(Stopwatch sw)
    {
        sw.Stop();
        return new ObfuscationResult { Success = false, Diagnostics = _diag, Elapsed = sw.Elapsed };
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
