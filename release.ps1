<#
.SYNOPSIS
    Builds SAURUS, packages it as an installer, and publishes it to GitHub Releases.

.DESCRIPTION
    One command from your machine puts a new version in front of everyone running it.
    Their copy checks GitHub, downloads in the background, and applies the update the next
    time they quit and start it.

    Self-contained by default: the .NET runtime is bundled, so a friend downloads one file
    and runs it with nothing installed. That makes the first download large (~80 MB) and
    every update afterwards small, because Velopack ships deltas.

.PARAMETER Version
    Version to publish, e.g. 1.0.1. Must be higher than the last release or nobody updates.

.PARAMETER Repo
    GitHub repository URL. Defaults to whatever `origin` points at.

.PARAMETER Token
    GitHub personal access token with `repo` scope. Falls back to $env:GITHUB_TOKEN.

.PARAMETER LocalOnly
    Build and package but do not upload. Use this to test the installer yourself first.

.EXAMPLE
    .\release.ps1 -Version 1.0.1
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repo,
    [string]$Token = $env:GITHUB_TOKEN,
    [switch]$LocalOnly,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Saurus\Saurus.csproj'
$publishDir = Join-Path $root 'publish'
$releaseDir = Join-Path $root 'releases'

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# --- prerequisites --------------------------------------------------------
# .Source, not the CommandInfo itself: Test-Path against a CommandInfo stringifies it to the
# command name rather than its path, so this silently claimed dotnet was missing.
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCmd) { $dotnetCmd.Source } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found. Install the .NET 10 SDK." }

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Step "Installing the Velopack CLI (one time)"
    & $dotnet tool install -g vpk
    $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
}

if (-not $Repo) {
    $Repo = (git -C $root remote get-url origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $Repo) {
        throw "No -Repo given and no git 'origin' remote found. Pass -Repo https://github.com/you/saurus"
    }
    $Repo = $Repo -replace '\.git$', ''
}

Step "Publishing SAURUS $Version to $Repo"

# --- build ----------------------------------------------------------------
Step "Building (self-contained, win-x64)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& $dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# --- package --------------------------------------------------------------
# Start from a clean local releases directory, then pull the published ones back down.
# Velopack builds delta packages by diffing against the previous release, so it needs those
# present - and a stale local copy of the version being built makes it refuse to pack at all.
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDir | Out-Null

if (-not $LocalOnly) {
    Step "Fetching published releases (for delta generation)"
    & vpk download github --repoUrl $Repo --outputDir $releaseDir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "   none found - this will be a full release" -ForegroundColor DarkGray
    }
}

Step "Packaging installer"
$vpkArgs = @(
    'pack',
    '--packId', 'Saurus',
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'Saurus.exe',
    '--packTitle', 'SAURUS',
    '--packAuthors', 'SAURUS',
    '--outputDir', $releaseDir
)
& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "Packaging failed." }

if ($LocalOnly) {
    Step "Done (local only)"
    Write-Host "Installer: $(Join-Path $releaseDir 'Saurus-win-Setup.exe')"
    Write-Host "Run it yourself to test before publishing."
    return
}

# --- upload ---------------------------------------------------------------
# Prefer the GitHub CLI. It reads the credential you created with `gh auth login` out of
# Windows Credential Manager, so no token has to be typed, pasted into a script, or left
# sitting in an environment variable. Falling back to a token only if gh is absent.
$useGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)

if ($useGh) {
    Step "Uploading to GitHub Releases (via gh)"

    gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "gh is installed but not logged in. Run: gh auth login" }

    $assets = Get-ChildItem $releaseDir -File |
              Where-Object { $_.Name -notlike '*.nupkg' -or $_.Name -like '*full.nupkg' }

    $notes = "SAURUS $Version"
    $ghArgs = @('release', 'create', "v$Version", '--title', "SAURUS $Version", '--notes', $notes)
    if ($Prerelease) { $ghArgs += '--prerelease' }
    $ghArgs += ($assets | ForEach-Object { $_.FullName })

    # $ghArgs already begins with 'release','create' - splatting after naming them again
    # produced `gh release create release create ...`.
    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}
else {
    if (-not $Token) {
        throw "Neither gh nor a token is available. Either install the GitHub CLI and run " +
              "``gh auth login``, or set `$env:GITHUB_TOKEN (needs 'repo' scope)."
    }

    Step "Uploading to GitHub Releases (via vpk)"
    $uploadArgs = @(
        'upload', 'github',
        '--repoUrl', $Repo,
        '--token', $Token,
        '--outputDir', $releaseDir,
        '--tag', "v$Version",
        '--releaseName', "SAURUS $Version",
        '--publish'
    )
    if ($Prerelease) { $uploadArgs += '--pre' }

    & vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) { throw "Upload failed." }
}

Step "Published $Version"
Write-Host "Everyone running SAURUS will pick this up within 6 hours, or immediately via"
Write-Host "the tray menu's 'Check for updates'. It installs when they next restart the app."
