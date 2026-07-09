# Unity Agent Rules — Let AI Understand Your Unity Project

> Place Agent rule files in any Unity project root directory, run one command, and AI can automatically index the project architecture.
>
> For Unity MCP installation (letting AI directly operate the Unity Editor), see the separate `unity-mcp-setup/` directory in the LLM AI Toolkit.

---

## What You Get

- **AI Automatically Understands Your Project**: One command generates a project index, and AI immediately knows your Unity version, render pipeline, code structure, and dependencies
- **Design Specification Constraints**: 8 professional Skill modules covering architecture blueprints, design patterns, code quality, performance analysis, and documentation writing
- **Works Out of the Box**: Copy files → Run script → AI starts working
- **Optional MCP Integration**: Pair with Unity MCP to let AI directly operate the Unity Editor

---

## Quick Start (2 Steps)

### Step 1: Copy Files to Your Unity Project

```bash
# Copy from the LLM AI Toolkit's unity-agent-rules directory
cp unity-agent-rules/AGENTS.md       <your-Unity-project>/
cp -r unity-agent-rules/.agents      <your-Unity-project>/
cp -r unity-agent-rules/tools        <your-Unity-project>/
```

Or use the deployment script (see LLM AI Toolkit's `DEPLOY.md`):

```powershell
# From the toolkit root directory
unity-agent-rules\tools\deploy-agent-rules.ps1 -ProjectPath "D:\Your Unity Project"
```

### Step 2: Generate Project Index

```powershell
# Run in the Unity project root directory (PowerShell)
.\tools\generate-snapshot.ps1

# Or specify the project path
.\tools\generate-snapshot.ps1 -ProjectPath "D:\Your Unity Project"
```

**Done!** The script will automatically scan the project and generate:

| Generated File | Content | Size |
|---------|------|------|
| `.agents/context/project-overview.md` | Tech stack, coding conventions, known constraints | ~1-2 KB |
| `.agents/context/architecture-snapshot.md` | Directory structure, key scripts, scenes, package list | ~3-5 KB |

Information automatically detected by the script includes:

- Unity version, render pipeline, input system, UI framework
- Target platform, scripting backend, .NET version
- Installed packages and their purposes
- C# script analysis (class names, base classes, singletons, editor scripts, etc.)
- Coding conventions (naming style, serialization approach, event system, DI framework, testing framework)
- Directory structure tree
- Known constraints (auto-detected)

### Final Project Structure

```
YourUnityProject/
├── AGENTS.md              ← Global rules + Skill routing table
├── .agents/
│   ├── skills/            ← 8 design specification modules
│   │   ├── README.md
│   │   ├── unity-runtime-dev/
│   │   ├── unity-patterns/
│   │   ├── unity-blueprints/
│   │   ├── unity-scene-contracts/
│   │   ├── unity-editor-tooling/
│   │   ├── unity-package-dev/
│   │   ├── unity-performance-analysis/
│   │   └── unity-documentation/
│   └── context/           ← Auto-generated project index
│       ├── project-overview.md
│       └── architecture-snapshot.md
├── tools/
│   └── generate-snapshot.ps1  ← Index generation script
├── Assets/
├── Packages/
└── ProjectSettings/
```

> **Note**: Unity MCP installation tools (`install-unity-mcp.ps1`, `package-unity-mcp.ps1`, etc.) are maintained
> separately in the `unity-mcp-setup/` directory of the LLM AI Toolkit and are not deployed to individual Unity projects.

---

## AI Workflow

When an AI assistant (Roo Code, Cursor, Claude Code, etc.) opens your project:

```
Session starts
  ↓
Read AGENTS.md (global rules)
  ↓
Read .agents/context/project-overview.md (tech stack)
  ↓
Read .agents/context/architecture-snapshot.md (architecture quick reference)
  ↓
Automatically load the corresponding Skill module based on task type
  ↓
Start working (project structure understood, design specifications followed)
```

If the context files do not exist or are in template state, AI will prompt you to run `generate-snapshot.ps1`.

---

## Optional: Install Unity MCP (AI Directly Operates Unity)

> Agent rules work without MCP. MCP is an additional capability that lets AI directly create scenes, write scripts, and manage assets in the Unity Editor.
>
> Unity MCP installation tools are maintained separately in the `unity-mcp-setup/` directory of the LLM AI Toolkit.

For complete installation instructions, see:
- **Quick guide**: `unity-mcp-setup/README.md`
- **Detailed deployment guide**: `unity-mcp-setup/docs/unity-mcp-deployment-guide.md`
- **Toolkit deployment guide**: `DEPLOY.md` (Phase B3)

### Quick Reference

```powershell
# From the LLM AI Toolkit root directory:

# Auto-detect Unity version and install corresponding MCP
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project"

# Check status only
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Check

# Offline installation (sandbox / no network)
unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Local
```

---

## Rule Module Descriptions

| Module | Purpose | When It Takes Effect |
|------|------|---------|
| `unity-runtime-dev` | Runtime code specifications | Writing game logic, fixing bugs |
| `unity-patterns` | Design pattern selection | "Which pattern should I use" |
| `unity-blueprints` | Architecture blueprints | New project startup |
| `unity-scene-contracts` | Scene assembly contracts | Building scenes, wiring references |
| `unity-editor-tooling` | Editor tool specifications | Creating menu tools, Inspector |
| `unity-package-dev` | Package development specifications | Creating UPM plugins |
| `unity-performance-analysis` | Performance analysis specifications | Stuttering, GC optimization |
| `unity-documentation` | Documentation writing specifications | Writing proposals, ADRs |

---

## Update Project Index

When the project structure changes significantly (adding modules, introducing new dependencies, restructuring directories, etc.), re-run:

```powershell
.\tools\generate-snapshot.ps1
```

The script supports automatic project path detection (searching upward from the script location or current working directory for `Assets/`).

---

## FAQ

**Q: Will these files affect Unity compilation?**
→ No. `AGENTS.md`, `.agents/`, and `tools/` are all plain text files. Unity will ignore them.

**Q: Can I use only Agent rules without MCP?**
→ Yes. Agent rules work independently. AI will follow the specifications and understand the project structure, but cannot directly operate the Unity Editor.

**Q: Which AI clients are supported?**
→ Any AI assistant that can read project files can use Agent rules (Roo Code, Cursor, Claude Code, Codex CLI, OpenCode, etc.). MCP features require the client to support the MCP protocol.

**Q: Does `generate-snapshot.ps1` only support Windows?**
→ PowerShell 7+ supports cross-platform (Windows / macOS / Linux). Install via: `dotnet tool install --global PowerShell`.

**Q: Should the project index be committed to Git?**
→ Recommended. This way, team members and AI assistants in CI environments can use it directly without each person regenerating it.

**Q: Unity MCP status is not Healthy after installation?**
→ Confirm that the Unity version matches the MCP version (use v9.5.3 for 2022.3). Run `unity-mcp-setup\tools\install-unity-mcp.ps1 -ProjectPath "<project>" -Check` from the toolkit to quickly verify.

**Q: Will AI automatically install Unity MCP?**
→ AI will not automatically perform installation. When AI detects that a task requires MCP but it is not installed, it will prompt you to run the install script from the toolkit's `unity-mcp-setup/tools/` directory, and you decide whether to execute it.

**Q: How to add new Unity version mappings?**
→ Edit `unity-mcp-setup/tools/unity-mcp-config.json` in the toolkit, add new entries in `version_map`, and record test results in `tested_combinations`.

---

## License

MIT License
