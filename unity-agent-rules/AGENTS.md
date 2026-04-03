# `Unity` Workspace Rules File

> This file defines the universal LLM specifications for the `Unity` workspace, guiding AI assistants in Unity game development, plugin development, tool development, technical research, problem analysis, and documentation writing within this directory.
>
> The goal is to keep outputs as consistent as possible across different sessions, models, and stages, reducing accidental modifications, over-engineering, and documentation drift.

---

## Directory Purpose

The `Unity` workspace is intended to host the following content:

- Unity game projects
- Unity plugins / Packages / SDKs
- Unity editor tools and automation scripts
- Unity technical validation, performance analysis, and research reports
- Specification documents, implementation plans, and execution records related to Unity development

This workspace is not limited to a single project form and may simultaneously contain:

- Complete Unity projects
- Standalone Package repositories
- Demo / Sandbox / PoC
- Pure documentation and analysis directories

---

## 1. Working Principles

### 1.1 Understand Context Before Taking Action

Before modifying any Unity project, plugin, or analysis document, first confirm:

- Whether the current directory is a complete Unity project, Package, tool repository, or pure documentation directory
- Unity version, render pipeline, target platform, key dependencies
- Existing directory structure, naming conventions, code style, testing approach
- Whether the user wants to implement a feature, fix a problem, analyze a cause, or produce documentation

Do not directly perform large-scale rewrites of directory structures or replace implementation approaches without first understanding the context.

### 1.2 Goal-Oriented, Minimal Changes

- Only make changes that directly serve the current objective
- Prioritize local fixes and incremental improvements; avoid unrelated refactoring
- Do not proactively upgrade Unity versions, package versions, input systems, or render pipelines unless requested
- Do not proactively introduce large third-party dependencies unless requested

### 1.3 Auditable, Reproducible, Reversible

All outputs should satisfy as much as possible:

- Clear rationale for changes
- Clear scope of impact
- Clear verification method
- Document conclusions traceable to their basis

---

## 2. Unity Hard Rules

> The following rules must not be violated by default, unless the user explicitly requests an override.

### 2.1 Respect Unity Project Structure

If the current directory is a Unity project, follow the existing structure first. Common directories include:

```text
Assets/
Packages/
ProjectSettings/
UserSettings/
Library/
Logs/
Temp/
```

Rules:

- `Assets/`, `Packages/`, `ProjectSettings/` are considered core directories
- `Library/`, `Temp/`, `Logs/`, `Obj/` are considered generated directories by default; do not edit them unless explicitly needed
- Do not mix runtime code, editor code, and test code together
- Do not scatter analysis documents into Unity runtime directories; place them in `docs/` instead

### 2.2 Do Not Arbitrarily Break `.meta` Files and GUIDs

- Do not delete, recreate, or overwrite existing `.meta` files unless the impact is clearly understood
- When moving or renaming existing assets, prefer preserving the original `.meta`
- For assets sensitive to reference chains, avoid the "delete and recreate" replacement approach

### 2.3 Separate Editor Code from Runtime Code

Must distinguish:

- Runtime: runtime logic
- Editor: editor-only logic
- Tests: test code

Recommended convention:

```text
Assets/<Module>/Runtime/
Assets/<Module>/Editor/
Assets/<Module>/Tests/
Assets/<Module>/Samples~/        # Package samples
```

Do not write `UnityEditor`-related APIs directly into runtime code.

### 2.4 No Implicit Upgrades

The following items are prohibited from proactive changes by default:

- Unity major version / LTS version
- URP / HDRP / Built-in pipeline switching
- Input System and legacy Input Manager switching
- Major version upgrades of key dependencies in `manifest.json`
- Compilation boundary changes caused by asmdef splitting or merging

If an upgrade is truly necessary, the documentation or explanation must state:

- Why the upgrade is necessary
- Potential breaking surface
- Verification scope

### 2.5 Code Generation and Auto-Fix Must Be Restrained

- Do not batch-generate large amounts of "potentially useful" script skeletons
- Do not fill in unverified API calls just to "look complete"
- Do not treat guesses as Unity facts
- When encountering version differences, package differences, or platform differences, must label "confirmed" vs. "inferred"

### 2.6 Generated Directories and Commit Boundaries

Content not recommended for version control or modification by default:

