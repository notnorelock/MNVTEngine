using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;

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

          new <Name> [--template empty-2d] [--path <parent folder>]
                                Create a project from a template (see `mnvt new --list`)
          editor <project> [--frames N]
                                Open the project in the editor (--frames: close after N frames)
          run <project> [--frames N] [--screenshot file.png]
                                Play the project from its sources
          build <project> [--profile name] [--configuration debug|release] [--target win-x64]
                          [--output folder] [--run [frames]]
                                Export a standalone game (default: the "release" profile into Builds/<profile>)
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
                "new" => CommandLine.Parse(args, ["--template", "--path"], ["--list"]),
                "editor" => CommandLine.Parse(args, ["--frames"], []),
                "run" => CommandLine.Parse(args, ["--frames", "--screenshot"], ["--debug-gl"]),
                "build" => CommandLine.Parse(args, ["--profile", "--configuration", "--target", "--output"], [], optionalValues: ["--run"]),
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
                "editor" => await EditorAsync(command, output, cancellationToken).ConfigureAwait(false),
                "run" => Run(command, loggerFactory),
                "build" => await BuildAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
                _ => await ValidateAsync(command, output, loggerFactory, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (FormatException ex)
        {
            output.WriteLine($"error: {ex.Message}");
            return UsageError;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException)
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
                output.WriteLine($"  {t.Id,-12} {t.Description}");
            }

            return Ok;
        }

        var name = command.Single("project name");
        var id = command.Value("--template") ?? "empty-2d";
        var template = templates.FirstOrDefault(t => t.Id == id)
            ?? throw new FormatException($"Unknown template '{id}'; available: {string.Join(", ", templates.Select(t => t.Id))}.");
        var target = Path.GetFullPath(Path.Combine(command.Value("--path") ?? ".", name));
        var file = ProjectTemplates.Create(template, name, target, engineRoot);
        output.WriteLine($"Created {file} from '{template.Id}'.\nNext: mnvt run \"{target}\"  or  mnvt editor \"{target}\"");
        return Ok;
    }

    /// <summary>Starts the editor from the engine sources (the CLI never references the editor) and waits for it to close.</summary>
    private static async Task<int> EditorAsync(CommandLine command, TextWriter output, CancellationToken cancellationToken)
    {
        var project = GameProject.Load(command.Single("project")).RootDirectory;
        var editor = EngineLocation.EditorProject(EngineLocation.FindRoot());
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var argument in new[] { "run", "--project", editor, "-c", "Debug", "--", project }.Concat(command.Passthrough("--frames")))
        {
            start.ArgumentList.Add(argument);
        }

        output.WriteLine($"Starting the editor for {project}...");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the editor.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? Ok : Failed;
    }

    private static int Run(CommandLine command, ILoggerFactory loggerFactory)
    {
        var project = command.Single("project");
        return DevelopmentRun.Run(
            [project, .. command.Passthrough("--frames", "--screenshot", "--debug-gl")],
            loggerFactory,
            (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
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
            TestRunFrames = command.Has("--run") ? command.Int("--run", 120) : null,
        };
        var result = await new GameBuilder(loggerFactory.CreateLogger("Build")).BuildAsync(command.Single("project"), options, cancellationToken).ConfigureAwait(false);
        Report(output, result.Diagnostics);
        var seconds = result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        output.WriteLine(result.Succeeded
            ? $"Build succeeded in {seconds} s: {result.Executable}"
            : $"Build FAILED after {seconds} s.");
        return result.Succeeded ? Ok : Failed;
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

    private static void Report(TextWriter output, IEnumerable<BuildDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(d => d.Severity != BuildSeverity.Info))
        {
            output.WriteLine(diagnostic.ToString());
        }
    }
}
