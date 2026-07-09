# Unity Documentation Writing Standards

> **Purpose**: Guide LLMs in producing structured, maintainable, and deliverable technical documentation within the Unity workspace.
>
> **Applicable Scope**: Implementation proposals, integration guides, troubleshooting documents, plugin documentation, architecture descriptions, research reports, architecture decision records.

---

## 1. Core Principles

1. Lead with conclusions
2. Separate facts from inferences
3. Focus on actionability, avoid vague summaries
4. Write to team reuse standards by default

---

## 2. Default Structure

```markdown
# Title

Document Date: YYYY-MM-DD
Objective:
Applicable Scope:

## 1. Conclusion
## 2. Background / Current State
## 3. Recommended Approach
## 4. Implementation Steps / Design Details
## 5. Risks and Considerations
## 6. Verification Methods
## 7. References
```

---

## 3. Hard Rules

### 3.1 Titles Must Be Direct

- Titles must directly reflect the document topic
- Avoid vague titles such as "Summary", "Notes", "Record"

### 3.2 Documents Must Be Actionable

Documents should enable the reader to perform at least one of these actions:

- Make a decision
- Implement a solution
- Troubleshoot an issue
- Verify a result

### 3.3 Prerequisites Must Be Stated

Unity documents should specify whenever possible:

- Unity version
- Render pipeline
- Target platform
- Package dependencies
- Access entry points

### 3.4 External Conclusions Must Cite Sources

- Official documentation
- Repositories
- Issues / PRs
- Local experiment results
- Log or code evidence

---

## 4. Architecture Decision Records (ADR)

When the project makes important technical decisions, use the ADR template to document them:

### 4.1 ADR Template

```markdown
# ADR-NNN: Decision Title

Document Date: YYYY-MM-DD
Status: Proposed / Accepted / Deprecated / Superseded

## Background
Why is this decision needed? What problem are we currently facing?

## Decision
What approach have we decided to adopt?

## Alternatives Considered
What other approaches were considered? Why were they not chosen?

| Approach | Advantages | Disadvantages | Reason Not Chosen |
|----------|-----------|---------------|-------------------|
| Approach A | ... | ... | ... |
| Approach B | ... | ... | ... |

## Impact
What consequences will this decision bring?

- Positive impacts
- Negative impacts / trade-offs
- Items requiring follow-up

## Verification Methods
How do we confirm this decision is correct?

## References
Related documents, issues, discussion links.
```

### 4.2 ADR Use Cases

An ADR should be created in the following situations:

- Choosing a render pipeline (URP / HDRP / Built-in)
- Choosing an input system (new Input System / legacy Input Manager)
- Choosing a UI framework (UI Toolkit / uGUI)
- Choosing a state management approach
- Choosing a networking framework
- Adding or removing significant third-party dependencies
- Changing the project directory structure or asmdef boundaries

### 4.3 ADR Storage Location

```
docs/adr/
├── ADR-001-Choose-URP-Render-Pipeline.md
├── ADR-002-Adopt-New-Input-System.md
└── ADR-003-UI-Framework-Selection.md
```

---

## 5. Visualization Tiers

### L1 Iterative Enhancement

Suitable for:

- Internal reviews
- Quick convergence
- Continuously iterated documents

Characteristics:

- Basic structure
- Essential tables
- Simple Mermaid diagrams

### L2 Polished Presentation

Suitable for:

- Formal deliverables
- Long-term maintenance standards
- Frequently referenced guides

Characteristics:

- More consistent formatting
- More complete diagrams and charts
- Higher information density

---

## 6. Quality Checklist

- [ ] Conclusion is front-loaded
- [ ] Title is accurate
- [ ] Date, objective, and applicable scope are stated
- [ ] Facts and inferences are distinguished
- [ ] Steps, risks, and verification are clearly described
- [ ] References are provided
- [ ] Important decisions assessed for ADR need

---

## 7. Meta-Rules

### Meta-Rule 1: Documentation Is Not an Afterthought

Documentation itself is a deliverable.

### Meta-Rule 2: Clarity Before Completeness

Readability takes priority over piling on information.

### Meta-Rule 3: Conclusions Must Have Boundaries

Any recommendation should state its applicable prerequisites and limitations.

### Meta-Rule 4: Decisions Must Leave a Trail

Important technical choices should be recorded with ADRs to avoid future confusion about "why we did it this way".

---

## 8. Related Skills

- Game architecture blueprints → refer to `unity-blueprints` (architecture documentation at project startup)
- Performance analysis reports → refer to `unity-performance-analysis` (performance document format)
- Scene assembly contracts → refer to `unity-scene-contracts` (scene documentation)
