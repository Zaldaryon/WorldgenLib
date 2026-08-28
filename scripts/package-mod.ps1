[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$modInfoPath = Join-Path $repoRoot 'WorldgenLib/modinfo.json'
$buildOutput = Join-Path $repoRoot "WorldgenLib/bin/$Configuration/Mods/mod"

if (-not (Test-Path -LiteralPath $modInfoPath -PathType Leaf)) {
    throw 'WorldgenLib/modinfo.json is missing. Source publication is required before packaging.'
}
if (-not (Test-Path -LiteralPath $buildOutput -PathType Container)) {
    throw "Build output not found: $buildOutput"
}

$modInfo = Get-Content -LiteralPath $modInfoPath -Raw | ConvertFrom-Json
$version = [string]$modInfo.version
$modId = [string]$modInfo.modid
if ($modId -ne 'worldgenlib' -or $version -notmatch '^\d+\.\d+\.\d+(?:-(?:pre|rc|dev)\.\d+)?$') {
    throw "Invalid WorldgenLib metadata: modid=$modId version=$version"
}

$requiredFiles = @(
    'WorldgenLib.VintageStory.dll',
    'modinfo.json',
    'modicon.png'
)

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$stagePath = Join-Path $tempRoot "worldgenlib-package-$version"
if (Test-Path -LiteralPath $stagePath) {
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stagePath | Out-Null

foreach ($fileName in $requiredFiles) {
    $sourcePath = Join-Path $buildOutput $fileName
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required package file is missing from the build output: $fileName"
    }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stagePath $fileName) -Force
}

$outputPath = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$zipPath = Join-Path $outputPath "WorldgenLib-$version.zip"
$hashPath = "$zipPath.sha256"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $hashPath) { Remove-Item -LiteralPath $hashPath -Force }

Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $hashPath,
    "$hash  WorldgenLib-$version.zip`n",
    [Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actualNames = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $expectedNames = @($requiredFiles | Sort-Object)
    if (($actualNames -join "`n") -ne ($expectedNames -join "`n")) {
        throw "Package contains an unexpected file set: $($actualNames -join ', ')"
    }
} finally {
    $archive.Dispose()
}

if ($env:GITHUB_OUTPUT) {
    "version=$version" >> $env:GITHUB_OUTPUT
    "zip=$zipPath" >> $env:GITHUB_OUTPUT
    "sha256=$hash" >> $env:GITHUB_OUTPUT
}

Write-Host "Created $zipPath"
Write-Host "SHA256: $hash"
