# Unity Runtime Development Guidelines

> **Purpose**: Guide LLM in writing, modifying, and reviewing runtime code in Unity projects.
>
> **Scope**: `Assets/**/Runtime/`, runtime scripts, game logic, system modules, business code.

---

## 1. Goal

This Skill constrains runtime code modifications, with key focus on ensuring:

- Correct logic
- Clear structure
- Explicit lifecycle
- Controllable performance cost
- No misuse of editor APIs

---

## 2. Hard Rules

### 2.1 Do Not Reference `UnityEditor`

- Runtime code must not directly depend on `UnityEditor`
- Editor functionality must be moved to the `Editor/` directory or editor assemblies

### 2.2 Respect Existing Architecture First

- Follow existing module divisions, naming conventions, event flows, and dependency boundaries
- Do not rewrite existing architecture just to apply a template

### 2.3 Lifecycle Must Be Explainable

- Responsibilities of `Awake`, `OnEnable`, `Start`, `Update`, `OnDisable`, `OnDestroy` must be clear
- Do not mix initialization, subscription, and cleanup logic together

### 2.4 Avoid Waste in High-Frequency Paths

Be vigilant by default about:

- Unnecessary queries in `Update`
- Repeated `GetComponent` calls
- Frequent allocation of temporary objects causing GC
- Using polling instead of events

### 2.5 Do Not Perform Unrequested Large Refactors

- Do not batch-introduce design patterns
- Do not split classes without reason
- Do not switch input, UI, state machine, or DI approaches without reason

---

## 3. Recommended Practices

### 3.1 Script Organization

- Each class should focus on one clear responsibility
- Public fields should be evaluated for whether they should become `[SerializeField] private`
- Break complex logic into private methods to avoid overly long methods

### 3.2 Reference Management

- Prefer serialized injection over runtime blind searches
- Prefer caching components over repeated lookups
- Cross-object dependencies should consider object lifecycle and null reference risks

### 3.3 State and Events

- High-frequency state switching should prefer explicit state management
- Event subscribe and unsubscribe must be paired
- Be cautious with static events to avoid leaks and hidden coupling

### 3.4 Testability

- Decouple pure logic from MonoBehaviour as much as possible
- Do not stuff all unit-testable logic into component lifecycle methods

### 3.5 Async and Update Strategies

When choosing execution modes, follow this priority:

| Requirement | Recommended Approach | Avoid |
|-------------|---------------------|-------|
| Per-frame detection | `Update` + condition guard | Unconditional empty Update |
| Delayed execution | `Invoke` / Coroutine | Manual timing in `Update` (for simple cases) |
| Async loading / networking | Coroutine or `async/await` (Unity 2023+) | Blocking the main thread |
| Complex async chains | UniTask (if already in the project) | Do not introduce UniTask for a single await |
| Timed repetition | `InvokeRepeating` / Coroutine | `timer += deltaTime` in Update for simple timing |

**Key rules**:

- Do not recommend UniTask by default unless the project already uses it or has clear complex async needs
- Coroutine `StopCoroutine` and `OnDisable` cleanup must be paired
- `async void` should only be used for event handler entry points; use `async Task` or `async UniTask` for other scenarios

---

## 4. Common Output Scenarios

Tasks suitable for this Skill:

- Implementing runtime logic for characters, UI, interaction, saves, quests, combat, etc.
- Fixing NRE, state corruption, initialization order issues
- Consolidating messy MonoBehaviour logic
- Local refactoring without breaking existing structure

Tasks not suitable for this Skill:

- Editor tool development → refer to `unity-editor-tooling`
- Package publishing structure design → refer to `unity-package-dev`
- System-level performance tuning reports → refer to `unity-performance-analysis`

---

## 5. Inspector Field Design

### 5.1 Serialization Guidelines

```csharp
// ✅ Recommended: Private field + SerializeField + Header grouping
[Header("Movement")]
[SerializeField] private float moveSpeed = 5f;
[SerializeField] private float jumpForce = 8f;

[Header("References")]
[SerializeField] private Transform groundCheck;
[SerializeField] private LayerMask groundLayer;

// ❌ Avoid: Exposed public fields
public float speed;
public GameObject target;
```

