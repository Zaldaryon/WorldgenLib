# Contributing to WorldgenLib

WorldgenLib is a compatibility layer for Vintage Story world-generation mods. Contributions should preserve that role and keep integrations composable.

## Current repository status

The public repository is being prepared for the first source publication. Until the source tree is published, documentation, build automation, release tooling, and carefully scoped issue reports are the useful contribution areas. Do not copy source, binaries, decompiled files, or files from outside this repository into a pull request.

## Design boundaries

- Add integrations through explicit API and ordered hook points.
- Keep the library server-side and cooperative with other world-generation mods.
- Do not turn WorldgenLib into a hard replacement for `GenTerra` or `GenMaps`.
- Preserve vanilla behavior when no consumer hook is registered.
- Document version-sensitive behavior and compatibility assumptions.

## Pull requests

1. Open an issue first for a substantial API or compatibility change.
2. Create a focused branch from `main`.
3. Keep one logical change per pull request.
4. Update documentation and the changelog when behavior or public API changes.
5. Run the applicable build, test, and validation commands before requesting review.
6. Explain compatibility impact and migration requirements in the pull request.

Do not include Vintage Story installers, game DLLs, generated build output, world saves, server logs containing sensitive data, or files from outside this repository.

## Local checks

Once the source tree is published, the standard checks are documented in [docs/building.md](docs/building.md). Release metadata is validated against the tag by the same scripts used in CI.

## Reporting compatibility problems

Include the WorldgenLib version, Vintage Story version, relevant consumer mods, the server-side reproduction steps, and a scrubbed log excerpt. Do not attach proprietary game files or sensitive player data.
