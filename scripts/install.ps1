# Windows counterpart of install.sh: same steps, same invariants — dry run by default,
# nothing edited without a backup, optional steps report through a tri-state instead of
# aborting a finished install, and the staged binary is verified from the prefix before
# it becomes the installed one. Where the two differ it is because Windows differs:
# PATH is the user environment value rather than an rc file, and the prefix defaults to
# a directory of engram's own under LocalAppData so the PATH entry is clean.
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Prefix = "",
    [string]$Binary = "",
    [string]$SdkDir = "",
    [string]$DotnetInstall = "",
    [switch]$NoPath,
    [switch]$WithPlugin,
    [switch]$GrantPermissions,
    [switch]$NoGrantPermissions,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

function Show-Usage {
    Write-Output @'
Usage: scripts/install.ps1 [options]
  -Apply               Actually perform the installation (default is a dry run)
  -Prefix DIR          Install directory (default: $env:LOCALAPPDATA\Programs\engram)
  -Binary PATH         Install this prebuilt binary instead of building one
  -SdkDir DIR          Where a bootstrapped .NET SDK lives (default: <repo>\.dotnet)
  -DotnetInstall PATH  Use this local copy of Microsoft's dotnet-install.ps1 instead of
                       downloading it (air-gapped machines)
  -NoPath              Do not modify the user PATH
  -WithPlugin          Also register the Claude Code marketplace and install the plugin
  -GrantPermissions    Allow Claude Code to call Engram's memory tools without prompting
  -NoGrantPermissions  Never grant them, and do not ask
  -Help                Show usage

No .NET SDK is required up front: when none of the right version is found, one is
downloaded privately into the SDK directory, and nothing outside it is touched.
'@
}

if ($Help) {
    Show-Usage
    exit 0
}

# Windows PowerShell 5.1 has no $IsWindows, and only ever runs on Windows; treating
# undefined as Windows is what keeps this script honest on both hosts.
$onWindows = if ($PSVersionTable.PSVersion.Major -ge 6) { $IsWindows } else { $true }
if (-not $onWindows) {
    [Console]::Error.WriteLine('install.ps1 is the Windows installer; on this platform use scripts/install.sh')
    exit 1
}

function Say([string]$Message) { Write-Output $Message }
function Would([string]$Message) { Write-Output "would: $Message" }
# Write-Error under ErrorActionPreference=Stop throws before any exit after it runs;
# this is what makes a failure's exit code deterministic instead of host-dependent.
function Fail([string]$Message) { [Console]::Error.WriteLine($Message); exit 1 }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrEmpty($Prefix)) { $Prefix = Join-Path $env:LOCALAPPDATA 'Programs\engram' }
if ([string]::IsNullOrEmpty($SdkDir)) { $SdkDir = Join-Path $repoRoot '.dotnet' }
if ($DotnetInstall -and -not (Test-Path -LiteralPath $DotnetInstall -PathType Leaf)) {
    [Console]::Error.WriteLine("-DotnetInstall path not found: $DotnetInstall")
    exit 1
}
$target = Join-Path $Prefix 'engram.exe'

