# Windows counterpart of uninstall.sh: dry run by default, backups before edits, and it
# removes only what install.ps1 can prove it created — the named binary and sidecar, the
# natives listed in the install manifest, and the prefix entry in the user PATH.
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Prefix = "",
    [switch]$Purge,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Show-Usage {
    Write-Output @'
Usage: scripts/uninstall.ps1 [options]
  -Apply       Actually perform the uninstall (default is a dry run)
  -Prefix DIR  Install directory (default: $env:LOCALAPPDATA\Programs\engram)
  -Purge       ALSO delete the Engram home (%USERPROFILE%\.engram) and everything in it
  -Help        Show usage
'@
}

if ($Help) {
    Show-Usage
    exit 0
}

$onWindows = if ($PSVersionTable.PSVersion.Major -ge 6) { $IsWindows } else { $true }
if (-not $onWindows) {
    [Console]::Error.WriteLine('uninstall.ps1 is the Windows uninstaller; on this platform use scripts/uninstall.sh')
    exit 1
}

function Say([string]$Message) { Write-Output $Message }
function Would([string]$Message) { Write-Output "would: $Message" }

if ([string]::IsNullOrEmpty($Prefix)) { $Prefix = Join-Path $env:LOCALAPPDATA 'Programs\engram' }
$target = Join-Path $Prefix 'engram.exe'

# --- Resolve the Engram home path up front: it informs both -Purge and the summary,
#     and asking the binary itself (which honors ENGRAM_HOME) beats reimplementing its
#     resolution here. ---

$engramHome = ''
if (Test-Path -LiteralPath $target -PathType Leaf) {
    try {
        $homeOutput = & $target home 2>$null
        $rootLine = $homeOutput | Where-Object { $_ -like 'Root=*' } | Select-Object -First 1
        if ($rootLine) { $engramHome = $rootLine.Substring('Root='.Length) }
    }
    catch { }
}
if (-not $engramHome) {
    $engramHome = if ($env:ENGRAM_HOME) { $env:ENGRAM_HOME } else { Join-Path $env:USERPROFILE '.engram' }
}

# --- Stop the daemon if the binary is present ---

if (Test-Path -LiteralPath $target -PathType Leaf) {
    if ($Apply) {
        Say "Stopping engram daemon at $target ..."
        try { & $target stop 2>$null | Out-Null } catch { }
    }
    else {
        Would "stop the engram daemon at $target (ignoring failure)"
    }
}

# --- Remove the Claude Code plugin and marketplace, if claude is present ---

$claude = Get-Command claude -ErrorAction SilentlyContinue
if ($claude) {
    if ($Apply) {
        Say "Removing the Claude Code plugin and marketplace (tolerating absence) ..."
        try { & $claude.Source plugin uninstall engram -y 2>$null } catch { }
        try { & $claude.Source plugin marketplace remove engram 2>$null } catch { }
    }
    else {
        Would "claude plugin uninstall engram -y (tolerating absence)"
        Would "claude plugin marketplace remove engram (tolerating absence)"
    }
}
else {
    Say "claude is not on PATH; skipping plugin/marketplace removal."
}

# --- Take back the MCP tool permissions we granted ---

# This has to happen while the binary and the home are both still here: the record of
# which permissions.allow entries were ours lives in the home.
$permissionsResult = 'none'
if (Test-Path -LiteralPath $target -PathType Leaf) {
    if ($Apply) {
        try {
            $revokeOutput = & $target permissions --remove --apply 2>&1
            $revokeOutput | ForEach-Object { Write-Output $_ }
            if (($revokeOutput | Out-String) -match 'Removed ') { $permissionsResult = 'removed' }
        }
        catch { }
    }
    else {
        Would "take back only the permissions.allow entries Engram added to Claude Code's settings"
        try { & $target permissions --remove 2>&1 } catch { }
    }
}

# --- Remove the installed binary ---

if (Test-Path -LiteralPath $target -PathType Leaf) {
    if ($Apply) {
        Remove-Item -LiteralPath $target -Force
        Say "Removed $target"
    }
    else {
        Would "remove $target"
    }
}
else {
    Say "$target does not exist; nothing to remove."
}

# Named exactly, never globbed: removing whatever else happens to be in the directory
# is not this script's business.
$sidecarTarget = Join-Path $Prefix 'e_sqlite3.dll'
if (Test-Path -LiteralPath $sidecarTarget -PathType Leaf) {
    if ($Apply) {
        Remove-Item -LiteralPath $sidecarTarget -Force
        Say "Removed $sidecarTarget"
    }
    else {
        Would "remove $sidecarTarget"
    }
}

# --- Remove the llama.cpp natives the install recorded ---

