# Releasing

Maintainer notes for cutting a new release of `dotnet-coverage-mcp`. End users do not need to read this — see the top-level [README](README.md) for installation and configuration.

Releases are automated via `.github/workflows/release.yml`, which fires on any `v*.*.*` tag.

## One-time setup

1. Generate a NuGet API key at <https://www.nuget.org/account/apikeys>.
2. Add it as repo secret `NUGET_API_KEY` (Settings → Secrets and variables → Actions).

## Cutting a release

```bash
# Update "version" in server.json (both the top-level and packages[0])
# to match the tag you are about to push, then:
git commit -am "Release vX.Y.Z"
git tag vX.Y.Z
git push origin main --tags
```

The package version is injected from the git tag via `-p:Version=` in the release workflow, so there is no `<Version>` element to edit in the csproj.

The workflow builds, tests, packs, pushes the package to NuGet, and creates a GitHub Release with auto-generated notes.

## Submitting to the MCP registry

After NuGet has indexed the new version (5–15 minutes — verify by visiting `https://www.nuget.org/packages/dotnet-coverage-mcp/<version>`), publish to the [official MCP registry](https://registry.modelcontextprotocol.io) with the `mcp-publisher` CLI:

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

The registry verifies NuGet package ownership by scanning the published package's README for the literal marker `mcp-name: <namespace>/<server>` (already present in this repo's README near the top). If you fork or rename, update that marker to match your `server.json` `name` field, otherwise the publish will fail with a 400.

The `name` field in `server.json` is also case-sensitive — it must match your GitHub username's casing exactly (e.g. `io.github.Hyeonu-Cha/...`, not `io.github.hyeonu-cha/...`).
