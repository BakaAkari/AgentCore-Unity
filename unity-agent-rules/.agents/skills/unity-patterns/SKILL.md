# Unity Design Pattern Selector

> **Purpose**: Help AI select appropriate design patterns in Unity projects, avoiding over-engineering or pattern abuse.
>
> **Scope**: Game logic architecture, inter-system communication, state management, object lifecycle, data organization.
>
> **Trigger Scenarios**: User asks "what pattern should I use", "how to decouple", "events or direct references", "should I use a singleton", etc.

---

## 1. Core Principles

1. Recommend at most 1-3 patterns each time, and explain why a simpler approach is insufficient
2. Patterns are means, not goals — identify the problem first, then choose the tool
3. Prefer solutions natively supported by the Unity ecosystem

---

## 2. Pattern Quick Reference

### 2.1 ScriptableObject

**Suitable for**:

- Designer-editable configuration data (weapon stats, skill tables, level parameters)
- Shared static data assets
- Event channels (ScriptableObject Event Channel)
- Reusable data containers

**Not suitable for**:

- Default storage for runtime-mutable game state (health, score, inventory)
- Dynamic data that needs to be serialized to save files

**Decision criteria**: Is the data "determined at design time" or "dynamically changing at runtime"? Use SO for the former; use plain C# classes or dedicated state containers for the latter.

### 2.2 C# Events / Delegates

**Suitable for**:

- One-to-many notifications with a clear owner and unsubscribe timing
- Loosely coupled communication between components (e.g., health change → UI update)

**Not suitable for**:

- Imperative flows requiring return values or execution order guarantees
- Deep event chains that are difficult to debug

**Key rule**: Subscribe and unsubscribe must always be paired, typically in `OnEnable`/`OnDisable`.

### 2.3 Global Event Bus / Observer Hub

**Suitable for**:

- Multiple unrelated systems that genuinely need broadcast-style decoupled notifications
- Cross-scene global events (e.g., achievement system listening to all kill events)

**Not suitable for**:

- As the default answer for "decoupling" — it often hides ownership relationships and makes debugging harder
- Simple communication between two components

**Warning**: If there are more than 10 event types on the event bus, consider whether the architecture itself needs to be re-examined.

### 2.4 Interface

**Suitable for**:

- Multiple implementations needed (e.g., `IDamageable` implemented by player, enemy, destructible objects)
- Clear dependency boundaries and substitutability needed
- Mock testing needed

**Not suitable for**:

- Each interface has only one implementation with no testing requirements
- Adding interfaces to every class just for "formal correctness"

### 2.5 State Machine

**Suitable for**:

- Entities with mutually exclusive states and explicit transitions, such as characters, AI, UI panels
- 3-15 states with clear transition rules

**Not suitable for**:

- Only 2 states (a bool or enum will suffice)
- Extremely many states with unclear transition rules (consider behavior trees)

**Implementation choices**:

| Complexity | Recommended Approach |
|------------|---------------------|
| 2-3 states | Enum + switch |
| 4-8 states | State pattern (one class per state) |
| 8+ states + complex transitions | Finite state machine framework or behavior tree |

### 2.6 Object Pool

**Suitable for**:

- High-frequency spawn/destroy objects (bullets, VFX, enemies, UI list items)
- Profiler shows `Instantiate`/`Destroy` as hotspots

**Not suitable for**:

- Objects with simple lifecycles and low quantity
- Pre-optimizing without performance evidence

**Unity native solution**: Unity 2021+ provides `UnityEngine.Pool.ObjectPool<T>`, prefer using it.

### 2.7 Service Layer / Manager

**Suitable for**:

- A small number of cross-scene systems (audio, save, network, localization)
- Explicit initialization and interface isolation needed

**Not suitable for**:

- Turning everything into an `XxxManager` singleton
- Implicit global state scattered everywhere

**Recommended approaches**:

1. Small projects: `DontDestroyOnLoad` + explicit references
2. Medium projects: Simple Service Locator + interfaces
3. Large projects: DI framework (VContainer / Zenject), but only when the team is familiar with it

### 2.8 Generics / Custom Attributes

**Suitable for**:

- Eliminating repetitive boilerplate code with clear type-safety benefits
- Editor metadata annotations (e.g., `[ReadOnly]`, `[RequireInterface]`)

**Not suitable for**:

- Making game logic code harder to read than straightforward duplication

---

## 3. Decision Process

When facing "what pattern should I use", think in the following order:

```
1. What is the problem? (Not "what pattern to use", but "what specific problem needs solving")
2. What is the simplest solution? (Direct reference? Enum? Bool?)
3. What are the pain points of the simple solution? (Coupling? Extensibility? Readability?)
4. Which pattern precisely addresses this pain point?
5. What is the cost of introducing this pattern? (Complexity, learning curve, debugging difficulty)
```

---

## 4. Output Format

```markdown
## Recommended Pattern
## Why It Fits the Current Scenario
## Why Not Use a Simpler Approach
## Minimum Implementation Boundary
## Known Trade-offs
```

---

## 5. Guardrail Rules

- Do not recommend more than 3 patterns at once
- Do not recommend heavy architecture during the prototyping phase
- Do not use "textbook correctness" as a recommendation reason — must consider project scale and team context
- Do not default to recommending a global event bus, forcing UniTask, or DI frameworks unless the project context clearly requires it

---

## 6. Related Skills

- Architecture layering decisions → refer to `unity-blueprints`
- Scene assembly and reference wiring → refer to `unity-scene-contracts`
- Script quality review → refer to `unity-runtime-dev` Section 7
- Performance hotspot identification → refer to `unity-performance-analysis`
