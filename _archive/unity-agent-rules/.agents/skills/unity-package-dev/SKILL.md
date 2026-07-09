# Unity Package Development Standards

> **Purpose**: Guide LLMs in designing, implementing, and maintaining Unity Packages / UPM plugins.
>
> **Applicable Scope**: Standalone package repositories, embedded packages in `Packages/`, shared SDKs, reusable plugins.

---

## 1. Recommended Structure

```text
<package-root>/
├── package.json
├── Runtime/
├── Editor/
├── Tests/
├── Samples~/
├── Documentation~/
└── CHANGELOG.md
```

---

## 2. Hard Rules

### 2.1 Package Structure Must Be Clear

- `Runtime/` and `Editor/` must be separated
- Samples go in `Samples~/`
- Documentation goes in `Documentation~/` or root-level docs

### 2.2 `package.json` Must Be Complete

At minimum, ensure the following fields are present:

- `name`
- `displayName`
- `version`
- `unity`
- `description`
- `dependencies`

### 2.3 Public API Stability Takes Priority

- Do not casually rename public types, namespaces, or key entry points
- If there are breaking changes, they must be clearly documented

### 2.4 Do Not Hardcode Project-Internal Assumptions into Packages

- Avoid binding to specific project paths
- Avoid implicit dependencies on specific scenes or internal objects
- Avoid tight coupling to the host project structure

---

## 3. Recommended Practices

### 3.1 Naming

- Use stable, clear, publishable namespaces for package names
- Maintain consistency between directory and assembly naming

### 3.2 Samples and Documentation

- Provide at least one minimal runnable sample
- Documentation should clearly cover installation, initialization, common usage, and limitations
- Sample code should prioritize demonstrating "typical usage", not covering every scenario

### 3.3 Dependency Control

- Prefer a minimal dependency set
- Clearly distinguish between runtime dependencies and editor dependencies
- Do not introduce heavy third-party packages without justification

### 3.4 Compatibility

- Clearly state the supported Unity version range
- If the package depends on a specific render pipeline, platform, or input system, state it explicitly

---

## 4. Quality Checklist

- [ ] Directory structure follows Package conventions
- [ ] `package.json` information is complete
- [ ] Runtime / Editor are separated
- [ ] A minimal sample or usage guide exists
- [ ] Public API has no undocumented breaking changes
- [ ] Unity version and dependency prerequisites are stated

---

## 5. Meta-Rules

### Meta-Rule 1: A Package Is a Product, Not Project Scaffolding Fragments

Think from the perspective of reuse, compatibility, and upgrade costs.

### Meta-Rule 2: Make It Usable First, Then Talk About Feature Coverage

Installation, initialization, and a minimal sample take priority over "having lots of features".
