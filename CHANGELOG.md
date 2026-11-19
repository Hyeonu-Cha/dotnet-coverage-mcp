# Changelog

All notable changes to dotnet-coverage-mcp are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.0.4] - 2026-11-19

### Fixed
- `AppendTestCode` inserted the code exactly as the agent sent it: usually with LF line
  endings and no indentation, after two blank lines. In a CRLF file that left mixed line
  endings, and the new tests sat at column 0 inside the class, so the result failed
  `dotnet format --verify-no-changes` and needed reformatting before review. Inserted
  members now take the indentation of the members around them (or one level inside an
  empty class, with the file's indent character), keep their own nesting, use the file's
  line endings, and get one blank line from the previous member. Text inside multi-line
  string literals is left exactly as written, since tests compare against it.

### CI
- The release workflow now publishes to the MCP registry itself: a second job waits for
  NuGet to index the new version, then logs in with the workflow's GitHub OIDC token and
  publishes `server.json`. No device-code login is needed, and the registry's
  few-minute token no longer expires while NuGet is still indexing. `mcp-publisher` is
  pinned and its signature checked before it runs.
- Workflow hardening: every action is pinned to a commit SHA, checkouts no longer leave
  the job token in `.git/config` (`persist-credentials: false`), and every job has a
  `timeout-minutes` instead of GitHub's 6-hour default. A failed build uploads the test
  results (TRX) for that OS.
- A repository `nuget.config` restores from nuget.org only, so a machine-wide feed (a
  company one, say) is no longer queried for this repository's packages, nor able to
  supply one with the same name. The end-to-end fixtures, restored from a temp copy, get
  the same file.
- Releases carry a CycloneDX SBOM next to the `.nupkg`, and the workflow attests the
  package's build provenance and its SBOM before pushing it, so a download from the
  GitHub Release can be checked with `gh attestation verify` (SECURITY.md has the
  commands). Before publishing to the MCP registry, the workflow installs the version
  from nuget.org and runs the smoke test on that copy.

## [2.0.3] - 2026-11-19

### Fixed
- `CleanupSession` without a `sessionId` removed only artifacts older than
  `maxAgeMinutes` (120 by default), so the cleanup an agent runs when it finishes a
  single-agent session left that session's `TestResults-mcp` folder, `.coverage-state`
  and `.coverage-prev.xml` behind. Those are now removed whatever their age; other
  sessions' artifacts still go only once older than `maxAgeMinutes`. The
  `improve-test-coverage` skill now ends with the cleanup step.
- `DescribeTestTarget` looked for existing test files from the nearest `.git` or
  solution folder even when that was above `COVERAGE_MCP_ALLOWED_ROOT`, listing files
  outside it, and followed symlinks out of the tree. One unreadable folder anywhere in
  that tree ended the search with no results, so the agent could create a duplicate test
  file. The search now stops at the allowed root, does not follow links, and skips
  folders it cannot read.

### Security
- On Linux, `COVERAGE_MCP_ALLOWED_ROOT` was checked ignoring case, so a root of
  `/home/u/repo` also admitted `/home/u/Repo`, a different folder. Paths are now compared
  case-sensitively on Linux; Windows and macOS, whose file systems ignore case by default,
  are unchanged.

## [2.0.2] - 2026-11-18

### Fixed
- A build that failed only because its output files were in use was reported as
  `buildError`, so the agent went looking for a mistake in tests that were fine. This
  happens when another `dotnet test` or build of the same project, an IDE build or a
  still-running test host holds the files (`CS2012`, `CS0016`, `MSB3021`, `MSB3027`, or
  "being used by another process"). `RunTestsWithCoverage` now returns `projectBusy` for
  it, with the locking errors and a note to run again. A real compile error alongside
  still gives `buildError`.
- `RunTestsWithCoverage` without a `sessionId` wrote to `<project>/TestResults` and deleted
  it at the start of every run. That is the folder plain `dotnet test` and Visual Studio
  write to by default, so results you produced yourself were wiped. Single-agent runs now
  use `TestResults-mcp`. The age-based `CleanupSession` sweep no longer deletes the bare
  `TestResults` or `coveragereport` folders either; it removes only the hyphen-suffixed
  folders the tool creates (`coveragereport-*` ones are left over from before 2.0.1).
- `AppendTestCode` removed the UTF-8 byte-order mark from the files it edited, and
  rewrote UTF-16 files as UTF-8: every write used UTF-8 without a BOM. Visual Studio saves
  C# files with a BOM, so the first edit showed the first line as changed and broke an
  `.editorconfig` `charset = utf-8-bom`. A rewritten file now keeps the encoding its BOM
  indicates.

## [2.0.1] - 2026-11-18

2.0.0 was tagged but never published to NuGet; 2.0.1 is the first 2.x release there.
Upgrading from 1.x, read the 2.0.0 notes below as well.

### Removed
- **`reportgenerator` is no longer needed.** `RunTestsWithCoverage` used to run it
  after every passing test run to write a `Summary.json` that only
  `GetCoverageSummary` read, and `GetCoverageSummary` now reads the Cobertura XML.
  Without the global tool installed, a default run failed with `reportFailed` even
  though the tests had passed. The step is gone, along with the `skipReport`
  parameter (clients that still pass it are not rejected) and the
  `COVERAGE_MCP_REPORTGEN_TIMEOUT_MS` setting. The result now gives only the
  Cobertura XML path.

### Fixed
- `GetCoverageSummary` returned `parseFailed` on every real run. It parsed a
  `Summary.json` shape (`assemblies[].classes[].methods[]` with `linecoverage`)
  that `reportgenerator` does not produce: its JsonSummary nests classes under
  `classesinassembly`, names the line rate `coverage`, makes `branchcoverage`
  nullable, and carries no per-method data at all. `GetCoverageSummary` now
  summarises the Cobertura XML directly, through the same parsed,
  compiler-generated-folded model the per-file tools already use, so it keeps its
  method-level, worst-branch-first output. Its first argument is now
  `coberturaXmlPath` (with the usual `.coverage-state` / `sessionId` fallback that
  `GetFileCoverage` and friends have).
- `AppendTestCode` wrote the supplied code into whatever file `testFilePath`
  pointed at, including a non-`.cs` file such as `settings.json` or a `.csv`,
  corrupting it and reporting success. It now requires a `.cs` target and rejects
  anything else with `invalidParameter`, leaving the file untouched. Any `.cs` file
  is still accepted: telling a test file from a production one is up to the client.
- `CleanupSession` with a `sessionId` also deleted the **unsuffixed**
  `TestResults`/`coveragereport` directories, wiping output owned by a single-agent run or by
  `dotnet test` / Visual Studio (which write `TestResults` by default). Session-scoped cleanup
  now removes only that session's own `TestResults-{hash}` / `coveragereport-{hash}` artifacts;
  the age-based sweep (no `sessionId`) remains the path that clears unsuffixed dirs.
- Path validation could be escaped with a symlink or junction. `COVERAGE_MCP_ALLOWED_ROOT`
  was enforced with a purely lexical check, so a link **inside** the root that pointed
  **outside** it (`root/link/secret.cs`) passed, letting a read-only tool return an arbitrary
  file's contents, and a folder scan followed such links out of the tree. Paths (and the root
  itself) are now resolved through symlinks/junctions before the containment check, and the
  `GetSourceFiles` / `GetCoverageGaps` folder and project scans no longer descend into or return
  reparse points.