- `Library/`
- `Temp/`
- `Logs/`
- `Obj/`
- Build output directories
- Local caches, export caches, auto-generated intermediate files

### 2.7 User's Existing Structure Takes Priority

If the project already has established patterns, inherit them first:

- Existing naming conventions
- Existing asmdef boundaries
- Existing dependency injection approach
- Existing event system / state machine / UI framework
- Existing test framework and directory layout

Do not break the repository's current state just to apply a "standard answer."

### 2.8 File Modification Permission Boundaries (Hard Rules)

> **Core Principle**: The Agent has full read/write access to its own files; for Unity project files, **read-only during initialization**, and after initialization can operate but **each modification requires user confirmation**.

#### Two-Phase Permission Model

| Phase | Agent-Owned Files | Unity Project Files | Description |
|-------|-------------------|---------------------|-------------|
| **Initialization Phase** | ✅ Read/Write | 🔒 Read-Only | Read context, load Skills, do not touch project files |
| **Working Phase** | ✅ Read/Write | ⚠️ Requires User Confirmation | Must obtain explicit user consent before each modification |

#### Agent-Owned Files (Always Read/Write)

The following directories and files are managed by the Agent rule system; the AI can freely read and write them at any phase:

```text
AGENTS.md                          # Global rules file
.agents/                           # All Agent context and skills
  ├── context/                     # Auto-generated project index
  ├── skills/                      # Design specification modules
  ├── memories/                    # Decision records (if any)
  └── rules/                       # Extended rules (if any)
tools/                             # Agent tool scripts
  └── generate-snapshot.ps1        # Project snapshot generator
```

> **Note**: Unity MCP installation tools (`install-unity-mcp.ps1`, `package-unity-mcp.ps1`, etc.) are maintained separately
> in the `unity-mcp-setup/` directory of the LLM AI Toolkit, and are **not** part of the per-project Agent Rules deployment.

#### Unity Project Files (Read-Only During Init, Confirmation Required During Working Phase)

The following directories and files belong to the Unity project itself:

```text
Assets/                            # All game assets and code
Packages/manifest.json             # Package dependency manifest
Packages/packages-lock.json        # Package lock file
ProjectSettings/                   # Project settings
UserSettings/                      # User settings
Library/                           # Cache (should not be read/written at any phase)
Temp/                              # Temporary files (should not be read/written at any phase)
```

Permission rules:

- **Initialization phase**: These files **can only be read**, used to understand project structure and tech stack, **absolutely must not be written to**
- **Working phase**: The AI can propose modification plans, but **each modification must be confirmed by the user before execution**
- `Library/` and `Temp/` should not be read or written at any phase

#### Phase Delineation Criteria

**Initialization phase** includes:

1. Reading `.agents/context/` context files
2. Detecting Unity version, render pipeline, target platform
3. Checking Unity MCP installation status
4. Loading the corresponding Skill specification
5. Understanding the user's task intent

**Working phase** begins when:

- The AI has completed context reading and understands the project structure
- The user has issued a specific work instruction (e.g., "help me modify this script", "create a new scene")

#### Specific Rules

1. **Init phase**: The AI only performs **read** operations on Unity project files, does not modify any project files, and does not automatically execute any scripts that would modify project files
2. **Agent file maintenance**: The AI can autonomously update `.agents/context/`, fix `AGENTS.md` formatting, etc. at any phase
3. **Project file modifications during working phase**: The AI can modify project files, but **must first explain the modification to the user and obtain confirmation before executing**
4. **Unity MCP installation**: Modifies `Packages/manifest.json`, which is a project file change that **must be manually run by the user or explicitly authorized**; the AI can only suggest the command (see §7.3.1 for details)
5. **generate-snapshot.ps1**: Only writes to `.agents/context/` (Agent-owned area), does not modify project files; the AI can suggest running it

#### Decision Criteria

When the AI is uncertain whether an operation requires user authorization, use the following judgment:

- File path under `.agents/`, `tools/`, `docs/`, `AGENTS.md` → **Can modify autonomously** (any phase)
- File path under `Assets/`, `Packages/`, `ProjectSettings/`:
  - Initialization phase → **Read-only, cannot modify**
  - Working phase → **Can modify, but must be confirmed by user**
- Executing scripts that modify project files → **Only suggest, let the user decide whether to execute**

---

## 3. Recommended Directory Organization

