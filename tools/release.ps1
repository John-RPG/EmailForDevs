<#
.SYNOPSIS
    Builds, packages and publishes an eeeMail release, then checks it the way
    the updater and the installer will read it.

.DESCRIPTION
    Every release gets a new version, without anyone having to remember to
    bump it: if the version in Mail.App.csproj has already been released, the
    patch number is incremented. A version set higher by hand (a minor or
    major bump) is used as it is.

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

    The version bump is committed and pushed, and the release is tagged at
    that exact commit. Without an explicit target GitHub tags whatever the
    remote branch points at, which is not the code that was built if the
    local branch was ahead.

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

# The bump commit must contain only the version change, so the project file
# itself must have nothing else pending.
git diff --quiet HEAD -- $ProjectPath
if ($LASTEXITCODE -ne 0) { throw "$ProjectPath has uncommitted changes; commit or stash them first." }

function Test-Released([string] $tag) {
    # Windows PowerShell turns redirected native stderr into an error record,
    # which 'Stop' then throws - so "release not found", the expected answer
    # for a new tag, would abort the script. Local to this function.
    $ErrorActionPreference = 'Continue'
    gh release view $tag --repo $Repository 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

[xml] $project = Get-Content $ProjectPath
$current = [version](($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version)
if (-not $current) { throw "No <Version> in $ProjectPath." }

$version = $current
while (Test-Released "v$version") {
    $version = [version]::new($version.Major, $version.Minor, $version.Build + 1)
}
$tag = "v$version"
if ($version -ne $current) { Write-Output "v$current is already released; this release is $tag" }
else { Write-Output "Releasing $tag" }

Write-Output 'Running tests...'
dotnet test --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not releasing.' }

function Set-ProjectVersion([version] $value) {
    # Text replacement rather than an XML round trip, which would reformat the
    # file. Written back as UTF-8 with BOM, as the project file already is.
    $path = (Resolve-Path $ProjectPath).Path
    $text = [IO.File]::ReadAllText($path)
    $text = $text -replace '<Version>[^<]*</Version>', "<Version>$value</Version>"
    $text = $text -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$value</InformationalVersion>"
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $true))
}

$bumped = $version -ne $current
if ($bumped) { Set-ProjectVersion $version }

try {
    $out = '.scratch\release'
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output 'Publishing self-contained build...'
    dotnet publish $ProjectPath -c Release -p:PublishSingleFile=true -o "$out\publish" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $exe = "$out\publish\eeeMail.exe"
    $built = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $built.StartsWith("$version")) { throw "Built $built, expected $version." }
}
catch {
    # Leave the project as it was, so a failed release changes nothing.
    if ($bumped) { git checkout -- $ProjectPath }
    throw
}

if ($bumped) {
    git commit -q -m "Release $tag" -- $ProjectPath
    if ($LASTEXITCODE -ne 0) { throw 'Committing the version bump failed.' }
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
Write-Output "OK: $tag is latest at $($commit.Substring(0, 7)), $($asset.name) selected by the consumers' rule, digest matches."