- `RunTestsWithCoverage` passed `testProjectPath` to `dotnet test` verbatim as its
  first argument, so a value like `--help` or `-bl:evil.binlog` was parsed as a
  CLI/MSBuild option (`--help` printed usage; an MSBuild logger switch wrote a file
  outside the allowed root). The path is now resolved to a rooted path, must end in
  `.csproj`, and must exist; anything else is rejected with `invalidParameter` or
  `fileNotFound` before any process starts. The existence check matters on Linux and
  macOS, where `/p:Foo=bar.csproj` is already a rooted path and `dotnet` would still
  read it as a property switch. A relative `testProjectPath` is also resolved now,
  instead of failing with `MSB1009`.

## [2.0.0] - 2026-11-17

### Upgrading from 1.x
- **.NET 10 or later is required** to run the tool (it was .NET 9).
- **Method and class names in coverage results changed.** Async methods,
  lambdas and local functions are reported under the method they belong to
  (`AddAsync`, not `MoveNext` in `<AddAsync>d__2`); a class no longer appears
  once per state machine; overloads are labelled `Name (line N)`. Coverage
  diffs against a 1.x baseline will show these as removed and re-added once.
- **`GetUncoveredBranches` reports more, in a new format.** Half-covered
  branches are now listed, as `condition N (jump, 50% covered)`.
