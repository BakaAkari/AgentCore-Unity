# Unity Editor Tooling Standards

> **Purpose**: Guide LLMs in writing and modifying Unity editor tools, menu commands, custom Inspectors, batch processing tools, and development utilities.
>
> **Applicable Scope**: `Assets/**/Editor/`, editor windows, menu items, import processors, bulk fix tools.

---

## 1. Goals

The core goals of this Skill:

- Improve editor workflow efficiency
- Avoid polluting runtime assemblies
- Reduce risk of accidental operations
- Make tool behavior predictable, reversible, and reusable

---

## 2. Hard Rules

### 2.1 Editor Code Must Be Isolated

- Editor code belongs in `Editor/` directories or editor asmdef assemblies
- Runtime assemblies must not directly depend on editor assemblies

### 2.2 Batch Operations Must Be Cautious

Be conservative by default when performing the following:

- Bulk modifying Prefabs
- Bulk modifying Scenes
- Bulk renaming or relocating assets
- Bulk generating / deleting assets

Requirements:

- Provide previews, filters, or clearly defined scope whenever possible
- State the affected objects
- Avoid irreversible implicit batch processing

### 2.3 Menus and Entry Points Must Be Intuitive

- Menu paths should be clear
- Names should directly describe the purpose
- Do not create semantically ambiguous entry points

### 2.4 Avoid Disguising One-Off Scripts as Long-Term Tools

- Distinguish between temporary fix scripts and long-term maintenance tools
- One-off migration logic should not be permanently placed in menus by default

---

## 3. Recommended Practices

### 3.1 Tool Classification

Suitable for long-term retention:

- Inspector enhancements
- Menu item tools
- Asset validators
- Bulk fix tools
- Import rule tools

Suitable for temporary use:

- One-off migration scripts
- Single-use data fix scripts
- Helper scripts that only serve a specific version transition

### 3.2 User Feedback

- Explain what will be done before execution
- Output a result summary after execution
- On error, report the failed objects and reasons

### 3.3 Asset Modification

- Consider `AssetDatabase` refresh and save timing before modifying assets
- Be aware of differences between Prefab, Scene, and ScriptableObject during batch processing
- Do not carelessly trigger a full-project reimport

---

## 4. Quality Checklist

- [ ] Editor code is isolated from runtime code
- [ ] Tool entry points are clearly named
- [ ] Batch operation scope is well-defined
- [ ] Will not accidentally modify large numbers of assets
- [ ] Basic result or error feedback is output
- [ ] Verification or rollback methods are documented

---

## 5. Meta-Rules

### Meta-Rule 1: Tools Must Be Safe First

The primary goal of editor tools is not to be flashy, but to reduce accidental operations.

### Meta-Rule 2: Separate Long-Term Tools from Temporary Scripts

Do not stuff one-off migration logic into the permanent tool system.

### Meta-Rule 3: The Larger the Impact, the More Visible It Must Be

For bulk modifications, increase visibility and confirmation cost by default.
