---
name: scene-navigation
description: "Scene & camera navigation in the Unity Editor. How to look around a 3D scene (orbit/pan/dolly/frame the SceneView camera) and operate scenes (open/save/create/query). Triggers: 视角, 环绕, orbit, pan, zoom, frame, look around, scene view, scene, 摄像机, 场景, rotate view, dolly. Builtin AgentCore skill — tool names match AgentCore's native manage_* tools."
category: unity-operations
version: 1.0.0
---

# Scene & Camera Navigation

Navigate the Unity Editor's **Scene view camera** (the viewport you inspect a 3D scene from) and manage scene assets. All tool calls use AgentCore's **native** `manage_camera` and `manage_scene` tools — NOT any external `unity_skills`/REST service.

## Two different cameras (do not confuse)

| What | Tool | Meaning |
|------|------|---------|
| **Scene View camera** (the editor viewport you look through) | `manage_camera` actions `get_scene_view` / `set_scene_view` / `orbit_scene_view` / `pan_scene_view` / `dolly_scene_view` / `frame_selected` | The camera used to **inspect** the scene in the Editor. NOT rendered in builds. |
| **Game Camera** (a real Camera component in the scene) | `manage_camera` actions `create` / `configure` / `look_at` / `render_to_texture` / `list_cameras` | Actual cameras that render the game. May live in the scene/MainCamera. |

> Use Scene View camera actions to **look around**. Use Game Camera actions to **set up rendering** cameras.

## 1. Look around the scene (visual inspection)

This is the correct workflow when you need to inspect a 3D scene from different angles. All actions operate on `SceneView.lastActiveSceneView` (requires an open Scene view window).

### Recommended loop
1. **`get_scene_view`** → read current `pivot` (look-at target), `size` (zoom/distance), `rotation`/`rotation_euler` (orientation), `orthographic`.
2. **`frame_selected`** (or `set_scene_view` with `pivot=<target>`) → establish a stable look-at target. Frame needs a selection; if nothing selected, set `pivot` explicitly.
3. **`orbit_scene_view`** with small deltas → circle around the target:

| Param | Meaning | Convention |
|-------|---------|-----------|
| `azimuth_delta` | horizontal degrees (yaw) | `+` = clockwise from above (right-drag). e.g. `90` turns a quarter turn; `±30–45` is a gentle nudge |
| `elevation_delta` | vertical degrees (pitch) | `+` = look up. Clamped — a value that would flip over the pole returns an error; use smaller steps (`±5–10`) |

4. **`dolly_scene_view`** with `factor` → adjust the Scene View **zoom** (scales `size`, the view's diagonal measure). This is **the same in orthographic and perspective** — it changes how much of the scene fits in the viewport, NOT the camera's literal distance to the pivot:

| `factor` | Effect on `size` | What you see |
|----------|------------------|--------------|
| `0 < factor < 1` (e.g. `0.5`) | shrinks the view | objects appear **LARGER** — **zoom IN** for detail |
| `> 1` (e.g. `2`) | grows the view | objects appear **SMALLER** — **zoom OUT** for overview |

> **Mental model**: `factor` scales the visible area. `factor=0.5` cuts the view in half (magnifies objects), `factor=2` doubles the view (shrinks objects). To zoom IN pass `0.5`; to zoom OUT pass `2`.

5. **`pan_scene_view`** with `dx` / `dy` (world units) → slide sideways **without turning** (`dx` along camera's own right axis, `dy` along its up axis). Use when the target is off-center.

6. **OBSERVE LOOP (observe, then re-navigate if needed)** — don't stop after one framing. After navigating, call `vision_analyze source=scene action=analyze` (prompt = the user's visual question) to SEE the current view. `vision_analyze source=scene` observes **the same `SceneView.lastActiveSceneView`** these navigation actions drive, so they compose into a closed loop: navigate → observe → if the description shows the target is still unclear / out of frame / occluded / too small, re-navigate (steps 3–5) → observe again → repeat until you can answer, then stop (after ~3–4 repositions without convergence, report what you've seen and ask). **The vision model only describes the fixed frame you hand it — it does NOT propose camera moves; you decide the next viewpoint.** Requires both `manage_camera` (activate `Specialized`) and `vision_analyze` (activate `Meta`).

> **Prefer relative increments (orbit/pan/dolly) over computing absolute `rotation`/`pivot`** — relative ops keep the look-at target stable and are far easier to reason about.

### Worked example — "inspect the player from the side"
```
1) get_scene_view                          → read current view
2) set_scene_view(pivot={x,0,z})           → or frame_selected on the player
3) orbit_scene_view(azimuth_delta=90)
4) get_scene_view                          → confirm rotation_euler.y changed ~90, pivot unchanged
5) dolly_scene_view(factor=0.5)            → zoom in for detail
```

## 2. Frame / focus a specific object
- **`frame_selected`** → instantly centers the Scene view on the currently selected object's bounds (needs a selection; a non-renderer object like an empty GO/camera/light falls back to a 1-unit box around its position).
- For a fixed world point with no selection, use `set_scene_view(pivot=<world pos>)`.

## 3. Scene assets (open / save / create / query)

Use `manage_scene` (not `manage_camera`):

`manage_scene` actions: `list`, `get_active`, `list_open_scenes`, `open`/`open_scene`, `save`, `save_scene_as`, `create`/`new_scene`, `set_active`/`set_active_scene`, `get_hierarchy`, `merge_scenes`, `get_build_scenes`, `add_to_build`.

| Task | Call |
|------|------|
| List scenes in project | `manage_scene(list)` |
| Open a scene | `manage_scene(open, name="<scene>")` |
| Save current scene | `manage_scene(save)` |
| Create a new empty scene | `manage_scene(new_scene)` |
| Get hierarchy of open scene | `manage_scene(get_hierarchy)` |

> `manage_scene` works on scene ASSETS / the open scene. For per-object operations inside a scene, see the `object-creation` skill (`manage_gameobject`/`manage_component`).

## Guardrails / pitfalls
- **Scene View camera is NOT a Game Camera.** Don't call `render_to_texture` on the Scene View camera to "capture what the user sees" unless you specifically render `SceneView.lastActiveSceneView.camera` — for real build cameras use a Game Camera's `render_to_texture` or `vision_analyze`.
- `orbit_scene_view` requires an **open Scene view window** (`SceneView.lastActiveSceneView` non-null). If you get "No active SceneView", tell the user to focus/close-and-reopen the Scene view.
- `elevation_delta` too large returns an error (pole flip guard) — that's expected, not a bug; step smaller.
- These are editor-time operations; they do not persist to the built game.
