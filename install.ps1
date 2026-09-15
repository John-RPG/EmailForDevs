<#
.SYNOPSIS
    Installs or updates eeeMail.

.DESCRIPTION
    Downloads the latest release from GitHub, verifies it against the SHA-256
    digest the GitHub API publishes for the asset, unpacks it to a per-user
    location, and adds a Start Menu shortcut.

    The digest check is the point of this script rather than an extra. eeeMail
    ships as an unsigned binary, so Windows offers no guarantee about what it is;
    checking the hash at least proves the bytes that landed are the bytes GitHub
    published for that release. A mismatch deletes the download and stops.

    No administrator rights are needed: everything goes under the user profile.

.PARAMETER InstallDirectory
    Where to install. Defaults to %LOCALAPPDATA%\Programs\eeeMail.

.PARAMETER Version
    A release tag such as 'v0.1.1'. Defaults to the latest release.

.PARAMETER Repository
    The owner/repo to install from. Point this at a fork only if you trust it as
    much as the original: it decides what code runs on this machine.

.PARAMETER NoShortcut
    Skip creating the Start Menu shortcut.

.PARAMETER NoLaunch
    Install without starting the app afterwards.

.PARAMETER Uninstall
    Remove eeeMail: the install directory and the Start Menu shortcut. Mail
    databases and settings live elsewhere and are left alone.

.EXAMPLE
    irm https://raw.githubusercontent.com/John-RPG/EmailForDevs/master/install.ps1 | iex

.EXAMPLE
    # Passing arguments needs a scriptblock. Piping to iex cannot take them:
    # "| iex -Uninstall" binds the switch to iex, which ignores it and installs.
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/John-RPG/EmailForDevs/master/install.ps1))) -Uninstall

.EXAMPLE
    .\install.ps1 -Version v0.1.1 -NoLaunch
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\eeeMail'),
    [string] $Version,
    [string] $Repository = 'John-RPG/EmailForDevs',
    [switch] $NoShortcut,
    [switch] $NoLaunch,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AppName      = 'eeeMail'
$ExeName      = 'eeeMail.exe'
$ShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Write-Step($message) { Write-Host "  $message" }

# ---- uninstall ---------------------------------------------------------------

if ($Uninstall) {
    Write-Host "Removing $AppName..." -ForegroundColor Cyan

    # Running from inside the directory being deleted would keep a lock on it.
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Step "stopping running instance (pid $($_.Id))"
            $_ | Stop-Process -Force
        }
    Start-Sleep -Milliseconds 500

    if (Test-Path $ShortcutPath) {
        Remove-Item $ShortcutPath -Force
        Write-Step 'removed Start Menu shortcut'
    }
    if (Test-Path $InstallDirectory) {
        Remove-Item $InstallDirectory -Recurse -Force
        Write-Step "removed $InstallDirectory"
    }

    Write-Host ''
    Write-Host "$AppName removed." -ForegroundColor Green
    Write-Host 'Your mail databases and settings were not touched. They live in the' -ForegroundColor DarkGray
    Write-Host 'profile directory the app reported under Help > About.' -ForegroundColor DarkGray
    return
}

# ---- find the release --------------------------------------------------------

Write-Host "Installing $AppName from $Repository..." -ForegroundColor Cyan

# TLS 1.2 is not the default on older Windows PowerShell hosts, and GitHub
# refuses anything less.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$api = if ($Version) {
    "https://api.github.com/repos/$Repository/releases/tags/$Version"
} else {
    "https://api.github.com/repos/$Repository/releases/latest"
}

try {
    $release = Invoke-RestMethod -Uri $api -Headers @{
        'User-Agent' = 'eeeMail-installer'
        'Accept'     = 'application/vnd.github+json'
    }
} catch {
    throw "Could not reach the GitHub release API for $Repository. $($_.Exception.Message)"
}

$asset = $release.assets | Where-Object { $_.name -like '*win-x64.zip' } | Select-Object -First 1
if (-not $asset) {
    throw "Release $($release.tag_name) has no win-x64 asset to install."
}

Write-Step "release  $($release.tag_name)"
Write-Step "asset    $($asset.name)  ($([math]::Round($asset.size / 1MB)) MB)"

# The API reports 'sha256:<hex>'. Anything else is treated as no digest at all
# rather than trusted, because a digest we cannot check is not a check.
$expected = $null
if (($asset.PSObject.Properties.Name -contains 'digest') -and $asset.digest) {
    if ($asset.digest -match '^sha256:([0-9a-fA-F]{64})$') { $expected = $Matches[1].ToLower() }
}
if (-not $expected) {
    throw ("Release $($release.tag_name) publishes no SHA-256 digest, so the download " +
           'cannot be verified. Install it by hand from the release page if you trust it.')
}

# ---- download and verify -----------------------------------------------------

$staging = Join-Path ([IO.Path]::GetTempPath()) "eeeMail-install-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$zip = Join-Path $staging $asset.name

try {
    Write-Step 'downloading...'
    # Progress rendering makes Invoke-WebRequest dramatically slower on large
    # files in Windows PowerShell, and this asset is around 73 MB.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
    } finally {
        $ProgressPreference = $previousProgress
    }

    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
        throw ("The download did not match the digest GitHub published. Expected " +
               "$expected, got $actual. The file was discarded and nothing was installed.")
    }
    Write-Step "verified SHA-256 $actual"

    # ---- install -------------------------------------------------------------

    # Stop a running copy, or the exe cannot be replaced.
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Step "stopping running instance (pid $($_.Id))"
            $_ | Stop-Process -Force
        }
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Expand-Archive -Path $zip -DestinationPath $InstallDirectory -Force

    $exe = Join-Path $InstallDirectory $ExeName
    if (-not (Test-Path $exe)) {
        $found = Get-ChildItem $InstallDirectory -Filter '*.exe' -Recurse | Select-Object -First 1
        if (-not $found) { throw "No executable found in $($asset.name)." }
        $exe = $found.FullName
    }
    Write-Step "installed to $InstallDirectory"

    if (-not $NoShortcut) {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($ShortcutPath)
        $link.TargetPath = $exe
        $link.WorkingDirectory = $InstallDirectory
        $link.Description = 'A mail client for people who read headers'
        $link.Save()
        Write-Step 'added Start Menu shortcut'
    }
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$AppName $($release.tag_name) installed." -ForegroundColor Green
Write-Host ''
Write-Host 'The binary is unsigned, so Windows SmartScreen may warn on first run.' -ForegroundColor DarkGray
Write-Host 'The hash above was checked against the one GitHub published for this release.' -ForegroundColor DarkGray
Write-Host ''
Write-Host "To update later:    the app offers updates itself, or re-run this script"
Write-Host 'To uninstall:'
# Not "| iex -Uninstall": that binds the switch to iex, which ignores it and
# reinstalls instead. Arguments reach a downloaded script only via a scriptblock.
Write-Host "  & ([scriptblock]::Create((irm https://raw.githubusercontent.com/$Repository/master/install.ps1))) -Uninstall"

if (-not $NoLaunch) { Start-Process -FilePath $exe }
