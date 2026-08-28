[CmdletBinding()]
param(
    [string]$MetadataDirectory = '_api',
    [string]$SiteDirectory = '_site'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$metadataPath = Join-Path $repoRoot $MetadataDirectory
$sitePath = Join-Path $repoRoot $SiteDirectory

if (-not (Test-Path -LiteralPath $metadataPath -PathType Container)) {
    throw "DocFX metadata directory was not created: $metadataPath"
}

$metadataFiles = @(Get-ChildItem -LiteralPath $metadataPath -Recurse -Filter '*.yml' -File)
if ($metadataFiles.Count -eq 0) {
    throw 'DocFX produced no API metadata. Check that the public WorldgenLib source is available.'
}

if (-not (Test-Path -LiteralPath (Join-Path $sitePath 'api/index.html') -PathType Leaf)) {
    throw "DocFX API entry point was not created: $(Join-Path $sitePath 'api/index.html')"
}

$apiSitePath = Join-Path $sitePath 'api'
$apiPages = @()
if (Test-Path -LiteralPath $apiSitePath -PathType Container) {
    $apiPages = @(Get-ChildItem -LiteralPath $apiSitePath -Recurse -Filter '*.html' -File)
}
if ($apiPages.Count -eq 0) {
    throw 'DocFX produced no rendered API pages.'
}

Write-Host "Validated $($metadataFiles.Count) API metadata files and $($apiPages.Count) rendered API pages."
