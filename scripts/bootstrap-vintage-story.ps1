[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$InstallPath,
    [string]$ArchivePath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $repoRoot 'build-manifest.json'
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Build manifest not found: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.vintageStoryVersion
$archive = $manifest.clientArchive

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid Vintage Story version in build-manifest.json: $version"
}
if (-not $archive.name -or -not $archive.url -or -not $archive.extractor.url) {
    throw 'The build manifest is missing the official archive or extractor fields.'
}

$archiveUri = [Uri]$archive.url
if ($archiveUri.Scheme -ne 'https' -or $archiveUri.Host -ne 'cdn.vintagestory.at') {
    throw "The game archive must come from cdn.vintagestory.at: $($archive.url)"
}

$tempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$cacheRoot = Join-Path $tempRoot 'worldgenlib-vs-archives'
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $cacheRoot $archive.name
}
if (-not $InstallPath) {
    $InstallPath = Join-Path $tempRoot "worldgenlib-vintagestory-$version"
}

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Download-Atomic([string]$Url, [string]$Destination) {
    Ensure-Directory (Split-Path -Parent $Destination)
    $partial = "$Destination.partial"
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }

    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) {
        throw 'curl.exe is required to download Vintage Story build inputs.'
    }

    & $curl.Source -L --fail --retry 3 --retry-delay 5 -o $partial $Url
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        throw "Download failed: $Url"
    }
    if (-not (Test-Path -LiteralPath $partial -PathType Leaf) -or (Get-Item -LiteralPath $partial).Length -lt 1MB) {
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        throw "Downloaded file is missing or unexpectedly small: $Url"
    }
    Move-Item -LiteralPath $partial -Destination $Destination -Force
}

if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
    Write-Host "Downloading official Vintage Story $version archive"
    Download-Atomic $archive.url $ArchivePath
} else {
    Write-Host "Using cached official Vintage Story archive: $ArchivePath"
}

$toolsRoot = Join-Path $tempRoot 'worldgenlib-tools'
$innounpPath = Join-Path $toolsRoot $archive.extractor.executable
if (-not (Test-Path -LiteralPath $innounpPath -PathType Leaf)) {
    $extractorArchive = Join-Path $toolsRoot $archive.extractor.name
    Write-Host 'Downloading the pinned Inno Setup extractor'
    Download-Atomic $archive.extractor.url $extractorArchive
    Ensure-Directory $toolsRoot
    Expand-Archive -LiteralPath $extractorArchive -DestinationPath $toolsRoot -Force
    $found = Get-ChildItem -LiteralPath $toolsRoot -Recurse -Filter $archive.extractor.executable -File |
        Select-Object -First 1
    if (-not $found) {
        throw "The extractor archive did not contain $($archive.extractor.executable)."
    }
    if ($found.FullName -ne $innounpPath) {
        Copy-Item -LiteralPath $found.FullName -Destination $innounpPath -Force
    }
}

if ($Force -and (Test-Path -LiteralPath $InstallPath)) {
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

$markerPath = Join-Path $InstallPath "assets/version-$version.txt"
$hasExpectedInstall = Test-Path -LiteralPath $markerPath -PathType Leaf
if (-not $hasExpectedInstall) {
    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }

    $extractPath = "$InstallPath.extracting"
    if (Test-Path -LiteralPath $extractPath) {
        Remove-Item -LiteralPath $extractPath -Recurse -Force
    }
    Ensure-Directory $extractPath

    $arguments = "-x -d`"$extractPath`" -c`"{app}`" `"$ArchivePath`""
    Write-Host "Extracting Vintage Story $version"
    $process = Start-Process -FilePath $innounpPath -ArgumentList $arguments -NoNewWindow -PassThru -Wait
    $appPath = Join-Path $extractPath '{app}'
    $sourcePath = if (Test-Path -LiteralPath $appPath -PathType Container) { $appPath } else { $extractPath }
    $executablePath = Join-Path $sourcePath 'Vintagestory.exe'
    if ($process.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "The official Vintage Story archive could not be extracted. innounp exit code: $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw 'The extracted Vintage Story tree has no Vintagestory.exe.'
    }

    Ensure-Directory $InstallPath
    Get-ChildItem -LiteralPath $sourcePath -Force | Move-Item -Destination $InstallPath -Force
    Remove-Item -LiteralPath $extractPath -Recurse -Force
}

$expectedFiles = @(
    'VintagestoryAPI.dll',
    'VintagestoryLib.dll',
    'Mods/VSEssentials.dll',
    'Mods/VSSurvivalMod.dll',
    'Lib/0Harmony.dll',
    'Lib/Newtonsoft.Json.dll',
    'Lib/protobuf-net.dll',
    'Lib/OpenTK.Mathematics.dll',
    'Lib/Microsoft.Data.Sqlite.dll',
    'Lib/SkiaSharp.dll'
)
foreach ($relativePath in $expectedFiles) {
    $path = Join-Path $InstallPath $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Vintage Story reference is missing: $relativePath"
    }
}

$versionMarker = Get-ChildItem -LiteralPath (Join-Path $InstallPath 'assets') -Filter 'version-*.txt' -File |
    Select-Object -First 1
if (-not $versionMarker -or $versionMarker.Name -ne "version-$version.txt") {
    throw "Extracted Vintage Story version marker does not match $version."
}

Write-Host "Vintage Story $version references ready at $InstallPath"
Write-Output $InstallPath
