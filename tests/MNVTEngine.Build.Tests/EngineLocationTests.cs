// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Build;
using MNVTEngine.Versioning;

namespace MNVTEngine.Build.Tests;

public sealed class EngineLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-installed-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnInstalledEngine_BuildsGamesWithItsPrebuiltParts_AndItsOwnVersion()
    {
        Assert.False(EngineLocation.IsInstalled(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Engine"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "MNVTEngine.version.json"), Path.Combine(_root, "Engine", VersionSession.MetadataFile));

        Assert.True(EngineLocation.IsInstalled(_root));
        Assert.Equal(Path.Combine(_root, "Engine", "GameHost", "MNVTEngine.GameHost.csproj"), EngineLocation.GameHostProject(_root));
        Assert.Equal(Path.Combine(_root, "Engine", "CrashHandler", "aot"), EngineLocation.PrebuiltCrashHandler(_root, aot: true));

        // Nothing is allocated: the game gets the version the engine was built as.
        List<BuildDiagnostic> diagnostics = [];
        var version = PipelineVersion.Begin(_root, "test", new BuildContext(), Path.Combine(_root, "session"), null, diagnostics);
        Assert.NotNull(version);
        Assert.Equal(EngineLocation.InstalledVersion(_root)!.Version, version.Info.Version);

        // The source tree wins when both are present (a release staged inside a checkout).
        File.WriteAllText(Path.Combine(_root, "MNVTEngine.sln"), "");
        Assert.False(EngineLocation.IsInstalled(_root));
    }
}
