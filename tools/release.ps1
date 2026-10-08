<#
.SYNOPSIS
    Builds, packages and publishes an eeeMail release, then checks it the way
    the updater and the installer will read it.

.DESCRIPTION
    Every build stamps its own version (see Mail.App.csproj), so a release is
    tagged with whatever the published exe says it is - there is nothing to
    bump. The script refuses to release a version that is not newer than the
    current latest release, which would only happen with a wrong clock.

    Two things consume a release: the in-app updater (UpdateChecker) and
    install.ps1. Both select the asset whose name ends in "win-x64.zip" and
    refuse it without a sha256 digest. v0.1.6 and v0.1.7 were first published
    by hand as a bare .exe, which both consumers silently ignored - the
    updater reported the previous version as the latest, and the install
    one-liner would have failed outright.

    So the packaging lives here rather than in anyone's memory, and the last
    step reads the published release back through the same selection rule
    the consumers use. A release that fails that check is reported, not
    assumed good.

    The release is tagged at the exact commit that was built, after pushing
    it. Without an explicit target GitHub tags whatever the remote branch
    points at, which is not the built code if the local branch was ahead. For
    the same reason the source must have no uncommitted changes: they would
    be in the exe but not in the tagged commit.

.EXAMPLE
    .\tools\release.ps1 -NotesFile .scratch\notes.md
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $NotesFile,

    [string] $Repository = 'John-RPG/EmailForDevs'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# The asset-name contract. Must match UpdateChecker.Parse and install.ps1.
$AssetSuffix = 'win-x64.zip'
$ProjectPath = 'src\Mail.App\Mail.App.csproj'

if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }

$dirty = git status --porcelain -- src tests
if ($dirty) { throw "Uncommitted changes under src or tests; commit them first:`n$($dirty -join "`n")" }

Write-Output 'Running tests...'
dotnet test --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not releasing.' }

$out = '.scratch\release'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Publishing self-contained build...'
dotnet publish $ProjectPath -c Release -p:PublishSingleFile=true -o "$out\publish" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# FileVersion rather than ProductVersion: the latter carries "+<commit>".
$exe = "$out\publish\eeeMail.exe"
$version = [version](Get-Item $exe).VersionInfo.FileVersion
$tag = "v$version"
Write-Output "Built $tag"

$headers = @{ 'User-Agent' = 'eeeMail-release-check' }
$previous = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers $headers
$previousVersion = [version]($previous.tag_name -replace '^[vV]', '')
if ($version -le $previousVersion) {
    throw "$tag is not newer than the latest release $($previous.tag_name). Check the system clock."
}

$commit = (git rev-parse HEAD).Trim()
Write-Output "Pushing $($commit.Substring(0, 7))..."
git push -q origin HEAD
if ($LASTEXITCODE -ne 0) { throw 'Push failed; not releasing.' }

# Same layout every release has used: eeeMail.exe at the zip root.
$zip = "$out\eeeMail-$tag-$AssetSuffix"
Compress-Archive -Path $exe -DestinationPath $zip -Force
$localHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

Write-Output "Creating release with $(Split-Path $zip -Leaf)..."
gh release create $tag $zip --repo $Repository --target $commit --title "eeeMail $tag" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }

# Read it back exactly as the consumers do.
$latest = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers $headers
$asset = $latest.assets | Where-Object { $_.name -like "*$AssetSuffix" } | Select-Object -First 1

$problems = @()
if ($latest.tag_name -ne $tag) { $problems += "releases/latest is $($latest.tag_name), not $tag" }
if (-not $asset) { $problems += "no *$AssetSuffix asset - updater and installer would both ignore this release" }
elseif (-not $asset.digest) { $problems += 'asset has no digest - updater would refuse it' }
elseif ($asset.digest -ne "sha256:$localHash") { $problems += "published digest $($asset.digest) does not match local $localHash" }

if ($problems.Count -gt 0) {
    Write-Output 'RELEASE CHECK FAILED:'
    $problems | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "OK: $tag is latest at $($commit.Substring(0, 7)), $($asset.name) selected by the consumers' rule, digest matches."
