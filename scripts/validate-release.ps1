[CmdletBinding()]
param(
    [string]$Tag = $env:GITHUB_REF_NAME
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$modInfoPath = Join-Path $repoRoot 'WorldgenLib/modinfo.json'
$projectPath = Join-Path $repoRoot 'WorldgenLib/WorldgenLib.VintageStory.csproj'

if (-not $Tag -or $Tag -notmatch '^v\d+\.\d+\.\d+(?:-(?:pre|rc|dev)\.\d+)?$') {
    throw "Release tags must use the form vMAJOR.MINOR.PATCH, optionally with -pre.N, -rc.N or -dev.N: $Tag"
}
if (-not (Test-Path -LiteralPath $modInfoPath -PathType Leaf)) {
    throw 'WorldgenLib source has not been published yet: WorldgenLib/modinfo.json is missing.'
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw 'WorldgenLib source has not been published yet: WorldgenLib.VintageStory.csproj is missing.'
}

$modInfo = Get-Content -LiteralPath $modInfoPath -Raw | ConvertFrom-Json
$tagVersion = $Tag.Substring(1)
if ([string]$modInfo.version -ne $tagVersion) {
    throw "Tag $Tag does not match modinfo.json version $($modInfo.version)."
}
if ([string]$modInfo.modid -ne 'worldgenlib') {
    throw "modinfo.json has unexpected modid: $($modInfo.modid)"
}

Write-Host "Release metadata is valid for WorldgenLib $tagVersion"
