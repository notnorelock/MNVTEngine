// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Runtime;

namespace MNVTEngine.CLI;

/// <summary><c>mnvt package build|inspect|verify|analyze|extract|keys</c>; the work is done by MNVTEngine.Build.</summary>
internal static class PackageCommand
{
    public static async Task<int> RunAsync(CommandLine command, TextWriter output, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var verb = command.Positional.Count > 0 ? command.Positional[0] : throw new FormatException("Use: mnvt package build|inspect|verify|analyze|extract|keys|dlc|bundle|editor|import|benchmark ...");
        var target = command.Positional.Count == 2 ? command.Positional[1] : throw new FormatException($"Use: mnvt package {verb} <{(verb is "build" or "analyze" or "keys" or "dlc" or "bundle" or "import" ? "project" : verb == "editor" ? "folder" : "file.mnvtpack")}>.");
        var project = command.Value("--project") is { } projectPath ? GameProject.Load(projectPath) : null;
        switch (verb)
        {
            case "build":
            {
                var (directory, packages, diagnostics) = await new GameBuilder(loggerFactory.CreateLogger("Package")).PackageAsync(target, Options(command), cancellationToken).ConfigureAwait(false);
                Cli.Report(output, diagnostics);
                if (directory is null)
                {
                    output.WriteLine("Packaging FAILED.");
                    return Cli.Failed;
                }

                output.WriteLine($"{packages.Count} packages in {directory}: {string.Join(", ", packages.Select(p => $"{p.File} ({p.Entries})"))}");
                return Cli.Ok;
            }

            case "inspect":
                output.Write(PackageTools.Inspect(target, PackageTools.OptionsFor(target, project), command.Has("--entries")));
                return Cli.Ok;

            case "verify" when GamePackage.FindDataDirectory(target) is not null:
            {
                var diagnostics = BuildValidator.Validate(target, project is null ? null : ContentKeys.KeyRingFor(project));
                Cli.Report(output, diagnostics);
                output.WriteLine(diagnostics.HasErrors() ? "Verification FAILED." : "Verified: signatures, package identities and every chunk.");
                return diagnostics.HasErrors() ? Cli.Failed : Cli.Ok;
            }

            case "verify":
            {
                var options = PackageTools.OptionsFor(target, project, command.Value("--trust"));
                var problems = PackageTools.Verify(target, options, requireTrustedSignature: command.Has("--trust"));
                foreach (var problem in problems)
                {
                    output.WriteLine($"error: {problem}");
                }

                output.WriteLine(problems.Count == 0 ? "Verified: signature and every chunk." : $"Verification FAILED ({problems.Count} problems).");
                return problems.Count == 0 ? Cli.Ok : Cli.Failed;
            }

            case "analyze":
            {
                var (loaded, _, content, diagnostics) = await new GameBuilder(loggerFactory.CreateLogger("Analyze")).AnalyzeContentAsync(target, Options(command), cancellationToken).ConfigureAwait(false);
                Cli.Report(output, diagnostics);
                if (loaded is null || content is null)
                {
                    output.WriteLine("Analysis FAILED.");
                    return Cli.Failed;
                }

                output.Write(PackageTools.Analyze(loaded, content));
                return diagnostics.HasErrors() ? Cli.Failed : Cli.Ok;
            }

            case "extract":
            {
                var folder = command.Value("--output") ?? throw new FormatException("Use: mnvt package extract <file.mnvtpack> --output <folder>.");
                var count = PackageTools.Extract(target, folder, PackageTools.OptionsFor(target, project));
                output.WriteLine($"Extracted {count} files to {Path.GetFullPath(folder)} (inspection only: this is not an editable project).");
                return Cli.Ok;
            }

            case "keys":
                return Keys(GameProject.Load(target), command, output);

            case "dlc":
            {
                var loadedProject = GameProject.Load(target);
                var diagnostics = new List<BuildDiagnostic>();
                var profile = GameBuilder.ResolveProfile(loadedProject, Options(command), diagnostics) ?? throw new FormatException(string.Join(" ", diagnostics.Select(d => d.Message)));
                var obfuscation = command.Has("--obfuscate") ? new ObfuscationSettings { Scheme = command.Value("--obfuscate-scheme") ?? "letters" } : null;
                var result = await DlcBuilder.BuildAsync(loadedProject, profile, command.Value("--folder") ?? throw new FormatException("Use: mnvt package dlc <project> --folder <Assets/...> --name <Name>."),
                    command.Value("--name") ?? throw new FormatException("Use: mnvt package dlc <project> --folder <Assets/...> --name <Name>."), command.Value("--output"), command.Value("--signing-key"), command.Has("--separate-key"), obfuscation, cancellationToken).ConfigureAwait(false);
                Cli.Report(output, result.Diagnostics);
                output.WriteLine(result.PackageFile is null ? "DLC packaging FAILED." : $"{result.Assets} assets in {result.PackageFile}; copy it into <Game>_Data/{GameContent.DlcFolder}.");
                return result.PackageFile is null ? Cli.Failed : Cli.Ok;
            }

            case "bundle":
            {
                const string usage = "Use: mnvt package bundle <project> --root <Assets/...prefab> --kind <kind> --name <Name> [--provided <folder>[,<folder>...]] [--output <folder>].";
                var request = new BundleRequest(
                    command.Value("--root") ?? throw new FormatException(usage),
                    command.Value("--kind") ?? throw new FormatException(usage),
                    command.Value("--name") ?? throw new FormatException(usage),
                    command.Value("--output"),
                    command.Value("--provided")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var result = BundleBuilder.Build(GameProject.Load(target), request);
                Cli.Report(output, result.Diagnostics);
                output.WriteLine(result.File is null ? "Bundle packaging FAILED." : $"{result.Assets} assets in {result.File}.");
                return result.File is null ? Cli.Failed : Cli.Ok;
            }

            case "editor":
            {
                const string usage = "Use: mnvt package editor <folder with Assets/, Scripts/, Editor/> --name <Name> --version <version> [--description <text>] [--output <file.mnvtbundle>].";
                var name = command.Value("--name") ?? throw new FormatException(usage);
                var file = EditorPackageFile.Write(target, new EditorPackageManifest(name, command.Value("--version") ?? throw new FormatException(usage), command.Value("--description")),
                    command.Value("--output") ?? Path.Combine(target, name + Bundles.ContentBundleFile.Extension));
                using var written = EditorPackageFile.Open(file);
                output.WriteLine($"Editor package {name} {written.Manifest.Version}: {written.Entries.Count} files in {file}.");
                return Cli.Ok;
            }

            case "import":
            {
                const string usage = "Use: mnvt package import <project> --file <package.mnvtbundle>. The package's C# code runs in the editor: import only packages you trust.";
                var destination = GameProject.Load(target);
                using var package = EditorPackageFile.Open(command.Value("--file") ?? throw new FormatException(usage));
                var plan = EditorPackageImporter.Plan(destination, package);
                foreach (var problem in plan.Problems)
                {
                    output.WriteLine("error: " + problem);
                }

                if (!plan.CanImport)
                {
                    output.WriteLine("Import FAILED; nothing was changed.");
                    return Cli.Failed;
                }

                EditorPackageImporter.Import(destination, package, EngineLocation.FindRoot());
                output.WriteLine($"Imported {plan.Manifest.Name} {plan.Manifest.Version}{(plan.InstalledVersion is { } old ? $" (was {old})" : "")}: {plan.Files.Count} files"
                    + $"{(plan.Removed.Count > 0 ? $", {plan.Removed.Count} old files removed" : "")}{(plan.CreatesScriptsProject ? ", scripts project created" : "")}{(plan.CreatesEditorProject ? ", editor project created" : "")}.");
                return Cli.Ok;
            }

            case "benchmark":
            {
                var data = GamePackage.FindDataDirectory(target) ?? target;
                output.Write(PackageBenchmark.Run(data, PackageTools.OptionsFor(data, project), repack: !command.Has("--no-repack")));
                return Cli.Ok;
            }

            default:
                throw new FormatException($"Unknown package command '{verb}'; use build, inspect, verify, analyze, extract, keys, dlc, bundle, editor, import or benchmark.");
        }
    }

    private static BuildOptions Options(CommandLine command) => new()
    {
        Profile = command.Value("--profile"),
        Configuration = command.Value("--configuration"),
        Target = command.Value("--target"),
        OutputDirectory = command.Value("--output"),
        SigningKey = command.Value("--signing-key"),
    };

    private static int Keys(GameProject project, CommandLine command, TextWriter output)
    {
        if (command.Has("--rotate"))
        {
            var (retired, current) = ContentKeys.Rotate(project);
            output.WriteLine($"New encryption key {current}{(retired is null ? "" : $"; the previous key {retired} is kept as {ContentKeys.RelativePath}.{retired}.retired")}. Commit {ContentKeys.RelativePath}.");
        }

        if (command.Value("--new-signing-key") is { } file)
        {
            var full = Path.GetFullPath(file);
            if (full.StartsWith(Path.GetFullPath(project.RootDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Keep the release signing key outside the project folder: it must never be committed with the project or shipped.");
            }

            if (File.Exists(full))
            {
                throw new IOException($"{full} exists; not overwriting a signing key.");
            }

            output.WriteLine($"Created release signing key {SigningKeys.Create(full)} in {full}. Keep it private and backed up; build releases with --signing-key \"{full}\".");
        }

        var diagnostics = new List<BuildDiagnostic>();
        var key = ContentKeys.LoadOrCreate(project, diagnostics);
        output.WriteLine($"Encryption key   {key?.IdText ?? "invalid"} ({ContentKeys.RelativePath})");
        foreach (var id in ContentKeys.KeyRingFor(project).Ids.Where(id => id != key?.Id))
        {
            output.WriteLine($"  retired key    {IO.Packages.PackageEncryptionKey.FormatId(id)}");
        }

        if (File.Exists(SigningKeys.DevelopmentKeyPath))
        {
            using var development = IO.Packages.PackageSigningKey.FromPem(File.ReadAllText(SigningKeys.DevelopmentKeyPath));
            output.WriteLine($"Development key  {development.PublicKey.Id} ({SigningKeys.DevelopmentKeyPath})");
        }
        else
        {
            output.WriteLine("Development key  none yet (created by the first build)");
        }

        output.WriteLine($"Release key      {(Environment.GetEnvironmentVariable(SigningKeys.EnvironmentVariable) is { Length: > 0 } release ? release : $"not set (pass --signing-key or set {SigningKeys.EnvironmentVariable})")}");
        Cli.Report(output, diagnostics);
        return Cli.Ok;
    }
}
