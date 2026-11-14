<#
.SYNOPSIS
Writes or refreshes the author header at the top of every C# file in the repository.

.DESCRIPTION
    // SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
    // SPDX-License-Identifier: MIT
    // Part of MNVTEngine. Last modified 2026-11-14.

SPDX lines follow the REUSE specification (https://reuse.software), which license scanners read.
The date is the day the file last changed: today for files with uncommitted changes (other than the header itself),
otherwise the date of the file's last commit. Commits whose message starts with "Update file headers" are ignored,
so committing the headers does not reset every date. Files under templates/ are skipped: they become users' games.
Run it again at any time; files whose header is already correct are not touched.

  powershell -ExecutionPolicy Bypass -File scripts\Update-Headers.ps1 [-Check]

-Check changes nothing and exits with 1 when a header is missing or out of date (for CI).
#>
param([switch]$Check)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8 # git output (paths, file contents) is UTF-8
$root = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Set-Location $root
$today = Get-Date -Format 'yyyy-MM-dd'
$headerLine = '^///? (SPDX-|Part of MNVTEngine\.|@(author|project|license|date) )' # current and earlier header formats
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Header([string]$date, [string]$newline) {
    $lines = @(
        "// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>",
        '// SPDX-License-Identifier: MIT',
        "// Part of MNVTEngine. Last modified $date.")
    ($lines -join $newline) + $newline + $newline
}

# Text without the header, to tell real changes from header-only ones.
function Body([string]$text) {
    $lines = $text -split "`r?`n"
    $i = 0
    while ($i -lt $lines.Count -and $lines[$i] -match $headerLine) { $i++ }
    if ($i -gt 0 -and $i -lt $lines.Count -and $lines[$i] -eq '') { $i++ }
    ($lines[$i..($lines.Count - 1)] -join "`n")
}

$files = @(git ls-files --cached --others --exclude-standard -- '*.cs') | Where-Object { $_ -notmatch '^templates/' -and $_ -notmatch '(^|/)(bin|obj)/' -and $_ -notmatch '\.g\.cs$' }

# Last commit date per file, newest first, in one pass over the history.
$committed = @{}
$date = $null
foreach ($line in git log --format='@%cs' --name-only --invert-grep --grep='^Update file headers' -- '*.cs') {
    if ($line.StartsWith('@')) { $date = $line.Substring(1) }
    elseif ($line -and -not $committed.ContainsKey($line)) { $committed[$line] = $date }
}

$changed = @(git status --porcelain --untracked-files=all -- '*.cs' | ForEach-Object { $_.Substring(3).Trim('"') -replace '^.* -> ', '' })
$stale = 0
foreach ($file in $files) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $file))
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = $utf8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

    $fileDate = $committed[$file]
    if (-not $fileDate) {
        $fileDate = $today # not committed yet
    } elseif ($changed -contains $file -and (Body ((git show "HEAD:$file") -join "`n")).TrimEnd() -cne (Body $text).TrimEnd()) {
        $fileDate = $today # changed since its last commit
    }

    $body = (Body $text) -replace "`n", $newline
    $updated = (Header $fileDate $newline) + $body
    if ($updated -ceq $text) { continue }

    $stale++
    if ($Check) { Write-Output "header out of date: $file"; continue }
    $out = $utf8.GetBytes($updated)
    if ($bom) { $out = [byte[]](0xEF, 0xBB, 0xBF) + $out }
    [IO.File]::WriteAllBytes((Join-Path $root $file), $out)
}

if ($Check) {
    if ($stale -gt 0) { Write-Output "$stale of $($files.Count) files need scripts\Update-Headers.ps1"; exit 1 }
    Write-Output "All $($files.Count) headers are up to date."
} else {
    Write-Output "Updated $stale of $($files.Count) files."
}
