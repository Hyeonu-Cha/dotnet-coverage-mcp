---
name: improve-test-coverage
description: "Iteratively write .NET unit tests until every file in scope reaches 80% line and branch coverage. USE FOR: autonomously running tests, finding gaps, writing targeted tests, and repeating until every file is at 80% or plateaus. Uses the dotnet-coverage-mcp MCP tools GetSourceFiles, RunTestsWithCoverage, GetCoverageGaps, GetCoverageDiff, AppendTestCode and CleanupSession (plus GetFileCoverage, GetUncoveredBranches, GetCoverageSummary for drill-down). Uses smart batching by line budget — small files grouped, large files solo. DO NOT USE FOR: just running tests without writing new ones (use run-coverage), inspecting a single method (use analyze-coverage-gaps)."
---

# Improve Test Coverage Iteratively

Autonomously run tests, identify coverage gaps, write targeted unit tests, and repeat until both line and branch coverage reach 80% or no further improvement is possible.

## When to Use
- User asks to "improve coverage", "get to 80% coverage", or "write tests for X"
- Starting an automated coverage improvement session for a class or project
- Coverage is below 80% and the user wants tests written automatically

## When Not to Use
- User only wants a coverage report without writing tests — use `run-coverage`
- User wants to inspect a specific method's branches only — use `analyze-coverage-gaps`
- Project has no existing test file to append to (create one first)

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| `sourcePath` | Yes | Scope target: a `.cs` file, a folder, or a `.csproj` project |
| `testProjectPath` | Yes | Full path to the `.csproj` test project |
| `filter` | No | Test filter. Omit to run every test (the usual choice for batch/folder/project scope), or pass a namespace or class name to narrow the run |
| `lineBudget` | No | Max total lines per batch (default: 300). Small files get grouped, large files run solo |
| `workingDir` | No | Working directory; defaults to project directory |
| `includeClass` | No | Do NOT pass this in the iterative workflow. It scopes coverage to a single class and would skew batch-level numbers. Only used by `run-coverage` for single-class reports. |

## Workflow

### Step 0: Setup and discover scope
1. Call `GetSourceFiles` with `sourcePath` and `lineBudget` to get:
   - `batches` — files grouped by line budget (small files together, large files solo), each file carrying its `lines` and `methodCount` metadata
   - `batchCount` — how many cycles to expect
   - `excluded` — files that are **not** targets: entry points (`Program.cs`), DI registration, generated code, data-only models, and test projects. Do not write tests for them and do not count them toward the 80% goal. If the user insists on one of them, pass it to `GetSourceFiles` by path; named files are always kept

Example response:
```json
{
  "scope": "folder",
  "totalFiles": 18,
  "totalLines": 4200,
  "lineBudget": 300,
  "batchCount": 8,
  "batches": [
    [{"path": "SmallA.cs", "lines": 80}, {"path": "SmallB.cs", "lines": 120}, {"path": "SmallC.cs", "lines": 90}],
    [{"path": "BigService.cs", "lines": 920}]
  ]
}
```

### Step 0.5: Per-batch setup (repeat for each batch)
For each file in the current batch:
1. Call `DescribeTestTarget` on the source file: constructor dependencies with how to provide each (mock, `NullLogger`, `Options.Create`, real instance for sealed/concrete classes, SQLite for a `DbContext`), the methods with their decision points, calls a test can't control, the suggested test type, and any existing test file. Read the source itself only for the methods `GetCoverageGaps` points at
2. Check if a matching test file exists; if not, use `scaffold-test-files` to create one
3. Call `GetTestEnvironment` once for the test project and write every test in that stack: its `conventions` give the exact attributes for tests, data-driven cases, setup and teardown, and `mocking` / `assertions` name the libraries to use. Read its `warnings` (e.g. no coverage collector) before the first run
4. Verify the test file has per-test setup in that framework's style (xUnit: constructor; NUnit: `[SetUp]`; MSTest: `[TestInitialize]`); add one if missing using `AppendTestCode`

### Step 1: Run coverage for the batch (ONE test run)
Call `RunTestsWithCoverage` with `testProjectPath` and **no filter** (runs every test), or a namespace-level filter.
This runs `dotnet test` **once** and collects coverage across all source files.

Leave `includeClass` unset. It would silently limit collection to a single class and zero out the rest of the batch.

### Step 2: Get the gaps for the whole batch (ONE call)
Call `GetCoverageGaps` with `coberturaXmlPath` and `path` = the batch's file paths, comma-separated (or the folder / `.csproj` for a small scope). The goal is **every file at 80% line and branch**, and the response is organised per file:
- `filesDone` / `done` — files already at target; nothing to do
- `remaining` — files below target, smallest gap first. Each has `linesToCover` / `branchesToCover` (how many more lines / branch outcomes must be covered) and `methods`: the fewest methods that close that file's gap, each with `uncoveredLines` (ranges), `uncoveredBranches`, and `source` (the code around each gap)
- `closesGap: false` — the listed methods alone don't reach the target; there is more to do after them
- `notInReport` — files with no coverage data at all: the test project probably doesn't reference their project. Tell the user; no test can cover them as things stand