This workspace recommends a layered approach of "projects / plugins / docs / prototypes / tools" rather than mixing all files in the root directory.

Recommended structure:

```text
Unity/
├── AGENTS.md
├── docs/                      # Technical plans, research, troubleshooting records, implementation docs
├── projects/                  # Complete Unity projects
├── packages/                  # Standalone Unity Packages / UPM plugins
├── tools/                     # Utility tools, scripts, automation
├── sandbox/                   # Experimental projects, PoC, minimal reproductions
└── .agents/                   # Optional: future skill and context extensions
```

If the current repository is not organized this way, do not force a restructure, but new content should align towards this structure as much as possible.

---

## 4. Unity Development Standards

### 4.1 Script Design

- Prioritize writing clear, direct, maintainable C# code
- Avoid unnecessary over-abstraction
- Be explicit about MonoBehaviour lifecycle
- Avoid piling uncontrolled polling logic in `Update()`
- Prefer event-driven approaches when possible
- Cache references instead of repeated lookups

### 4.2 Performance Awareness

Key focus areas:

- Unnecessary overhead in `Update` / `LateUpdate` / `FixedUpdate`
- GC Alloc, especially on hot paths
- Abuse of `GetComponent` / `FindObjectOfType` / `GameObject.Find`
- UI rebuilds, layout thrashing, frequent SetActive
- Resource loading timing and lifecycle

Do not perform "comprehensive" performance refactoring unless requested; prioritize targeted changes for hotspot issues.

### 4.3 Scenes and Prefabs

- Avoid breaking existing reference relationships as much as possible
- Be aware of override impact when modifying Prefabs
- When modifying scene-level objects, explain whether existing scene content is affected
- If batch resource modifications are needed, provide a verifiable, rollback-friendly path

### 4.4 asmdef and Package Boundaries

- When adding new modules, first consider whether an asmdef is needed
- Do not create large numbers of granular asmdefs without reason
- Separate editor-exclusive assemblies from runtime assemblies
- Test assemblies should define dependencies independently

### 4.5 Package / Plugin Development

If the current directory is a Unity Package, recommended structure:

```text
<package-root>/
├── package.json
├── Runtime/
├── Editor/
├── Tests/
├── Samples~/
├── Documentation~
└── CHANGELOG.md
```

Rules:

- `package.json` information must be complete and accurate
- Keep public APIs stable; avoid undocumented breaking changes
- Place sample content in `Samples~`
- User-facing documentation should be placed in `Documentation~` or the root document

### 4.6 Testing and Verification

Prioritize:

- EditMode tests for pure logic and editor logic
- PlayMode tests for runtime behavior verification
- When automated tests cannot be added, at least provide manual verification steps

After each change, at minimum explain:

- Whether compilation should pass
- Which scene / menu / object to verify on
- What the expected result is

---

## 5. Analysis and Research Task Standards

This workspace does not only handle coding but also analysis, evaluation, technology selection, and troubleshooting. When handling such tasks:

### 5.1 Conclusion First

Documents or responses should prioritize:

- Conclusion
- Recommended approach
- Not-recommended approach
- Risk points

Then expand on the basis and details.

### 5.2 Distinguish "Confirmed" from "Engineering Judgment"

Must clearly distinguish:

- What is directly supported by source code, logs, documentation, or test results as fact
- What is engineering inference based on context

Do not write inferences as established facts.

### 5.3 Implementation-Oriented

Analysis documents should not remain at abstract descriptions; they should provide as much as possible:

- Applicable scenarios
- Constraints
- Recommended path
- Implementation steps
- Verification methods

---

## 6. Documentation Writing Rules

> This section inherits the documentation standards from the `Dockers` workspace and adapts them for reuse in Unity scenarios.

### 6.1 Documentation Goals

Documentation in the Unity workspace should serve the following purposes:

- Technical plan design
- Problem investigation and retrospection
- Deployment / integration / onboarding guides
- Plugin usage instructions
- Architecture decisions and experience preservation

### 6.2 Default Writing Structure

Recommended structure:

```markdown
# Title

Document Date: YYYY-MM-DD
Goal:
Scope:

## 1. Conclusion
## 2. Background / Current State
## 3. Recommended Approach
## 4. Implementation Steps / Design Details
## 5. Risks and Considerations
## 6. Verification Method
## 7. References
```

