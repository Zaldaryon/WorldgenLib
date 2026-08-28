# Building WorldgenLib

These instructions describe the build inputs and commands for the first public source release. The current repository setup does not contain the WorldgenLib source tree yet, so the build commands will fail with the same intentional preflight message as the release workflow until that source is published.

## Requirements

- Windows, because the official Vintage Story reference package is a Windows installer.
- .NET 10 SDK.
- PowerShell 7 or newer.
- Git and internet access to the official Vintage Story CDN.

## Reference assemblies

[`build-manifest.json`](../build-manifest.json) pins the Vintage Story version used for compilation and lists the official installer URL. The bootstrap script downloads that installer to a local cache, extracts it with the pinned InnoUnpacker utility, and checks the expected version marker and reference DLLs.

The game installer and extracted files are build inputs. They are not copied into the repository and are not included in the mod ZIP.

From the repository root, run:

```powershell
$version = '1.22.7'
$vsRoot = Join-Path $env:TEMP "worldgenlib-vintagestory-$version"
pwsh .\scripts\bootstrap-vintage-story.ps1 -InstallPath $vsRoot
$env:VINTAGE_STORY = $vsRoot

dotnet build .\WorldgenLib\WorldgenLib.VintageStory.csproj -c Release --nologo
dotnet test .\WorldgenLib.Tests\WorldgenLib.Tests.csproj -c Release --nologo
dotnet tool restore
dotnet tool run docfx docfx.json --warningsAsErrors
pwsh .\scripts\validate-api-output.ps1
pwsh .\scripts\package-mod.ps1 -Configuration Release -OutputDirectory .\dist
```

The `VINTAGE_STORY` environment variable points the project at the extracted official game installation. Do not replace it with a checked-in copy of game assemblies.

## Validation

For a release tag, the workflow runs [`validate-release.ps1`](../scripts/validate-release.ps1) before bootstrapping the game references. The API documentation workflow always publishes the WorldgenLib landing page. When the source tree is public, it also runs DocFX after the same reference bootstrap and publishes the generated API pages under `_site/api`. The package script verifies that the archive contains only the intended mod files and writes a SHA-256 checksum next to the ZIP.
