# Contributing

## Releasing

Releases of the Braintrust .NET SDK are cut end-to-end from a single
gated GitHub Actions workflow. Release managers do **not** push tags
from a local checkout.

### How to cut a release

1. Pick the commit you want to release. Copy its full 40-character SHA
   from the GitHub commit page (use the "Copy full SHA" button).
2. Go to **Actions → Release → Run workflow**.
3. Fill in:
   - `version` — semver tag like `v1.2.3` (or `v1.2.3-beta.1` for a
     prerelease).
   - `sha` — the full 40-char commit SHA from step 1.
4. Click **Run workflow**. The job will pause on the protected
   `release` environment until a required reviewer approves it.
5. After approval, the workflow validates the inputs, verifies the SHA
   is reachable from `origin/main`, runs CI on the pinned SHA, creates
   and pushes the annotated tag `vX.Y.Z` pointing at that SHA, checks
   out the tag and re-runs CI, packs all NuGet packages, creates the
   GitHub Release with the `.nupkg`/`.snupkg` files attached, and
   publishes to NuGet.org via OIDC trusted publishing.

### Why the SHA is required (and not just a branch name)

The workflow pauses at the environment approval gate. During that
pause, new commits can land on `main`. If we tagged "whatever `main`
is right now" at publish time, those just-landed commits would be
silently included in the release.

Requiring the releaser to pin an explicit commit SHA at dispatch time
makes the released contents reviewable: what gets approved is exactly
what ships, regardless of how long the approval takes.

### Approval gate and secrets

The release job runs in one of two GitHub Environments depending on
whether the version is a stable release or a prerelease:

- **`release`** — used for stable versions (e.g. `v1.2.3`).
  Configured in **Settings → Environments → release** with **required
  reviewers** (release managers) and the NuGet publish secrets
  (`NUGET_USER`, plus anything else the publish step relies on).
- **`release-prerelease`** — used for prereleases (any semver with a
  `-` suffix, e.g. `v1.2.3-beta.1`). Configured in **Settings →
  Environments → release-prerelease** with the **same publish
  secrets** but **no required reviewers**, so prerelease iteration is
  fast.

The workflow picks the environment dynamically from the `version`
input via `contains(inputs.version, '-')`. The input validation step
enforces the semver shape `vX.Y.Z(-prerelease)?`, so the check is
safe: stable versions never contain `-`, prereleases always do.

Secrets are scoped to each environment, so they are only accessible to
jobs that have entered that environment.

If you are cutting a stable release and forget to leave off the
prerelease suffix, the workflow will silently take the ungated path.
Double-check the version before you click "Run workflow".

### Re-publishing a failed release

If a release fails partway through (e.g. NuGet flaked on one of the
packages), re-run the `Release` workflow with the **same** `version`.
Any valid SHA on `main` may be passed for `sha`; it is ignored once
the tag already exists.

On re-run, the workflow:

- Detects the existing tag and skips the tag-creation step.
- Re-runs CI at the tag.
- Re-packs all NuGet packages.
- Updates the existing GitHub Release and re-uploads assets with
  `--clobber` so partial uploads from the prior run are replaced.
- Re-pushes to NuGet.org with `--skip-duplicate`, so packages that
  already made it through on the previous attempt are not treated as
  failures.

### No source version constant to bump

Package versions are passed into `dotnet pack` at build time via
`-p:PackageVersion="$VERSION"`. There is no version constant in source
to bump as part of preparing a release — the release workflow derives
everything from the `version` input.

### Adding a new package

If you add a new project under `src/` that should ship as a NuGet
package, `scripts/verify-release-config.sh` (run in CI) will fail
until you also:

1. Add the project to `Braintrust.Sdk.sln`.
2. Add a `dotnet pack` invocation for it in
   `.github/workflows/release.yml` (the "Pack NuGet packages" step).
3. Add a `find ./artifacts -name "<Project>.${VERSION}.nupkg"` entry
   in the "Find built artifacts" step, and corresponding `.snupkg`
   lookup if the project produces symbols.
4. Reference the new `nupkg`/`snupkg` outputs in the "Create or update
   GitHub Release" step (uploads) and the "Publish to NuGet.org" step
   (push loop).
5. Add a status URL line in the "Wait for NuGet.org indexing" step.

### Local fallback

`scripts/release.sh` is retained as a local fallback for testing tag
creation (e.g. `./scripts/release.sh v1.2.3 --skip-push` to make the
tag locally without pushing). It does **not** publish to NuGet and is
not the canonical release path. The `push: tags: v*` workflow trigger
has been removed, so pushing a tag from this script will no longer
trigger any release automation — only the gated `Release` workflow
publishes artifacts.
