#Requires -Version 7.0
# Optional automation. The first alpha can be uploaded entirely through GitHub's web UI.
[CmdletBinding()]
param([string]$Archive, [string]$Setup, [switch]$IncludePortableArchive, [string]$Repository = 'JustSeanC/AeroScience')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
if ($Repository -ne 'JustSeanC/AeroScience') { throw 'This workflow publishes to the existing AeroScience repository only.' }
function Invoke-GitHub([string[]]$Arguments, [switch]$AllowMissing) {
    $result = & gh @Arguments 2>&1
    $code = $LASTEXITCODE
    $resultText = ($result | Out-String).Trim()
    if ($code -ne 0) {
        if ($AllowMissing -and $resultText -match 'HTTP 404') { return $null }
        throw "GitHub operation failed: $resultText"
    }
    return $resultText
}
Push-Location $repo
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('aeroscience-publish-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    if (-not $Archive) {
        $metaJson = python scripts/release/package_release.py metadata
        if ($LASTEXITCODE -ne 0) { throw 'Invalid version metadata.' }
        $Archive = Join-Path $repo ('artifacts/' + ($metaJson | ConvertFrom-Json).asset_name)
    }
    $Archive = (Resolve-Path -LiteralPath $Archive).Path
    $metaJson = python scripts/release/package_release.py metadata
    if ($LASTEXITCODE -ne 0) { throw 'Invalid version metadata.' }
    $meta = $metaJson | ConvertFrom-Json
    if (-not $Setup) { $Setup = Join-Path ([IO.Path]::GetDirectoryName($Archive)) $meta.setup_name }
    $Setup = (Resolve-Path -LiteralPath $Setup).Path
    $record = Join-Path ([IO.Path]::GetDirectoryName($Setup)) $meta.setup_record_name
    $notes = Join-Path $temporary 'release-notes.md'
    $commit = (git rev-parse HEAD).Trim()
    $infoJson = python scripts/release/installer_release.py verify --archive $Archive --setup $Setup --record $record --notes-output $notes --expected-commit $commit
    if ($LASTEXITCODE -ne 0) { throw 'Final archive verification failed; publication stopped.' }
    $info = $infoJson | ConvertFrom-Json
    $remote = Invoke-GitHub @('api', "repos/$Repository") | ConvertFrom-Json
    if ($remote.private -ne $false -or $remote.full_name -ne $Repository) { throw 'Expected the authorized existing public repository.' }
    $tag = $info.tag
    $tagRef = Invoke-GitHub @('api', "repos/$Repository/git/ref/tags/$tag") -AllowMissing
    if ($tagRef) {
        $object = ($tagRef | ConvertFrom-Json).object
        for ($depth = 0; $object.type -eq 'tag' -and $depth -lt 5; $depth++) {
            $object = ((Invoke-GitHub @('api', "repos/$Repository/git/tags/$($object.sha)")) | ConvertFrom-Json).object
        }
        if ($object.type -ne 'commit' -or $object.sha -ne $info.source_commit) { throw 'The existing tag points at a different source revision. It was not moved.' }
    } else {
        Invoke-GitHub @('api', "repos/$Repository/git/refs", '-f', "ref=refs/tags/$tag", '-f', "sha=$($info.source_commit)") | Out-Null
    }
    $existing = Invoke-GitHub @('api', "repos/$Repository/releases/tags/$tag") -AllowMissing
    if (-not $existing) {
        $arguments = @('release', 'create', $tag, '--repo', $Repository, '--verify-tag', '--draft', '--title', $info.title, '--notes-file', $notes)
        if ($info.prerelease) { $arguments += @('--prerelease', '--latest=false') }
        Invoke-GitHub $arguments | Out-Null
    }
    $release = (Invoke-GitHub @('api', "repos/$Repository/releases/tags/$tag")) | ConvertFrom-Json
    if ($release.prerelease -ne $info.prerelease) { throw 'Existing release has a conflicting stable/prerelease status. It was not changed.' }
    $assets = @($Setup, "$Setup.sha256", $record)
    if ($IncludePortableArchive) { $assets += @($Archive, "$Archive.sha256") }
    foreach ($file in $assets) {
        $name = [IO.Path]::GetFileName($file)
        $asset = @($release.assets | Where-Object { $_.name -eq $name })
        if ($asset.Count -gt 1) { throw "Duplicate existing release assets: $name" }
        if ($asset.Count -eq 1) {
            $expected = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($asset[0].digest) {
                if ($asset[0].digest -ne $expected) { throw "Existing asset differs: $name. Nothing was overwritten." }
            } else {
                $download = Join-Path $temporary $name
                Invoke-GitHub @('release', 'download', $tag, '--repo', $Repository, '--pattern', $name, '--dir', $temporary) | Out-Null
                if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) { throw "Existing asset differs: $name" }
            }
        } else {
            if (-not $release.draft) { throw 'An already published release is incomplete. Review it manually; no published assets were changed.' }
            Invoke-GitHub @('release', 'upload', $tag, $file, '--repo', $Repository) | Out-Null
        }
    }
    if ($release.draft) {
        $arguments = @('release', 'edit', $tag, '--repo', $Repository, '--draft=false')
        if ($info.prerelease) { $arguments += @('--prerelease', '--latest=false') }
        Invoke-GitHub $arguments | Out-Null
    }
    $confirmed = (Invoke-GitHub @('api', "repos/$Repository/releases/tags/$tag")) | ConvertFrom-Json
    if ($confirmed.draft -or $confirmed.prerelease -ne $info.prerelease) { throw 'GitHub has not confirmed the intended public release status.' }
    # Verify ordinary unauthenticated public downloads; a successful upload alone is insufficient.
    foreach ($file in $assets) {
        $name = [IO.Path]::GetFileName($file)
        $public = Join-Path $temporary ('public-' + $name)
        $url = "https://github.com/$Repository/releases/download/$tag/$name"
        Invoke-WebRequest -Uri $url -OutFile $public -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $public -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash) { throw "Public asset verification failed: $name" }
    }
    Write-Host "Published and verified: $($confirmed.html_url)"
    Write-Host "Download: https://github.com/$Repository/releases/download/$tag/$($info.asset_name)"
    Write-Host "SHA-256: $($info.sha256)"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    Pop-Location
}
