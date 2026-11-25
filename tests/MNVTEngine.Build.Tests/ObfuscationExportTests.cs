// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Build;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build.Tests;

/// <summary>Real exports with <c>--obfuscate</c>: the game still loads and runs, so renaming and the scene remap agree.</summary>
[Collection(GameExports.Name)]
public sealed class ObfuscationExportTests(TestBuildState state) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-obf-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string NewProject(string name)
    {
        _ = state;
        var engine = EngineLocation.FindRoot();
        var template = ProjectTemplates.List(EngineLocation.TemplatesDirectory(engine)).Single(t => t.Id == "basic-2d");
        var directory = Path.Combine(_root, name);
        ProjectTemplates.Create(template, name, directory, engine);
        return directory;
    }

    private static BuildOptions Options(string backend) => new()
    {
        ScriptingBackend = backend,
        TestRunFrames = 10,
        Obfuscation = new ObfuscationSettings { Seed = "obfuscation-export-test" },
    };

    [Fact]
    public async Task Jit_RenamesTheComponentClass_AndTheRemappedSceneStillLoads()
    {
        Gpu.SkipWithoutVulkan14(); // the test run starts the exported game
        var directory = NewProject("ObfJit");
        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(directory, Options("jit"), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        // The component ran: it was registered under its new name and the scene's type id was remapped to match.
        // Were the rename and the remap inconsistent, the component would load as MissingComponent and never log this.
        var log = File.ReadAllText(Path.Combine(result.OutputDirectory!, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal);

        // The class name is gone from the shipped scene and code packages.
        foreach (var file in Directory.GetFiles(Path.Combine(result.OutputDirectory!, "ObfJit_Data"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("PlayerController", Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Aot_KeepsTypeNames_ObfuscatesStrings_AndRuns()
    {
        Gpu.SkipWithoutVulkan14(); // the test run starts the exported game
        var directory = NewProject("ObfAot");
        var result = await new GameBuilder(NullLogger.Instance).BuildAsync(directory, Options("aot"), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("\n", result.Diagnostics));

        var log = File.ReadAllText(Path.Combine(result.OutputDirectory!, "Logs", "game.log"));
        Assert.Contains("[script] Move to the goal with WASD", log, StringComparison.Ordinal);
    }
}
