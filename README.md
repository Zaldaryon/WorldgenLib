# WorldgenLib

WorldgenLib is a server-side API library for Vintage Story world-generation mods. It gives modders ordered hook points for terrain and map work so separate mods can compose their changes through a shared interface.

## Project status

This repository currently contains the public build and release scaffolding. The WorldgenLib source is intentionally not published in this initial repository setup. The release workflow will stop with a clear validation error until the source and its tests are published.

## What it provides

- An API for world-generation integrations instead of a competing world generator.
- Ordered hook points for map, terrain, and post-processing work.
- Cooperative composition for multiple world-generation mods.
- Diagnostics for incompatible takeover or patching strategies.
- A server-side mod package. Clients do not need to install the library itself.

WorldgenLib is additive. It does not ship a hard replacement for Vintage Story's `GenTerra` or `GenMaps` pipeline.

## Installation

When a release is available, download its `WorldgenLib-<version>.zip` asset and place it in the server's `Mods` directory. The SHA-256 file published beside the archive can be used to verify the download.

Mods that use WorldgenLib may list it as a server dependency. Follow each consumer mod's installation instructions for its own client and server needs.

## Compatibility

The build manifest pins the official Vintage Story build used to compile and test the library. The current pin is 1.22.7. Each release should document its tested Vintage Story versions and consumer-mod compatibility separately.

## Development

The build obtains reference assemblies from the official Vintage Story Windows installer at build time. The installer and extracted game files are build inputs only and are never included in a WorldgenLib release archive.

After the source tree is published, see [Building](docs/building.md) for local build and test commands. Release maintainers should also read [Releasing](docs/releasing.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for design and pull request guidelines. WorldgenLib's public API is intended to remain additive, ordered, and explicit about compatibility boundaries.

## License

WorldgenLib is released under the [MIT License](LICENSE).
