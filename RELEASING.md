# Releasing

Maintainer notes for cutting a new release of `dotnet-coverage-mcp`. End users do not need to read this — see the top-level [README](README.md) for installation and configuration.

Releases are automated via `.github/workflows/release.yml`, which fires on any `v*.*.*` tag.

## One-time setup

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so there is no API key to create, store or renew. The workflow trades its GitHub OIDC token for a short-lived key at release time.

The one requirement is a Trusted Publishing policy on nuget.org (account `ericcha901` → Trusted Publishing):

| Field | Value |
|---|---|
| Repository Owner | `Hyeonu-Cha` |
| Repository | `dotnet-coverage-mcp` |
| Workflow File | `release.yml` |
| Environment | *(empty; the workflow uses none)* |

Renaming the workflow file or the repository, or adding an `environment:` to the job, needs a matching change to the policy, or the `NuGet login` step fails.

## Cutting a release

1. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` and add a new empty `## [Unreleased]` above it.
2. Set `version` in `server.json` (top-level and `packages[0]`) to `X.Y.Z`.
3. Merge both as a `Release X.Y.Z` pull request, then tag the merge commit:

```bash
git checkout main && git pull
git tag vX.Y.Z
git push origin vX.Y.Z
```

The package version is injected from the git tag via `-p:Version=` in the release workflow, so there is no `<Version>` element to edit in the csproj.

The workflow builds, tests and packs, writes a CycloneDX SBOM, attests the package's build provenance and SBOM (see [SECURITY.md](SECURITY.md#verifying-a-release)), pushes the package to NuGet, and creates a GitHub Release with the `.nupkg`, the SBOM and auto-generated notes. Its second job, `mcp-registry`, then waits for NuGet to index the version (usually 5–20 minutes), installs it from nuget.org and runs the smoke test on it, and publishes `server.json` to the [official MCP registry](https://registry.modelcontextprotocol.io), logging in with the workflow's GitHub OIDC token. Nothing to do by hand.

If that job fails (NuGet took over an hour, or the registry was down), use **Re-run failed jobs** on the run; it retries only the registry step.

## Submitting to the MCP registry by hand

Only needed when the `mcp-registry` job can't be used. After NuGet has indexed the new version (verify at `https://www.nuget.org/packages/dotnet-coverage-mcp/<version>`), publish with the `mcp-publisher` CLI:

```bash
# macOS
brew install mcp-publisher
# Linux/Windows: download the binary from
# https://github.com/modelcontextprotocol/registry/releases
# and put it on your PATH

mcp-publisher login github     # opens device-code OAuth in your browser
mcp-publisher validate         # sanity-checks server.json against the schema
mcp-publisher publish          # submits the local server.json
```

The login token lasts only a few minutes, so log in immediately before `publish`; otherwise it fails with `401` and "token is expired".

The registry verifies NuGet package ownership by scanning the published package's README for the literal marker `mcp-name: <namespace>/<server>` (already present in this repo's README near the top). If you fork or rename, update that marker to match your `server.json` `name` field, otherwise the publish will fail with a 400.

The `name` field in `server.json` is also case-sensitive — it must match your GitHub username's casing exactly (e.g. `io.github.Hyeonu-Cha/...`, not `io.github.hyeonu-cha/...`).
