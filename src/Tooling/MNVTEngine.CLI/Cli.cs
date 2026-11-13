using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.CLI;

/// <summary>
/// <c>mnvt</c> commands. Each command only parses arguments and reports results; project creation, build
/// orchestration, packaging and validation live in MNVTEngine.Build.
/// </summary>
public static class Cli
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int UsageError = 64;

    public const string Usage = """
        Usage: mnvt <command> [options]

          new <Name> --profile 2d|3d [--template <id>] [--path <parent folder>]
                                Create a 2D or 3D project (Empty 2D / Empty 3D, or a template of that profile; see `mnvt new --list`)
          modules list [project]  Engine modules: profiles, activation, dependencies (and their status in the project)
          modules validate <project>
                                Resolve the project's modules and check its scenes use only components of enabled modules
          modules graph <project> The project's modules in startup order, their dependencies, and the modules it does not have
          migrate <project> [--profile 2d|3d]
                                Save the profile of a project made before profiles (inferred from its scenes; required when they mix 2D and 3D)
          editor [project] [--frames N]
                                Open the project in the editor; without one, the Project Hub (--frames: close after N frames)
          run <project> [--frames N] [--screenshot file.png] [--profile report.txt]
                                Play the project from its sources
          build <project> [--profile name] [--configuration debug|release] [--target win-x64]
                          [--output folder] [--signing-key key.pem] [--run [frames]]
                                Export a standalone game (default: the "release" profile into Builds/<profile>)
          package build <project> [--profile name] [--configuration ...] [--output folder] [--signing-key key.pem]
                                Build only the content packages (.mnvtpack) and their signed manifest
          package inspect <file.mnvtpack> [--project <project>] [--entries]
                                Header, signature, encryption and contents of a package
          package verify <file.mnvtpack | exported game> [--project <project>] [--trust key.pem]
                                Check signatures and every chunk of a package (or validate a whole game)
          package analyze <project> [--profile name]
                                What each package of a build would hold: sizes, duplicates, stripped assets
          package extract <file.mnvtpack> --output <folder> [--project <project>]
                                Developer tool: write a package's files to a folder (not an editable project)
          package keys <project> [--rotate] [--new-signing-key key.pem]
                                Show the project's encryption key and signing keys; rotate the key; create a release signing key
          package benchmark <exported game> [--project <project>] [--no-repack]
                                Read times from the packages against the same files on disk, and each compression profile
          validate <project | exported game folder> [--run [frames]]
                                Check a project, or an exported game, without building

        <project> is a .mnvtproj file or the folder containing it.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? UsageError : Ok;
        }

        if (args[0] == "--version")
        {
            output.WriteLine($"mnvt {GamePackager.EngineVersion}");
            return Ok;
        }

        CommandLine command;
        try
        {
            command = args[0] switch
            {
                "new" => CommandLine.Parse(args, ["--profile", "--template", "--path"], ["--list"]),
                "migrate" => CommandLine.Parse(args, ["--profile"], []),
                "modules" => CommandLine.Parse(args, [], []),
                "editor" => CommandLine.Parse(args, ["--frames"], []),
                "run" => CommandLine.Parse(args, ["--frames", "--screenshot", "--profile"], ["--gpu-validation"]),
                "build" when args.Length > 1 && args[1] == "analyze" => CommandLine.Parse(["analyze", .. args[2..]], ["--profile", "--configuration", "--target", "--scripting-backend", "--json"], []),
                "build" => CommandLine.Parse(args, ["--profile", "--configuration", "--target", "--output", "--scripting-backend", "--signing-key"], [], optionalValues: ["--run"]),
                "package" => CommandLine.Parse(args, ["--profile", "--configuration", "--target", "--output", "--signing-key", "--project", "--trust", "--new-signing-key"], ["--entries", "--rotate", "--no-repack"]),
                "validate" => CommandLine.Parse(args, [], [], optionalValues: ["--run"]),
                _ => throw new FormatException($"Unknown command '{args[0]}'."),
            };
        }
        catch (FormatException ex)
        {
            output.WriteLine($"error: {ex.Message}\n\n{Usage}");
            return UsageError;
        }

        try
        {
            return command.Name switch
            {
                "new" => New(command, output),
                "migrate" => Migrate(command, output),
                "modules" => ModulesCommand(command, output),
                "editor" => await EditorAsync(command, output, cancellationToken).ConfigureAwait(false),
                "run" => Run(command, loggerFactory),
                "build" => await BuildAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
                "analyze" => await AnalyzeAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
                "package" => await PackageCommand.RunAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
                _ => await ValidateAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (FormatException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return UsageError;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException or Serialization.SerializationException)
        {
            output.WriteLine($"error: {ex.Message}");
            return Failed;
        }
    }

    private static int New(CommandLine command, TextWriter output)
    {
        var engineRoot = EngineLocation.FindRoot();
        var templates = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engineRoot));
        if (command.Has("--list"))
        {
            foreach (var t in templates)
            {
                output.WriteLine($"  {t.Id,-12} {t.Profile.DisplayName(),-3} {t.Description}");
            }

            return Ok;
        }

        var name = command.Single("project name");
        ProjectProfile? profile = command.Value("--profile") is { } text ? ProjectProfiles.Parse(text) : null;
        var id = command.Value("--template") ?? (profile is { } p ? ProjectTemplates.DefaultTemplateId(p)
            : throw new FormatException("Choose the project's profile: --profile 2d or --profile 3d."));
        var template = templates.FirstOrDefault(t => t.Id == id)
            ?? throw new FormatException($"Unknown template '{id}'; available: {string.Join(", ", templates.Select(t => t.Id))}.");
        if (profile is { } wanted && wanted != template.Profile)
        {
            throw new FormatException($"Template '{template.Id}' is {template.Profile.DisplayName()}, not {wanted.DisplayName()}.");
        }

        var target = Path.GetFullPath(Path.Combine(command.Value("--path") ?? ".", name));
        var file = ProjectTemplates.Create(template, name, target, engineRoot);
        output.WriteLine($"Created {template.Profile.DisplayName()} project {file} from '{template.Id}'.\nNext: mnvt run \"{target}\"  or  mnvt editor \"{target}\"");
        return Ok;
    }

    private static int ModulesCommand(CommandLine command, TextWriter output)
    {
        var verb = command.Positional.Count > 0 ? command.Positional[0] : throw new FormatException("Use: mnvt modules list [project] | validate <project> | graph <project>.");
        if (verb is not ("list" or "validate" or "graph"))
        {
            throw new FormatException($"Unknown modules command '{verb}'; use list, validate or graph.");
        }

        var path = command.Positional.Count switch
        {
            1 when verb == "list" => null,
            2 => command.Positional[1],
            _ => throw new FormatException(verb == "list" ? "Use: mnvt modules list [project]." : $"Use: mnvt modules {verb} <project>."),
        };

        var catalog = ModuleCatalog.Builtin;
        if (path is null)
        {
            output.Write(ModuleReports.List(catalog));
            return Ok;
        }

        GameProject project;
        try
        {
            project = GameProject.Load(path);
            if (verb == "list")
            {
                output.Write(ModuleReports.List(catalog, ProjectModules.Resolve(project), project.Name));
                return Ok;
            }

            if (verb == "graph")
            {
                var resolution = ProjectModules.Resolve(project);
                output.Write(ModuleReports.Graph(resolution, project.Name));
                return resolution.Succeeded ? Ok : Failed;
            }

            var (resolved, diagnostics) = ProjectModules.Validate(project);
            output.Write(ModuleReports.Validate(resolved, diagnostics, project.Name));
            return diagnostics.Any(d => d.Severity == ModuleSeverity.Error) ? Failed : Ok;
        }
        catch (Exception ex) when (ex is Serialization.SerializationException or ProfileConflictException)
        {
            output.WriteLine($"error: {ex.Message}");
            return Failed;
        }
    }

    private static int Migrate(CommandLine command, TextWriter output)
    {
        var project = GameProject.Load(command.Single("project"));
        ProjectProfile? choice = command.Value("--profile") is { } text ? ProjectProfiles.Parse(text) : null;
        try
        {
            var result = ProjectProfileMigration.Migrate(project, choice);
            output.WriteLine(result.Inference is { } inference
                ? $"Saved profile {result.Profile.DisplayName()} in {project.ProjectFile}. {inference.Describe()} The previous file is kept as {Path.GetFileName(project.ProjectFile)}.v1.bak."
                : $"{project.ProjectFile} already has profile {result.Profile.DisplayName()}; nothing to migrate.");
            return Ok;
        }
        catch (InvalidOperationException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return Failed;
        }
    }

    /// <summary>Starts the editor from the engine sources (the CLI never references the editor) and waits for it to close.</summary>
    private static async Task<int> EditorAsync(CommandLine command, TextWriter output, CancellationToken cancellationToken)
    {
        // Without a project the editor opens its Project Hub (recent, new, open).
        string[] project = command.Positional.Count == 0 ? [] : [GameProject.Load(command.Single("project")).RootDirectory];
        var editor = EngineLocation.EditorProject(EngineLocation.FindRoot());
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1"; // the build before the run leaves no idle MSBuild nodes
        foreach (var argument in new[] { "run", "--project", editor, "-c", "Debug", "--" }.Concat(project).Concat(command.Passthrough("--frames")))
        {
            start.ArgumentList.Add(argument);
        }

        output.WriteLine(project.Length == 0 ? "Starting the editor (Project Hub)..." : $"Starting the editor for {project[0]}...");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the editor.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? Ok : Failed;
    }

    private static int Run(CommandLine command, ILoggerFactory loggerFactory)
    {
        var project = command.Single("project");
        return DevelopmentRun.Run(
            [project, .. command.Passthrough("--frames", "--screenshot", "--profile", "--gpu-validation")],
            loggerFactory,
            (window, log) => VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, log, VulkanDeviceOptions.ForApplication("Player", window.Options.VSync, window.Options.GraphicsValidation).WithShaderc()),
            Audio.OpenAL.OpenALAudioDevice.Create) == 0 ? Ok : Failed;
    }

    private static async Task<int> BuildAsync(CommandLine command, TextWriter output, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var options = new BuildOptions
        {
            Profile = command.Value("--profile"),
            Configuration = command.Value("--configuration"),
            Target = command.Value("--target"),
            OutputDirectory = command.Value("--output"),
            ScriptingBackend = command.Value("--scripting-backend"),
            TestRunFrames = command.Has("--run") ? command.Int("--run", 120) : null,
            SigningKey = command.Value("--signing-key"),
        };
        var result = await new GameBuilder(loggerFactory.CreateLogger("Build")).BuildAsync(command.Single("project"), options, cancellationToken).ConfigureAwait(false);
        Report(output, result.Diagnostics);
        var seconds = result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        output.WriteLine(result.Succeeded
            ? $"Build succeeded in {seconds} s: {result.Executable}"
            : $"Build FAILED after {seconds} s.");
        return result.Succeeded ? Ok : Failed;
    }

    private static async Task<int> AnalyzeAsync(CommandLine command, TextWriter output, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var options = new BuildOptions { Profile = command.Value("--profile"), Configuration = command.Value("--configuration"), Target = command.Value("--target"), ScriptingBackend = command.Value("--scripting-backend") };
        var (report, diagnostics) = await new GameBuilder(loggerFactory.CreateLogger("Analyze")).AnalyzeAsync(command.Single("project"), options, cancellationToken).ConfigureAwait(false);
        Report(output, diagnostics);
        if (report is null)
        {
            output.WriteLine("Analysis FAILED.");
            return Failed;
        }

        output.Write(BuildReport.Describe(report));
        if (command.Value("--json") is { } json)
        {
            BuildReport.Write(report, json);
            output.WriteLine($"Report written to {json}");
        }

        return diagnostics.HasErrors() ? Failed : Ok;
    }

    private static async Task<int> ValidateAsync(CommandLine command, TextWriter output, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var target = command.Single("project or exported game");
        List<BuildDiagnostic> diagnostics;
        if (GamePackage.FindDataDirectory(target) is not null)
        {
            diagnostics = [.. BuildValidator.Validate(target)];
            if (command.Has("--run") && !diagnostics.HasErrors())
            {
                diagnostics.AddRange(await BuildValidator.RunAsync(target, command.Int("--run", 120), TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false));
            }
        }
        else
        {
            if (command.Has("--run"))
            {
                throw new FormatException("--run applies to exported games; use `mnvt run` for projects.");
            }

            diagnostics = [.. await new GameBuilder(loggerFactory.CreateLogger("Validate")).ValidateProjectAsync(target, cancellationToken).ConfigureAwait(false)];
        }

        Report(output, diagnostics);
        var errors = diagnostics.Count(d => d.Severity == BuildSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == BuildSeverity.Warning);
        output.WriteLine(errors == 0 ? $"Valid ({warnings} warnings)." : $"{errors} errors, {warnings} warnings.");
        return errors == 0 ? Ok : Failed;
    }

    internal static void Report(TextWriter output, IEnumerable<BuildDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(d => d.Severity != BuildSeverity.Info))
        {
            output.WriteLine(diagnostic.ToString());
        }
    }
}