# install.ps1 writes runtimes/.engram-manifest listing every file it copied, and this
# removes precisely that list. A file somebody else put under runtimes/ is not in the
# manifest and survives, along with any directory that holding it keeps non-empty.
$nativesRoot = Join-Path $Prefix 'runtimes'
$nativesManifest = Join-Path $nativesRoot '.engram-manifest'

if (Test-Path -LiteralPath $nativesManifest -PathType Leaf) {
    if ($Apply) {
        foreach ($rel in Get-Content -LiteralPath $nativesManifest) {
            if ([string]::IsNullOrWhiteSpace($rel) -or $rel.Contains('..') -or $rel.StartsWith('/') -or $rel.StartsWith('\')) { continue }
            Remove-Item -LiteralPath (Join-Path $nativesRoot $rel) -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $nativesManifest -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $nativesRoot) {
            Get-ChildItem -LiteralPath $nativesRoot -Recurse -Directory |
                Sort-Object { $_.FullName.Length } -Descending |
                Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
                Remove-Item
            if (-not (Get-ChildItem -LiteralPath $nativesRoot -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $nativesRoot -Force -ErrorAction SilentlyContinue
            }
        }
        Say "Removed the llama.cpp natives listed in the install manifest from $nativesRoot"
    }
    else {
        $count = @(Get-Content -LiteralPath $nativesManifest).Count
        Would "remove the $count llama.cpp native files listed in $nativesManifest, then prune emptied directories"
    }
}
elseif (Test-Path -LiteralPath $nativesRoot) {
    Say "Not touching ${nativesRoot}: no engram manifest there, so nothing in it is provably ours."
}

# --- Remove the prefix from the user PATH ---

$pathChanged = $false
$pathBackup = ''
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($null -eq $userPath) { $userPath = '' }
$trimmedPrefix = $Prefix.TrimEnd('\')
$entries = $userPath.Split(';') | Where-Object { $_ }
$ours = $entries | Where-Object { $_.TrimEnd('\') -eq $trimmedPrefix }

if ($ours) {
    if ($Apply) {
        # The backup lands in TEMP rather than the prefix, because the prefix is what
        # this script is in the middle of emptying.
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
        $pathBackup = Join-Path ([System.IO.Path]::GetTempPath()) "engram-path-backup-$stamp.txt"
        Set-Content -LiteralPath $pathBackup -Value $userPath
        $newPath = ($entries | Where-Object { $_.TrimEnd('\') -ne $trimmedPrefix }) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Say "Removed $Prefix from the user PATH (previous value saved to $pathBackup)"
        $pathChanged = $true
    }
    else {
        Would "save the current user PATH to a backup file in TEMP"
        Would "remove $Prefix from the user PATH"
    }
}

# --- -Purge ---

if ($Purge) {
    $suspicious = (-not $engramHome) -or ($engramHome -eq $env:USERPROFILE) -or ($engramHome.TrimEnd('\') -match '^[A-Za-z]:$')
    if ($suspicious) {
        [Console]::Error.WriteLine("error: refusing to purge suspicious Engram home path: '$engramHome'")
        exit 1
    }

    if (Test-Path -LiteralPath $engramHome -PathType Container) {
        $fileCount = @(Get-ChildItem -LiteralPath $engramHome -Recurse -File -Force).Count
        if ($Apply) {
            Say "Purging Engram home: $engramHome ($fileCount files)"
            Remove-Item -LiteralPath $engramHome -Recurse -Force
            Say "Deleted $engramHome"
        }
        else {
            Would "delete the Engram home at $engramHome ($fileCount files)"
        }
    }
    else {
        Say "-Purge given, but no Engram home found at $engramHome; nothing to delete."
    }
}

# --- Summary ---

Write-Output ''
if ($Apply) {
    Write-Output 'Summary:'
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Write-Output "  engram binary: still present at $target (removal may have failed)"
    }
    else {
        Write-Output "  engram binary: removed (or was already absent) from $target"
    }
    if ($pathChanged) {
        Write-Output "  PATH: removed $Prefix from the user PATH (backup: $pathBackup)"
    }
    else {
        Write-Output '  PATH: no engram entry found; nothing changed'
    }
    switch ($permissionsResult) {
        'removed' { Write-Output '  MCP tool permissions: removed the entries Engram added' }
        default { Write-Output "  MCP tool permissions: none of ours found; Claude Code's settings left alone" }
    }
    if ($Purge) {
        Write-Output "  Engram home: purged ($engramHome)"
    }
    else {
        Write-Output "  Engram home: left untouched. Nothing under $engramHome was deleted; pass -Purge to remove it."
    }
}
else {
    Write-Output 'Dry run only - nothing was changed. Re-run with -Apply to perform this uninstall.'
}

exit 0
