# Releasing WorldgenLib

Releases are created only from Git tags. A normal branch or commit push does not publish a GitHub release.

## Before tagging

1. Merge the reviewed source and documentation changes into `main`.
2. Set the version in `WorldgenLib/modinfo.json`.
3. Update [`CHANGELOG.md`](../CHANGELOG.md) with the release notes.
4. Confirm that the source tree and `WorldgenLib.Tests` are present.
5. Run the local build and test commands in [Building](building.md).

The tag version must exactly match `modinfo.json`. Tags use semantic versions, for example `v0.1.0`, `v0.2.0-rc.1`, or `v0.2.0-pre.1`.

## Create the release

After the versioned commit is on `main`, create and push an annotated tag:

```powershell
git fetch origin
git checkout main
git pull --ff-only origin main
git tag -a v0.1.0 -m 'Release v0.1.0'
git push origin v0.1.0
```

Pushing a matching `v*.*.*` tag starts `.github/workflows/release.yml` on `windows-latest`. It will:

1. Check out the tagged commit.
2. Validate the tag and mod metadata.
3. Download and extract the official Vintage Story build from the manifest.
4. Build and test WorldgenLib.
5. Create the mod ZIP and SHA-256 checksum.
6. Create the GitHub release with generated notes and both assets.

Do not retag a published version. If a release needs correction, increment the version and publish a new tag.

## Build inputs and permissions

The workflow uses the official Vintage Story installer only as a build input. It does not upload the installer or game DLLs. The release job has the minimum repository permission it needs to create a GitHub release: `contents: write`.

If a tag is pushed before the source tree is published, the metadata preflight fails before packaging. No release is created in that state.
