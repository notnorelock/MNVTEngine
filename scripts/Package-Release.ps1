# SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
# SPDX-License-Identifier: MIT
#
# Assembles an installed engine: a release without sources (docs/build-system/building.md, "Installed engine").
#
#   Editor/              the editor in its clean layout (scripts/Publish-Editor.ps1), with the browser host
#   CLI/                 mnvt
#   Engine/version.json  the version this engine was built as; marks the folder as an installed engine
#   Engine/Packages/     the engine's libraries and MNVTEngine.Sdk as NuGet packages (game hosts and game scripts)
#   Engine/GameHost/     the game host's startup project, built per game from those packages (name, keys, modules)
#   Engine/CrashHandler/ MNVTCrashHandler, published: jit/ (framework-dependent) and aot/ (native)
#   Engine/BrowserHost/  MNVTBrowserHost with CEF, published with a private runtime (used by AOT games)
#   templates/           project templates (mnvt new, the Project Hub)
#
# Run after a pipeline build (scripts\build.cmd Release): everything is the build's output, stamped with its version.
#
#   scripts\Package-Release.ps1 [-Output artifacts\release] [-Zip]

param(
    [string]$Output = "artifacts/release",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$configuration = "Release"

$session = Join-Path $root "artifacts/version/last-build"
if (-not (Test-Path (Join-Path $session "version.json"))) { throw "No pipeline build to package: run scripts\build.cmd Release first (artifacts/version/last-build)." }
$info = Get-Content (Join-Path $session "version.json") -Raw | ConvertFrom-Json
$version = if ($info.prerelease) { "$($info.fileVersion)-$($info.prerelease)" } else { $info.fileVersion } # EngineBuildInfo.PackageVersion
$name = "MNVTEngine-$($info.version)-win-x64"
$stage = Join-Path $out $name
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) "mnvt-release-$([Guid]::NewGuid().ToString('N'))"

function Invoke-Dotnet {
    & dotnet @args -nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) $($args[1]) failed." }
}

# Editor and CLI: the tested build, not rebuilt.
& (Join-Path $PSScriptRoot "Publish-Editor.ps1") -Configuration $configuration -Output (Join-Path $stage "Editor") -NoBuild
Invoke-Dotnet publish (Join-Path $root "src/Tooling/MNVTEngine.CLI/MNVTEngine.CLI.csproj") -c $configuration --no-build -o (Join-Path $stage "CLI")

$engine = Join-Path $stage "Engine"
$packages = Join-Path $engine "Packages"
New-Item -ItemType Directory -Force -Path $packages | Out-Null
Copy-Item (Join-Path $session "version.json") (Join-Path $engine "version.json")

# The engine's libraries as packages: their package dependencies bring the native libraries (GLFW, OpenAL, VMA, ...).
# The executables (game host, crash handler, browser host) are not libraries: they ship below.
$executables = @("MNVTEngine.GameHost", "MNVTEngine.CrashHandler", "MNVTEngine.Browser.Host")
$libraries = Get-ChildItem -Path (Join-Path $root "src/Engine") -Directory | Where-Object { $executables -notcontains $_.Name }
foreach ($library in $libraries) {
    Invoke-Dotnet pack (Join-Path $library.FullName "$($library.Name).csproj") -c $configuration --no-build -o $packages "-p:PackageVersion=$version" "-p:MnvtVersionSession=$session"
}
Invoke-Dotnet pack (Join-Path $root "src/Sdk/MNVTEngine.Sdk/MNVTEngine.Sdk.csproj") -c $configuration --no-build -o $packages "-p:PackageVersion=$version" "-p:MnvtVersionSession=$session"

