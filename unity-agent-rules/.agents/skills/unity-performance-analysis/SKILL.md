# Unity Performance Analysis Guidelines

> **Purpose**: Guide LLM in analyzing runtime performance, editor performance, and resource overhead for Unity projects, providing issue identification and actionable recommendations.
>
> **Scope**: Stutter analysis, GC analysis, UI performance, resource loading, script hotspots, rendering and update pipeline investigation.

---

## 1. Goal

Performance analysis output should aim to answer three things:

1. Where is the bottleneck most likely located
2. What is the evidence
3. What are the top priorities to address first

---

## 2. Hard Rules

### 2.1 Do Not Present Guesses as Conclusions

Must distinguish between:

- Confirmed: Supported by Profiler, Frame Debugger, logs, code paths, reproduction steps
- Inferred: High-probability judgment based on experience

### 2.2 Do Not Give "Broad and Empty" Optimization Advice

Avoid outputting only these kinds of ineffective conclusions:

- "Reduce Draw Calls"
- "Optimize Update"
- "Reduce GC"

Must identify specific objects, modules, paths, or mechanisms whenever possible.

### 2.3 Prioritize Finding Hotspots

Analyze first:

- High-frequency call paths
- Large object count paths
- UI refresh hotspots
- Resource loading spikes
- Garbage collection spikes

Do not start with global refactoring.

---

## 3. Red Flag Checklist

### 3.1 CPU Red Flags

| Red Flag | Severity | How to Check |
|----------|----------|-------------|
| Multiple unrelated `Update`/`LateUpdate`/`FixedUpdate` loops | 🔴 High | Search all Update methods in the project |
| Repeated `Find`/`GetComponent`/`Camera.main`/`tag` lookups in hot paths | 🔴 High | Code review + Profiler |
| Frequent `Instantiate`/`Destroy` of objects suitable for pooling | 🟡 Medium | Profiler GC Alloc column |
| Reflection used in runtime hot paths | 🔴 High | Search for `GetType`/`Invoke`/`GetMethod` |
| Editor-only code leaking into runtime | 🟡 Medium | Search for missing `#if UNITY_EDITOR` guards |
| Physics/Animation/UI update frequency mismatch | 🟡 Medium | Check responsibility split between FixedUpdate and Update |

### 3.2 GC Red Flags

| Red Flag | Severity | How to Check |
|----------|----------|-------------|
| Per-frame LINQ queries (`Where`/`Select`/`ToList`) | 🔴 High | Code search |
| Per-frame string concatenation (`+` or `$""`) | 🔴 High | Code search |
| Closure captures (lambda referencing external variables) | 🟡 Medium | Code review |
| Boxing (value types passed to `object` parameters) | 🟡 Medium | Code review |
| Per-frame temporary collection creation (`new List<T>()`) | 🔴 High | Code search |
| Debug.Log not disabled in release builds | 🟡 Medium | Search for `Debug.Log` |

### 3.3 UI Red Flags

| Red Flag | Severity | How to Check |
|----------|----------|-------------|
| Frequent Canvas rebuilds (modifying text/layout every frame) | 🔴 High | Profiler → UI module |
| Cascading Layout components (nested LayoutGroups) | 🟡 Medium | Hierarchy inspection |
| Frequent `SetActive` toggling of UI elements | 🟡 Medium | Code search |
| Large number of UI elements under a single Canvas | 🟡 Medium | Hierarchy inspection |
| Dynamic/static Canvas not separated | 🟡 Medium | Scene inspection |

### 3.4 Resource Red Flags

| Red Flag | Severity | How to Check |
|----------|----------|-------------|
| Synchronous loading of large assets blocking the main thread | 🔴 High | Search for `Resources.Load` |
| Duplicate loading of the same asset | 🟡 Medium | Profiler → Asset Loading |
| Unreleased resources causing continuous memory growth | 🔴 High | Memory Profiler |
| Large textures uncompressed or resolution too high | 🟡 Medium | Asset audit |
| Excessive initial load spike | 🟡 Medium | Profiler first-frame analysis |

---

## 4. Analysis Framework

### 4.1 CPU

Focus on:

- `Update` pipeline
- Physics, animation, navigation, script hotspots
- Scene object count and activation strategy

### 4.2 GC

Focus on:

- Per-frame allocations
- LINQ, string concatenation, boxing, temporary collections
- Allocations caused by UI and logging

### 4.3 UI

Focus on:

- Canvas rebuilds
- Cascading Layout components
- Frequent text updates
- Activation/hiding strategy

### 4.4 Resources

Focus on:

- Synchronous loading timing
- Duplicate loading
- Resource lifecycle
- Large asset initial load spikes

---

## 5. Output Format

```markdown
## Confirmed Red Flags
List performance issues supported by evidence, with severity ratings.

## Suspected Red Flags
List possible issues inferred from code review.

## High-Priority Optimization Items
1-3 things that should be done now, with specific change recommendations.

## Medium/Low-Priority Optimization Items
Improvements that can be addressed later.

## Expected Benefits
Expected benefit category for each optimization: frame time / GC / readability / extensibility.

## Verification Method
How to confirm the optimization is effective.
```

---

## 6. Quality Checklist

- [ ] Facts and inferences are distinguished
- [ ] Specific hotspots or high-risk paths are identified
- [ ] Red flag checklist has been reviewed item by item
- [ ] Recommendations are sorted by priority
- [ ] No vague generalizations
- [ ] Verification methods are provided

---

## 7. Meta-Rules

### Meta-Rule 1: Measure First, Then Recommend

Optimization conclusions without evidence have limited value.

### Meta-Rule 2: Optimization Is for Solving Hotspots, Not Pursuing Perfectionism

Do not deviate from real performance issues just for "more elegant code."

### Meta-Rule 3: Do Not Replace Simple Code with Unreadable "Optimized" Code

Unless the hot path is real, clarity takes priority.

---

## 8. Related Skills

- Script development guidelines → refer to `unity-runtime-dev`
- Design pattern selection → refer to `unity-patterns` (object pooling, etc.)
- Scene assembly → refer to `unity-scene-contracts` (object activation strategy)
