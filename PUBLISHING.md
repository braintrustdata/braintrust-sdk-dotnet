# Publishing the .NET SDK

The C# / .NET SDK is released from GitHub Actions via a single **Release** workflow
([`.github/workflows/release.yml`](.github/workflows/release.yml)). There is no local release script
and no separate tag-triggered publish workflow — one manual run drives the whole pipeline. Do not
publish from your local machine.

One run publishes all five NuGet packages together at the same version:

- `Braintrust.Sdk`
- `Braintrust.Sdk.OpenAI`
- `Braintrust.Sdk.Anthropic`
- `Braintrust.Sdk.AgentFramework`
- `Braintrust.Sdk.AzureOpenAI`

## Environments

- **Stable** releases (e.g. `v1.2.3`) run in the protected **`release`** environment, which
  **requires reviewer approval** before anything is tagged or published.
- **Prereleases** — any version containing `-`, e.g. `v1.2.3-beta.1` — run in the
  **`release-prerelease`** environment, which holds the same publish secrets but has **no approval
  gate**, so prerelease iteration stays fast.

## Release

1. Open a PR that bumps the version and merge it to `main`.
2. Copy the full 40-character SHA of the commit you want to release (use GitHub's **Copy full SHA**
   button).
3. Run the **Release** workflow (Actions → Release → Run workflow) with:
   - `version` — the release version, e.g. `v1.2.3` (or `v1.2.3-beta.1` for a prerelease).
   - `sha` — the full 40-char commit SHA to tag. Supplying an explicit SHA (not a branch) ensures
     commits that land on `main` during the approval gate are **not** silently included.
4. Approve the `release` environment when GitHub prompts (stable only).

Once approved, the workflow:

1. Validates the version and SHA, and verifies the SHA is an ancestor of `origin/main`.
2. Runs the full CI gate (`dotnet format --verify-no-changes`, build, test) on the chosen SHA.
3. Creates and pushes the annotated tag `vX.Y.Z` at that SHA, then re-runs the CI gate at the tag.
4. Packs all five NuGet packages (`.nupkg` + `.snupkg`) at the released version.
5. Creates the GitHub Release and uploads the package/symbol files.
6. Publishes to NuGet.org via OIDC trusted publishing (no long-lived API keys).

## Re-publishing a failed release

Re-run the **Release** workflow with the **same `version`** (any valid `main` SHA works — it is
ignored once the tag exists). The tag-creation step is skipped, GitHub Release asset uploads use
`--clobber`, and `dotnet nuget push` uses `--skip-duplicate`, so the workflow is safe to re-run.

## Verify

- GitHub Release: https://github.com/braintrustdata/braintrust-sdk-dotnet/releases
- NuGet: https://www.nuget.org/packages/Braintrust.Sdk
