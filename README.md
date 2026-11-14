# AgentCore Unity

**An AI agent that lives inside the Unity Editor.** Describe what you want in plain English (or Chinese) — it reads your project state, plans, calls native Unity tools, and gets it done.

`Ctrl+Shift+Q` → type → done.

---

## What it is

AgentCore Unity is an **Editor-only UPM package**, not a generic coding-agent wrapper. It's a native execution layer purpose-built for Unity projects: model reasoning, live Editor state, 50+ native tools, project knowledge, version control, code indexing, and verification feedback — wired into one governed loop.

- **Package**: `com.agentcore.unity` · **Version**: `1.20.1` · **Unity**: `2021.3+`
- Editor-only assembly — zero runtime footprint, never ships in your build
- Built-in tools covering scenes, GameObjects, components, prefabs, assets, materials, shaders, UI, physics, audio, cameras, Timeline, Cinemachine, ProBuilder, builds, tests, and more

## Why

Generic coding agents don't know what a `Prefab` variant override is, can't press Play, and can't read your Console. AgentCore does — it operates *inside* the Editor:

- **Real tool execution** — not code suggestions. It adds components, edits transforms, runs Play Mode, reads Console errors, and fixes them itself.
- **Play Mode safety** — the dispatcher permits declared runtime-safe actions and blocks unsupported writes. Tools using the write interceptor skip disk saves for runtime-only edits; this is not a sandbox for arbitrary code. See `SECURITY.md` for the limits.
- **Risk-gated execution** — destructive ops prompt for confirmation; session-level trust scopes (Trust Low/Med, YOLO) let you tune the friction.
- **Concurrent file reads** — up to four adjacent audited `manage_file` reads/searches run in parallel; writes and other tools remain ordered.
- **Survives recompiles** — Domain Reload doesn't kill your session; pending tool calls and conversation state recover automatically.
- **Multi-provider** — supports OpenAI-compatible endpoints (local models, OpenRouter, your own gateway) and native Anthropic connections. Provider Profiles let you keep several configs and hot-swap.

## Install

Requires Unity **2021.3 LTS** or newer.

**Option A — tarball**
1. `Window > Package Manager` → `+` → **Add package from tarball...**
2. Select `com.agentcore.unity-<version>.tgz`

**Option B — git URL**
1. `Window > Package Manager` → `+` → **Add package from git URL...**
2. Paste the repository URL

After install, `Window > AgentCore` appears in the menu bar.

## Quick Start

1. Open the window: `Ctrl+Shift+Q` (macOS: `Cmd+Shift+Q`)
2. Describe your task in plain language:
   > "Add a third-person character prototype to the current scene, put assets under `Assets/Prototype/`, don't touch the existing scene or input settings."
3. AgentCore plans, calls tools, watches the Console, and self-corrects on compile errors. High-risk actions (delete, force-push, etc.) prompt for confirmation first.

The default connection targets an enterprise intranet model. Outside that network, configure a reachable provider below before chatting.

## Configuration

Model connection is managed via **Provider Profiles** — `Edit > Project Settings > AgentCore` → **Model & Agent**. Each profile stores one full connection (endpoint, key, model, sampling params); switch the *Active Profile* to hot-swap providers.

| Field | Purpose |
|---|---|
| Endpoint | API base URL for the selected provider protocol |
| API Key | Stored locally via secure key storage, never committed |
| Model | Dropdown (auto-fetched) or free-text fallback |
| Override Temperature / MaxTokens / Reasoning | Per-profile overrides of the global defaults |
| Extra Request Body | Custom JSON merged into every request |

Click **Test Connection** before setting a profile active. See [`QUICK_START.md`](QUICK_START.md) for the full walkthrough, keyboard shortcuts, and troubleshooting table.

## Key Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+Q` | Open AgentCore |
| `Ctrl+Shift+X` | Inject context from whatever's focused (Console, asset, GameObject, any Editor window) |
| `Ctrl+Enter` | Newline in chat input |
| `Ctrl+N` | New session |
| `Ctrl+Shift+E` | Export current session |

macOS: use `Cmd` in place of `Ctrl`.

## Optional Components

Enable these in **Project Settings > AgentCore > Tools & Extensions**. Their scripting define symbols control compilation:

- **Version Control** (`AGENTCORE_VCS`) — Git / SVN / Perforce status, diff, log, commit workflows
- **Code Indexing** (`AGENTCORE_INDEXING`) — Roslyn-based symbol search, background incremental indexing
- **MCP Server** (`AGENTCORE_MCP`) — lets Claude Code, Codex and other MCP clients use AgentCore's Unity tools, resources (project context, scene, console, VCS status, files) and skills as prompts, with the same permission rules and review level as the chat. Supports MCP 2026-07-28 and the 2025 revisions, confirmation in the client (elicitation), the Tasks extension, and an optional local HTTP endpoint; survives script reloads. See `QUICK_START.md` §7

The AgentCore chat can also use tools from external MCP servers (stdio or HTTP, same `mcpServers` config format as Claude Desktop). See `QUICK_START.md` §8.

## Documentation

- [`QUICK_START.md`](QUICK_START.md) — usage guide, common workflows, troubleshooting
- [`CHANGELOG.md`](CHANGELOG.md) — version history
- [`AGENTS.md`](AGENTS.md) — architecture and contribution guide

## Verification and tool inventory

Unity EditMode tests live in `Editor/Tests/`, including MCP relay process integration tests. The recorded 1.20.1 acceptance run on Unity 2022.3.50f1 had **916 tests: 915 passed, 0 failed, 1 platform-specific skip**. This is a dated release result, not a guarantee for every Unity version; see [`CONTRIBUTING.md`](CONTRIBUTING.md) for setup and manual checks.

At 1.20.0, source declares **60 built-in tools** (52 under `Editor/Tools/Native`, plus file, cloud, VCS and indexing tools). Runtime availability depends on enabled components, disabled tools and Chat tool scope; external MCP tools are additional. Use the runtime tool list as the authority, rather than treating the source count as the number exposed to every client. MCP excludes Chat-only helpers and external MCP proxies.

## Known Limitations

- **Editor integration still needs manual validation.** Automated tests do not replace real-project checks for Undo, Play Mode, compilation/reload and SVN workflows. See [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Single maintainer.** No SLA, no guaranteed response time on issues/PRs.
- **`execute_code` and Play Mode write actions carry real risk if misused.**
  Read [`SECURITY.md`](SECURITY.md) before enabling write-capable tools on a
  project without version control.

## License

See [`LICENSE.md`](LICENSE.md).