- **New error types** clients may see: `testsFailed` (was `buildError` for a
  failing test), `noTestsMatched`, `syntaxError`, `insertFailed`,
  `missingExtension`, `ambiguous`, `noTestableTypes`.
- **`filter` is passed to `dotnet test --filter` unchanged** and is optional;
  `*` or omitting it runs every test.
- **`GetFileCoverage.sourceFile`** is the report path that matched, not the
  string passed in.
- New tools: `GetCoverageGaps`, `GetTestEnvironment`, `DescribeTestTarget`.

### Changed
- **Breaking:** the tool now targets .NET 10 (was .NET 9, which is out of
  support) and sets `RollForward=Major`. It runs on .NET 10 and any later
  runtime; a machine with only the .NET 9 runtime can no longer run it.
  Previously the package started only where a .NET 9 runtime was installed,
  so `dnx`-based installs on a .NET 10-only machine failed at startup.
- `RunTestsWithCoverage` writes a TRX file (`--logger trx`) and reports
  `Passed / Failed / Skipped` counts on success.
- Cobertura reports are parsed once and cached by path, write time and size, so
  repeated `GetFileCoverage` / `GetUncoveredBranches` / `GetCoverageDiff` calls
  on the same run do not re-read the XML.

### Fixed
- On Windows, writing a file right after it was last written could fail with
  "Access to the path is denied": replacing a file that antivirus or the search
  indexer briefly holds open without delete-sharing is refused. `AppendTestCode`
  and the coverage baseline write now retry the replace for up to ~300 ms.
- `RunTestsWithCoverage` with `filter: "*"` ran zero tests: VSTest has no wildcard,
  so `FullyQualifiedName~*` matched nothing, yet the run reported success with an
  all-zero coverage report. `*`, an empty filter, or no filter now runs every test,
  and `filter` is optional.
