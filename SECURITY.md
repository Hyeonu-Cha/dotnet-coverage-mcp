# Security Policy

## Supported Versions

dotnet-coverage-mcp is in early development. Security fixes target the latest released
version on the `main` branch only.

| Version | Supported |
| ------- | --------- |
| latest `main` | ✅ |
| older commits | ❌ |

## Reporting a Vulnerability

If you find a security issue, please **do not open a public GitHub issue**. Instead, use
**Report a vulnerability** on the repository's **Security** tab
(<https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/security/advisories/new>). It opens a
private advisory that only you and the maintainer can see, where the fix can be discussed
before anything is public.

Please include:

- A description of the issue and how to reproduce it
- The affected version (commit SHA or tag)
- Any proof-of-concept input or sample tool call

You should expect an initial acknowledgement within 7 days. We aim to release a fix
or mitigation within 30 days for high-severity issues.

## Threat Model

dotnet-coverage-mcp runs as a local stdio process launched by an MCP client (Claude Code,
Gemini CLI, etc.) and is not exposed to the network. The threat model assumes:

- The MCP client is trusted to launch the server with the user's permissions.
- Tool calls originate from a model that may be influenced by untrusted input
  (source files, test output, coverage XML).

The server defends against the following classes of issue:

| Risk | Mitigation |
| ---- | ---------- |
| Path traversal via tool arguments | Every tool validates `path`/`workingDir`/`testFilePath` against `COVERAGE_MCP_ALLOWED_ROOT`. Paths are resolved through symlinks/junctions before the check, so a link inside the root that points outside it is rejected; recursive `.cs` scans do not follow reparse points. Calls outside the configured root return `pathNotAllowed`. |
| File corruption from concurrent writes | `AtomicWriteFile` writes to `.tmp-{guid}` then renames; per-file `SemaphoreSlim` serializes writes within a process. |
| Test file mutation outside the target file | `append_test_code` only inserts into the file passed by the client; Roslyn AST insertion preserves existing structure, with a string fallback that targets the last class brace. |
| Long-running test processes | `dotnet test` invocations are wrapped with cancellation tokens and a default 10-minute timeout (override via `COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS`). |
| No allowed root configured | Not mitigated by default: with `COVERAGE_MCP_ALLOWED_ROOT` unset, every path is accepted and one warning is logged. A steered agent can then read any `.cs` file, write into any existing one, run any project's tests and remove the tool's own artifact folders anywhere. Set the variable (see below). The client's workspace roots are not used as a default: MCP deprecated roots (SEP-2577). |
| A `dotnet` executable planted in the repository | `dotnet` is started by absolute path: the dotnet host running the server, else the first on `PATH` (relative entries skipped), else under `DOTNET_ROOT`. Started by bare name, it was looked for in the server's current folder first, which MCP clients set to the repository. |
| Process memory written to disk by a hung test | The blame hang timeout runs with `--blame-hang-dump-type none`: the hung test is found from the Sequence file and the TRX, and no memory dump (test-host memory, environment variables included) lands in the project. |
| Entity expansion or external entities in untrusted XML | Coverage reports, TRX, Sequence and project files are read with DOCTYPEs ignored and no resolver, so neither internal nor external entities expand. |
| `cleanup_session` deleting the user's folders | It removes only the names the tool creates (`TestResults-mcp`, `TestResults-`/`coveragereport-` plus an 8-hex session key), never a prefix match or the bare `TestResults`. |
| A compromised build or test dependency publishing a release | The release workflow restores, builds, tests and packs with a read-only token. The job holding NuGet Trusted Publishing and attestation rights only downloads that package and publishes it, and the smoke test of the published package runs without publishing rights. |

## Hardening Recommendations

Operators running dotnet-coverage-mcp in shared environments should:

- Set `COVERAGE_MCP_ALLOWED_ROOT` to the repository root. When unset, the server
  logs a warning once and accepts any path.
- Run the server as the same user as the model client; do not elevate privileges.
- Treat the `.mcp-coverage/` directory as untrusted state and add it to
  `.gitignore`.

## Verifying a Release

Releases after 2.0.3 carry the `.nupkg` and a CycloneDX SBOM
(`dotnet-coverage-mcp.<version>.cdx.json`) listing every dependency the package ships.
The release workflow attests both: a signed statement, kept by GitHub, that the package
was built by `release.yml` from the tagged commit. To check a downloaded package
(`<version>` without the `v`):

```bash
gh release download v<version> -R Hyeonu-Cha/dotnet-coverage-mcp -p '*.nupkg'
gh attestation verify dotnet-coverage-mcp.<version>.nupkg --repo Hyeonu-Cha/dotnet-coverage-mcp
```

The attestation matches the GitHub Release file, not the copy nuget.org serves: nuget.org
adds its own repository signature (`.signature.p7s`) to every package, which changes its
bytes. For the nuget.org copy, `dotnet nuget verify` checks that signature.

## Out of Scope

The following are **not** considered vulnerabilities:

- The server reading/writing files inside the configured allowed root.
- The server invoking `dotnet test` with the user's
  environment.
- Tool calls that exhaust local disk space or CPU when invoked with very large
  inputs — operators should impose resource limits at the OS level.