Requirements:

- Conclusion comes first
- Titles should be direct; do not use vague titles
- Long documents should use numbered sections
- Provide a clear stance on "recommended / alternative / not recommended"

### 6.3 Documentation Content Rules

- Prioritize explaining "why it is done this way" before "how to do it"
- Important constraints must be explicitly stated
- Use code formatting for commands, paths, configurations, and version numbers
- Structure content as lists whenever possible
- Long documents should have clear sections; do not write in long prose paragraphs

### 6.4 Fact and Inference Labeling

When referencing external materials, source code, or runtime results:

- Clearly state the source for facts
- Explicitly label inferences as "engineering judgment" or "inference"
- If conclusions depend on version, platform, or package differences, clearly state the applicable prerequisites

### 6.5 Team Collaboration Oriented

Documentation should be suitable for team reuse by default, therefore:

- Do not rely on the author's personal memory
- Do not omit key prerequisites
- Do not hide key steps within paragraphs
- Distinguish first-time execution steps from subsequent routine steps

### 6.6 Unity Documentation Specific Requirements

When involving Unity, try to specify:

- Unity version
- Render pipeline
- Target platform
- Dependent packages or plugins
- Entry points, such as menu paths, Inspector paths, Package installation methods

### 6.7 Visualization Rules

Inherits the Markdown visualization tiered approach from `Dockers`:

- **L1 Iterative Enhancement**: For internal discussions, technical convergence, rapid knowledge capture
- **L2 Polished Presentation**: For formal deliverables, long-term maintenance, external sharing

Usage suggestions:

- Use L1 by default for regular technical documentation
- Use L2 for long-term maintained standards, guides, and overview documents
- Use Mermaid diagrams only when they genuinely improve understanding; do not force diagrams for aesthetics

### 6.8 References and Citations

At the end of documents, it is recommended to keep a `References` section listing:

- Official documentation
- Repository URLs
- Key issues / PRs / discussions
- Local analysis basis (such as logs, configurations, test results)

### 6.9 Documentation Quality Checklist

Before publishing or submitting, check at minimum:

- [ ] Does the title accurately reflect the topic
- [ ] Are the date, goal, and scope stated
- [ ] Is the conclusion presented first
- [ ] Are facts distinguished from inferences
- [ ] Are prerequisites, steps, risks, and verification clearly stated
- [ ] Are commands, paths, and versions directly identifiable
- [ ] Are references provided

---

## 7. `.agents` Skills and Context System

This workspace has implemented the `.agents/` directory, containing two parts: **Skills (skill specifications)** and **Context (project context)**.

### 7.1 Directory Structure

```text
.agents/
├── skills/                          # AI skill specifications (implemented)
│   ├── README.md                    # Skill index and usage guide
│   ├── unity-runtime-dev/           # Runtime code development
│   ├── unity-editor-tooling/        # Editor tool development
│   ├── unity-package-dev/           # Package / UPM plugin development
│   ├── unity-patterns/              # Design pattern selector
│   ├── unity-blueprints/            # Game architecture blueprints
│   ├── unity-scene-contracts/       # Scene assembly contracts
│   ├── unity-performance-analysis/  # Performance analysis specifications
│   └── unity-documentation/         # Documentation and ADR writing
└── context/                         # Project context (implemented)
    ├── project-overview.md          # Current project tech stack declaration
    └── architecture-snapshot.md     # Project code architecture quick reference
```

### 7.2 Skill Routing Table (Required Reading)

> **Rule**: When handling the following tasks, **you must first read the corresponding Skill file** and use it as a behavioral constraint.
> Do not act solely on general knowledge — Skills contain workspace-specific rules, guardrails, and output format requirements.

| Task Scenario | Required Skill | Path |
|---------------|----------------|------|
| New project setup, prototyping, game framework design | `unity-blueprints` | `.agents/skills/unity-blueprints/SKILL.md` |
| Scene assembly, reference wiring, initialization order | `unity-scene-contracts` | `.agents/skills/unity-scene-contracts/SKILL.md` |
| Design pattern selection, decoupling solutions, architecture decisions | `unity-patterns` | `.agents/skills/unity-patterns/SKILL.md` |
| Writing game logic, fixing bugs, code review | `unity-runtime-dev` | `.agents/skills/unity-runtime-dev/SKILL.md` |
| Editor tools, Inspector, batch processing | `unity-editor-tooling` | `.agents/skills/unity-editor-tooling/SKILL.md` |
| Package / UPM plugin development | `unity-package-dev` | `.agents/skills/unity-package-dev/SKILL.md` |
| Performance investigation, stutter analysis, GC optimization | `unity-performance-analysis` | `.agents/skills/unity-performance-analysis/SKILL.md` |
| Writing documentation, technical plans, ADRs, research reports | `unity-documentation` | `.agents/skills/unity-documentation/SKILL.md` |

