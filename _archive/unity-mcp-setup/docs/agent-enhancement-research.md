# Agent Enhancement Research and Solution Design

Document Date: 2026-03-29 (Consolidated Edition)
Objective: Record the research process, gap assessment, ecosystem analysis, and solution selection for AI Agent enhancement methods
Status: Research complete, candidate solutions pending validation

---

## 1. Conclusions and Current Decisions

### 1.1 Current Workspace Coverage

```
Agent = LLM + AGENTS.md(Rules) + Skills(Professional Capabilities) + MCP(Tools) 
        + Hooks(Automation) + Memory(Persistence) + RAG(Knowledge)
```

Current coverage is approximately **40%** (Rules + Skills + Partial MCP + Partial Context), mainly missing Memory and execution constraint mechanisms.

### 1.2 Key Findings

1. **AGENTS.md + Skills are mature**: 790 lines of rules + 8 Skills, industry-leading level
2. **MCP is partially deployed**: Unity MCP v9.5.3 is configured, meeting Unity development needs
3. **Memory and execution constraints are completely missing**: Every new session suffers "amnesia", AI knows the rules but doesn't necessarily follow them
4. **mem0/LightRAG don't solve the core problem**: The root cause is "lack of execution discipline" not "lack of knowledge"
5. **AGENTS.md has become the de facto industry standard**: OpenCode, GitHub Copilot, Roo Code, and Windsurf all natively support it

### 1.3 Candidate Solutions (Pending Validation)

Adopting a **"Session Memory + Mandatory Constraint Loading"** two-layer architecture:
- **SESSION_START.md**: Mandatory loading of project constraints and work boundaries at every new session
- **Session Archival**: Automatically record each session's work content for context recovery in next session
- **ADR (Optional)**: Record important design decisions

>  This solution has not been validated in practice; specific implementation approach may be adjusted.

### 1.4 Decision Evolution Record

