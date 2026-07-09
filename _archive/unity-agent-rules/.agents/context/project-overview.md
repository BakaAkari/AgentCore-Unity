# Project Context Declaration

> **Purpose**: Declare the current project's tech stack, versions, platforms, and key dependencies, serving as conditional judgment basis for AI assistants when executing Skills.
>
> **Maintenance Rule**: When the project tech stack changes (e.g., upgrading Unity version, switching render pipeline, introducing new dependencies), this file must be updated accordingly.

---

## Basic Information

| Item | Value |
|------|-------|
| Project Name | <!-- Fill in project name --> |
| Unity Version | <!-- e.g.: 2022.3.50f1 LTS --> |
| Render Pipeline | <!-- Built-in / URP / HDRP --> |
| Target Platform | <!-- e.g.: Windows, Android, iOS --> |
| Input System | <!-- Legacy Input Manager / New Input System / Both coexisting --> |
| UI Framework | <!-- uGUI / UI Toolkit / Both coexisting --> |
| Scripting Backend | <!-- Mono / IL2CPP --> |
| .NET Version | <!-- .NET Standard 2.1 / .NET Framework --> |

---

## Key Dependencies

> List important third-party packages and Unity official packages already introduced in the project.
> Conditional judgments in Skills (e.g., "if the project has introduced UniTask") depend on this list.

| Package Name | Version | Purpose | Notes |
|------|------|------|------|
| <!-- com.unity.inputsystem --> | <!-- 1.7.0 --> | <!-- Input handling --> | <!-- --> |
| <!-- com.unity.textmeshpro --> | <!-- 3.0.6 --> | <!-- Text rendering --> | <!-- --> |
| <!-- UniTask --> | <!-- 2.x --> | <!-- Async handling --> | <!-- Delete this row if not introduced --> |
| <!-- DOTween --> | <!-- 1.x --> | <!-- Animation tweening --> | <!-- Delete this row if not introduced --> |

---

## Project Structure Conventions

> Describe the current project's directory organization to help AI understand where code is placed.

```text
Assets/
├── Scripts/           # Runtime scripts
│   ├── Core/          # Core systems
│   ├── Gameplay/      # Game logic
│   └── UI/            # UI logic
├── Editor/            # Editor tools
├── Prefabs/           # Prefabs
├── Scenes/            # Scenes
├── ScriptableObjects/ # Data assets
├── Art/               # Art resources
└── Plugins/           # Third-party plugins
```

> If the project structure differs from the above, modify it to reflect the actual structure.

---

## Coding Conventions

| Convention | Value |
|------|-------|
| Naming Style | <!-- PascalCase class names / camelCase private fields / _camelCase private fields --> |
| Field Serialization | <!-- [SerializeField] private preferred / public fields --> |
| Event System | <!-- C# event / UnityEvent / ScriptableObject Event Channel / Third-party --> |
| State Management | <!-- Enum switch / State pattern / Third-party state machine --> |
| Dependency Injection | <!-- None / VContainer / Zenject / Manual Service Locator --> |
| Test Framework | <!-- Unity Test Framework / No automated testing --> |

---

## Known Constraints

> List known limitations, technical debt, or special situations that need attention in the current project.

- <!-- e.g.: Currently no automated testing, all verification is manual -->
- <!-- e.g.: UI uses legacy uGUI, not migrating to UI Toolkit for now -->
- <!-- e.g.: Some code uses Resources.Load, planned migration to Addressables -->

---

## Filling Guide

1. Replace the `<!-- -->` comments above with actual values
2. Rows that don't apply can be deleted
3. The key dependencies table should only list actually introduced packages, not "planned to introduce"
4. Fill in the project structure according to actual directories, no need to list all subdirectories
5. This file should be continuously updated as the project evolves