**Combination usage examples**:

- **New project setup** → First read `unity-blueprints` → Then read `unity-scene-contracts` → Read `unity-runtime-dev` when writing code
- **Performance investigation** → First read `unity-performance-analysis` → Read `unity-runtime-dev` when fixing code
- **Important technology selection** → First read `unity-patterns` → Read `unity-documentation` when recording decisions (ADR template)

### 7.3 unity-mcp Tool Operation Layer (Auto-Loaded)

> The Skills in this workspace focus on **design specifications** (how to write good code), complementing the Skills built into the unity-mcp plugin.
> The built-in unity-mcp skills focus on **tool operations** (how to use MCP tools effectively), are auto-synced by the plugin, and are not managed in this repository.

**Rule**: When using unity-mcp tools to perform Unity operations (creating scenes, building UI, managing assets, batch operations),
you should also refer to the unity-mcp built-in workflows and tools-reference, which contain:

- Best practices for MCP tool calls (compilation waiting, batch_execute pagination, screenshot verification loops)
- Parameter type conventions (string/boolean auto-conversion, URI format)
- UI component recipes (uGUI Slider/InputField/Toggle reference wiring steps)
- Error recovery patterns (Stale file recovery, Domain reload recovery)

Built-in skills are located by default at: `~/.codex/skills/unity-mcp-skill/` (auto-managed by the plugin; do not modify manually).

Collaboration relationship between the two systems:

```
User initiates task
    ↓
AGENTS.md Skill routing table → .agents/skills/ (Design specification layer)
    ↓
unity-mcp built-in skills → Auto-loaded (Tool operation layer)
    ↓
AI possesses both: Architecture judgment + Tool operation capability
```

### 7.3.1 Unity MCP Auto-Detection and Installation

> **Rule**: When the AI detects that the project needs Unity MCP tools but they are not yet installed, it should proactively guide the user to install them.
>
> **Note**: The MCP installation tools (`install-unity-mcp.ps1`, `package-unity-mcp.ps1`, etc.) are maintained in the
> `unity-mcp-setup/` directory of the LLM AI Toolkit. They are **not** deployed to individual Unity projects.
> The AI should guide the user to run these scripts from the toolkit directory.

#### Version Mapping Configuration

Version mapping relationships are maintained in the toolkit at `unity-mcp-setup/tools/unity-mcp-config.json`:

| Unity Version Range | Recommended MCP Version | Status | Description |
|---------------------|------------------------|--------|-------------|
| 6000.0+ (Unity 6) | v9.6.2 | recommended | Uses new BuildReport API |
| 2021.3 ~ 2023.x | v9.5.3 | tested | v9.6.x has compilation errors in this range |

> **Known Issue**: v9.6.x will fail to compile on Unity 2022.3 and below because the `BuildReport.SummarizeErrors()` API does not exist.
> The conditional compilation macro is incorrectly written as `#if UNITY_2022_3_OR_NEWER` (should be `#if UNITY_6000_0_OR_NEWER`).

#### AI Behavior Rules for Detecting Unity MCP

```
Session starts → Read context files
  ↓
Check whether com.coplaydev.unity-mcp exists in Packages/manifest.json
  ↓
├─ Installed and version is correct → Use MCP tools normally
├─ Installed but version mismatch → Inform user of version mismatch, suggest running install script to update
├─ Not installed → Decide based on scenario:
│   ├─ User task requires MCP tools → Prompt installation and provide command
│   └─ User task does not require MCP → Do not proactively prompt, work in pure-rules mode
└─ Cannot read manifest.json → Skip detection
```

#### Installation Commands

> These scripts are located in the LLM AI Toolkit's `unity-mcp-setup/tools/` directory, not in the Unity project.

