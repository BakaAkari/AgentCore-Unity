---
name: asset-pipeline
description: "Manage the asset pipeline: search/move/copy/import assets, configure texture/model importers, shaders, and packages. How to manage a Unity project's Assets folder and import settings. Triggers: asset, 资源, import, 导入, texture, 纹理, model, 模型, mesh, shader, 着色器, package, 包, "package manager", material, animation clip asset, sprite, atlas. Builtin AgentCore skill — uses AgentCore's native manage_asset/manage_asset_import/manage_texture_import/manage_model_import/manage_shader/manage_package tools."
category: unity-operations
version: 1.0.0
---

# Asset Pipeline (assets, importers, shaders, packages)

Manage the project's Assets and import configuration. All calls use AgentCore's **native** `manage_*` tools.

## 1. Asset files — `manage_asset`

| Action | Task |
|--------|------|
| `search` | Search assets by name/type/filter |
| `get_info` | Inspect an asset (path, type, GUID, importer) |
| `import` | Import an asset |
| `create_folder` | Create an Assets folder |
| `copy` / `move` / `delete` | File ops on assets |
| `get_dependencies` | List what an asset depends on |
| `find_references` | Find assets referencing a given asset |

> Asset paths are under `Assets/...`. Use forward slashes.

## 2. Import settings — `manage_asset_import`

| Action | Task |
|--------|------|
| `get_importer` / `find_by_importer` | Get/importer / find assets by importer type |
| `get_labels` / `set_labels` | Asset labels |
| `get_dependencies` | Dependencies |
| `get_import_log` | Import log |
| `reimport` / `reimport_batch` | Re-import asset(s) |
| `set_bundle` | Assign AssetBundle name |

## 3. Textures — `manage_texture_import`

Texture-specific import settings:

| Action | Task |
|--------|------|
| `get_settings` / `get_info` / `get_platform_settings` | Read import settings |
| `set_settings` / `set_settings_batch` / `set_platform_settings` | Write import settings |
| `set_type` | Texture type (default / sprite / normal / etc.) |
| `set_sprite_settings` | Sprite mode/packing |
| `find_assets` / `find_by_size` | Find textures |
| Size/compression values: `high`/`medium`/`low`/`compressed`/`compressedhq`/`compressedlq`/`uncompressed`/`normal` | Texture quality markers |

**Typical flow** to make a texture a UI sprite or tune quality:
1. `manage_texture_import(get_settings, name="...")` — read current
2. `manage_texture_import(set_type, name="...", type="sprite")` — change type
3. `manage_texture_import(set_settings, name="...", compressed=true, maxSize=1024)` — tune
4. AssetDatabase refresh happens automatically on import.

## 4. Models — `manage_model_import`

| Action | Task |
|--------|------|
| `get_settings` / `get_mesh_info` / `get_rig_info` / `get_materials_info` / `get_animations_info` | Read model import info |
| `set_settings` / `set_settings_batch` | Write import settings |
| `set_rig` | Set rig type (humanoid/generic/legacy) |
| `set_animation_clips` | Configure animation clips |
| `find_assets` | Find model assets |

**Typical flow** for a character model:
1. `get_settings` / `get_rig_info` — inspect
2. `set_rig(rigType="humanoid")` — set avatar type
3. `set_animation_clips(...)` — clip import/loop config

## 5. Shaders — `manage_shader`

| Action | Task |
|--------|------|
| `list` / `find` / `find_shaders` | Find/list shaders |
| `get_info` / `get_shader_info` / `get_properties` / `get_keywords` / `list_keywords` | Inspect shader |
| `custom` / `surface` / `unlit` | Shader preset/template types |

> `custom`/`surface`/`unlit` are shader type choices for creation, not standalone tools.

## 6. Packages — `manage_package`

| Action | Task |
|--------|------|
| `list` | List installed packages |
| `search` / `get_versions` | Search packages / versions |
| `install` / `remove` | Install / remove a package |
| `check_installed` / `get_info` | Status / info |
| `get_dependencies` | Package dependencies |
| `refresh` | Refresh package database |

**Typical**: `manage_package(search, name="Cinemachine")` → `install(name="com.unity.cinemachine", version="...")` → `list` to confirm.

## Guardrails / pitfalls
- **Tool names are `manage_asset`/`manage_asset_import`/`manage_texture_import`/`manage_model_import`/`manage_shader`/`manage_package`** — dispatched by `action`. There are NO flat tools like `texture_import`/`model_import`/`import_texture`. (The `asset_import` vs `texture_import`/`model_import` distinction: `manage_asset_import` is generic importer ops/labels; `manage_texture_import`/`manage_model_import` are the type-specific settings.)
- **Reimport / high quality settings can be expensive** on large asset sets; prefer targeting single assets or explicit batches.
- Import setting changes (texture type, model rig, compression) persist as .meta and affect the built game — deliberate before large batch changes.
- `manage_package` touches the project's Package manifest (irreversible-ish, affects the whole project + other devs). Confirm package name/version before install/remove.
- Asset paths use `Assets/` + forward slashes; never backslashes.
