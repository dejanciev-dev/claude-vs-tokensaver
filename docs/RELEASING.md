# Releasing

How a version gets from `main` to GitHub Releases and the Visual Studio Marketplace. The workflows do most of it;
you set the version, push a tag, and approve.

## Steps

1. **Set the version** in three places, all the same `X.Y.Z`:
   - `src/ClaudeVS/source.extension.vsixmanifest`: `Identity Version="X.Y.Z"` (the one the workflows check)
   - `src/ClaudeVS/Properties/AssemblyInfo.cs`: `AssemblyVersion` and `AssemblyFileVersion` (`X.Y.Z.0`)
   - `src/ClaudeVS/ClaudeVSPackage.cs`: the version in `InstalledProductRegistration`
2. **Changelog:** in `CHANGELOG.md`, rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` and add a new empty
   `## [Unreleased]` above it. The release notes are taken from that section.
3. **Merge to `main`** through a pull request (`main` is protected).
4. **Tag and push:** `git tag -a vX.Y.Z -m "vX.Y.Z: Marketplace release (preview)" && git push origin vX.Y.Z`.
   [`release.yml`](../.github/workflows/release.yml) then checks that the tag matches the manifest version (it fails
   if they differ), runs the Core tests, builds the solution in Release and creates a **draft** GitHub Release with
   `ClaudeVS-X.Y.Z.vsix` and that version's changelog section.
5. **Review the draft and publish it.** That starts [`publish.yml`](../.github/workflows/publish.yml), which waits
   for your approval in the `marketplace` environment, then uploads the release's `.vsix` with
   [`marketplace/publish.ps1`](../marketplace/publish.ps1). The Marketplace page text is
   [`marketplace/overview.md`](../marketplace/overview.md).

## Pre-releases

A release marked **pre-release** on GitHub stays on GitHub: `publish.yml` skips it. To publish one anyway, run
*Publish to Marketplace* by hand (Actions → Run workflow) with its tag.

## One-time setup

- A publisher at <https://marketplace.visualstudio.com/manage>; its ID as the repository variable
  `MARKETPLACE_PUBLISHER` (Settings → Secrets and variables → Actions).
- The secret `VS_MARKETPLACE_PAT`: an Azure DevOps personal access token for **one organization**, scope
  **Marketplace → Publish** (or Manage). Not "All accessible organizations": global PATs are being retired
  ([Azure DevOps blog](https://devblogs.microsoft.com/devops/retirement-of-global-personal-access-tokens-in-azure-devops)).
  The comment at the top of [`publish.yml`](../.github/workflows/publish.yml) says the same.
- The environment `marketplace` (Settings → Environments) with yourself as a required reviewer.

## The token expires

A PAT has an expiry date. Put it in your calendar and renew the token before then, then update the secret. An
expired, wrong-scope or global token makes the *Publish* step fail; `publish.ps1` says so when VsixPublisher.exe
exits with an error.

## Checking without publishing

`./marketplace/publish.ps1 -Vsix src/ClaudeVS/bin/Release/ClaudeVS.vsix -Publisher <id> -DryRun` checks the inputs
and finds VsixPublisher.exe, without uploading. `./build.ps1` runs the Release build and the tests.

## Fallback: upload by hand

Without a token, or when the workflow fails: build Release (`./build.ps1`), then in the Marketplace portal
(<https://marketplace.visualstudio.com/manage>) open the extension and upload `src/ClaudeVS/bin/Release/ClaudeVS.vsix`
(for the first version: **New extension → Visual Studio**). No PAT is needed. The portal's item name must stay the
one `marketplace/publishManifest.json` uses (`internalName`), or the next automated release creates a second listing.