```powershell
# Auto-detect Unity version and install corresponding MCP (requires network access to GitHub)
# Run from the toolkit directory:
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project"

# Check status only, do not install
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Check

# Force reinstall
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Force

# Manually specify version
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -McpVersion "9.5.3"
```

#### Offline Installation Commands (Sandbox / No Network Environments)

```powershell
# Step 1: Package .tgz on a machine with network access (only needs to be done once)
unity-mcp-setup\tools\package-unity-mcp.ps1

# Step 2: After copying .tgz to the target machine, install using offline mode
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Local
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Embedded
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Local -PackagePath "unity-mcp-setup\packages\com.coplaydev.unity-mcp-9.5.3.tgz"
```

> **AI Behavior Rule**: When network unavailability is detected (e.g., sandbox environment), prefer trying `-Local` mode first,
> and if no `.tgz` file is found, prompt the user to first run `package-unity-mcp.ps1` on a machine with network access.

#### Criteria for AI to Determine Whether MCP Is Needed

The following situations are considered as **requiring MCP**:

- User explicitly requests Unity Editor operations (creating scenes, managing assets, building UI, etc.)
- User requests screenshots, previews, or live debugging
- User requests running tests or building the project
- The current session already has MCP tools available (indicating a connection is established)

The following situations **do not require MCP**:

- Pure code writing, review, refactoring
- Documentation writing, plan design
- Performance analysis (based on static code analysis)
- Project structure planning

#### Version Mapping Maintenance

When a new Unity MCP version is released or a new Unity version needs testing, update `unity-mcp-setup/tools/unity-mcp-config.json` in the toolkit:

- `version_map`: Add new version mapping entries
- `tested_combinations`: Record tested and verified combinations
- `known_issues`: Record known compatibility issues

### 7.4 Project Context and Architecture Quick Reference (Required Reading + Auto-Generated)

> **Rule**: Before starting any substantive work, **you must first read the project context files**.
> Context files declare the current project's Unity version, render pipeline, target platform, and other key information.
> Conditional checks in Skills (e.g., "if the project has already introduced UniTask") depend on this information to execute correctly.

| File | Purpose | When to Read |
|------|---------|--------------|
| `.agents/context/project-overview.md` | Tech stack, version, platform, key dependencies, coding conventions | **Required reading at the start of every session** |
| `.agents/context/architecture-snapshot.md` | Project code architecture quick reference (directory structure, key scripts, core systems) | **Required reading at the start of every session** |

Both files are **auto-generated** by `tools/generate-snapshot.ps1` and do not need to be manually filled in.

#### Why Not Dynamic Scanning?

Large Unity projects may contain tens of thousands of files (the `Library/` directory alone can be several GB). Dynamic scanning at each session would cause:

- **Dramatically increased init time**: Scanning + analysis may take 30 seconds to several minutes
- **Token waste**: Large file lists consume context window, crowding out actual working space
- **Extremely low signal-to-noise ratio**: 99% of files are irrelevant to the current task

Therefore, a **pre-generated static snapshot** approach is adopted:

1. The user runs `tools/generate-snapshot.ps1` to regenerate when the project structure changes significantly
2. The script auto-detects the Unity project path, scans `Assets/`, analyzes C# scripts, and generates the two context files
3. The AI only needs to read these two files (a few KB each) at each session to obtain complete architectural awareness

#### Auto-Generation Script

```powershell
# Method 1: Auto-detect (when the script is located under tools/ at the project root)
.\tools\generate-snapshot.ps1

# Method 2: Specify project path
.\tools\generate-snapshot.ps1 -ProjectPath "D:\Your Unity Project"
```

The script will auto-generate:
- `.agents/context/project-overview.md` — Tech stack, coding conventions, known constraints
- `.agents/context/architecture-snapshot.md` — Directory structure, key scripts, scenes, package list

#### AI Initialization Behavior Rules

