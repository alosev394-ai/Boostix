# Code signing policy

> **Status: prepared, not active.** Boostix releases are not Authenticode-signed
> yet. This page is the policy Boostix will follow once SignPath Foundation
> accepts the project. Until then Windows SmartScreen may warn about the
> installer, and the integrity of updates rests on the RSA-signed update
> manifest described in [SECURITY.md](SECURITY.md).

Free code signing provided by [SignPath.io](https://about.signpath.io/),
certificate by [SignPath Foundation](https://signpath.org/).

## What is signed

Every release signs three Windows executables, each before it is built into
the next one:

| File | What it is |
| --- | --- |
| `Boostix.exe` | The app. The installer carries this exact signed file. |
| `Boostix-Setup-<version>.exe` | The installer and uninstaller. |
| `MajesticBoost-Setup-<version>.exe` | The bridge that lets very old versions update. |

Only files built from this repository's source by the
[`signpath-release.yml`](.github/workflows/signpath-release.yml) workflow on
GitHub-hosted runners are submitted for signing. Nothing built on a personal
computer is signed with the SignPath Foundation certificate.

## Team roles

| Role | Who |
| --- | --- |
| Author and committer | [@alosev394-ai](https://github.com/alosev394-ai) |
| Reviewer | [@alosev394-ai](https://github.com/alosev394-ai) |
| Signing approver | [@alosev394-ai](https://github.com/alosev394-ai) |

Every signing request is approved by hand in SignPath before it is signed.

## Privacy

Boostix has no accounts, ads, analytics or telemetry. The one network request
it makes on its own is the update check at start-up: an HTTPS read of the
signed update manifest from GitHub, or from its jsDelivr mirror when GitHub is
unreachable. The request carries no user name, e-mail or device identifier.
An installer is downloaded only when you choose to update. Diagnostics are
saved to a file on your computer, and you decide whether to send it to
support. Details: [PRIVACY.md](PRIVACY.md).

## How a signed release is made

1. Bump the version and update the changelogs as usual.
2. Run **Actions → Boostix signed release build → Run workflow**. It builds
   the app, has SignPath sign it, builds the installer around the signed app,
   has it signed, then does the same for the update bridge. Each signing
   request waits for approval in SignPath.
3. Download the `boostix-signed-release` artifact and run, on the
   maintainer's PC (the update key never leaves it):

   ```powershell
   .\build.ps1 -Stage Release -PrepareSignedUpdate `
     -SignedAppPath <artifact>\Boostix.exe `
     -SignedSetupPath <artifact>\Boostix-Setup-<version>.exe `
     -SignedBridgePath <artifact>\MajesticBoost-Setup-<version>.exe
   ```

   `build.ps1` refuses any file whose signature is not valid or whose
   version does not match the release.
4. Check that `Get-AuthenticodeSignature dist\*.exe` reports `Valid` with the
   SignPath Foundation certificate, commit `dist` and the update manifest, tag
   and publish.

## Setting up SignPath (one time)

1. The project needs an OSI-approved open-source license; SignPath Foundation
   signs only open-source software.
2. Apply at <https://signpath.org/apply>. After approval SignPath creates the
   organization and the project.
3. In the SignPath project create:
   - the artifact configurations `boostix-app`, `boostix-setup` and
     `boostix-bridge` from the files in
     [`.signpath/artifact-configurations`](.signpath/artifact-configurations);
   - a release signing policy with manual approval, connected to this
     repository through the GitHub trusted build system.
4. In the GitHub repository settings add:
   - the secret `SIGNPATH_API_TOKEN` (a SignPath CI user token);
   - the variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` and
     `SIGNPATH_SIGNING_POLICY_SLUG`.

Until all four are set, the workflow stops at its first step and says what is
missing.
