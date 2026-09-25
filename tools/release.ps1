<#
.SYNOPSIS
    Builds, packages and publishes an eeeMail release, then checks it the way
    the updater and the installer will read it.

.DESCRIPTION
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

if (-not (Test-Path $NotesFile)) { throw "Notes file not found: $NotesFile" }

[xml] $project = Get-Content 'src\Mail.App\Mail.App.csproj'
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Mail.App.csproj.' }
$tag = "v$version"
Write-Output "Releasing $tag"

$existing = gh release view $tag --repo $Repository 2>$null
if ($LASTEXITCODE -eq 0) { throw "$tag already exists. Bump <Version> first." }

Write-Output 'Running tests...'
dotnet test --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not releasing.' }

$out = '.scratch\release'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Publishing self-contained build...'
dotnet publish 'src\Mail.App\Mail.App.csproj' -c Release -p:PublishSingleFile=true -o "$out\publish" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = "$out\publish\eeeMail.exe"
$built = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $built.StartsWith($version)) { throw "Built $built, expected $version." }

# Same layout every release has used: eeeMail.exe at the zip root.
$zip = "$out\eeeMail-$tag-$AssetSuffix"
Compress-Archive -Path $exe -DestinationPath $zip -Force
$localHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

Write-Output "Creating release with $(Split-Path $zip -Leaf)..."
gh release create $tag $zip --repo $Repository --title "eeeMail $tag" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }

# Read it back exactly as the consumers do.
$latest = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" `
    -Headers @{ 'User-Agent' = 'eeeMail-release-check' }
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
Write-Output "OK: $tag is latest, $($asset.name) selected by the consumers' rule, digest matches."