| Time | Approach | Conclusion |
|------|----------|------------|
| 03-27 | Evaluated 6 enhancement methods, identified Hooks + Memory as P1 | Hooks are the most direct means to address execution discipline |
| 03-27 | Evaluated whether mem0/LightRAG can solve rule compliance issues | No — the problem is "not looking" rather than "not knowing" |
| 03-29 | Designed implementation plan for a 150-person team | Abandoned Hooks (single-person modules don't need commit interception), pivoted to session memory |

**Pivot reason**: Hooks are suited for "preventing non-compliant commits" (multi-person collaboration scenarios), but the team's actual pain point is "LLM amnesia in new sessions" (single-person module scenario). The two problems require different solutions.

---

## 2. Six Enhancement Methods Quick Reference

| # | Method | Positioning | Maturity | Core Value |
|---|--------|-------------|----------|------------|
| 1 | **MCP** | Tool integration protocol |  Industry standard | Standardize communication between AI and external tools ("AI's USB-C") |
| 2 | **Hooks** | Event-driven automation |  Mainstream support | Insert automated checks at Agent lifecycle event points |
| 3 | **Memory Systems** | Persistent memory |  Rapidly developing | Maintain context across sessions, learn preferences, accumulate knowledge |
| 4 | **Context/Project Memory** | Project context |  Common practice | Project-specific persistent information (tech stack, architecture, current tasks) |
| 5 | **RAG/Knowledge Base** | Knowledge retrieval |  Mature solution | Access external knowledge bases, surpass training data limitations |
| 6 | **Multi-Agent/Subagents** | Multi-agent collaboration |  Developing | Decompose complex tasks to multiple specialized Agents |

### Method Combination Recommendations

| Scenario | Recommended Combination |
|----------|------------------------|
| **Personal project** | AGENTS.md + Skills + Context |
| **Team collaboration** | + MCP (shared tools) + Hooks (quality gates) |
| **Enterprise-level** | + Memory Systems + RAG + Multi-Agent |

### Four Memory Types (CoALA Framework)

| Memory Type | Human Analogy | Agent Implementation | Current Status |
|-------------|---------------|---------------------|----------------|
| **Working Memory** | Current thinking | Current session context |  LLM native capability |
| **Procedural Memory** | Muscle memory | AGENTS.md + Skills |  Fully implemented |
| **Semantic Memory** | General knowledge | User preferences, domain knowledge |  Only architecture-snapshot exists |
| **Episodic Memory** | Autobiographical memory | Session logs, decision records |  Completely missing |

---

## 3. Current Workspace Gap Assessment

### 3.1 Six-Dimensional Gap Matrix

| # | Enhancement Method | Current Status | Gap Level | Priority |
|---|-------------------|----------------|-----------|----------|
| 1 | **AGENTS.md + Skills** |  790 lines of rules + 8 Skills |  No gap | — |
| 2 | **MCP** |  Unity MCP v9.5.3 configured |  Small gap | P2 |
| 3 | **Hooks** |  Completely missing |  Large gap | See decision evolution |
| 4 | **Memory Systems** |  Completely missing |  Large gap | **P1** |
| 5 | **Context / Project Memory** |  project-overview is an empty template |  Medium gap | P2 |
| 6 | **RAG / Knowledge Base** |  Completely missing (not needed at current scale) |  Medium gap | P3 |
| 7 | **Multi-Agent / Subagents** |  Roo Code multi-mode available |  Small gap | P4 |

### 3.2 Capability Radar Chart

```
          AGENTS.md + Skills
                
               /       \
    Multi-Agent         MCP
                  
        |               |
       RAG           Hooks
                      
         \            /
          Context/Memory
             
```

### 3.3 Detailed Assessment for Each Item

**AGENTS.md + Skills ( No gap)**: The rule system is complete, well-layered, with a clear routing mechanism. The only improvement point is the lack of an enforcement mechanism to ensure AI actually reads the Skill.

**MCP ( Small gap)**: Unity MCP is configured, meeting Unity development needs. Missing general-purpose MCP Servers (GitHub, search, etc.), but this is nice-to-have.

**Memory Systems ( Large gap)**: AGENTS.md §7.5 has planned the `.agents/memories/` directory structure but it was never implemented. Every new session suffers "amnesia", unaware of what solution was chosen last time or what pitfalls were encountered.

**Context ( Medium gap)**: `architecture-snapshot.md` has been auto-generated, but `project-overview.md` is still an empty template, causing Skills conditional judgments to fail.

**RAG ( Medium gap)**: The current project is small-scale (12 C# scripts), not needed for now. Consider when scripts exceed 50+ or memory files exceed 20.

**Multi-Agent ( Small gap)**: Roo Code multi-mode is already available, missing mode switching rules and custom expert modes.

---

## 4. Ecosystem Compatibility

### 4.1 AGENTS.md Tool Support Matrix

| Tool | AGENTS.md Support | Skills Support | MCP | Hooks | Notes |
|------|-------------------|----------------|-----|-------|-------|
| **OpenCode** |  Native auto |  |  |  Plugin | Open format |
| **GitHub Copilot** |  Native (requires enabling) |  |  |  Preview | VS Code requires setting `chat.useAgentsMdFile: true` |
| **Roo Code** |  Native auto |  |  |  | Supports mode-specific rules `.roo/rules-{mode}/` |
| **Windsurf** |  Native auto |  |  |  | Subdirectory AGENTS.md only applies to that directory |
| **Cursor** |  Needs workaround |  MDC format |  |  15+ events | Reference AGENTS.md in `.cursorrules` |
| **Continue** |  Config-driven |  |  |  | Requires configuration in config.json |

**Key conclusion**: AGENTS.md has gained support from mainstream vendors. Cursor is the main exception but can be made compatible through workarounds.

### 4.2 Agent Skills Specification Key Points

**Standard source**: Developed by Anthropic, released as an open standard in December 2025 (https://agentskills.io)

**Core format**:

```yaml
# .agents/skills/<skill-name>/SKILL.md
---
name: skill-name              # kebab-case, must match directory name
description: |
  Describe functionality and trigger conditions.
  Must include "Use this skill when..." trigger phrase
---
```

**Progressive disclosure**:
- Level 1 (frontmatter): Feature description + trigger conditions → Auto-loaded
- Level 2 (SKILL.md body): Detailed specifications, workflows → Loaded on demand
- Level 3 (references/): Reference documents, checklists → Loaded during deep usage

**Cross-tool compatibility approach**:

```
AGENTS.md                          # Main rules file (universal across all tools)
.agents/skills/                    # Main Skill directory
.claude/skills -> .agents/skills   # Symlink (Claude Code compatible)
.cursorrules                       # Cursor fallback (one line referencing AGENTS.md)
```

### 4.3 Configuration Path Quick Reference for Each Tool

| Tool | AGENTS.md Path | Skills Path | MCP Path | Hooks Path |
|------|---------------|-------------|----------|-----------|
| Claude Code | `CLAUDE.md` | `.claude/skills/` | `.mcp.json` | `.claude/settings.json` |
| Cursor | `.cursorrules` | `.cursor/rules/` | `.cursor/mcp.json` | `.cursor/hooks/` |
| Copilot | `.github/copilot-instructions.md` | `.github/skills/` | `.vscode/mcp.json` | `.github/hooks/` |
| OpenCode | `AGENTS.md` | `.opencode/skills/` | `opencode.json` | `.opencode/plugins/` |
| Roo Code | `AGENTS.md` | `.roo/skills/` | `.roo/mcp.json` |  Not supported |
| Kimi Code | `AGENTS.md` | Via AGENTS.md | `.vscode/mcp.json` |  Not supported |

---

## 5. Memory Solution Evaluation

### 5.1 Why mem0/LightRAG Are Not Applicable

**Core diagnosis**: The root cause of LLM deviating from rules is "lack of execution discipline", not "lack of knowledge".

| Root Cause | Description | Can mem0/LightRAG solve it? |
|------------|-------------|---------------------------|
| **Attention dilution** | As conversation grows, the attention weight on rules in the System Prompt gets diluted |  Memory retrieval adds content to context, actually worsening dilution |
| **Path locking** | LLM tends to continue its reasoning path rather than go back and check rules |  Doesn't change reasoning inertia |
| **Passive rule triggering** | "Must read Skill first" is a text instruction that the LLM can "forget" |  mem0 retrieval is also passive |
| **Long-task degradation** | Behavior in the later stages of complex tasks is increasingly driven by recent context |  Limited help |
| **Rule-action decoupling** | Rules are static text, actions are tool calls, with no programmatic binding |  Doesn't provide action constraint capability |

> In one sentence: The problem is not "the LLM doesn't know the rules", but "the LLM knows but doesn't look at / doesn't follow them".

### 5.2 Five Candidate Solutions Comparison

| Solution | Principle | Recommendation | Implementation Difficulty |
|----------|-----------|----------------|--------------------------|
| **A. Mandatory Checkpoints** | Define self-check checklists in AGENTS.md as a substitute for Hooks |  |  Low (just modify files) |
| **B. Rules as Tools** | Transform rules into MCP tools that must be called |  |  Medium (requires developing MCP Server) |
| **C. Supervisor Pattern** | Introduce a "rule guardian" Agent to review every operation |  |  High (doubles cost) |
| **D. Injection Position Optimization** | Leverage the "Lost in the Middle" phenomenon to optimize rule placement |  |  Low |
| **E. mem0 as Supplement** | Record violation patterns and decision history |  |  Medium |

**Recommended implementation path**:

```
Phase 1 (Can do immediately): Add mandatory self-check checklists to AGENTS.md + Optimize rule injection position
    ↓
Phase 2 (Requires development): Develop skill-validator MCP Server
    ↓
Phase 3 (Architecture upgrade): Supervisor Agent + mem0 to record violation patterns
```

### 5.3 Session Memory Solution Design Draft

>  The following is an unvalidated design draft, targeting a 150-person team (single-person module ownership model).

**Two-layer architecture**:

```
Layer 1: Session Constraint Layer
  Mandatory reading of SESSION_START.md at every new session
  → Let the LLM know "what I can do and what I cannot do"

Layer 2: Memory Recovery Layer
  Automatically read the most recent Session records
  → Let the LLM continue the previous work instead of starting from scratch
```

**SESSION_START.md core content**:
- Personal work boundaries (which directories can be modified, which cannot)
- Project tech stack constraints
- Current Sprint/task focus
- Mandatory recitation confirmation mechanism

**Session archival structure**:

```
.agents/memories/sessions/
├── 2026-03-28-143022.md     # Historical session
├── 2026-03-29-091511.md     # Current session
└── current.md -> ...         # Symlink
```

**Key differences from the Hooks approach**:

| Dimension | Hooks Approach | Session Memory Approach |
|-----------|---------------|------------------------|
| Core problem | Prevent non-compliant commits | Solve LLM amnesia |
| Enforcement point | Intercept at commit time | Load at session start |
| Developer burden | Must record ADR to commit | Voluntary recording, non-blocking |
| Applicable scenario | Multi-person conflicts, compliance auditing | Single-person modules, self-discipline-based |

**Supporting tools (to be developed)**:
- `agent-memory` CLI: Manage session start/end, checkpoint, decision
- IDE Adapter: Automatically trigger session lifecycle
- Supported platforms: OpenCode, VS Code, LibreChat

---

## 6. Next Steps

### Items Pending Validation

- [ ] Whether the SESSION_START.md mandatory recitation mechanism is effective (does the LLM actually comply)
- [ ] Whether the information density of session archives is sufficient to recover context
- [ ] Whether the `agent-memory` CLI PowerShell implementation is cross-platform viable
- [ ] Actual effectiveness of soft Hooks (self-check checklists) in Roo Code
- [ ] Integration approach of the session memory solution with the existing AGENTS.md system

### Implementation Priority

| Priority | Item | Dependencies | Estimated Effort |
|----------|------|--------------|------------------|
| **P1** | Fill in `project-overview.md` (run generate-snapshot.ps1) | None | 5 minutes |
| **P2** | Add soft Hooks to AGENTS.md (mandatory self-check checklists) | None | 1 hour |
| **P3** | Create `.agents/memories/` directory and templates | None | 30 minutes |
| **P4** | Develop `agent-memory` CLI prototype | P3 | 1-2 days |
| **P5** | Evaluate whether tool-native Hooks are needed | P2 validation results | Depends on situation |

---

## 7. References

### Standards and Specifications
- **Agent Skills Specification** — https://agentskills.io
- **MCP Specification** — https://modelcontextprotocol.io
- **Claude Code Hooks** — https://docs.anthropic.com/en/docs/claude-code/hooks

### Tool Documentation
- **OpenCode** — https://opencode.ai/docs
- **GitHub Copilot AGENTS.md** — https://github.blog/changelog/2025-08-28-copilot-coding-agent-now-supports-agents-md-custom-instructions/
- **Roo Code Custom Instructions** — https://docs.roocode.com/features/custom-instructions
- **Windsurf Cascade Memories** — https://docs.windsurf.com/windsurf/cascade/memories
- **Cursor Rules** — https://docs.cursor.com/context/rules-for-ai

### Frameworks and Tools
- **Mem0** — https://mem0.ai (Self-improving memory framework)
- **Zep** — https://zep.ai (Temporal knowledge graph)
- **LightRAG** — https://github.com/HKUDS/LightRAG

### Academic References
- **CoALA Memory Framework** — Four memory type classification (Working / Procedural / Semantic / Episodic)
- **Lost in the Middle** — Liu et al., 2023. Attention degradation phenomenon for information in the middle of long contexts in LLMs

### This Workspace
- `AGENTS.md` — Global rules file (790 lines, 9 major sections, 5 meta-rules)
- `.agents/skills/` — 8 Skill specification files
- `docs/unity-mcp-deployment-guide.md` — Unity MCP Deployment Guide
- `docs/device-system-report.md` — Development Environment Baseline

---

> Consolidation note: This document was consolidated from the following 8 documents (2026-03-29):
> - `ai-agent-enhancement-methods.md` → §2 Methods Quick Reference
> - `agent-gap-analysis.md` → §3 Gap Assessment + §5.2 Solution Comparison
> - `agent-spec-ecosystem-analysis.md` → §4 Ecosystem Compatibility
> - `llm-memory-infra-analysis.md` → §5.1 mem0 Analysis
> - `llm-collaboration-spec/implementation-plan.md` → §5.3 Session Memory Draft
> - `llm-collaboration-spec/quick-start-guide.md` → Unvalidated, not retained
> - `llm-collaboration-spec/adapter-development-guide.md` → Unvalidated, not retained
> - `llm-collaboration-spec/agent-memory-CLI-technical-spec.md` → Unvalidated, not retained