> **Permission Reminder**: Throughout the entire initialization process below, the AI has **read-only permissions** for Unity project files,
> must not write to any project files, and must not automatically execute scripts that modify project files.
> After initialization is complete and the working phase begins, the AI can operate on project files, but each modification requires user confirmation.
> See [2.8 File Modification Permission Boundaries](#28-file-modification-permission-boundaries-hard-rules).

```
Session starts
  ↓
╔══════════════════════════════════════════════════╗
║  Initialization Phase (Unity project files: read-only)  ║
╠══════════════════════════════════════════════════╣
║                                                  ║
║  Check if .agents/context/ directory exists       ║
║    ↓                                             ║
║  ├─ Exists and content is valid → Read both context files → Continue  ║
║  ├─ Exists but is template/empty → Prompt user to run script → Wait  ║
║  └─ Does not exist → Check Assets/ directory      ║
║        ├─ Yes → Prompt user to run script → Wait  ║
║        └─ No → Work in non-Unity project mode     ║
║    ↓                                             ║
║  Read project-overview.md (tech stack)  ← ~1-2KB  ║
║    ↓                                             ║
║  Read architecture-snapshot.md          ← ~3-5KB  ║
║    ↓                                             ║
║  Check Unity MCP installation status (read-only)  ║
║    ↓                                             ║
║  Based on task type, consult Skill routing table to load corresponding Skill  ║
║                                                  ║
╚══════════════════════════════════════════════════╝
  ↓
╔══════════════════════════════════════════════════╗
║  Working Phase (Unity project files: operable, requires user confirmation)  ║
╠══════════════════════════════════════════════════╣
║                                                  ║
║  Begin substantive work                          ║
║  - Agent-owned files: free read/write            ║
║  - Unity project files: explain and obtain user confirmation before each modification  ║
║                                                  ║
╚══════════════════════════════════════════════════╝
```

#### Determining Whether Context Files Are Valid

Context files are considered **invalid/template state** in the following cases:

- File does not exist
- File content contains unreplaced `<!-- Fill in` or `<!-- e.g.` placeholders
- The "Project Name" line in `project-overview.md` is still a `<!-- -->` comment
- "Unity Version" in `architecture-snapshot.md` is `Unknown`

When an invalid state is detected, the AI should:

1. **Inform the user**: Context files have not been generated or are outdated
2. **Provide the command**: `.\tools\generate-snapshot.ps1 -ProjectPath "<project path>"`
3. **Wait for the user to execute and then re-read**, rather than guessing the project structure

**Notes**:

- The AI **should not** proactively execute `list_files` or `find` to scan the entire project at the start of each session
- If a task requires understanding details of a specific directory, the AI should **inspect locally on demand** rather than performing a full scan
- Context files are auto-generated by the script; the user only needs to optionally fill in architecture intent in the `<!-- TODO -->` sections of `architecture-snapshot.md`

### 7.5 Future Extension Directions

For further knowledge preservation, the following can be gradually added:

```text
.agents/
├── context/
│   ├── project-overview.md        # Tech stack declaration (implemented)
│   ├── architecture-snapshot.md   # Code architecture quick reference (implemented)
│   ├── tech-stack.md              # Detailed tech stack description
│   └── directory-structure.md     # Complete directory structure description
├── memories/
│   ├── decisions/                 # Historical decision records
│   ├── issues/                    # Known issues and solutions
│   └── workflows/                 # Common workflows
└── rules/
    ├── code-style.md              # Code style standards
    └── testing.md                 # Testing standards
```

---

## 8. Meta-Rules (LLM Decision Framework)

### Meta-Rule 1: Do Not Pretend to Know

When uncertain about versions, APIs, or package capabilities, do not write guesses as facts.

### Meta-Rule 2: Prioritize Compatibility with Current State

Existing project structure, naming, and workflows take priority over generic templates.

### Meta-Rule 3: Solve the Problem First, Then Pursue Perfection

Prioritize completing the current objective; do not perform unrelated refactoring on the side.

### Meta-Rule 4: Analysis Must Be Actionable

Plans, reports, and recommendations must all be convertible into next-step actions.

### Meta-Rule 5: Documentation Is a Formal Deliverable

Documentation is not supplementary description but a formal deliverable of the workspace, required to be reusable, maintainable, and auditable.

---

## 9. Current Applicability Notes

This file applies to the `Unity` root directory and its subordinate projects, plugins, and analysis documents.

If a sub-project has more specific local rule files in the future, the following precedence should be followed:

1. Sub-directory local rules take priority
2. This file serves as the global baseline
3. In case of conflict, the rule closest to the work objective takes precedence

---

> Maintenance principle: This file should be continuously updated as the workspace content evolves, especially after Unity version strategies, Package structures, documentation systems, and common problem patterns stabilize, and should continue to be split into more granular `.agents` skill and rule files.
