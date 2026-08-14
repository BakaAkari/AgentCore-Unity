---
name: object-creation
description: "Create, place, and configure GameObjects, components, prefabs, and terrain in a Unity scene. How to build out a scene's object graph: objects, transforms, components, prefab instantiate/apply/revert, terrain editing. Triggers: 创建物体, create object, spawn, GameObject, component, prefab, 预制体, terrain, 地形, transform, parent, hierarchy, 创建/添加/删除物体, duplicate, instantiate. Builtin AgentCore skill — uses AgentCore's native manage_gameobject/manage_component/manage_prefab/manage_terrain tools."
category: unity-operations
version: 1.0.0
---

# Object Creation & Scene Building

Create, place, and configure GameObjects, components, prefabs, and terrain. All calls use AgentCore's **native** `manage_*` tools (NOT any external `unity_skills`/REST service).

> For **looking around / camera control** use the `scene-navigation` skill (`manage_camera` navigate actions). This skill is about *adding/configuring objects* within the scene.

## 1. GameObjects — the core object tool

Use **`manage_gameobject`** for object lifecycle:

| Action | Task |
|--------|------|
| `create` / `create_batch` | Create one or more empty GameObjects (with name/position/rotation/scale/parent) |
| `delete` / `delete_batch` | Delete object(s) |
| `duplicate` | Duplicate an object |
| `get_info` | Inspect transform, components, active state, path |
| `modify` / `modify_batch` | Change position/rotation/scale/tag/name/parent |
| `set_transform` | Set transform position/rotation/scale directly |
| `set_parent` | Reparent under a new parent |
| `set_active_batch` | Enable/disable multiple objects |

**Typical object-creation flow**:
1. `manage_gameobject(get_info, name="...")` — if unsure whether it exists / to find its path
2. `manage_gameobject(create, name="Enemy_01", parent="SpawnPoints", position={x,y,z})` — create
3. `manage_gameobject(modify, name="Enemy_01", scale={2,2,2})` — place/scale
4. `manage_component(add, name="Enemy_01", type="Rigidbody")` — add behavior
5. `manage_gameobject(set_parent, name="Enemy_01", parent="Enemies")` — organize

**Name resolution**: `name` accepts either an exact name or a hierarchy path like `Player/Weapon_Holder`. When in doubt, run `find_gameobjects` (meta tool) or `manage_gameobject(get_info)` first.

## 2. Components

Use **`manage_component`**:

| Action | Task |
|--------|------|
| `add` / `add_batch` | Add component(s) to object(s) (by type name, e.g. `Rigidbody`, `BoxCollider`, `MeshFilter`, `MeshRenderer`) |
| `remove` / `remove_batch` | Remove component(s) |
| `get` / `get_components_batch` | Read component data |
| `list` | List components on an object |
| `modify` / `set_property_batch` | Change component property values |
| `set_enabled` | Toggle component enabled/disabled |
| `copy_component` | Copy component from one object to another |

**Property editing**: to set specific properties you often need both the component's type and property name. Use `get`/`list` to inspect available properties first, then `modify` or `set_property_batch` to change them.

## 3. Prefabs

Use **`manage_prefab`**:

| Action | Task |
|--------|------|
| `create` | Create a prefab asset from an object |
| `instantiate` | Spawn an instance of a prefab into the scene |
| `apply` | Apply instance changes back to the prefab asset |
| `revert` | Revert instance to prefab defaults (discard overrides) |
| `unpack` | Unpack instance into plain GameObjects (breaks prefab link) |
| `get_info` | Inspect a prefab/instance |
| `completely` | (with apply) apply all overrides completely |

> Prefab `instantiate` is how you'd spawn prefab instances (like enemy spawns) rather than building objects from scratch.

## 4. Terrain

Use **`manage_terrain`**:

| Action | Task |
|--------|------|
| `create` | Create a new Terrain |
| `get_info` / `get_height` | Inspect terrain / read height at a point |
| `set_height` / `flatten` | Set terrain height / flatten |
| `paint_texture` | Paint a texture layer |
| `add_tree` | Place trees |
| `add_layer` | Add terrain layers |
| `generate_perlin` | Generate procedural perlin heightmap |
| `smooth` | Smooth terrain |

## Guardrails / pitfalls
- **NOT `gameobject_set_transform`-style flat commands.** The tools are `manage_gameobject` / `manage_component` / `manage_prefab` / `manage_terrain`, dispatched by an `action` param. There are NO standalone tools named e.g. `gameobject_create`/`component_add` — those are external Unity-Skills names and DO NOT exist here.
- `manage_component(add)` takes a **component type name string**, not a Unity type object — pass `"Rigidbody"`, `"BoxCollider"`, `"UnityEngine.Transform"`, etc.
- Deleting is irreversible (`manage_gameobject(delete)` / `manage_gameobject(delete_batch)`). Confirm the target name/path first with `get_info`; the user may want an undo (AgentCore records undo via `RecordUndo` where supported).
- Setting a property via `manage_component(modify/set_property_batch)` requires knowing the property name; inspect via `get`/`list` first if unsure.
- Prefab ops change prefab assets — `apply` writes back to the .prefab asset (persists), `revert` discards scene overrides. Be deliberate.
