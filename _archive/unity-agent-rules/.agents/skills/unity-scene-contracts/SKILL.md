# Unity Scene Assembly Contracts

> **Purpose**: Define required objects in scenes, component dependencies, bootstrap sequences, and reference wiring rules to avoid runtime blind searches and implicit dependencies.
>
> **Scope**: Scene setup, prefab design, bootstrap/initialization flow, Inspector wiring guidelines.
>
> **Trigger Scenarios**: User asks "what does the scene need", "how to wire references", "how to define initialization order", "why NullReference", etc.

---

## 1. Core Principles

1. Scene assembly should be explicit, not implicit — if it can be wired in the Inspector, do not `Find` at runtime
2. Bootstrap objects should be small and focused, not become omnipotent managers
3. Runtime-generated objects and scene-preset objects must be clearly distinguished

---

## 2. Scene Contract Definition

Define the following for each scene:

### 2.1 Required Root Objects

List the top-level GameObjects that must exist in the scene and their responsibilities:

```
[Required] GameManager     — Game flow control, state management
[Required] UICanvas        — Root of all UI panels
[Required] MainCamera      — Main camera
[Required] EventSystem     — UI event system
[Optional] AudioManager    — Audio management (use DontDestroyOnLoad for cross-scene)
```

### 2.2 Component Dependencies

Required components on each key object:

```
GameManager:
  - GameFlowController (required)
  - SceneTransition (required)

Player:
  - PlayerController (required)
  - Rigidbody / Rigidbody2D (required)
  - Collider / Collider2D (required)
  - Animator (optional, required when animations exist)
```

### 2.3 Inspector Wiring Rules

```
 Recommended:
  - [SerializeField] private fields + Inspector drag-and-drop assignment
  - Components on the same GameObject guaranteed via [RequireComponent]
  - Child object references via [SerializeField] instead of GetComponentInChildren

 Use with caution:
  - Cross-Prefab references (consider using ScriptableObject event channels instead)
  - Scene-level singleton references (consider using interface + explicit registration)

 Avoid:
  - Runtime GameObject.Find / FindObjectOfType as regular reference methods
  - String path lookups ("Canvas/Panel/Button")
  - Implicit dependency on global static variables
```

---

## 3. Bootstrap Sequence Design

### 3.1 Initialization Order

Unity does not guarantee `Awake`/`Start` execution order for scripts with the same priority. Recommended approaches:

| Approach | Use Case | Implementation |
|----------|----------|---------------|
| Script Execution Order | A few critical scripts | Project Settings → Script Execution Order |
| Explicit Bootstrap script | Medium projects | A single `Bootstrap` script that initializes systems in order |
| Layered scene loading | Large projects | `Bootstrap` scene → Additive load game scenes |

### 3.2 Bootstrap Pattern

```csharp
// Recommended: Explicit bootstrap, not relying on Awake order
public class Bootstrap : MonoBehaviour
{
    [SerializeField] private GameConfig config;
    [SerializeField] private AudioManager audio;
    [SerializeField] private UIManager ui;

    private void Awake()
    {
        // Initialize in explicit order
        config.Initialize();
        audio.Initialize();
        ui.Initialize(config);
    }
}
```

### 3.3 Cross-Scene Persistent Objects

```
Rules:
1. Only objects that truly need to persist across scenes should use DontDestroyOnLoad
2. Typically only 2-4: AudioManager, SaveManager, SceneLoader, (optional) NetworkManager
3. Create them in the Bootstrap scene, do not place a copy in every scene
4. Provide null checks or fallbacks to avoid crashes when testing a single scene independently
```

---

## 4. Prefab Wiring Guidelines

### 4.1 Self-Contained Principle

Prefabs should be as self-contained as possible, minimizing hard references to external scene objects:

```
 Good prefab:
  - Internal references wired via [SerializeField]
  - External communication through events or interfaces
  - Can be dropped into any scene and work directly

 Bad prefab:
  - Depends on specifically named objects in the scene
  - Finds external objects in Awake
  - Silently fails when components are missing
```

### 4.2 Prefab Variant Rules

```
- Base prefab defines common structure and default values
- Variants only override properties that need to change
- Do not add/remove components in variants (causes override confusion)
- Prefer nested prefabs over deep variant chains
```

---

## 5. Validation Rules

After scene assembly is complete, you should be able to answer the following questions:

- [ ] Do all required objects exist?
- [ ] Are all `[SerializeField]` fields assigned? (No yellow warnings in Inspector)
- [ ] Is the initialization order explicit? (Not relying on implicit Awake order)
- [ ] Are cross-scene references passed through safe methods? (Not Find)
- [ ] Can this scene be opened and run independently? (Or has a clear Bootstrap entry point)
- [ ] Do runtime-generated objects have explicit lifecycle management?

---

## 6. Output Format

When providing an assembly contract for a scene, output in the following structure:

```markdown
## Scene Object Contract
List required/optional root objects and their components.

## Bootstrap Sequence
Describe the initialization order and Bootstrap logic.

## Inspector Wiring Checklist
List key serialized field wiring relationships.

## Validation Rules
List verifiable validation conditions.

## Implicit Dependency Risks
Point out potential hidden dependencies.
```

---

## 7. Guardrail Rules

- Prefer explicit scene wiring, minimize runtime `Find` chains
- Keep Bootstrap objects small and focused
- Do not stuff all initialization logic into a single God Manager
- Limit cross-scene persistent objects to 2-4
- Design prefabs with self-containment as the goal

---

## 8. Related Skills

- Game architecture blueprints → refer to `unity-blueprints`
- Design pattern selection → refer to `unity-patterns`
- Script development guidelines → refer to `unity-runtime-dev`