Skip methods you marked as blocked (failed 3 times with no improvement). `GetFileCoverage` and `GetUncoveredBranches` remain available to drill into one file or method.

### Step 3: Read the gaps
Each uncovered branch is a source line with its uncovered conditions, e.g. `condition 23 (jump, 50% covered)`: the `if`/`?:`/`&&` on that line only ever went one way, so write a test for the other outcome. `0% covered` means neither outcome ran (usually the line itself never executed). Read that line in the source to see which input takes the missing path.

### Step 4: Write targeted tests for the batch
For each uncovered branch, write a focused unit test using `AppendTestCode`:
- Target one specific branch condition per test
- Max 1–3 assertions per test
- Use the framework's data-driven attribute for data variations (xUnit `[Theory]` + `[InlineData]`, NUnit `[TestCase]`, MSTest `[DataRow]`), and separate test methods for different mock setups
- Use `insertAfterAnchor` to place tests after the relevant test class or last test method
- Tests may go to **different test files** across the batch — that's expected

**Test quality rules (strictly enforce):**
- No bulk POCO tests (avoid mass property getter/setter tests with no logic)
- Behavioral focus: test calculations, conditional logic, or external interactions
- Descriptive names: `MethodName_Condition_ExpectedResult` pattern
- Each test must exercise a specific branch, not just increase line count

### Step 4.5: Handle build/compilation errors
If `RunTestsWithCoverage` returns a build error after writing tests:
1. Read the error message to identify the file and line number
2. Use your standard file editing tools (Edit, Read) to fix the syntax error in the test file
3. Common causes: missing semicolon, unclosed brace, wrong type name, missing `using` statement
4. After fixing, re-run `RunTestsWithCoverage` before continuing
5. If the same test fails to compile 3 times, remove it with Edit and move on to the next uncovered branch

**Do NOT call `AppendTestCode` to fix errors** — use Edit to surgically fix the broken line instead.

### Step 4.6: Handle failing tests
If `RunTestsWithCoverage` returns `errorType: "testsFailed"`, the build succeeded but a test assertion failed:
1. Read `failures` — each entry has the test name, assertion message, and the top frames of the stack in your code
2. Fix the test (usually a wrong expectation or mock setup) with Edit; do not delete it just to make the run pass
3. The response still carries `coberturaXmlPath`: coverage from the passing tests is valid, so `GetCoverageGaps` works before re-running

If it returns `errorType: "noTestsMatched"`, the `filter` selected nothing. Omit it to run every test, or use a shorter name.

### Step 5: Re-run and check batch-level progress
Call `RunTestsWithCoverage` **once** with the same broad filter.
Call `GetCoverageGaps` again for the batch, and `GetCoverageDiff` to see method-level deltas.

### Step 6: Repeat or advance
- **If `filesRemaining` > 0** → return to Step 3 with the new gaps
- **If all files in batch are done OR 3 cycles with no improvement** → move to next batch
- **If all batches complete** → report final coverage summary, including how many files were excluded and why (by kind)

## Termination Criteria

| Condition | Action |
|-----------|--------|
| All files in batch at ≥ 80% line AND branch | Batch done — move to next batch |
| No improvement for 3 consecutive cycles in a batch | Batch plateau — move to next batch |
| Method fails 3 times with no gain | Mark as blocked, skip in future cycles |
| All batches processed | Session complete — report final state |

## Performance Notes

- **One `dotnet test` per cycle** — not per file. This is the key optimization.
- `GetCoverageGaps` answers for the whole batch in one call and needs no test run; the report is parsed once and cached
- Small files (< 100 lines) get batched together so AI handles 3–5 at once
- Large files (> lineBudget) get their own batch for focused attention
- Omit `filter` (or use a namespace) for batch/folder/project scope
- Do NOT pass `includeClass` — it restricts coverage collection to one class and would zero out the rest of the batch

## Validation
- [ ] `GetSourceFiles` returned batches grouped by line budget
- [ ] Only one `RunTestsWithCoverage` call per cycle (not per file)
- [ ] `GetCoverageGaps` called for the batch after each test run
- [ ] Each test targets a specific uncovered branch (not random line coverage)
- [ ] No bulk POCO/property tests added
- [ ] Files marked done and removed from batch when they hit 80%
- [ ] Final report shows per-file and overall coverage percentages

## Common Pitfalls

| Pitfall | Solution |
|---------|----------|
| Tests compile but coverage doesn't improve | The test may not be reaching the target branch — verify mock setup matches the condition |
| `AppendTestCode` inserts in wrong location | Pass `targetClass` with the test class name, or a precise `insertAfterAnchor` matching an existing test method signature |
| Plateau after 1–2 cycles | Check if remaining uncovered branches require complex setup (async, private methods, static calls) — mark as blocked |
| Over-testing simple properties | Skip auto-properties with no logic; focus on methods with conditionals |
| Running every test is too slow | Pass a namespace or class name as `filter` |
| Batch too large for AI context | Reduce `lineBudget` (e.g., 200) to get smaller batches |