# The game host's startup project, built per game from the packages (MnvtEnginePackages; see its csproj).
$gameHost = Join-Path $engine "GameHost"
New-Item -ItemType Directory -Force -Path $gameHost | Out-Null
Get-ChildItem -Path (Join-Path $root "src/Engine/MNVTEngine.GameHost") -File | Where-Object { $_.Extension -in ".cs", ".csproj" } | Copy-Item -Destination $gameHost
Set-Content -Encoding utf8 -Path (Join-Path $engine "Directory.Build.props") -Value @"
<Project>
  <!-- An installed MNVTEngine $($info.version): the game host builds from the engine's packages in Packages/. -->
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <MnvtEnginePackages>true</MnvtEnginePackages>
    <Version>$version</Version>
  </PropertyGroup>
</Project>
"@
$engineVersions = ($libraries | ForEach-Object { "    <PackageVersion Include=`"$($_.Name)`" Version=`"$version`" />" }) -join "`n"
$centralVersions = (Get-Content (Join-Path $root "Directory.Packages.props") -Raw) -replace '</Project>\s*$', "  <ItemGroup>`n    <!-- The installed engine's own packages (Packages/). -->`n$engineVersions`n  </ItemGroup>`n</Project>`n"
Set-Content -Encoding utf8 -Path (Join-Path $engine "Directory.Packages.props") -Value $centralVersions
Set-Content -Encoding utf8 -Path (Join-Path $engine "NuGet.config") -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="MNVTEngine" value="Packages" />
  </packageSources>
</configuration>
"@

# Executables that games ship as they are (GamePackager copies them into each game).
$crashHandler = Join-Path $root "src/Engine/MNVTEngine.CrashHandler/MNVTEngine.CrashHandler.csproj"
$common = @("-c", $configuration, "-r", "win-x64", "--self-contained", "false", "-p:DebugType=none", "-p:Deterministic=true", "-p:ContinuousIntegrationBuild=true", "-p:MnvtVersionSession=$session")
Invoke-Dotnet publish $crashHandler @common -o (Join-Path $engine "CrashHandler/jit") --artifacts-path (Join-Path $work "crash-jit")
Invoke-Dotnet publish $crashHandler @common -o (Join-Path $engine "CrashHandler/aot") --artifacts-path (Join-Path $work "crash-aot") "-p:PublishAot=true" "-p:TreatWarningsAsErrors=false"
Invoke-Dotnet publish (Join-Path $root "src/Engine/MNVTEngine.Browser.Host/MNVTEngine.Browser.Host.csproj") @common -o (Join-Path $engine "BrowserHost") --artifacts-path (Join-Path $work "browser") "-p:MnvtPrivateRuntime=true"
Get-ChildItem -Path (Join-Path $engine "CrashHandler"), (Join-Path $engine "BrowserHost") -Recurse -Filter *.pdb | Remove-Item -Force

Copy-Item -Recurse (Join-Path $root "templates") (Join-Path $stage "templates")
Copy-Item (Join-Path $root "LICENSE") $stage
Set-Content -Encoding utf8 -Path (Join-Path $stage "README.md") -Value @"
# MNVTEngine $($info.version)

- ``Editor\MNVTEngine.Editor.exe``: the editor (Project Hub without a project).
- ``CLI\mnvt.exe``: the command line (``mnvt new``, ``mnvt build``, ``mnvt run``); add ``CLI`` to PATH to use it anywhere.

Needs the .NET 11 SDK: game scripts and the game executable are compiled when you play and export. The engine itself
ships prebuilt in ``Engine`` (no sources).
"@
if (Test-Path $work) { Remove-Item -Recurse -Force $work }

Write-Host "Installed engine assembled in $stage"
if ($Zip) {
    $archive = Join-Path $out "$name.zip"
    if (Test-Path $archive) { Remove-Item -Force $archive }
    & "$env:SystemRoot/System32/tar.exe" -a -c -f $archive -C $out $name # Windows bsdtar (zip support), not Git's GNU tar
    if ($LASTEXITCODE -ne 0) { throw "Zipping $stage failed." }
    Write-Host "  Zip: $archive"
}
