---
name: scene-authoring
description: "Author a scene: manage scene assets, lighting, cameras (incl. Cinemachine), UI (UGUI + UI Toolkit), Timeline and Animation. How to set up and compose the visual/lighting/camera/UI layer of a Unity scene. Triggers: scene, 场景, lighting, light, 灯光, camera, 摄像机, Cinemachine, UI, UGUI, UI Toolkit, UXML, USS, timeline, Timeline, animation, 动画, animator, post-processing. Builtin AgentCore skill — uses AgentCore's native manage_scene/manage_lighting/manage_camera/manage_cinemachine/manage_ui/manage_ui_toolkit/manage_timeline/manage_animation tools."
category: unity-operations
version: 1.0.0
---

# Scene Authoring (scenes, lighting, cameras, UI, timeline, animation)

Compose the visual layer of a Unity scene. All calls use AgentCore's **native** `manage_*` tools.

## 1. Scene assets — `manage_scene`

| Action | Task |
|--------|------|
| `list` / `list_open_scenes` | List scene assets / open scenes |
| `get_active` / `set_active` (`set_active_scene`) | Get / set the active scene |
| `open` / `open_scene` | Open a saved scene |
| `save` / `save_scene_as` | Save current scene / save as new file |
| `create` / `new_scene` | Create a new (empty) scene |
| `get_hierarchy` | Get the object hierarchy of the open scene |
| `merge_scenes` | Merge scenes |
| `get_build_scenes` / `add_to_build` | Inspect / edit the Build Settings scene list |

> Scene ops affect scene ASSETS / the open scene. For per-object work inside a scene use `object-creation` skill.

## 2. Lighting — `manage_lighting`

| Action | Task |
|--------|------|
| `create` | Create a light (directional/point/spot/area via type) |
| `list` / `get_info` | List / inspect lights |
| `modify` | Change light props (intensity, color, range, shadows, type) |
| `get_lightmap_settings` | Read global lightmapping settings |
| `bake` | Bake lighting |
| `clear` | Clear baked lightmaps |
| (`directional`/`point`/`spot`/`area` are type sub-values, not standalone tools) | |

Typical: `manage_lighting(create, type="directional", name="Sun", intensity=1.2, color=...)` → `modify` to tune → `bake` when ready.

## 3. Cameras — `manage_camera`

- **Game Camera** (real render camera): `create`, `get_info`, `configure` (FOV/clip/clear/culling), `look_at`, `set_main_camera`, `render_to_texture`, `create_render_texture`, `list_cameras`, `align_to_view`
- **Scene View navigation** (editor viewport): `get_scene_view`, `set_scene_view`, `orbit_scene_view`, `pan_scene_view`, `dolly_scene_view`, `frame_selected` — see the `scene-navigation` skill for how to look around.

## 4. Cinemachine — `manage_cinemachine`

For virtual camera systems (do NOT use plain `manage_camera` for these):

| Action | Task |
|--------|------|
| `create_virtual_camera` | Create a Virtual Camera |
| `create_freelook` / `create_clearshot` / `create_state_driven` / `create_sequencer` / `create_dolly_track` | Create specific Cinemachine camera types |
| `set_target` | Set follow/look-at target |
| `configure_lens` | Configure FOV/ortho lens |
| `configure_body` / `configure_aim` | Set body & aim behaviour |
| `configure_freelook_orbits` | FreeLook rig orbits |
| `set_priority` | Camera priority (which is active) |
| `add_state_camera` / `add_sequencer_entry` | Build state-driven / sequencer graphs |
| `setup_brain` | Set up Cinemachine Brain |
| `configure_impulse` / `set_noise` | Effects |
| `list` / `get_info` / `set_blend_list` | Inspect / blends |

> Cinemachine virtual cameras are a different system from plain `manage_camera` Game Cameras — route Cinemachine work here, not to `manage_camera`.

## 5. UI — `manage_ui` (UGUI) and `manage_ui_toolkit` (UI Toolkit)

**`manage_ui`** (legacy UGUI Canvas system):
- Canvas: `create_canvas`, `configure_canvas` (render mode: overlay / camera / world), `world`, `screen_space_overlay`, `screen_space_camera`, `camera`
- Elements: `create_element`, `list`, `find_element`, `get_info`, `modify_element`, `delete_element`, `duplicate_element`, `reorder_element`
- Standard controls: `button`, `text`, `image`, `input_field`, `slider`, `toggle`, `dropdown`, `scroll_view`, `panel`, `raw_image`
- Layout: `add_layout_group`, `set_layout`, `add_ui_component`, anchors (`top`, `bottom`, `left`, `right`, `center`, `stretch`, `*_left`/`*_right`/`*_top`/`*_bottom` combos)
- Alignment/distribution: `align_elements`, `distribute_elements`
- Props: `set_text`, `set_image`, `set_interactable`, `grid`/`horizontal`/`vertical` (layout types)

**`manage_ui_toolkit`** (new UI Toolkit system):
- Authoring: `create_uxml`, `create_uss`, `get_uxml_content`, `get_uss_content`, `validate_uxml`
- Structure: `add_element`, `add_class`, `remove_class`, `remove_element`, `query_element`, `list_elements`, `set_attribute`, `set_style`
- Docs/windows: `create_ui_document`, `configure_ui_document`, `create_editor_window_template`, `create_custom_element_template`, `create_panel_settings`, `list_ui_documents`, `add_binding`, `list_assets`

> UGUI (`manage_ui`) and UI Toolkit (`manage_ui_toolkit`) are two different systems. Identify which the scene uses before operating.

## 6. Timeline — `manage_timeline`

| Action | Task |
|--------|------|
| `create` | Create a Timeline asset |
| `add_track` / `remove_track` | Manage tracks |
| `add_clip` | Add a clip |
| `set_binding` | Bind track to object |
| `set_duration` | Timeline length |
| `play` / `pause` / `stop` / `loop` | Playback control |
| `activation`/`animation`/`audio`/`control`/`signal` | Track types |
| `hold` / `none` | Clip post-extrapolation |
| `get_info` / `list_tracks` | Inspect |

## 7. Animation — `manage_animation`

| Action | Task |
|--------|------|
| `create_animation_clip` | Create an Animation Clip |
| `get_clip_info` / `get_controller_info` / `list_clips` / `list_parameters` / `list_animator_states` / `get_layers` | Inspect |
| `set_parameter` / `set_layer_weight` | Drive animator |
| `loop` / `once` / `ping_pong` / `clamp_forever` | Clip wrap modes |

> Use `manage_animation` for Animation Clips / Animator Controller parameters; use `manage_timeline` for Timeline sequences.

## Guardrails / pitfalls
- **Cinematic cameras (Cinemachine) ≠ plain Game Cameras** — route `manage_cinemachine`, not `manage_camera`.
- **UGUI ≠ UI Toolkit** — identify the system first; the two are not interchangeable.
- Lighting `bake` is expensive and persists lightmap data; confirm scope before running.
- Scene `save`/`save_scene_as` persist to disk — deliberate. `new_scene` discards/replaces current context only when you intend it.
- These are all native `manage_*` tools. There are NO flat standalone tools like `scene_load`/`light_create`/`ui_button` — dispatch via the `action` param.