$cleanupDirs = [System.Collections.Generic.List[string]]::new()
function New-StagingDirectory {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("engram-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $cleanupDirs.Add($dir)
    return $dir
}

function Invoke-EngramInit([string]$Exe, [string]$EngramHome) {
    $old = $env:ENGRAM_HOME
    try {
        $env:ENGRAM_HOME = $EngramHome
        & $Exe init *> $null
        return $LASTEXITCODE
    }
    catch {
        return 1
    }
    finally {
        if ($null -eq $old) { Remove-Item Env:ENGRAM_HOME -ErrorAction SilentlyContinue } else { $env:ENGRAM_HOME = $old }
    }
}

function Test-Net10Sdk([string]$DotnetExe) {
    try {
        $sdks = & $DotnetExe --list-sdks 2>$null
        return ($LASTEXITCODE -eq 0) -and ($null -ne ($sdks | Where-Object { $_ -match '^10\.' }))
    }
    catch {
        return $false
    }
}

try {

# --- 1. Preflight ---

$dotnetCmd = ''
$bootstrapSdk = $false
$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

if (-not $Binary) {
    Say "Detected runtime identifier: $rid"

    # AOT publish drives MSVC's linker, which no SDK carries. Fatal under -Apply only
    # when no Visual Studio installation exists at all; an installation whose component
    # list does not name the C++ tools gets a warning either way, because that query
    # can miss a toolchain that is genuinely present and a false block is worse here
    # than a late build failure.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $toolchainProblem = ''
    $toolchainFatal = $false
    if (-not (Test-Path -LiteralPath $vswhere)) {
        $toolchainProblem = 'no Visual Studio installer found; building needs the "Desktop development with C++" workload of Visual Studio 2022 Build Tools (https://visualstudio.microsoft.com/downloads/)'
        $toolchainFatal = $true
    }
    else {
        $any = & $vswhere -latest -products * -property installationPath 2>$null
        if (-not $any) {
            $toolchainProblem = 'no Visual Studio installation found; building needs the "Desktop development with C++" workload of Visual Studio 2022 Build Tools'
            $toolchainFatal = $true
        }
        else {
            $vc = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
            if (-not $vc) {
                $toolchainProblem = 'Visual Studio is installed but the C++ build tools component was not detected; if the build fails, add the "Desktop development with C++" workload'
            }
        }
    }
    if ($toolchainProblem) {
        if ($Apply -and $toolchainFatal) {
            Fail "error: $toolchainProblem"
        }
        Say "warning: $toolchainProblem"
    }

    $pathDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    $localDotnet = Join-Path $SdkDir 'dotnet.exe'
    if ($pathDotnet -and (Test-Net10Sdk $pathDotnet.Source)) {
        $dotnetCmd = $pathDotnet.Source
    }
    elseif ((Test-Path -LiteralPath $localDotnet) -and (Test-Net10Sdk $localDotnet)) {
        $dotnetCmd = $localDotnet
        Say "Using the .NET SDK previously bootstrapped into $SdkDir"
    }
    else {
        $bootstrapSdk = $true
        $dotnetCmd = $localDotnet
    }
}
elseif (-not (Test-Path -LiteralPath $Binary -PathType Leaf)) {
    Fail "-Binary path not found: $Binary"
}

# --- 2. Stop a running daemon before replacing the binary ---

if (Test-Path -LiteralPath $target -PathType Leaf) {
    if ($Apply) {
        Say "Stopping existing daemon at $target ..."
        & $target stop 2>$null | Out-Null
    }
    else {
        Would "stop the existing daemon at $target (ignoring failure)"
    }
}

# --- 3. Bootstrap a .NET SDK, when no usable one exists ---

# Private on purpose: -InstallDir plus -NoPath means nothing outside $SdkDir is created
# or edited, so there is nothing here for uninstall to undo and nothing that can fight
# an SDK the user installs later — the PATH one wins the next run's resolution the
# moment it exists.
if (-not $Binary -and $bootstrapSdk) {
    if ($Apply) {
        Say "No .NET 10 SDK found; installing one privately into $SdkDir (a few hundred MB; PATH is not touched) ..."
        if ($DotnetInstall) {
            $installScript = $DotnetInstall
        }
        else {
            $installScript = Join-Path (New-StagingDirectory) 'dotnet-install.ps1'
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installScript -UseBasicParsing
        }
        # A nested shell with -ExecutionPolicy Bypass, because a script this one just
        # downloaded is exactly what a restrictive policy exists to block, and asking
        # the user to loosen their policy machine-wide is not an acceptable step 3.
        $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
        & $shell -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel '10.0' -InstallDir $SdkDir -NoPath
        if ($LASTEXITCODE -ne 0) {
            Fail "error: the .NET SDK bootstrap exited $LASTEXITCODE"
        }
        if (-not (Test-Net10Sdk $dotnetCmd)) {
            Fail "error: the SDK bootstrap finished but $dotnetCmd reports no .NET 10 SDK"
        }
    }
    else {
        Would "download dotnet-install.ps1 and install the .NET 10 SDK into $SdkDir (private to that directory; no PATH changes)"
    }
}

# --- 4. Build, unless -Binary was given ---

if ($Binary) {
    $binaryPath = $Binary
    Say "Using prebuilt binary: $binaryPath"
}
elseif ($Apply) {
    $stagingDir = New-StagingDirectory
    Say "Building engram for $rid into $stagingDir ..."
    $env:DOTNET_NOLOGO = '1'
    & $dotnetCmd publish (Join-Path $repoRoot 'src\Engram.Cli') -c Release -r $rid -o $stagingDir
    if ($LASTEXITCODE -ne 0) {
        Fail "error: dotnet publish exited $LASTEXITCODE"
    }

    $binaryPath = Join-Path $stagingDir 'engram.exe'
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
        Fail "error: expected published binary not found at $binaryPath"
    }

    Say "Removing debug symbols from $stagingDir ..."
    Get-ChildItem -LiteralPath $stagingDir -Filter '*.pdb' -File | Remove-Item
}
else {
    Would "$dotnetCmd publish $repoRoot\src\Engram.Cli -c Release -r $rid -o <temp staging dir>"
    Would "remove .pdb files from the staging dir"
    $binaryPath = '<built binary>'
}

# The name is fixed rather than discovered by globbing the source directory: a bin
# directory should receive exactly the files this installer meant to put there, and
# uninstall has to be able to name what it removes.
$sidecarName = 'e_sqlite3.dll'
$sidecarSource = Join-Path (Split-Path -Parent $binaryPath) $sidecarName
$sidecarTarget = Join-Path $Prefix $sidecarName

# llama.cpp's natives are the one part of the publish that keeps its runtimes/ tree, and
# LLamaSharp finds them by that layout relative to the executable — replicated under the
# prefix exactly. Install records every file it copies in a manifest, and uninstall
# removes exactly that list.
$nativesSource = Join-Path (Split-Path -Parent $binaryPath) (Join-Path 'runtimes' (Join-Path $rid 'native'))
$nativesTarget = Join-Path $Prefix 'runtimes'
$nativesManifest = Join-Path $nativesTarget '.engram-manifest'

function Remove-ManifestFiles {
    if (-not (Test-Path -LiteralPath $nativesManifest -PathType Leaf)) { return }
    foreach ($rel in Get-Content -LiteralPath $nativesManifest) {
        if ([string]::IsNullOrWhiteSpace($rel) -or $rel.Contains('..') -or $rel.StartsWith('/') -or $rel.StartsWith('\')) { continue }
        Remove-Item -LiteralPath (Join-Path $nativesTarget $rel) -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $nativesManifest -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $nativesTarget) {
        Get-ChildItem -LiteralPath $nativesTarget -Recurse -Directory |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
            Remove-Item
        if (-not (Get-ChildItem -LiteralPath $nativesTarget -Force -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $nativesTarget -Force -ErrorAction SilentlyContinue
        }
    }
}

# --- 5. Verify the built binary runs before installing it ---

# 'init' rather than 'home': home only prints paths, so it exits 0 on a binary that
# cannot open a database at all.
if ($Apply) {
    $verifyHome = New-StagingDirectory
    Say "Verifying the binary runs (ENGRAM_HOME=$verifyHome) ..."
    if ((Invoke-EngramInit $binaryPath $verifyHome) -ne 0) {
        Fail "error: '$binaryPath init' did not exit 0 - refusing to install an unverified binary"
    }
}
else {
    Would "verify the binary runs (engram init) against a throwaway ENGRAM_HOME"
}

# --- 6. Install ---

if ($Apply) {
    New-Item -ItemType Directory -Path $Prefix -Force | Out-Null

    if (Test-Path -LiteralPath $sidecarSource -PathType Leaf) {
        $tmpSidecar = "$sidecarTarget.new-$PID"
        Copy-Item -LiteralPath $sidecarSource -Destination $tmpSidecar
        Move-Item -LiteralPath $tmpSidecar -Destination $sidecarTarget -Force
        Say "Installed $sidecarTarget"
    }

    if (Test-Path -LiteralPath $nativesSource -PathType Container) {
        # A reinstall clears what the previous install recorded before copying, so a
        # native that stopped shipping does not linger and get loaded over its successor.
        Remove-ManifestFiles
        $nativeDest = Join-Path $nativesTarget (Join-Path $rid 'native')
        New-Item -ItemType Directory -Path $nativeDest -Force | Out-Null
        Copy-Item -Path (Join-Path $nativesSource '*') -Destination $nativeDest -Recurse -Force
        $manifestLines = Get-ChildItem -LiteralPath $nativeDest -Recurse -File | ForEach-Object {
            $_.FullName.Substring($nativesTarget.Length + 1).Replace('\', '/')
        }
        Set-Content -LiteralPath $nativesManifest -Value $manifestLines
        Say "Installed $nativeDest ($($manifestLines.Count) files, recorded for uninstall)"
    }

    # Copy to a sibling then Move-Item over the destination, so a failed in-prefix
    # verification leaves the previous binary untouched.
    $tmpTarget = "$target.new-$PID"
    Copy-Item -LiteralPath $binaryPath -Destination $tmpTarget

    # Verify the copy, from inside the prefix, before it becomes the target: step 5 ran
    # the binary where it was built, with every native dependency beside it, so it
    # passes whether or not the install carried those across.
    $installedHome = New-StagingDirectory
    if ((Invoke-EngramInit $tmpTarget $installedHome) -ne 0) {
        Remove-Item -LiteralPath $tmpTarget -Force
        Fail "error: the staged binary could not initialise a home from $Prefix - a native dependency did not survive the install; leaving $target as it was"
    }

    Move-Item -LiteralPath $tmpTarget -Destination $target -Force
    Say "Installed $target"
}
else {
    Would "create $Prefix"
    if (Test-Path -LiteralPath $sidecarSource -PathType Leaf) {
        Would "install $sidecarName to $sidecarTarget (via staged replace)"
    }
    if (Test-Path -LiteralPath $nativesSource -PathType Container) {
        Would "install runtimes\$rid\native (llama.cpp) to $nativesTarget, recording a manifest for uninstall"
    }
    Would "install binary to $target (via staged replace)"
    Would "run the staged binary from $Prefix against a throwaway ENGRAM_HOME, and abort the install if it cannot open a database"
}

# --- 7. PATH ---

$pathChanged = $false
$pathBackup = ''

if ($NoPath) {
    Say "Skipping PATH setup (-NoPath)."
}
else {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $userPath) { $userPath = '' }
    $entries = $userPath.Split(';') | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\') }
    if ($entries -contains $Prefix.TrimEnd('\')) {
        Say "$Prefix is already on the user PATH; nothing to change."
    }
    elseif ($Apply) {
        # The previous value is written down before the edit, because a PATH this
        # installer mangles is a PATH the user has to be able to put back without
        # remembering what it said.
        $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
        New-Item -ItemType Directory -Path $Prefix -Force | Out-Null
        $pathBackup = Join-Path $Prefix "path.engram-backup-$stamp.txt"
        Set-Content -LiteralPath $pathBackup -Value $userPath
        $newPath = if ($userPath) { "$userPath;$Prefix" } else { $Prefix }
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        Say "Added $Prefix to the user PATH (previous value saved to $pathBackup)"
        $pathChanged = $true
    }
    else {
        Would "save the current user PATH to $Prefix\path.engram-backup-<UTC timestamp>.txt"
        Would "append $Prefix to the user PATH"
    }
}

# --- 8. Initialise the home ---

if ($Apply) {
    Say "Initialising the Engram home ..."
    & $target init
    if ($LASTEXITCODE -ne 0) {
        Fail "error: '$target init' exited $LASTEXITCODE"
    }
}
else {
    Would "run $target init to initialise the Engram home (idempotent, will not overwrite an existing config)"
}

# --- 9. -WithPlugin ---

# installed | no-claude | failed. Only read when -WithPlugin was given. A failure here
# must not abort: by this point the binary, the PATH entry and the home are all durable,
# and the summary still owes the user an account of what happened.
$pluginResult = 'no-claude'
if ($WithPlugin) {
    if ($Apply) {
        $claude = Get-Command claude -ErrorAction SilentlyContinue
        if ($claude) {
            Say "Registering the Claude Code marketplace and installing the plugin ..."
            $marketOk = $false
            try {
                & $claude.Source plugin marketplace add $repoRoot
                $marketOk = ($LASTEXITCODE -eq 0)
                if ($marketOk) {
                    & $claude.Source plugin install engram@engram
                    if ($LASTEXITCODE -eq 0) { $pluginResult = 'installed' } else { $pluginResult = 'failed' }
                }
                else {
                    $pluginResult = 'failed'
                }
            }
            catch {
                $pluginResult = 'failed'
            }
            if ($pluginResult -eq 'failed') {
                Say "the plugin step failed; run these commands yourself to finish it:"
                Say "  claude plugin marketplace add $repoRoot"
                Say "  claude plugin install engram@engram"
            }
        }
        else {
            Say "claude is not on PATH; run these commands yourself to install the plugin:"
            Say "  claude plugin marketplace add $repoRoot"
            Say "  claude plugin install engram@engram"
        }
    }
    else {
        Would "claude plugin marketplace add $repoRoot"
        Would "claude plugin install engram@engram"
    }
}

# --- 10. MCP tool permissions ---

$grantResult = 'skipped'
if (-not $NoGrantPermissions) {
    if (-not $Apply) {
        Would "offer to add Engram's memory tools to permissions.allow in Claude Code's user settings"
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            & $target permissions 2>$null
        }
    }
    elseif ($GrantPermissions) {
        & $target permissions --apply
        if ($LASTEXITCODE -eq 0) { $grantResult = 'granted' }
    }
    elseif (-not [Console]::IsInputRedirected) {
        Write-Output ''
        & $target permissions 2>$null
        $reply = Read-Host 'Grant these now? [y/N]'
        if ($reply -match '^[yY]') {
            & $target permissions --apply
            if ($LASTEXITCODE -eq 0) { $grantResult = 'granted' } else { $grantResult = 'declined' }
        }
        else {
            $grantResult = 'declined'
            Say "Left Claude Code's settings alone. Grant later with: engram permissions --apply"
        }
    }
    else {
        $grantResult = 'declined'
        Say "Not a terminal, so not asking about tool permissions. Grant with: engram permissions --apply"
    }
}

# --- 11. Summary ---

Write-Output ''
if ($Apply) {
    Write-Output 'Summary:'
    Write-Output "  Installed engram to: $target"
    if ($NoPath) {
        Write-Output '  PATH: not modified (-NoPath)'
    }
    elseif ($pathChanged) {
        Write-Output "  PATH: added $Prefix to the user PATH (backup: $pathBackup)"
    }
    else {
        Write-Output "  PATH: $Prefix was already on the user PATH; nothing changed"
    }
    if ($WithPlugin) {
        switch ($pluginResult) {
            'installed' { Write-Output '  Claude Code plugin: registered and installed' }
            'no-claude' { Write-Output '  Claude Code plugin: NOT installed (claude was not on PATH); run the commands printed above' }
            'failed' { Write-Output '  Claude Code plugin: NOT installed (claude reported an error); run the commands printed above' }
        }
    }
    switch ($grantResult) {
        'granted' { Write-Output '  MCP tool permissions: granted (recall, remember, digest, status)' }
        'declined' { Write-Output "  MCP tool permissions: not granted; run 'engram permissions --apply' to change that" }
        'skipped' { Write-Output '  MCP tool permissions: not touched (-NoGrantPermissions)' }
    }
    Write-Output ''
    Write-Output 'Next steps:'
    if ($pathChanged) {
        Write-Output '  Open a new terminal so the PATH change is picked up'
    }
    if ($WithPlugin -and $pluginResult -eq 'installed') {
        Write-Output '  In a running Claude Code session, run: /reload-plugins'
    }
    if ($grantResult -eq 'granted') {
        Write-Output '  Nothing to restart: Claude Code watches its settings file and reloads permissions'
    }
}
else {
    Write-Output 'Dry run only - nothing was changed. Re-run with -Apply to perform this installation.'
}

exit 0

}
finally {
    foreach ($dir in $cleanupDirs) {
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