### 5.2 Field Design Rules

- Use `[Header]` to group fields for better Inspector readability
- Use `[Tooltip]` to add descriptions for non-obvious fields
- Use `[Range]` to constrain value ranges, preventing designers from entering invalid values
- Use `[RequireComponent]` to declare component dependencies
- Prefer enum fields over bool combinations (when 3+ bools control the same behavior)

### 5.3 Runtime Read-Only Fields

```csharp
// For debugging: Visible in Inspector but not editable
[SerializeField, HideInInspector] private int _debugOnlyField;

// Or use a custom ReadOnly attribute (if available in the project)
[ReadOnly] [SerializeField] private float currentSpeed;
```

---

## 6. Script Role Classification

When writing or reviewing scripts, first determine which role it belongs to:

| Role | Characteristics | Typical Base Class |
|------|----------------|-------------------|
| **Behavior Component** | Attached to GameObject, controls behavior | `MonoBehaviour` |
| **Data Container** | Stores configuration, contains no logic | `ScriptableObject` |
| **Pure Logic** | Does not depend on Unity lifecycle | Plain C# class |
| **Manager** | Coordinates multiple systems, usually unique | `MonoBehaviour` (use Singleton cautiously) |
| **Utility** | Collection of static methods | `static class` |

**Decision rules**:

- If it doesn't need `Update`/`Start`/`OnEnable`, consider whether it truly needs to be a `MonoBehaviour`
- If it only stores data with no logic, use `ScriptableObject` or plain C# class
- If the logic can run independently of a GameObject, use a plain C# class (easier to test)

---

## 7. Script Quality Review Checklist

When reviewing or generating scripts, check each item:

### Responsibility

- [ ] Does this script do only one thing?
- [ ] Is its role a behavior component, data container, pure logic, or manager?
- [ ] Does it truly need to be a MonoBehaviour?

### Coupling

- [ ] Are dependencies explicit (SerializeField / constructor injection / interface)?
- [ ] Are there hidden global state dependencies?
- [ ] Is the cross-object communication approach reasonable (direct reference / event / interface)?

### Lifecycle

- [ ] Are subscribe and unsubscribe paired?
- [ ] Are coroutines and async operations cleaned up in OnDisable/OnDestroy?
- [ ] Is callback safety considered after the object is destroyed?

### Performance

- [ ] Are there unnecessary allocations or lookups in high-frequency paths?
- [ ] Are repeatedly used references cached?

### Naming

- [ ] Do class names and field names express intent?
- [ ] Are cryptic abbreviations avoided?

### Inspector

- [ ] Are serialized fields grouped with descriptions?
- [ ] Do numeric fields have reasonable range constraints?

---

## 8. Quality Checklist (Pre-Submission)

- [ ] No `UnityEditor` references introduced
- [ ] Lifecycle responsibilities are clear
- [ ] No obvious waste in high-frequency paths
- [ ] Changes are compatible with existing structure
- [ ] Null references, subscriptions, and destruction paths have been considered
- [ ] Inspector field design is reasonable
- [ ] Basic verification method is provided

---

## 9. Meta-Rules

### Meta-Rule 1: Stability First, Then Elegance

Ensure correct behavior first, then consider structural aesthetics.

### Meta-Rule 2: If It Can Be Solved Locally, Do Not Rewrite the System

Prefer the minimum effective change.

### Meta-Rule 3: Runtime Code Is First and Foremost Behavioral Code

Do not sacrifice runtime semantics for "good-looking form."

### Meta-Rule 4: Do Not "Optimize" Away Readability

Unless the Profiler proves it's a hotspot, clarity takes priority over "efficiency."

---

## 10. Related Skills

- Design pattern selection → refer to `unity-patterns`
- Game architecture blueprints → refer to `unity-blueprints`
- Scene assembly contracts → refer to `unity-scene-contracts`
- Performance analysis → refer to `unity-performance-analysis`
