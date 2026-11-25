# SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
# SPDX-License-Identifier: MIT
#
# Publishes the editor as a clean, game-like layout: MNVTEngine.Editor.exe at the top with every library tucked into
# Dependencies/Managed (native libraries stay in runtimes/<rid>/native), instead of the flat pile a `dotnet build`
# produces. The editor resolves them through DependencyLayout, as an exported game does through HostLayout. Framework-dependent: it uses the installed
# .NET (the editor is a developer tool, not a shipped game).
#
#   scripts\Publish-Editor.ps1 [-Configuration Release] [-Output artifacts\editor]

param(
    [string]$Configuration = "Release",
    [string]$Output = "artifacts/editor"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Editor/MNVTEngine.Editor/MNVTEngine.Editor.csproj"
$out = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Portable (no RID): a RID publish duplicates the transitively-referenced versioning tool's apphost. The native
# libraries then sit under runtimes/<rid>/native, where they stay.
dotnet publish $project -c $Configuration -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$managed = Join-Path $out "Dependencies/Managed"
New-Item -ItemType Directory -Force -Path $managed | Out-Null

# Only the managed pile (the mess) moves to Dependencies/Managed; the two executables' entry assemblies stay by their
# .exe, and native libraries stay under runtimes/<rid>/native where Silk.NET's loaders look for them.
$keepAtRoot = @("MNVTEngine.Editor.dll", "MNVTCrashHandler.dll")

foreach ($dll in Get-ChildItem -Path $out -Filter *.dll -File) {
    if ($keepAtRoot -contains $dll.Name) { continue }

    $isManaged = $true
    try { [void][System.Reflection.AssemblyName]::GetAssemblyName($dll.FullName) } catch { $isManaged = $false }
    if (-not $isManaged) { continue } # a native library at the root: leave it there

    Move-Item -Force $dll.FullName (Join-Path $managed $dll.Name)
    foreach ($ext in ".pdb", ".xml") {
        $side = [IO.Path]::ChangeExtension($dll.FullName, $ext)
        if (Test-Path $side) { Move-Item -Force $side (Join-Path $managed ([IO.Path]::GetFileName($side))) }
    }
}

# Keep only the Windows native libraries (the editor runs on Windows); drop the other operating systems' copies.
$runtimes = Join-Path $out "runtimes"
if (Test-Path $runtimes) {
    Get-ChildItem -Path $runtimes -Directory | Where-Object { $_.Name -notlike "win*" } | Remove-Item -Recurse -Force
}

# Without a deps.json the host resolves the app's managed assemblies through DependencyLayout (and the crash handler
# through its own resolver); native libraries stay in runtimes/win-x64/native.
Get-ChildItem -Path $out -Filter *.deps.json -File | Remove-Item -Force

# The versioning build tool is dragged in transitively but is not a runtime dependency; drop it from the clean output.
Get-ChildItem -Path $out, $managed -Filter "MNVTEngine.Versioning.*" -File -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $out "MNVTEngine.Editor.exe"
Write-Host "Clean editor published to $out"
Write-Host "  Run: `"$exe`" <project>"
