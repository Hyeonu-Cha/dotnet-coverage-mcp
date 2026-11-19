# dotnet-coverage-mcp

<!-- mcp-name: io.github.Hyeonu-Cha/dotnet-coverage-mcp -->

[![build](https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/actions/workflows/dotnet.yml/badge.svg)](https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/actions/workflows/dotnet.yml)
[![tests](https://img.shields.io/github/actions/workflow/status/Hyeonu-Cha/dotnet-coverage-mcp/dotnet.yml?branch=main&label=tests)](https://github.com/Hyeonu-Cha/dotnet-coverage-mcp/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/dotnet-coverage-mcp.svg)](https://www.nuget.org/packages/dotnet-coverage-mcp/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

An MCP (Model Context Protocol) server that gives AI assistants — Claude Code, Gemini CLI, and others — direct access to .NET test-coverage tooling. Run dotnet test, parse Cobertura XML, identify uncovered branches, diff coverage between runs, and append test code — all over stdio.

## Purpose

This server lets an AI assistant run unit tests, collect coverage data, and analyse results — all without leaving the chat. Instead of manually running `dotnet test` and parsing reports, the AI can call the server's tools directly to:

- Discover source files and build smart batches by line budget
- Run a filtered set of tests and collect coverage
- Read compact, AI-optimised coverage summaries (method-level line/branch rates)
- Check per-file coverage against a configurable target rate (default 80%)
- Identify uncovered branches as structured JSON
- Diff coverage between runs to see only what changed
- Append new test code to an existing test file with atomic writes

## How It Works

The server starts as a console process and communicates over **stdio** using the MCP protocol. An MCP-compatible client (Claude Code, Gemini CLI, etc.) launches the process and calls its tools as if they were functions.

```
AI Client  <--stdio/MCP-->  dotnet-coverage-mcp  <--shell-->  dotnet test
```

## Available Tools

MCP clients list these tools in snake_case (`run_tests_with_coverage`, `get_file_coverage`, ...). The names below are the C# method names used throughout these docs.

| Tool | Description |
|------|-------------|
| `GetSourceFiles` | Discover `.cs` files from a file, folder, or `.csproj` project. For a folder or project, files that are not test targets (entry points, DI registration, generated code, data-only models, test projects) are left out and listed under `excluded` with the reason; a `.csproj` scope also stops at nested projects. Returns file metadata (lines, method count) and smart batches grouped by `lineBudget`. |
| `RunTestsWithCoverage` | Run `dotnet test` with code coverage (coverlet on VSTest; Microsoft's CodeCoverage extension when `global.json` selects Microsoft.Testing.Platform). Returns pass/fail/skip counts and the path to the Cobertura XML that every other coverage tool reads. Distinguishes `testsFailed` (with each failure's message and stack, coverage kept), `noTestsMatched`, `buildError` (compiler errors only) and `projectBusy` (the build's output files were held by another build, IDE or test host; run again). Supports `forceRestore` and `sessionId` for concurrent isolation. |
| `GetCoverageSummary` | Summarise class/method coverage from the Cobertura XML, sorted worst-first by branch coverage. Optional `belowTarget`/`topN`/`methodsPerClass` filters trim the response to what still needs work. |
| `GetFileCoverage` | Get coverage for a single source file from Cobertura XML. Returns `allMeetTarget` (true when all classes meet the configured `targetRate` for both line and branch coverage; default 0.8). Supports `sessionId`. |
| `DescribeTestTarget` | For a source file, per class: constructor dependencies with how to provide each in a test (mock, `NullLogger`, `Options.Create`, real instance for sealed/concrete classes, SQLite for a `DbContext`), public/internal methods with signatures and decision points, calls a test can't control (`DateTime.Now`, `File.*`, `Guid.NewGuid()`...) with the usual seam, a suggested test type with reasons, and existing test files. Syntax-only. |
| `GetTestEnvironment` | Detect a test project's stack: framework (xUnit v2/v3, NUnit, MSTest) with the attributes to use, runner (VSTest or Microsoft.Testing.Platform), coverage collector, mocking and assertion libraries, integration-test packages, plus warnings such as a missing coverage collector. Reads `obj/project.assets.json` when restored. |
| `GetCoverageGaps` | For a scope (file, folder, `.csproj` or list), report per file whether it meets `targetRate` on lines and branches, and for each file below it the fewest methods that close its gap, with uncovered line ranges, uncovered branches and the source around them. One call per test cycle. Non-target files are skipped as in `GetSourceFiles`. |
| `GetUncoveredBranches` | Find uncovered branch conditions for methods matching a given name. Returns all matching methods with partial name support. Supports `sessionId`. |
| `GetCoverageDiff` | Compare current Cobertura XML against baseline. Shows method-level changes including new and removed methods. Supports `sessionId` for concurrent isolation. |
| `AppendTestCode` | Insert or append C# test code into a test file. Rejects code that is not valid C# member syntax with `syntaxError`, leaving the file unchanged. Supports anchor-based insertion with whitespace-tolerant fallback matching; a fallback insertion that would break a file that compiled is refused with `insertFailed`. The inserted code is laid out like the members around it (indentation, the file's line endings, one blank line between members). Uses atomic writes to prevent file corruption. |
| `CleanupSession` | Remove session state files and the `TestResults-*` directories the tool created (never the bare `TestResults` that `dotnet test` and Visual Studio use). Pass the `sessionId` you ran with to remove that session's artifacts; omit it to remove the single-agent ones (`TestResults-mcp/`, `.coverage-state`, `.coverage-prev.xml`) plus other sessions' artifacts older than `maxAgeMinutes` (default 120). |

### Protocol details

- **Tool annotations.** The six query tools are marked `readOnlyHint` + `idempotentHint`, so clients that auto-approve read-only tools won't prompt for them. `run_tests_with_coverage`, `append_test_code` and `get_coverage_diff` write files but are not destructive. Only `cleanup_session` is `destructiveHint: true`. Only `run_tests_with_coverage` is `openWorldHint: true`, since a restore can reach NuGet.
- **Server instructions.** The `initialize` result carries a short workflow (environment → run → gaps → append → repeat) that clients put in the model's context.
- **Progress.** When the client sends a progress token, `run_tests_with_coverage` reports milestones while `dotnet test` runs: restore, each project built, tests started, the result line.

## Batch Workflow

For projects with many source files, the recommended workflow is:

1. **Discover** — Call `GetSourceFiles` on a folder or `.csproj` to get all files and smart batches
2. **Run once** — Call `RunTestsWithCoverage` without a filter (runs every test) to collect coverage across all files
3. **Find the gaps** — Call `GetCoverageGaps` for the batch: per file below target, the methods to test, with the uncovered branches and source around them (no test re-run)
4. **Write tests** — Use `AppendTestCode` to add test methods
5. **Re-run and diff** — Run tests once, call `GetCoverageDiff` to verify improvement
6. **Repeat** — Continue until batch files meet the target rate (default 80%) or 3 cycles with no improvement, then move to next batch
7. **Clean up** — Once the goal is reached, call `CleanupSession` (with the same `sessionId`, if you used one) to remove `TestResults-mcp/` or `TestResults-{hash}/` and the session state files. These are **not** deleted automatically — they persist until `CleanupSession` is called.

This minimises `dotnet test` invocations (the main bottleneck) while still tracking per-file progress.

## Concurrency

Multiple AI agents can run in parallel by passing a `sessionId` to each tool call, which isolates their coverage artifacts:

- **Isolated output directories** — `RunTestsWithCoverage` creates `TestResults-{hash}/` per session, preventing one agent from deleting another's XML mid-parse
- **Scoped state files** — Coverage state is written to `.mcp-coverage/.coverage-state-{hash}`, so `ResolveCoberturaPath` resolves to the correct XML for each session
- **Scoped baselines** — `GetCoverageDiff` stores baselines as `.coverage-prev-{hash}.xml` per session
- **Atomic writes** — All file writes (state files and test code) use write-to-temp-then-rename to prevent corruption from race conditions or process crashes

> **Limitation — build outputs are not session-scoped.** `sessionId` isolates coverage *artifacts*, not the .NET *build*. `dotnet test` compiles the target project into its shared `obj/` and `bin/`, which are not per-session, so two agents running `RunTestsWithCoverage` against the **same** test project at the same time collide on those outputs. The run that loses returns `projectBusy` (its build hit `CS2012`/`MSB3027`, "being used by another process"), not `buildError`, and succeeds when run again after the other build finishes. Run parallel agents against **different** test projects, or on separate working copies of the repo. Multiple agents on one project are fine as long as their `dotnet test` builds don't overlap.

Without `sessionId`, tools use shared defaults — safe for single-agent use.

## Requirements

- **.NET 10 (or later)** to run the tool — [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download). The projects it tests do not have to be .NET 10: `net8.0` and `net9.0` test projects work, including in a repository whose `global.json` pins an older SDK. As with any `dotnet test`, the runtime the tests target must be installed.
- **A coverage collector in the test project.** On VSTest (the default), `coverlet.collector`. On Microsoft.Testing.Platform, `Microsoft.Testing.Extensions.CodeCoverage`, plus `Microsoft.Testing.Extensions.TrxReport` for per-failure details. MTP is used when the repository's `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`, which the .NET 10 SDK requires for MTP projects anyway. `GetTestEnvironment` reports what a project has and what is missing.
- An MCP-compatible client (Claude Code, Gemini CLI, etc.)
- **`COVERAGE_MCP_ALLOWED_ROOT`** — recommended. Set to your repository root to restrict every tool's filesystem access to that subtree. Any path passed by the client outside this root is rejected with `pathNotAllowed`. When unset, the server logs a warning once and accepts any path (backward-compatible, but not recommended for shared environments).

  ```bash
  export COVERAGE_MCP_ALLOWED_ROOT=/path/to/your/repo
  ```

## Install

Install the server as a global .NET tool from NuGet:

```bash
dotnet tool install --global dotnet-coverage-mcp
```

After install, the `dotnet-coverage-mcp` command is on your PATH.

## Build & Run (from source)

```bash
cd <path-to-dotnet-coverage-mcp>

# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run
```

The server will start and wait for MCP messages over stdin/stdout.

## MCP Client Configuration

After installing the global tool (`dotnet tool install --global dotnet-coverage-mcp`),
register the server with your MCP client. Set `COVERAGE_MCP_ALLOWED_ROOT` to the
repository you want the server to operate on.

### Claude Code

```bash
claude mcp add coverage --env COVERAGE_MCP_ALLOWED_ROOT=/path/to/your/repo -- dotnet-coverage-mcp
```

### Claude Desktop

Add to `claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "coverage": {
      "command": "dotnet-coverage-mcp",
      "env": {
        "COVERAGE_MCP_ALLOWED_ROOT": "/path/to/your/repo"
      }
    }
  }
}
```

### Cursor

Add to `~/.cursor/mcp.json` (global) or `.cursor/mcp.json` (per-project):

```json
{
  "mcpServers": {
    "coverage": {
      "command": "dotnet-coverage-mcp",
      "env": {
        "COVERAGE_MCP_ALLOWED_ROOT": "/path/to/your/repo"
      }
    }
  }
}
```

### VS Code (GitHub Copilot)

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "coverage": {
      "type": "stdio",
      "command": "dotnet-coverage-mcp",
      "env": {
        "COVERAGE_MCP_ALLOWED_ROOT": "/path/to/your/repo"
      }
    }
  }
}
```

### Run from source

To run from source instead of the global tool, use `dotnet run`:

```json
{
  "mcpServers": {
    "coverage": {
      "command": "dotnet",
      "args": ["run", "--project", "<path-to-dotnet-coverage-mcp>"],
      "transport": "stdio"
    }
  }
}
```

Or point directly at the compiled executable:

```json
{
  "mcpServers": {
    "coverage": {
      "command": "<path-to-dotnet-coverage-mcp>\\bin\\Debug\\net10.0\\DotNetCoverageMcp.exe",
      "transport": "stdio"
    }
  }
}
```

## Tool Parameters

### `GetSourceFiles`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `path` | string | Yes | Path to a `.cs` file, folder, `.csproj` project, or a comma-separated list of `.cs` files. Files you name directly are always kept, even ones a folder scan would exclude |
| `lineBudget` | int | No | Max total lines per batch (default: 300). Small files are grouped together; large files get their own batch. |

### `RunTestsWithCoverage`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testProjectPath` | string | Yes | Path to the `.csproj` test project. Resolved to a rooted path and required to end in `.csproj` and exist; anything else (e.g. a CLI option) is rejected with `invalidParameter` or `fileNotFound`. |
| `filter` | string | No | Passed to `dotnet test --filter`. A plain name matches tests whose fully qualified name contains it (`OrderServiceTests`, `OrderServiceTests.PlacesOrder`); full VSTest expressions also work (`OrderTests\|PaymentTests`, `Category=Unit`). Omit or pass `*` to run every test. |
| `workingDir` | string | No | Working directory; defaults to the project directory |
| `forceRestore` | bool | No | When `true`, skips the `--no-restore` flag. Use after scaffolding a new test project or adding NuGet packages. |
| `sessionId` | string | No | Isolates the output directory (`TestResults-{hash}/`) and state files for concurrent multi-agent use. Without it, results go to `TestResults-mcp/`. |
| `includeClass` | string | No | Restrict coverage collection to types matching this name (coverlet `Include` filter, applied via a generated runsettings file passed with `--settings`). Independent of `filter` — pass an explicit value to scope coverage; omit it to collect coverage for everything the run touches. Namespace-qualified names are not supported. |

### `GetCoverageSummary`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (returned by `RunTestsWithCoverage`). Falls back to `.mcp-coverage/.coverage-state` if not found. |
| `belowTarget` | double | No | When set (a fraction in `[0,1]`, e.g. `0.8`), return only classes whose line OR branch coverage is below this threshold. Omit for all classes. |
| `topN` | int | No | Return only the N lowest-branch-coverage classes (results are sorted worst-first). Omit for all classes. |
| `methodsPerClass` | int | No | Keep at most this many lowest-branch-coverage methods per class, trimming the rest. Omit to keep all methods. |
| `sessionId` | string | No | Resolves the session-scoped state file for concurrent isolation. |

### `GetFileCoverage`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (falls back to `.mcp-coverage/.coverage-state` if not found) |
| `sourceFileName` | string | Yes | File to look up: an absolute path (as `GetSourceFiles` returns), a repo-relative path (`Services/ExampleService.cs`) or a bare name (`ExampleService.cs`). Matched on whole path segments; a bare name found in several folders returns `ambiguous` with the candidates. The response's `sourceFile` is the path that matched. |
| `sessionId` | string | No | Resolves session-scoped state file for concurrent isolation. |
| `targetRate` | double | No | Coverage threshold (0.0–1.0) used to compute `allMeetTarget`. Default `0.8`. |

### `DescribeTestTarget`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sourceFilePath` | string | Yes | Full path to the `.cs` file to be tested |

Dependencies are classified from where their types are declared in the file's project (interface, abstract, sealed, `DbContext` subclass...), falling back to well-known framework types and the `I`-prefix convention for external types. Classes without public/internal methods (records, DTOs, exceptions) are not listed.

### `GetTestEnvironment`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testProjectPath` | string | Yes | Full path to the test project's `.csproj` |

Returns a one-line `summary` (e.g. `xunit 2.9.3 on VSTest, coverlet.collector, Moq + FluentAssertions`), the detected packages, `conventions` (test, data-driven, setup and teardown attributes for the framework) and `warnings`.

### `GetCoverageGaps`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (falls back to `.mcp-coverage/.coverage-state` if not found) |
| `path` | string | Yes | Scope: a `.cs` file, folder, `.csproj`, or comma-separated list of `.cs` files (e.g. a batch) |
| `targetRate` | double | No | Per-file target (0.0–1.0) for both line and branch coverage. Default `0.8`. |
| `maxFiles` | int | No | Below-target files returned in detail, smallest gap first; the rest are listed with rates only. Default `5`. |
| `maxMethodsPerFile` | int | No | Maximum methods returned per file. Default `5`. |
| `includeSource` | bool | No | Include the source lines around each gap (2 lines of context, at most 30 per method). Default `true`. |
| `sessionId` | string | No | Resolves the session-scoped state file for concurrent isolation. |

Methods are ranked by how many uncovered lines and branch outcomes they hold, counting only the metric that is still below target, and picked until the file's gap is closed (`closesGap`). Files in the scope with no coverage data are listed under `notInReport`. A file edited after the coverage run is marked `sourceChangedSinceRun: true` and its source left out, since the report's line numbers may no longer match it; run the tests again first.

### `GetUncoveredBranches`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to `coverage.cobertura.xml` (falls back to `.mcp-coverage/.coverage-state` if not found) |
| `methodName` | string | Yes | Method name as written in source (partial match supported; returns all matching methods). Async methods, lambdas and local functions are reported under the method they belong to; overloads are labelled `Name (line N)`. |
| `sessionId` | string | No | Resolves session-scoped state file for concurrent isolation. |

### `GetCoverageDiff`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `coberturaXmlPath` | string | Yes | Path to the current `coverage.cobertura.xml` |
| `workingDir` | string | No | Directory for storing baseline; defaults to the XML's parent directory |
| `sessionId` | string | No | Isolates baseline as `.coverage-prev-{hash}.xml` and resolves session-scoped state file. |

### `AppendTestCode`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `testFilePath` | string | Yes | Full path to the target `.cs` test file. A path that is not a `.cs` file is rejected with `invalidParameter`. |
| `codeToAppend` | string | Yes | C# code to insert |
| `insertAfterAnchor` | string | No | If provided, inserts code after the last member containing this string (whitespace-tolerant), in whichever class holds it. If omitted, appends at the end of the target class. |
| `targetClass` | string | No | Simple name of the class to insert into, nested classes included. If omitted, the file's test class is used: the last top-level class with test attributes (`[Fact]`, `[Test]`, `[TestMethod]`, `[TestFixture]`, ...), else the last top-level class. |

### `CleanupSession`
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `workingDir` | string | Yes | Project working directory containing `.mcp-coverage/` and TestResults artifacts |
| `sessionId` | string | No | When set, removes only state files and directories scoped to this session. When omitted, removes the single-agent artifacts whatever their age. |
| `maxAgeMinutes` | int | No | When `sessionId` is omitted, also removes other sessions' artifacts older than this many minutes. Default `120`. |

## State Files

All state files are written to a `.mcp-coverage/` subdirectory inside the working directory, keeping the project root clean. Add `.mcp-coverage/` to the target repository's `.gitignore`.

| File | Purpose |
|------|---------|
| `.coverage-state` | Default Cobertura XML path for single-agent use |
| `.coverage-state-{hash}` | Session-scoped Cobertura XML path |
| `.coverage-prev.xml` | Default coverage baseline for diff |
| `.coverage-prev-{hash}.xml` | Session-scoped coverage baseline |

## Plugin (Skills & Agent)

This repo includes a `plugin/` directory with Claude Code skills and an agent definition for guided test coverage workflows:

```
plugin/
├── plugin.json
├── agents/
│   └── test-coverage.agent.md
└── skills/
    ├── scaffold-test-files/     — Create test directories and files mirroring source structure
    ├── run-coverage/            — Run tests and view coverage reports
    ├── analyze-coverage-gaps/   — Find uncovered branches and compare diffs
    └── improve-test-coverage/   — Iterative loop to reach 80% coverage
```

The skills support NUnit, xUnit, and MSTest with framework-agnostic reference docs in `references/unit.md` and `references/integration.md`.

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.Extensions.Hosting` | 10.0.15 | DI and hosting |
| `ModelContextProtocol` | 2.4.0 | MCP server framework |
| `Microsoft.CodeAnalysis.CSharp` | 5.12.0 | Roslyn AST for safe code insertion and accurate method counting (~15MB) |

## Security

dotnet-coverage-mcp runs as a local stdio process and validates every tool argument against `COVERAGE_MCP_ALLOWED_ROOT` to confine filesystem access. See [SECURITY.md](SECURITY.md) for the threat model, hardening recommendations, and how to report a vulnerability.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for development
setup, pull request guidelines, and code conventions. Notable changes are tracked
in [CHANGELOG.md](CHANGELOG.md).

## Releasing

Maintainer-only — release process, NuGet publishing, and MCP registry submission are documented in [RELEASING.md](RELEASING.md).
