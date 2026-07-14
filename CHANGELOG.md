# Changelog

All notable changes to dotnet-coverage-mcp are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Parameter-level `[Description]` attributes on every tool in `CoverageTools`.
  AI clients now see per-argument guidance (expected format, validation rules,
  common conventions) in the MCP `tools/list` schema, instead of having to
  infer it from the method-level description or parameter names.
- `skipReport` parameter on `RunTestsWithCoverage` (default `false`). When set,
  the reportgenerator JSON-summary step is skipped and only the Cobertura XML
  path is returned — a meaningful speedup for the inner test loop, where the
  per-file tools (`GetFileCoverage`/`GetUncoveredBranches`/`GetCoverageDiff`)
  read the XML directly and never touch `Summary.json`.
- `COVERAGE_MCP_REPORTGEN_TIMEOUT_MS` environment override for the
  reportgenerator timeout (default 60s), mirroring the existing
  `COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS` override.
- `COVERAGE_MCP_HANG_TIMEOUT_SECONDS` environment override for the per-test
  `dotnet test --blame-hang-timeout` (default 30s). Raise it when a project has
  legitimately long-running integration tests that would otherwise trip the
  hang dump and abort the entire run.
- `belowTarget`, `topN`, and `methodsPerClass` filters on `GetCoverageSummary`,
  so callers can request only the classes/methods still below target instead of
  the full report. Classes are now returned worst-branch-coverage first.

### Fixed
- `includeClass` on `RunTestsWithCoverage` now actually scopes coverage. It was
  emitted as an MSBuild `/p:Include` property, which `coverlet.collector` ignores
  (it reads filters from runsettings); it is now written to a generated
  runsettings file and passed with `--settings`, so coverage is restricted to the
  matching types as documented.
- `reportgenerator` stdout/stderr read tasks are now drained on cancel/timeout
  before the process is disposed, preventing an `UnobservedTaskException` once
  the killed process's pipes close. This matches the existing `dotnet test`
  drain path.
- Age-based `CleanupSession` (no `sessionId`) now also sweeps the unsuffixed
  `TestResults`/`coveragereport` directories created by single-agent runs,
  matching the `sessionId`-scoped cleanup which already removed them.

### Changed
- `RunDotnetTestAsync` now reports its result via a `TestRunOutcome` enum
  (`Success`/`BuildError`/`Cancelled`/`Timeout`) instead of overloading the
  `Error` string with `"cancelled"`/`"timeout"` sentinels, so consumers branch
  on a stable, refactor-safe signal.
- `GetCoverageDiff` copies the coverage-XML baseline with a streaming
  `File.Copy` (new `IFileService.AtomicCopyFile`) instead of reading the whole
  file into a string and writing it back, avoiding a Large Object Heap
  allocation per diff on large reports. Behaviour is unchanged.
- `ParseSummary` returns typed `SummaryClass`/`SummaryMethod` records instead of
  a `List<object>` of anonymous types, matching the other coverage result types.
  Serialized JSON output is unchanged.
- `CoverageTools` moved into the `DotNetCoverageMcp` namespace (it previously sat
  in the global namespace, inconsistent with every other type).
- Tool responses trimmed to cut token usage that persists in the agent
  transcript: `RunTestsWithCoverage` no longer echoes the full `dotnet test`
  stdout on success (paths only) and returns just the tail on failure;
  `GetSourceFiles` drops the flat `files` array that duplicated `batches`;
  `GetCoverageDiff` reports `unchangedCount` instead of listing every unchanged
  method; and `GetUncoveredBranches` omits matched methods with no uncovered
  branches and caps results at 25. Skill docs updated to match.

## [0.1.1] - 2026-05-05

### Added
- `mcp-name: io.github.Hyeonu-Cha/dotnet-coverage-mcp` marker in README so
  the MCP registry can verify NuGet package ownership.

### Fixed
- `server.json` now uses the case-correct `io.github.Hyeonu-Cha/...`
  namespace so `mcp-publisher publish` is accepted by the registry.

## [0.1.0] - 2026-05-05

First public release. Published to NuGet as `dotnet-coverage-mcp` 0.1.0
(installable via `dotnet tool install -g dotnet-coverage-mcp` or
`dnx dotnet-coverage-mcp`). Discoverable in the MCP registry under
`io.github.hyeonu-cha/dotnet-coverage-mcp`.

### Added
- `SECURITY.md` documenting threat model, private vulnerability reporting, and
  hardening recommendations.
- `CONTRIBUTING.md` with development setup and pull request guidelines.
- Issue and pull request templates under `.github/`.
- Dependabot configuration for NuGet and GitHub Actions ecosystems.
- CodeQL workflow running `security-and-quality` queries on every push, PR, and
  weekly cron.
- Least-privilege `permissions: contents: read` block on the `build` workflow.
- NuGet packaging metadata gated on `-p:IsPacking=true` so the default
  `dotnet pack` is a no-op (prevents accidental publishes of a malformed
  library package). The `McpServer` package type and embedded
  `.mcp/server.json` make the package discoverable in NuGet.org's MCP UI.

### Changed
- **Project renamed** from `CoverageMcpServer` to `dotnet-coverage-mcp`. NuGet
  package id is `dotnet-coverage-mcp`, tool command is `dotnet-coverage-mcp`,
  and the C# namespace is `DotNetCoverageMcp`. The new name reflects the
  .NET-only scope and aligns with MCP registry naming conventions.
- `build` workflow now runs on a `ubuntu-latest`, `windows-latest`, `macos-latest`
  matrix to verify cross-platform support claimed in the README.
- `AppendTestCode` now catches generic exceptions and returns a structured
  `insertFailed` error, matching the error-handling shape of peer tools.
- README documents the `CleanupSession` tool, the list-of-paths input to
  `GetSourceFiles`, the `includeClass` parameter on `RunTestsWithCoverage`,
  and the configurable `targetRate` on `GetFileCoverage`.

## Earlier history

Prior to the introduction of this changelog, changes were tracked only in commit
history and merged pull requests. See `git log` and the
[Pulls page](https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/pulls?q=is%3Apr+is%3Amerged)
for context.

Highlights:
- Concurrent multi-agent support via `sessionId`-scoped output directories,
  state files, and baselines.
- Path validation against `COVERAGE_MCP_ALLOWED_ROOT` for every tool.
- Cancellation tokens and a 10-minute default timeout on `dotnet test` and
  `reportgenerator` invocations
  (`COVERAGE_MCP_DOTNET_TEST_TIMEOUT_MS` overrides).
- Atomic writes for state files and inserted test code.
