# SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
# SPDX-License-Identifier: MIT
#
# Publishes the editor as a clean, game-like layout: MNVTEngine.Editor.exe at the top with every library tucked into
# Dependencies/Managed and Dependencies/Native, instead of the flat pile a `dotnet build` produces. The editor resolves
# them through DependencyLayout, as an exported game does through HostLayout. Framework-dependent: it uses the
# installed .NET (the editor is a developer tool, not a shipped game).
#
#   scripts\Publish-Editor.ps1 [-Configuration Release] [-Output artifacts\editor] [-NoBuild]
#
# -NoBuild publishes the existing build (e.g. after scripts\build.cmd Release), keeping its version stamp.

param(
    [string]$Configuration = "Release",
    [string]$Output = "artifacts/editor",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Editor/MNVTEngine.Editor/MNVTEngine.Editor.csproj"
$out = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Portable (no RID): a RID publish duplicates the transitively-referenced versioning tool's apphost. The native
# libraries then sit under runtimes/<rid>/native; the win-x64 ones move into Dependencies/Native below.
dotnet publish $project -c $Configuration -o $out $(if ($NoBuild) { "--no-build" })
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$managed = Join-Path $out "Dependencies/Managed"
New-Item -ItemType Directory -Force -Path $managed | Out-Null

# Only the managed pile (the mess) moves to Dependencies/Managed; the two executables' entry assemblies stay by their
# .exe. Native libraries at the root are left alone; the package ones come from runtimes/win-x64/native below.
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

# The editor runs as a 64-bit Windows process: its native libraries go to Dependencies/Native (DependencyLayout preloads
# them) and the other platforms' copies are dropped with the runtimes folder.
$native = Join-Path $out "Dependencies/Native"
$runtimes = Join-Path $out "runtimes"
New-Item -ItemType Directory -Force -Path $native | Out-Null
Get-ChildItem -Path (Join-Path $runtimes "win-x64/native") -File | Move-Item -Destination $native -Force
Remove-Item -Recurse -Force $runtimes

# The browser host (MNVTBrowserHost.exe, CEF) in an exported game's layout (GamePackager.PublishBrowserHostAsync):
# the exe next to the editor's, CefSharp in Dependencies/Managed, CEF in Dependencies/Native/Chromium (a folder of its
# own: DependencyLayout preloads only the top of Dependencies/Native). BrowserService finds it next to the editor.

# A C++/CLI (mixed-mode) assembly: its CLR header lacks the ILOnly flag, as GamePackager.IsMixedMode checks. Read by
# hand: Windows PowerShell has no System.Reflection.Metadata.
function Test-MixedMode([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    $optional = $pe + 24
    $directories = $optional + $(if ([BitConverter]::ToUInt16($b, $optional) -eq 0x20B) { 112 } else { 96 })
    $clr = [BitConverter]::ToUInt32($b, $directories + 14 * 8) # data directory 14: the CLR header
    $sections = $optional + [BitConverter]::ToUInt16($b, $pe + 20)
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($b, $pe + 6); $i++) {
        $s = $sections + 40 * $i
        $va = [BitConverter]::ToUInt32($b, $s + 12)
        if ($clr -ge $va -and $clr -lt $va + [BitConverter]::ToUInt32($b, $s + 8)) {
            $flags = [BitConverter]::ToUInt32($b, $clr - $va + [BitConverter]::ToUInt32($b, $s + 20) + 16)
            return ($flags -band 1) -eq 0 # COMIMAGE_FLAGS_ILONLY
        }
    }
    return $false
}

$browserHost = Join-Path ([IO.Path]::GetTempPath()) "mnvt-editor-browser-host"
if (Test-Path $browserHost) { Remove-Item -Recurse -Force $browserHost }
dotnet publish (Join-Path $root "src/Engine/MNVTEngine.Browser.Host/MNVTEngine.Browser.Host.csproj") -c $Configuration -o $browserHost $(if ($NoBuild) { "--no-build" })
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of the browser host failed." }

# CEF's Vulkan loader and SwiftShader stay out (a vulkan-1.dll there could stand in for the system's), as in games.
$browserExcluded = @("vulkan-1.dll", "vk_swiftshader.dll", "vk_swiftshader_icd.json", "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.dll", "CefSharp.BrowserSubprocess.runtimeconfig.json")
$chromium = Join-Path $native "Chromium"
foreach ($file in Get-ChildItem -Path $browserHost -File -Recurse) {
    $relative = $file.FullName.Substring($browserHost.TrimEnd('\', '/').Length + 1)
    $name = $file.Name
    if ($browserExcluded -contains $name -or $name -like "*.deps.json" -or $name -like "*.pdb" -or $name -like "*.xml") { continue }

    $isManaged = $true
    try { [void][System.Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { $isManaged = $false }
    if ($name -like "MNVTBrowserHost.*" -and $relative -notmatch '[\\/]') {
        $target = Join-Path $out $name
    }
    # CEF's files and CefSharp's C++/CLI assemblies, which need Ijwhost.dll beside them.
    elseif ($relative -match '[\\/]' -or $name -notlike "*.dll" -or -not $isManaged -or (Test-MixedMode $file.FullName)) {
        $target = Join-Path $chromium $relative
    }
    else {
        $target = Join-Path $managed $name
        if (Test-Path $target) { continue } # the editor ships it already (Newtonsoft.Json, the protocol)
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -Force $file.FullName $target
}
Remove-Item -Recurse -Force $browserHost

# Without a deps.json the host resolves the app's managed assemblies through DependencyLayout (and the crash handler
# through its own resolver).
Get-ChildItem -Path $out -Filter *.deps.json -File | Remove-Item -Force

# The versioning build tool is dragged in transitively but is not a runtime dependency; drop it from the clean output.
Get-ChildItem -Path $out, $managed -Filter "MNVTEngine.Versioning.*" -File -ErrorAction SilentlyContinue | Remove-Item -Force

$exe = Join-Path $out "MNVTEngine.Editor.exe"
Write-Host "Clean editor published to $out"
Write-Host "  Run: `"$exe`" <project>"
