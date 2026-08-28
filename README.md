# WorldgenLib

[![Latest release](https://img.shields.io/github/v/release/Zaldaryon/WorldgenLib?sort=semver)](https://github.com/Zaldaryon/WorldgenLib/releases)
[![API reference](https://img.shields.io/badge/API-reference-356b46)](https://zaldaryon.github.io/WorldgenLib/api/)
[![License](https://img.shields.io/github/license/Zaldaryon/WorldgenLib)](LICENSE)

WorldgenLib is a server-side API library for Vintage Story world-generation mods. It gives modders ordered hook points for terrain and map work so separate mods can compose their changes through a shared interface.

## 0.1.0

The first public release is available from [GitHub Releases](https://github.com/Zaldaryon/WorldgenLib/releases/tag/v0.1.0). It publishes the WorldgenLib API, its unit tests, the server-only mod metadata, and the mod icon.

WorldgenLib is infrastructure, not a terrain overhaul. It does not replace `GenTerra` or `GenMaps`, and it does not add blocks, items, biomes, or world shape by itself.

## What it provides

- An API for world-generation integrations instead of a competing world generator.
- Ordered hook points for map, terrain, post-processing, and BlockLayers work.
- Cooperative composition for multiple world-generation mods.
- Diagnostics for incompatible takeover or patching strategies.
- A server-side mod package. Clients do not need to install the library itself.

## Installation

Download [`WorldgenLib-0.1.0.zip`](https://github.com/Zaldaryon/WorldgenLib/releases/download/v0.1.0/WorldgenLib-0.1.0.zip) from the release and place it in the server's `Mods` directory. The SHA-256 file published beside the archive can be used to verify the download.

WorldgenLib is server-side only. Clients do not need to install it. Mods that use WorldgenLib may list it as a server dependency, so follow each consumer mod's installation instructions for its own client and server needs.

## Compatibility

WorldgenLib 0.1.0 is built and tested against Vintage Story 1.22.0 through 1.22.7. The reproducible release build uses the official 1.22.7 archive as its pinned reference input. See the [compatibility and updates](https://github.com/Zaldaryon/WorldgenLib/wiki/Compatibility-and-Updates) page for the evidence and current limits.

## API reference

The [WorldgenLib site](https://zaldaryon.github.io/WorldgenLib/) is the public landing page. Its generated [C# API reference](https://zaldaryon.github.io/WorldgenLib/api/index.html) is built by DocFX from the public source tree. The [WorldgenLib Wiki](https://github.com/Zaldaryon/WorldgenLib/wiki) remains the place for conceptual guides, migration notes, and compatibility limits.

## Development

The build obtains reference assemblies from the official Vintage Story Windows installer at build time. The installer and extracted game files are build inputs only and are never included in a WorldgenLib release archive.

See [Building](docs/building.md) for local build and test commands. Release maintainers should also read [Releasing](docs/releasing.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for design and pull request guidelines. WorldgenLib's public API is intended to remain additive, ordered, and explicit about compatibility boundaries.

## License

WorldgenLib is released under the [MIT License](LICENSE).
