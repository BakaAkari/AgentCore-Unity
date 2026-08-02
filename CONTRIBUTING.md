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

There is currently **no automated test suite** for this project. Verification
today is:

1. A compile check (build the package in the Unity Editor) — catches syntax
   errors, missing usings, broken references. It does **not** catch runtime
   or behavioral bugs.
2. Manual verification in the Unity Editor: exercising the affected tool
   actions in both Edit Mode and Play Mode, checking Console output, and
   confirming Play Mode changes revert cleanly on Stop.

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
- Describe what changed and why, and list any manual verification performed.
- Breaking changes to public tool schemas should be called out explicitly.
