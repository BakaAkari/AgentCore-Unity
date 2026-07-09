# Unity `.agents/skills` Guide

> This directory contains Unity development skill specifications for AI assistants, used to constrain and guide LLM behavior across different task scenarios.
>
> Knowledge sources: Team practical experience + curated essentials from the [Unity-Skills](https://github.com/Besty0728/Unity-Skills) advisory module (MIT license).

---

## Skill List

### Development Standards

| Skill | Purpose | Trigger Scenarios |
|-------|---------|-------------------|
| `unity-runtime-dev` | Runtime code development, fixes, reviews | Writing game logic, fixing bugs, code reviews |
| `unity-editor-tooling` | Editor tools and batch processing | Menu tools, Inspector customization, bulk asset processing |
| `unity-package-dev` | Package / UPM plugin development | Building shared plugins, SDKs, UPM packages |

### Design Decisions

| Skill | Purpose | Trigger Scenarios |
|-------|---------|-------------------|
| `unity-patterns` | Design pattern selector | "Which pattern should I use", "how to decouple", "events vs direct references" |
| `unity-blueprints` | Game architecture blueprints | "Make a game", "start from scratch", "build a prototype" |
| `unity-scene-contracts` | Scene assembly contracts | "What does the scene need", "how to wire references", "initialization order" |

### Analysis & Documentation

| Skill | Purpose | Trigger Scenarios |
|-------|---------|-------------------|
| `unity-performance-analysis` | Performance diagnosis and optimization suggestions | Stuttering, GC, UI performance, asset hotspot analysis |
| `unity-documentation` | Technical documentation and ADR writing | Writing proposals, guides, troubleshooting docs, research, architecture decision records |

---

## Usage Recommendations

### Choose by Task

- **New project startup** → `unity-blueprints` → `unity-scene-contracts` → `unity-patterns`
- **Writing game logic** → `unity-runtime-dev` (includes script quality review, Inspector design, async strategies)
- **Building editor tools** → `unity-editor-tooling`
- **Building plugins/SDKs** → `unity-package-dev`
- **Performance troubleshooting** → `unity-performance-analysis` (includes red-flag checklist)
- **Writing docs/making decisions** → `unity-documentation` (includes ADR template)

### Relationships Between Skills

```
unity-blueprints ──→ unity-scene-contracts ──→ unity-runtime-dev
       │                      │                       │
       └──→ unity-patterns ───┘                       │
                                                      ↓
                                        unity-performance-analysis
```

- Blueprints define the architecture skeleton
- Scene contracts define assembly rules
- Pattern selector assists with specific design decisions
- Runtime standards constrain code quality
- Performance analysis intervenes when hotspots emerge

---

## Relationship with unity-mcp Built-in Skills

The Skills in this directory focus on the **design standards layer** (how to write good code, how to build good architecture), complementing the Skills bundled with the unity-mcp plugin:

| Layer | Source | Content | Loading Method |
|-------|--------|---------|----------------|
| **Design Standards Layer** | `.agents/skills/` (this directory) | Architecture, patterns, code quality, performance analysis, documentation writing | Manually triggered via AGENTS.md Skill routing table |
| **Tool Operations Layer** | unity-mcp built-in skills | MCP tool parameters, operation workflows, UI component recipes, error recovery | Automatically synced and loaded by the unity-mcp plugin |

> **Rule**: When using unity-mcp tools to perform Unity operations (e.g., creating scenes, building UI, managing assets),
> you should also refer to the unity-mcp built-in workflows and tools-reference,
> which contain best practices for tool invocation, parameter conventions, and error prevention patterns.
>
> Built-in skills are located by default at: `~/.codex/skills/unity-mcp-skill/` (automatically managed by the plugin, do not modify manually).

The two systems **should not be merged** — they have different update cadences, different responsibility layers, and different loading mechanisms.

---

## Version History

| Date | Changes |
|------|---------|
| 2026-03-26 | Added `unity-patterns`, `unity-blueprints`, `unity-scene-contracts`; enhanced `unity-runtime-dev` (+script quality review/Inspector design/async strategies), `unity-performance-analysis` (+red-flag checklist), `unity-documentation` (+ADR template) |
| Initial version | Created `unity-runtime-dev`, `unity-editor-tooling`, `unity-package-dev`, `unity-performance-analysis`, `unity-documentation` |
