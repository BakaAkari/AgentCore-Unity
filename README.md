# AgentCore Unity

**An AI agent that lives inside the Unity Editor.** Describe what you want in plain English (or Chinese) — it reads your project state, plans, calls native Unity tools, and gets it done.

`Ctrl+Shift+Q` → type → done.

---

## What it is

AgentCore Unity is an **Editor-only UPM package**, not a generic coding-agent wrapper. It's a native execution layer purpose-built for Unity projects: model reasoning, live Editor state, 50+ native tools, project knowledge, version control, code indexing, and verification feedback — wired into one governed loop.

- **Package**: `com.agentcore.unity` · **Version**: `1.14.3` · **Unity**: `2021.3+`
- Editor-only assembly — zero runtime footprint, never ships in your build
- ~108K lines, 56 native tools covering scenes, GameObjects, components, prefabs, assets, materials, shaders, UI, physics, audio, cameras, Timeline, Cinemachine, ProBuilder, builds, tests, and more

## Why

Generic coding agents don't know what a `Prefab` variant override is, can't press Play, and can't read your Console. AgentCore does — it operates *inside* the Editor:

- **Real tool execution** — not code suggestions. It adds components, edits transforms, runs Play Mode, reads Console errors, and fixes them itself.
- **Play Mode safety** — write actions in Play Mode run as in-memory edits and auto-revert on Stop; disk-mutating actions (asset create, scene save, domain reload) are hard-blocked.
- **Risk-gated execution** — destructive ops prompt for confirmation; session-level trust scopes (Trust Low/Med, YOLO) let you tune the friction.
- **Survives recompiles** — Domain Reload doesn't kill your session; pending tool calls and conversation state recover automatically.
- **Multi-provider** — works with any OpenAI-compatible endpoint (local models, OpenRouter, your own gateway). Provider Profiles let you keep several configs and hot-swap.

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

That's it — no config required to start using the default connection.

## Configuration

Model connection is managed via **Provider Profiles** — `Edit > Project Settings > AgentCore` → **Model & Agent**. Each profile stores one full connection (endpoint, key, model, sampling params); switch the *Active Profile* to hot-swap providers.

| Field | Purpose |
|---|---|
| Endpoint | OpenAI-compatible API base URL |
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

Disabled by default, enabled via scripting define symbols:

- **Version Control** (`AGENTCORE_VCS`) — Git / SVN / Perforce status, diff, log, commit workflows
- **Code Indexing** (`AGENTCORE_INDEXING`) — Roslyn-based symbol search, background incremental indexing

## Documentation

- [`QUICK_START.md`](QUICK_START.md) — usage guide, common workflows, troubleshooting
- [`CHANGELOG.md`](CHANGELOG.md) — version history
- [`AGENTS.md`](AGENTS.md) — architecture and contribution guide

## Known Limitations

- **No automated test suite.** Verification today is manual: exercising
  affected tool actions in the Unity Editor (Edit Mode + Play Mode) and
  checking Console output. Compiling cleanly does not guarantee correctness
  — see [`CONTRIBUTING.md`](CONTRIBUTING.md) for a concrete example of a
  bug that shipped with a clean compile.
- **Single maintainer.** No SLA, no guaranteed response time on issues/PRs.
- **`execute_code` and Play Mode write actions carry real risk if misused.**
  Read [`SECURITY.md`](SECURITY.md) before enabling write-capable tools on a
  project without version control.

## License

See [`LICENSE.md`](LICENSE.md).