- Filters containing a dot (e.g. `OrderServiceTests.PlacesOrder`, the example in
  the tool's own description) matched nothing, because they were turned into an
  exact `FullyQualifiedName=` match that needs the full namespace. The filter is
  now passed to `dotnet test --filter` unchanged, so dotted names, `A|B` and
  `Category=Unit` all work.
- A failing test was reported as `buildError` with a 1500-character log tail,
  and the coverage report was discarded even though every other test ran.
  Failing tests now return `testsFailed` with each failure's test name,
  assertion message and top stack frames in user code (up to 10), plus the
  Cobertura path, which stays usable by `GetFileCoverage` and friends.
- A run where no test matched the filter is now `noTestsMatched` instead of
  success with an all-zero report.
- `buildError` now lists the compiler errors, each once, instead of a log tail.
- `AppendTestCode` wrote syntactically invalid code into the test file and
  reported success: the Roslyn path rejected it, then the string fallback
  inserted it anyway, and the agent only found out at the next build. Invalid
  code (including namespace/class wrappers and bare statements) is now
  rejected with `syntaxError`, naming the compiler error and quoting the
  offending line, and the file is not touched.
- `AppendTestCode` without an anchor put tests into the last type in the file,
  counting nested types, so a nested fake, a trailing helper class or a
  test-data `record` received them. It now picks the last top-level class
  that has test attributes (xUnit, NUnit or MSTest), falling back to the last
  top-level class. An `insertAfterAnchor` is now found in whichever class
  holds it, not only the last one.

- Docs: the `improve-test-coverage` and `scaffold-test-files` skills and the
  agent told the model to use NUnit attributes (`[SetUp]`, `[Test]`,
  `[TestCase]`, `[TestFixture]`) whatever the project used; they now say to
  follow the framework the test project references. README notes that clients
  list the tools in snake_case, and CONTRIBUTING documents the
  `<type>/<summary>` branch naming rule.

- `GetFileCoverage` could not find a file by the absolute path `GetSourceFiles`
  returns (`noMatch`), because the report stores paths relative to its
  `<source>` root. It also matched on a plain suffix, so `Service.cs` pulled in
  `CoberturaService.cs` and `FileService.cs`, and `OrderController.cs` pulled
  in `AdminOrderController.cs`, giving a wrong `allMeetTarget`. It now accepts
  absolute, repo-relative and bare names, matches on whole path segments, and
  returns `ambiguous` with the candidate paths when a bare name exists in
  several folders. `sourceFile` in the result is the path that matched.
  An absolute path that goes through a symlinked folder (e.g. macOS
  `/var` → `/private/var`) is resolved before giving up, since coverlet
  records the resolved path.

- `GetSourceFiles` on a `.csproj` scanned the whole folder, so a test project
  nested under it (a common layout) contributed its test files as targets. A
  project scope now stops at nested projects.

- `GetUncoveredBranches` missed the most common gap. It listed only conditions
  at `0%`, but coverlet records an `if` that only ever went one way as a single
  jump condition at `50%`, so one-sided branches never showed up. Any condition
  below 100% is now listed, with its coverage
  (`condition 23 (jump, 50% covered)`), and a line with branch totals but no
  condition elements is reported as `N of M branch outcomes not covered`.
- Async methods could not be found by name: coverlet reports their bodies as
  `MoveNext` inside a generated `<Name>d__N` class, so `GetUncoveredBranches`
  returned `noMatch`, `GetFileCoverage` listed them as separate classes, and
  their class's own rate left their code out. Lambdas (`<>c`,
  `<>c__DisplayClassN_M`), async lambdas and local functions (`g__`) had the
  same problem. They are now folded into the method they came from: lines are
  merged per line number, branches from all parts are counted, overloads stay
  separate (matched by the compiler's ordinal, labelled `Name (line N)`), and
  generic arity is shown as in source (`Repo<T>`). `GetCoverageDiff` keys no
  longer include the state machine's ordinal, so adding a method above an async
  method no longer shows it as removed and re-added.

### Added
- `DescribeTestTarget`: for a source file, per class, the constructor
  dependencies with how to provide each in a test (mock an interface,
  `NullLogger`, `Options.Create`, a real instance for sealed or concrete
  classes, SQLite in-memory for a `DbContext`), public and internal methods
  with signatures and decision points, calls a test cannot control
  (`DateTime.Now`, `File.*`, `Guid.NewGuid()`, `new HttpClient()`...) with
  the usual seam, a suggested test type (unit / integration / either) with
  reasons, and existing test files. Syntax-only: dependency types are looked
  up in the file's project, so no build is needed. The scaffold and coverage
  skills call it instead of reading whole source files.
- MCP tool annotations on every tool: the six query tools are read-only and
  idempotent, `cleanup_session` is the only destructive one (the SDK's default
  is destructive, so the others now say they are not), and only
  `run_tests_with_coverage` is open-world. The smoke test checks them on every
  PR.
- Server instructions in the `initialize` result describing the intended
  workflow, so clients put it in the model's context before a tool is chosen.
- Progress notifications from `run_tests_with_coverage` when the client sends
  a progress token: restore, each project built, tests started, and the result
  line, read from `dotnet test` output as it runs (VSTest and MTP).
  ANSI colour codes, which Microsoft.Testing.Platform writes on CI runners,
  are stripped from the captured output first; they otherwise hid the summary
  lines from the progress reporting and from the test-count fallback.
- Microsoft.Testing.Platform support in `RunTestsWithCoverage`. When the
  repository's `global.json` selects MTP (on the .NET 10 SDK and later, MTP
  projects cannot run in `dotnet test`'s default VSTest mode at all), the run
  uses `dotnet test --project ... -- --coverage --coverage-output-format
  cobertura`, adds `--report-trx` only when the TrxReport extension is
  referenced (MTP rejects options of missing extensions with exit code 5), and
  classifies the run by MTP's exit codes (2 failed, 8 zero tests, 5 missing
  extension → new `missingExtension` error naming the package), falling back
  to the console summary for counts when there is no TRX. Microsoft's Cobertura
  dialect (`<guid>.cobertura.xml`, absolute paths, generated types nested with
  `.`) is handled by every coverage tool. `includeClass` is rejected under MTP
  because it is a coverlet setting. Verified end to end on xUnit v3 + MTP in CI.
