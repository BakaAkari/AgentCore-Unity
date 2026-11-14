# Contributing to AgentCore Unity

## Before you start

This is a single-maintainer open-source project. Response times and review
bandwidth are limited — please be patient, and consider that not every PR
or issue can be picked up immediately.

## Reporting bugs

Use the Bug report issue template. Include Unity version, package version,
and Console output — this project is Editor-only and most bugs are only
reproducible with exact repro steps.

## Testing model (please read before submitting PRs)

Automated EditMode tests are in `Editor/Tests/` (`AgentCore.Tests.Editor`), including process-level MCP relay tests. The 1.20.0 acceptance record on Unity 2022.3.50f1 is 905 tests: 904 passed, 0 failed, 1 non-Windows-only test skipped on Windows.

To run the full suite in a Unity host project:

1. Install this package and Unity Test Framework. For a UPM package, add `"testables": ["com.agentcore.unity"]` to the host's `Packages/manifest.json` (merge with existing entries).
2. Enable VCS, Code Indexing and MCP Server in AgentCore's Tools & Extensions settings. The test assembly references all three optional assemblies; testing with them disabled is not the full-suite configuration.
3. Open `Window > General > Test Runner`, choose **EditMode**, and run all AgentCore tests. Save the result XML and Editor log. For batch runs, use Unity's `-batchmode -projectPath <host> -runTests -testPlatform EditMode -testResults <xml> -logFile <log>` arguments.
4. Exercise affected tool actions manually in Edit Mode and Play Mode, inspect Console output, and check compilation/reload, cancellation and Undo as relevant. For runtime-only edits, verify their behavior after Stop.
5. Before packaging, run `npm run verify-meta`; `npm pack` runs metadata and tarball verification through the package scripts. These package checks do not run Unity tests.

The release count above is a historical result. Report the actual counts, enabled components and Unity version for your change.

Known limitation: some real bugs in this codebase have shipped with a clean
compile and only surfaced when exercised in Play Mode. See CHANGELOG v1.14.0
for a concrete example — a Play Mode exception was misdiagnosed twice before
the actual root cause (a disk-write API call unrelated to the originally
suspected code path) was found. **Compiling cleanly is not sufficient
evidence a change is correct.** If your PR touches tool execution, Play Mode
safety guards (`Editor/Tools/Safety/`), or Undo/dirty-marking logic, describe
how you manually verified it (Edit Mode + Play Mode) in the PR description.

## Code conventions

See `AGENTS.md` for architecture, the tool development template, and module
boundaries. New tools must use `[AgentTool]` + `IAgentTool` for auto-discovery
and declare appropriate risk/capability/visibility metadata.

## Pull requests

- Fork, branch, PR against `main`.
- Describe what changed and why, and list automated test results and manual verification performed.
- Breaking changes to public tool schemas should be called out explicitly.