- `GetTestEnvironment`: detects a test project's framework (xUnit v2/v3, NUnit,
  MSTest) and the attributes to use for tests, data-driven cases, setup and
  teardown; the runner (VSTest, or Microsoft.Testing.Platform via MSTest.Sdk,
  `EnableMSTestRunner` / `EnableNUnitRunner` /
  `UseMicrosoftTestingPlatformRunner` in the project or a Directory.Build.props,
  or `global.json`); the coverage collector; mocking and assertion libraries;
  integration-test packages. Reads `obj/project.assets.json` when restored, the
  `.csproj` otherwise, and warns about a missing coverage collector. The
  skills call it instead of guessing the framework.
- `GetCoverageGaps`: one call per test cycle for a whole scope (file, folder,
  `.csproj` or list). Per file it reports whether the file meets the target on
  both lines and branches; for each file below it, the fewest methods that
  close that file's gap, ranked by absolute uncovered lines and branch
  outcomes (only for the metric still below target), with uncovered line
  ranges, uncovered branches and the source around them. Replaces one
  `GetFileCoverage` per file plus one `GetUncoveredBranches` per method; the
  `improve-test-coverage` skill now uses it.
- `GetSourceFiles` leaves files that are not test targets out of the batches
  for folder and project scopes, and lists them under `excluded` with a kind
  and reason: entry points (`Program.cs`, `Startup.cs`, top-level statements,
  `static Main`), DI registration (static classes of `IServiceCollection` /
  `IApplicationBuilder` / ... extension methods), generated code (`*.g.cs`,
  `*.designer.cs`, `<auto-generated>`, `[GeneratedCode]`,
  `[ExcludeFromCodeCoverage]`), data-only models (properties, fields, enums,
  interfaces; a model with real methods is still a target), and test-project
  files. Detection reads the syntax tree; no build is needed. Files passed
  explicitly are always kept, which is how to override it.
- `targetClass` parameter on `AppendTestCode` to name the class to insert into,
  nested classes included. When it cannot be honoured the call fails with
  `insertFailed` instead of falling back to string insertion.
- End-to-end tests that run `RunTestsWithCoverage` with real `dotnet test`
  against a small fixture project (`DotNetCoverageMcp.Tests/Fixtures`).

### CI
- Every PR now packs the tool, installs it from the local package, starts it
  over stdio, and checks that `tools/list` returns every tool.
- The release workflow rejects a tag that is not on `main` or does not match
  both version fields in `server.json`, runs the same smoke test before
  publishing, and creates the GitHub Release with `gh` instead of a
  third-party action.
- CI reports line/branch coverage in the job summary and cancels superseded PR
  runs. Dependabot groups minor/patch bumps into one weekly PR.

## [1.0.0] - 2026-07-14

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
