using System.Collections.Generic;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// MCP 工具参数 Schema 静态映射表。
    /// <para>
    /// 由于 unity-mcp v9.5.3 的 C# 端工具类没有嵌套 Parameters 类，
    /// <see cref="MCPForUnity.Editor.Services.ToolDiscoveryService"/> 的 ExtractParameters()
    /// 始终返回空列表，导致所有工具的参数 schema 为空。
    /// </para>
    /// <para>
    /// 此类提供手工维护的静态 schema 映射，从 unity-mcp Python 端的 FastMCP Pydantic 定义
    /// 和 C# 工具源码中提取参数信息。当 ToolDiscovery 返回空 schema 时，
    /// <see cref="UnityMcpBridge"/> 会从此映射表中查找正确的 schema。
    /// </para>
    /// </summary>
    public static class McpToolSchemas
    {
        /// <summary>
        /// 对于未在映射表中的工具，使用此宽松 schema。
        /// 允许任意参数传入，不做约束。
        /// </summary>
        public const string FallbackSchema = @"{
  ""type"": ""object"",
  ""additionalProperties"": true
}";

        /// <summary>
        /// 获取所有工具的参数 schema 映射表。
        /// Key: 工具名称（如 "find_gameobjects"），Value: JSON Schema 字符串。
        /// </summary>
        /// <returns>工具名到 JSON Schema 的字典</returns>
        public static Dictionary<string, string> GetToolSchemas()
        {
            return new Dictionary<string, string>
            {
                // ==================== 场景管理 ====================
                ["manage_scene"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Scene action to perform."",
      ""enum"": [""create"", ""load"", ""save"", ""get_hierarchy"", ""get_active"", ""get_build_settings"", ""scene_view_frame"", ""close_scene"", ""set_active_scene"", ""get_loaded_scenes"", ""move_to_scene"", ""validate""]
    },
    ""name"": { ""type"": ""string"", ""description"": ""Scene name for create/load."" },
    ""path"": { ""type"": ""string"", ""description"": ""Scene file path."" },
    ""scene_name"": { ""type"": ""string"", ""description"": ""Scene name reference."" },
    ""scene_path"": { ""type"": ""string"", ""description"": ""Scene path reference."" },
    ""additive"": { ""type"": ""boolean"", ""description"": ""Load scene additively."" },
    ""template"": { ""type"": ""string"", ""description"": ""Template for scene creation."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject for move_to_scene."" },
    ""scene_view_target"": { ""type"": ""string"", ""description"": ""Target for scene_view_frame."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Parent node for hierarchy query."" },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Page size for hierarchy pagination."" },
    ""cursor"": { ""type"": ""integer"", ""description"": ""Pagination cursor."" },
    ""max_nodes"": { ""type"": ""integer"", ""description"": ""Max nodes to return."" },
    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max hierarchy depth."" },
    ""max_children_per_node"": { ""type"": ""integer"", ""description"": ""Max children per node."" },
    ""include_transform"": { ""type"": ""boolean"", ""description"": ""Include transform data."" },
    ""auto_repair"": { ""type"": ""boolean"", ""description"": ""Auto repair issues found by validate."" },
    ""build_index"": { ""type"": ""integer"", ""description"": ""Build index for scene."" },
    ""remove_scene"": { ""type"": ""boolean"", ""description"": ""Remove scene from build settings."" }
  },
  ""required"": [""action""]
}",

                // ==================== GameObject 搜索 ====================
                ["find_gameobjects"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""search_term"": { ""type"": ""string"", ""description"": ""The value to search for (name, tag, layer name, component type, or path)."" },
    ""search_method"": {
      ""type"": ""string"",
      ""description"": ""How to search for GameObjects."",
      ""enum"": [""by_name"", ""by_tag"", ""by_layer"", ""by_component"", ""by_path"", ""by_id""],
      ""default"": ""by_name""
    },
    ""include_inactive"": { ""type"": ""boolean"", ""description"": ""Include inactive GameObjects in search."" },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Number of results per page (default: 50, max: 500)."" },
    ""cursor"": { ""type"": ""integer"", ""description"": ""Pagination cursor (offset for next page)."" }
  },
  ""required"": [""search_term""]
}",

                // ==================== GameObject 管理 ====================
                ["manage_gameobject"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Action to perform on GameObject."",
      ""enum"": [""create"", ""modify"", ""delete"", ""duplicate"", ""move_relative"", ""look_at""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name, path, or instance ID."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name for new GameObject."" },
    ""new_name"": { ""type"": ""string"", ""description"": ""New name when renaming."" },
    ""primitive_type"": { ""type"": ""string"", ""description"": ""Primitive type (Cube, Sphere, Cylinder, etc.)."" },
    ""position"": { ""description"": ""Position [x,y,z]."" },
    ""rotation"": { ""description"": ""Rotation euler angles [x,y,z]."" },
    ""scale"": { ""description"": ""Scale [x,y,z]."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Parent GameObject name or path."" },
    ""tag"": { ""type"": ""string"", ""description"": ""Tag to assign."" },
    ""layer"": { ""type"": ""string"", ""description"": ""Layer to assign."" },
    ""is_static"": { ""type"": ""boolean"", ""description"": ""Set static flag."" },
    ""set_active"": { ""type"": ""boolean"", ""description"": ""Set active state."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find the target."", ""enum"": [""by_id"", ""by_name"", ""by_path"", ""by_tag"", ""by_layer"", ""by_component""] },
    ""components_to_add"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Components to add."" },
    ""components_to_remove"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Components to remove."" },
    ""component_properties"": { ""type"": ""object"", ""description"": ""Component property values to set."" },
    ""save_as_prefab"": { ""type"": ""boolean"", ""description"": ""Save as prefab after creation."" },
    ""prefab_path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
    ""prefab_folder"": { ""type"": ""string"", ""description"": ""Folder for prefab."" },
    ""direction"": { ""type"": ""string"", ""description"": ""Direction for move_relative."", ""enum"": [""left"", ""right"", ""up"", ""down"", ""forward"", ""back""] },
    ""distance"": { ""type"": ""number"", ""description"": ""Distance for move_relative."" },
    ""reference_object"": { ""type"": ""string"", ""description"": ""Reference object for relative movement."" },
    ""offset"": { ""description"": ""Offset [x,y,z] for move_relative."" },
    ""world_space"": { ""type"": ""boolean"", ""description"": ""Use world space coordinates."" },
    ""look_at_target"": { ""description"": ""Target position [x,y,z] for look_at."" },
    ""look_at_up"": { ""description"": ""Up vector [x,y,z] for look_at."" }
  }
}",

                // ==================== 组件管理 ====================
                ["manage_components"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Action to perform: add, remove, set_property."",
      ""enum"": [""add"", ""remove"", ""set_property""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject - instance ID or name/path."" },
    ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g., 'Rigidbody', 'BoxCollider')."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find the target."", ""enum"": [""by_id"", ""by_name"", ""by_path""] },
    ""property"": { ""type"": ""string"", ""description"": ""Property name to set."" },
    ""value"": { ""description"": ""Value to set for the property."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Dictionary of property names to values."" },
    ""component_index"": { ""type"": ""integer"", ""description"": ""Zero-based index when multiple components of same type exist."" }
  },
  ""required"": [""action"", ""target"", ""component_type""]
}",

                // ==================== 资产管理 ====================
                ["manage_asset"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Asset operation to perform."",
      ""enum"": [""import"", ""create"", ""modify"", ""delete"", ""duplicate"", ""move"", ""rename"", ""search"", ""get_info"", ""create_folder"", ""get_components""]
    },
    ""path"": { ""type"": ""string"", ""description"": ""Asset path or search scope."" },
    ""destination"": { ""type"": ""string"", ""description"": ""Destination path for move/duplicate."" },
    ""asset_type"": { ""type"": ""string"", ""description"": ""Type of asset to create."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Asset properties."" },
    ""search_pattern"": { ""type"": ""string"", ""description"": ""Search pattern for search action."" },
    ""filter_type"": { ""type"": ""string"", ""description"": ""Filter by asset type."" },
    ""filter_date_after"": { ""type"": ""string"", ""description"": ""Filter by date."" },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Results per page."" },
    ""page_number"": { ""type"": ""integer"", ""description"": ""Page number."" },
    ""generate_preview"": { ""type"": ""boolean"", ""description"": ""Generate asset preview."", ""default"": false }
  },
  ""required"": [""action"", ""path""]
}",

                // ==================== 编辑器管理 ====================
                ["manage_editor"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Editor action to perform."",
      ""enum"": [""telemetry_status"", ""telemetry_ping"", ""play"", ""pause"", ""stop"", ""set_active_tool"", ""add_tag"", ""remove_tag"", ""add_layer"", ""remove_layer"", ""deploy_package"", ""restore_package"", ""undo"", ""redo""]
    },
    ""tool_name"": { ""type"": ""string"", ""description"": ""Tool name for set_active_tool."" },
    ""tag_name"": { ""type"": ""string"", ""description"": ""Tag name for add/remove_tag."" },
    ""layer_name"": { ""type"": ""string"", ""description"": ""Layer name for add/remove_layer."" }
  },
  ""required"": [""action""]
}",

                // ==================== 脚本管理 ====================
                ["manage_script"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Script action: create, read, delete."",
      ""enum"": [""create"", ""read"", ""delete""]
    },
    ""name"": { ""type"": ""string"", ""description"": ""Script name."" },
    ""path"": { ""type"": ""string"", ""description"": ""Script path under Assets/."" },
    ""contents"": { ""type"": ""string"", ""description"": ""Script contents for create."" },
    ""namespace"": { ""type"": ""string"", ""description"": ""Namespace for the script."" },
    ""script_type"": { ""type"": ""string"", ""description"": ""Script type (MonoBehaviour, etc.)."" }
  },
  ""required"": [""action"", ""name"", ""path""]
}",

                // ==================== 脚本创建 ====================
                ["create_script"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"", ""description"": ""Path under Assets/ to create the script at."" },
    ""contents"": { ""type"": ""string"", ""description"": ""C# code contents of the script."" },
    ""namespace"": { ""type"": ""string"", ""description"": ""Namespace for the script."" },
    ""script_type"": { ""type"": ""string"", ""description"": ""Script type."" }
  },
  ""required"": [""path"", ""contents""]
}",

                // ==================== 脚本删除 ====================
                ["delete_script"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""uri"": { ""type"": ""string"", ""description"": ""URI of the script to delete."" }
  },
  ""required"": [""uri""]
}",

                // ==================== 脚本验证 ====================
                ["validate_script"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""uri"": { ""type"": ""string"", ""description"": ""URI of the script to validate."" },
    ""level"": { ""type"": ""string"", ""description"": ""Validation level."", ""enum"": [""basic"", ""standard""], ""default"": ""basic"" },
    ""include_diagnostics"": { ""type"": ""boolean"", ""description"": ""Include full diagnostics."", ""default"": false }
  },
  ""required"": [""uri""]
}",

                // ==================== 脚本结构化编辑 ====================
                ["script_apply_edits"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Name of the script to edit."" },
    ""path"": { ""type"": ""string"", ""description"": ""Path to the script under Assets/."" },
    ""edits"": { ""description"": ""List of edits (JSON array or stringified JSON). Each edit has op, className, methodName, replacement, etc."" },
    ""namespace"": { ""type"": ""string"", ""description"": ""Script namespace."" },
    ""script_type"": { ""type"": ""string"", ""description"": ""Script type (default: MonoBehaviour)."", ""default"": ""MonoBehaviour"" },
    ""options"": { ""type"": ""object"", ""description"": ""Options like validate, refresh."" }
  },
  ""required"": [""name"", ""path"", ""edits""]
}",

                // ==================== 文本编辑 ====================
                ["apply_text_edits"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""uri"": { ""type"": ""string"", ""description"": ""URI of the script to edit."" },
    ""edits"": { ""type"": ""array"", ""description"": ""List of edits with startLine, startCol, endLine, endCol, newText (1-indexed)."" },
    ""precondition_sha256"": { ""type"": ""string"", ""description"": ""Expected SHA256 hash for optimistic concurrency."" },
    ""strict"": { ""type"": ""boolean"", ""description"": ""Strict mode."" },
    ""options"": { ""type"": ""object"", ""description"": ""Additional options."" }
  },
  ""required"": [""uri"", ""edits""]
}",

                // ==================== SHA 获取 ====================
                ["get_sha"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""uri"": { ""type"": ""string"", ""description"": ""URI of the script."" }
  },
  ""required"": [""uri""]
}",

                // ==================== 代码执行 ====================
                ["execute_code"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Action to perform."",
      ""enum"": [""execute"", ""get_history"", ""replay"", ""clear_history""]
    },
    ""code"": { ""type"": ""string"", ""description"": ""C# code to execute (for execute action)."" },
    ""compiler"": { ""type"": ""string"", ""description"": ""Compiler backend."", ""enum"": [""auto"", ""roslyn"", ""codedom""], ""default"": ""auto"" },
    ""safety_checks"": { ""type"": ""boolean"", ""description"": ""Enable safety checks."", ""default"": true },
    ""index"": { ""type"": ""integer"", ""description"": ""History index for replay."" },
    ""limit"": { ""type"": ""integer"", ""description"": ""Number of history entries."", ""default"": 10 }
  },
  ""required"": [""action""]
}",

                // ==================== 菜单项执行 ====================
                ["execute_menu_item"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""menu_path"": { ""type"": ""string"", ""description"": ""Menu item path to execute (e.g., 'File/Save')."" }
  }
}",

                // ==================== 材质管理 ====================
                ["manage_material"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Material action."",
      ""enum"": [""ping"", ""create"", ""set_material_shader_property"", ""set_material_color"", ""assign_material_to_renderer"", ""set_renderer_color"", ""get_material_info""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""material_path"": { ""type"": ""string"", ""description"": ""Material asset path."" },
    ""shader"": { ""type"": ""string"", ""description"": ""Shader name."" },
    ""color"": { ""description"": ""Color value [r,g,b,a]."" },
    ""property"": { ""type"": ""string"", ""description"": ""Shader property name."" },
    ""value"": { ""description"": ""Property value."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Multiple properties."" },
    ""mode"": { ""type"": ""string"", ""description"": ""Material mode."", ""enum"": [""shared"", ""instance"", ""property_block"", ""create_unique""] },
    ""slot"": { ""type"": ""integer"", ""description"": ""Material slot index."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""Search method."" }
  },
  ""required"": [""action""]
}",

                // ==================== 纹理管理 ====================
                ["manage_texture"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Texture action."",
      ""enum"": [""create"", ""modify"", ""delete"", ""create_sprite"", ""apply_pattern"", ""apply_gradient"", ""apply_noise"", ""set_import_settings""]
    },
    ""path"": { ""type"": ""string"", ""description"": ""Texture asset path."" },
    ""width"": { ""type"": ""integer"", ""description"": ""Texture width."" },
    ""height"": { ""type"": ""integer"", ""description"": ""Texture height."" },
    ""fill_color"": { ""description"": ""Fill color [r,g,b,a]."" },
    ""pattern"": { ""type"": ""string"", ""description"": ""Pattern type."", ""enum"": [""checkerboard"", ""stripes"", ""stripes_h"", ""stripes_v"", ""stripes_diag"", ""dots"", ""grid"", ""brick""] },
    ""pattern_size"": { ""type"": ""integer"", ""description"": ""Pattern tile size."" },
    ""palette"": { ""description"": ""Color palette for patterns."" },
    ""gradient_type"": { ""type"": ""string"", ""description"": ""Gradient type."", ""enum"": [""linear"", ""radial""] },
    ""gradient_angle"": { ""type"": ""number"", ""description"": ""Gradient angle."" },
    ""noise_scale"": { ""type"": ""number"", ""description"": ""Noise scale."" },
    ""octaves"": { ""type"": ""integer"", ""description"": ""Noise octaves."" },
    ""as_sprite"": { ""type"": ""boolean"", ""description"": ""Create as sprite."" },
    ""import_settings"": { ""type"": ""object"", ""description"": ""Import settings."" },
    ""image_path"": { ""type"": ""string"", ""description"": ""External image path."" }
  },
  ""required"": [""action""]
}",

                // ==================== Shader 管理 ====================
                ["manage_shader"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Shader action."",
      ""enum"": [""create"", ""read"", ""update"", ""delete""]
    },
    ""name"": { ""type"": ""string"", ""description"": ""Shader name."" },
    ""path"": { ""type"": ""string"", ""description"": ""Asset path."" },
    ""contents"": { ""type"": ""string"", ""description"": ""Shader code contents."" }
  },
  ""required"": [""action"", ""name"", ""path""]
}",

                // ==================== 控制台读取 ====================
                ["read_console"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""Action: get or clear."", ""enum"": [""get"", ""clear""] },
    ""count"": { ""type"": ""integer"", ""description"": ""Number of entries to return."" },
    ""types"": { ""description"": ""Filter by message types: error, warning, log, all."" },
    ""filter_text"": { ""type"": ""string"", ""description"": ""Text filter."" },
    ""include_stacktrace"": { ""type"": ""boolean"", ""description"": ""Include stack traces."" },
    ""format"": { ""type"": ""string"", ""description"": ""Output format."", ""enum"": [""plain"", ""detailed"", ""json""] },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Page size."" },
    ""cursor"": { ""type"": ""integer"", ""description"": ""Pagination cursor."" }
  }
}",

                // ==================== 刷新 Unity ====================
                ["refresh_unity"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""description"": ""Refresh mode."", ""enum"": [""if_dirty"", ""force""], ""default"": ""if_dirty"" },
    ""scope"": { ""type"": ""string"", ""description"": ""Refresh scope."", ""enum"": [""assets"", ""scripts"", ""all""], ""default"": ""all"" },
    ""compile"": { ""type"": ""string"", ""description"": ""Whether to request compilation."", ""enum"": [""none"", ""request""], ""default"": ""none"" },
    ""wait_for_ready"": { ""type"": ""boolean"", ""description"": ""Wait until editor is ready."", ""default"": true }
  }
}",

                // ==================== 包管理 ====================
                ["manage_packages"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Package action."",
      ""enum"": [""list_packages"", ""search_packages"", ""get_package_info"", ""add_package"", ""remove_package"", ""embed_package"", ""resolve_packages"", ""list_registries"", ""add_registry"", ""remove_registry"", ""ping"", ""status""]
    },
    ""package"": { ""type"": ""string"", ""description"": ""Package identifier."" },
    ""query"": { ""type"": ""string"", ""description"": ""Search query."" },
    ""job_id"": { ""type"": ""string"", ""description"": ""Job ID for polling."" },
    ""force"": { ""type"": ""boolean"", ""description"": ""Force removal."" },
    ""name"": { ""type"": ""string"", ""description"": ""Registry name."" },
    ""url"": { ""type"": ""string"", ""description"": ""Registry URL."" },
    ""scopes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Registry scopes."" }
  },
  ""required"": [""action""]
}",

                // ==================== 相机管理 ====================
                ["manage_camera"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Camera action to perform."",
      ""enum"": [""ping"", ""ensure_brain"", ""get_brain_status"", ""create_camera"", ""set_target"", ""set_priority"", ""set_lens"", ""set_body"", ""set_aim"", ""set_noise"", ""add_extension"", ""remove_extension"", ""set_blend"", ""force_camera"", ""release_override"", ""list_cameras"", ""screenshot"", ""screenshot_multiview""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target camera name/path/ID."" },
    ""camera"": { ""type"": ""string"", ""description"": ""Camera to capture from."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find target."", ""enum"": [""by_id"", ""by_name"", ""by_path""] },
    ""properties"": { ""type"": ""object"", ""description"": ""Action-specific parameters."" },
    ""include_image"": { ""type"": ""boolean"", ""description"": ""Return screenshot as inline base64 PNG."" },
    ""max_resolution"": { ""type"": ""integer"", ""description"": ""Max resolution for inline image."" },
    ""batch"": { ""type"": ""string"", ""description"": ""Batch capture mode: surround or orbit."" },
    ""view_target"": { ""description"": ""Target to focus on."" },
    ""view_position"": { ""description"": ""World position [x,y,z]."" },
    ""view_rotation"": { ""description"": ""Euler rotation [x,y,z]."" },
    ""capture_source"": { ""type"": ""string"", ""description"": ""Screenshot source."", ""enum"": [""game_view"", ""scene_view""] },
    ""screenshot_file_name"": { ""type"": ""string"", ""description"": ""Screenshot file name."" },
    ""screenshot_super_size"": { ""type"": ""integer"", ""description"": ""Screenshot supersize multiplier."" },
    ""orbit_angles"": { ""type"": ""integer"", ""description"": ""Number of azimuth samples for orbit."" },
    ""orbit_elevations"": { ""description"": ""Elevation angles for orbit."" },
    ""orbit_distance"": { ""type"": ""number"", ""description"": ""Camera distance for orbit."" },
    ""orbit_fov"": { ""type"": ""number"", ""description"": ""Camera FOV for orbit."" }
  },
  ""required"": [""action""]
}",

                // ==================== 动画管理 ====================
                ["manage_animation"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""Animation action (prefix: animator_, controller_, clip_)."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find target."" },
    ""clip_path"": { ""type"": ""string"", ""description"": ""Asset path for AnimationClip."" },
    ""controller_path"": { ""type"": ""string"", ""description"": ""Asset path for AnimatorController."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Action-specific parameters."" }
  },
  ""required"": [""action""]
}",

                // ==================== 物理管理 ====================
                ["manage_physics"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Physics action."",
      ""enum"": [""ping"", ""get_settings"", ""set_settings"", ""get_collision_matrix"", ""set_collision_matrix"", ""create_physics_material"", ""configure_physics_material"", ""assign_physics_material"", ""add_joint"", ""configure_joint"", ""remove_joint"", ""raycast"", ""raycast_all"", ""linecast"", ""shapecast"", ""overlap"", ""validate"", ""simulate_step"", ""apply_force"", ""get_rigidbody"", ""configure_rigidbody""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""Search method."" },
    ""settings"": { ""type"": ""object"", ""description"": ""Physics settings."" },
    ""origin"": { ""description"": ""Ray origin [x,y,z]."" },
    ""direction"": { ""description"": ""Ray direction [x,y,z]."" },
    ""max_distance"": { ""type"": ""number"", ""description"": ""Max raycast distance."" },
    ""layer_mask"": { ""type"": ""string"", ""description"": ""Layer mask."" },
    ""force"": { ""description"": ""Force vector [x,y,z]."" },
    ""force_mode"": { ""type"": ""string"", ""description"": ""Force mode."" },
    ""joint_type"": { ""type"": ""string"", ""description"": ""Joint type."" },
    ""connected_body"": { ""type"": ""string"", ""description"": ""Connected body."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Additional properties."" },
    ""dimension"": { ""type"": ""string"", ""description"": ""Physics dimension: 3d or 2d."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name for physics material."" },
    ""path"": { ""type"": ""string"", ""description"": ""Asset path."" },
    ""material_path"": { ""type"": ""string"", ""description"": ""Physics material path."" },
    ""shape"": { ""type"": ""string"", ""description"": ""Overlap shape."" },
    ""position"": { ""description"": ""Position [x,y,z]."" },
    ""size"": { ""description"": ""Size value."" },
    ""component_index"": { ""type"": ""integer"", ""description"": ""Component index."" }
  },
  ""required"": [""action""]
}",

                // ==================== 图形管理 ====================
                ["manage_graphics"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""Graphics action (volume_*, bake_*, stats_*, pipeline_*, feature_*, skybox_*)."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target object."" },
    ""effect"": { ""type"": ""string"", ""description"": ""Effect type name."" },
    ""parameters"": { ""type"": ""object"", ""description"": ""Effect parameters."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Properties to set."" },
    ""settings"": { ""type"": ""object"", ""description"": ""Settings dict."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name for created objects."" },
    ""position"": { ""description"": ""Position [x,y,z]."" },
    ""is_global"": { ""type"": ""boolean"", ""description"": ""Whether Volume is global."" },
    ""priority"": { ""type"": ""number"", ""description"": ""Volume priority."" },
    ""weight"": { ""type"": ""number"", ""description"": ""Volume weight (0-1)."" },
    ""profile_path"": { ""type"": ""string"", ""description"": ""VolumeProfile asset path."" },
    ""level"": { ""type"": ""string"", ""description"": ""Quality level."" },
    ""feature_type"": { ""type"": ""string"", ""description"": ""Renderer feature type."" },
    ""fog_enabled"": { ""type"": ""boolean"", ""description"": ""Enable fog."" },
    ""fog_mode"": { ""type"": ""string"", ""description"": ""Fog mode."" },
    ""fog_color"": { ""description"": ""Fog color [r,g,b,a]."" },
    ""fog_density"": { ""type"": ""number"", ""description"": ""Fog density."" },
    ""ambient_mode"": { ""type"": ""string"", ""description"": ""Ambient mode."" },
    ""color"": { ""description"": ""Color [r,g,b,a]."" },
    ""intensity"": { ""type"": ""number"", ""description"": ""Intensity value."" }
  },
  ""required"": [""action""]
}",

                // ==================== Prefab 管理 ====================
                ["manage_prefabs"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Prefab action."",
      ""enum"": [""create_from_gameobject"", ""get_info"", ""get_hierarchy"", ""modify_contents"", ""open_prefab_stage"", ""save_prefab_stage"", ""close_prefab_stage""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""prefab_path"": { ""type"": ""string"", ""description"": ""Prefab asset path."" },
    ""name"": { ""type"": ""string"", ""description"": ""Name."" },
    ""position"": { ""description"": ""Position [x,y,z]."" },
    ""rotation"": { ""description"": ""Rotation [x,y,z]."" },
    ""scale"": { ""description"": ""Scale [x,y,z]."" },
    ""parent"": { ""type"": ""string"", ""description"": ""Parent path."" },
    ""tag"": { ""type"": ""string"", ""description"": ""Tag."" },
    ""layer"": { ""type"": ""string"", ""description"": ""Layer."" },
    ""set_active"": { ""type"": ""boolean"", ""description"": ""Active state."" },
    ""components_to_add"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Components to add."" },
    ""components_to_remove"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Components to remove."" },
    ""component_properties"": { ""type"": ""object"", ""description"": ""Component properties."" },
    ""create_child"": { ""description"": ""Child objects to create (object or array)."" },
    ""delete_child"": { ""description"": ""Child objects to delete (string or array)."" },
    ""allow_overwrite"": { ""type"": ""boolean"", ""description"": ""Allow overwrite."" }
  },
  ""required"": [""action""]
}",

                // ==================== ProBuilder ====================
                ["manage_probuilder"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""ProBuilder action (create_shape, extrude_faces, etc.)."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find target."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Action-specific parameters."" }
  },
  ""required"": [""action""]
}",

                // ==================== VFX 管理 ====================
                ["manage_vfx"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": { ""type"": ""string"", ""description"": ""VFX action (particle_*, vfx_*, line_*, trail_*)."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""search_method"": { ""type"": ""string"", ""description"": ""How to find target."" },
    ""properties"": { ""type"": ""object"", ""description"": ""Action-specific parameters."" },
    ""component_index"": { ""type"": ""integer"", ""description"": ""Component index."" }
  },
  ""required"": [""action""]
}",

                // ==================== UI 管理 ====================
                ["manage_ui"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""UI action."",
      ""enum"": [""ping"", ""create"", ""read"", ""update"", ""delete"", ""attach_ui_document"", ""detach_ui_document"", ""create_panel_settings"", ""update_panel_settings"", ""get_visual_tree"", ""render_ui"", ""link_stylesheet"", ""list"", ""modify_visual_element""]
    },
    ""path"": { ""type"": ""string"", ""description"": ""Asset path for UXML/USS."" },
    ""contents"": { ""type"": ""string"", ""description"": ""File contents."" },
    ""target"": { ""type"": ""string"", ""description"": ""Target GameObject."" },
    ""source_asset"": { ""type"": ""string"", ""description"": ""UXML source asset path."" },
    ""panel_settings"": { ""type"": ""string"", ""description"": ""PanelSettings asset path."" },
    ""stylesheet"": { ""type"": ""string"", ""description"": ""USS stylesheet path."" },
    ""element_name"": { ""type"": ""string"", ""description"": ""Visual element name."" },
    ""text"": { ""type"": ""string"", ""description"": ""Text content."" },
    ""style"": { ""type"": ""object"", ""description"": ""Inline styles."" },
    ""add_classes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Classes to add."" },
    ""remove_classes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Classes to remove."" },
    ""visible"": { ""type"": ""boolean"", ""description"": ""Visibility."" },
    ""enabled"": { ""type"": ""boolean"", ""description"": ""Enabled state."" },
    ""include_image"": { ""type"": ""boolean"", ""description"": ""Include rendered image."" },
    ""max_resolution"": { ""type"": ""integer"", ""description"": ""Max resolution."" },
    ""filter_type"": { ""type"": ""string"", ""description"": ""Filter type for list."" },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Page size."" },
    ""page_number"": { ""type"": ""integer"", ""description"": ""Page number."" },
    ""max_depth"": { ""type"": ""integer"", ""description"": ""Max depth for visual tree."" },
    ""sort_order"": { ""type"": ""integer"", ""description"": ""Sort order."" },
    ""width"": { ""type"": ""integer"", ""description"": ""Width."" },
    ""height"": { ""type"": ""integer"", ""description"": ""Height."" }
  },
  ""required"": [""action""]
}",

                // ==================== ScriptableObject 管理 ====================
                ["manage_scriptable_object"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Action: create or modify."",
      ""enum"": [""create"", ""modify""]
    },
    ""type_name"": { ""type"": ""string"", ""description"": ""ScriptableObject type name (for create)."" },
    ""asset_name"": { ""type"": ""string"", ""description"": ""Asset file name without extension (for create)."" },
    ""folder_path"": { ""type"": ""string"", ""description"": ""Target folder (for create)."" },
    ""target"": { ""description"": ""Target asset reference {guid|path} (for modify)."" },
    ""patches"": { ""description"": ""Patch list to apply."" },
    ""overwrite"": { ""type"": ""boolean"", ""description"": ""Overwrite existing asset."" },
    ""dry_run"": { ""type"": ""boolean"", ""description"": ""Validate without applying."" }
  },
  ""required"": [""action""]
}",

                // ==================== 测试运行 ====================
                ["run_tests"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""mode"": { ""type"": ""string"", ""description"": ""Test mode."", ""enum"": [""EditMode"", ""PlayMode""], ""default"": ""EditMode"" },
    ""test_names"": { ""description"": ""Specific test names to run."" },
    ""assembly_names"": { ""description"": ""Assembly names to filter."" },
    ""category_names"": { ""description"": ""Category names to filter."" },
    ""group_names"": { ""description"": ""Group names to filter."" },
    ""include_details"": { ""type"": ""boolean"", ""description"": ""Include all test details."", ""default"": false },
    ""include_failed_tests"": { ""type"": ""boolean"", ""description"": ""Include failed test details."", ""default"": false }
  }
}",

                // ==================== 测试结果查询 ====================
                ["get_test_job"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""job_id"": { ""type"": ""string"", ""description"": ""Job ID returned by run_tests."" },
    ""include_details"": { ""type"": ""boolean"", ""description"": ""Include all test details."", ""default"": false },
    ""include_failed_tests"": { ""type"": ""boolean"", ""description"": ""Include failed test details."", ""default"": false },
    ""wait_timeout"": { ""type"": ""integer"", ""description"": ""Wait timeout in seconds."" }
  },
  ""required"": [""job_id""]
}",

                // ==================== 批量执行 ====================
                ["batch_execute"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""commands"": { ""type"": ""array"", ""description"": ""List of commands with 'tool' and 'params' keys."" },
    ""parallel"": { ""type"": ""boolean"", ""description"": ""Run read-only commands in parallel."" },
    ""fail_fast"": { ""type"": ""boolean"", ""description"": ""Stop after first failure."" },
    ""max_parallelism"": { ""type"": ""integer"", ""description"": ""Max parallel workers."" }
  },
  ""required"": [""commands""]
}",

                // ==================== 构建管理 ====================
                ["manage_build"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Build action."",
      ""enum"": [""build"", ""status"", ""platform"", ""settings"", ""scenes"", ""profiles"", ""batch"", ""cancel""]
    },
    ""target"": { ""type"": ""string"", ""description"": ""Build target platform."" },
    ""output_path"": { ""type"": ""string"", ""description"": ""Output path."" },
    ""scenes"": { ""type"": ""string"", ""description"": ""Scene paths (JSON array or comma-separated)."" },
    ""development"": { ""type"": ""string"", ""description"": ""Development build (true/false)."" },
    ""options"": { ""type"": ""string"", ""description"": ""Build options JSON array."" },
    ""job_id"": { ""type"": ""string"", ""description"": ""Job ID for status/cancel."" },
    ""property"": { ""type"": ""string"", ""description"": ""Settings property name."" },
    ""value"": { ""type"": ""string"", ""description"": ""Settings property value."" },
    ""subtarget"": { ""type"": ""string"", ""description"": ""Build subtarget: player or server."" },
    ""scripting_backend"": { ""type"": ""string"", ""description"": ""Scripting backend: mono or il2cpp."" },
    ""profile"": { ""type"": ""string"", ""description"": ""Build profile path (Unity 6+)."" },
    ""activate"": { ""type"": ""string"", ""description"": ""Activate build profile."" },
    ""targets"": { ""type"": ""string"", ""description"": ""Batch build targets JSON array."" },
    ""profiles"": { ""type"": ""string"", ""description"": ""Batch build profiles JSON array."" },
    ""output_dir"": { ""type"": ""string"", ""description"": ""Base output directory for batch."" }
  },
  ""required"": [""action""]
}",

                // ==================== 性能分析 ====================
                ["manage_profiler"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Profiler action."",
      ""enum"": [""profiler_start"", ""profiler_stop"", ""profiler_status"", ""profiler_set_areas"", ""get_frame_timing"", ""get_counters"", ""get_object_memory"", ""memory_take_snapshot"", ""memory_list_snapshots"", ""memory_compare_snapshots"", ""frame_debugger_enable"", ""frame_debugger_disable"", ""frame_debugger_get_events""]
    },
    ""log_file"": { ""type"": ""string"", ""description"": ""Recording file path."" },
    ""enable_callstacks"": { ""type"": ""boolean"", ""description"": ""Enable callstacks."" },
    ""areas"": { ""type"": ""object"", ""description"": ""Profiler areas to toggle."" },
    ""category"": { ""type"": ""string"", ""description"": ""Counter category."" },
    ""counters"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Counter names."" },
    ""object_path"": { ""type"": ""string"", ""description"": ""Object path for memory."" },
    ""snapshot_path"": { ""type"": ""string"", ""description"": ""Snapshot output path."" },
    ""snapshot_a"": { ""type"": ""string"", ""description"": ""First snapshot for compare."" },
    ""snapshot_b"": { ""type"": ""string"", ""description"": ""Second snapshot for compare."" },
    ""search_path"": { ""type"": ""string"", ""description"": ""Search directory for snapshots."" },
    ""cursor"": { ""type"": ""integer"", ""description"": ""Cursor for frame debugger."" },
    ""page_size"": { ""type"": ""integer"", ""description"": ""Page size for frame debugger."" }
  },
  ""required"": [""action""]
}",

                // ==================== 文件搜索 ====================
                ["find_in_file"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""uri"": { ""type"": ""string"", ""description"": ""Resource URI to search."" },
    ""pattern"": { ""type"": ""string"", ""description"": ""Regex pattern to search for."" },
    ""ignore_case"": { ""type"": ""boolean"", ""description"": ""Case insensitive search."", ""default"": true },
    ""max_results"": { ""type"": ""integer"", ""description"": ""Max results."", ""default"": 200 },
    ""project_root"": { ""type"": ""string"", ""description"": ""Optional project root path."" }
  },
  ""required"": [""uri"", ""pattern""]
}",

                // ==================== 脚本能力查询 ====================
                ["manage_script_capabilities"] = @"{
  ""type"": ""object"",
  ""properties"": {}
}",

                // ==================== 工具组管理 ====================
                ["manage_tools"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Action to perform."",
      ""enum"": [""list_groups"", ""activate"", ""deactivate"", ""sync"", ""reset""]
    },
    ""group"": { ""type"": ""string"", ""description"": ""Group name for activate/deactivate."" }
  },
  ""required"": [""action""]
}",

                // ==================== Unity 文档查询 ====================
                ["unity_docs"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Documentation action."",
      ""enum"": [""get_doc"", ""get_manual"", ""get_package_doc"", ""lookup""]
    },
    ""class_name"": { ""type"": ""string"", ""description"": ""Unity class name."" },
    ""member_name"": { ""type"": ""string"", ""description"": ""Method or property name."" },
    ""slug"": { ""type"": ""string"", ""description"": ""Manual page slug."" },
    ""query"": { ""type"": ""string"", ""description"": ""Search query."" },
    ""queries"": { ""type"": ""string"", ""description"": ""Comma-separated search queries."" },
    ""package"": { ""type"": ""string"", ""description"": ""Package name."" },
    ""page"": { ""type"": ""string"", ""description"": ""Package doc page."" },
    ""pkg_version"": { ""type"": ""string"", ""description"": ""Package version."" },
    ""version"": { ""type"": ""string"", ""description"": ""Unity version."" }
  },
  ""required"": [""action""]
}",

                // ==================== Unity 反射 ====================
                ["unity_reflect"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""description"": ""Reflection action."",
      ""enum"": [""get_type"", ""get_member"", ""search""]
    },
    ""class_name"": { ""type"": ""string"", ""description"": ""C# class name."" },
    ""member_name"": { ""type"": ""string"", ""description"": ""Member name to inspect."" },
    ""query"": { ""type"": ""string"", ""description"": ""Search query."" },
    ""scope"": { ""type"": ""string"", ""description"": ""Assembly scope: unity, packages, project, all."" }
  },
  ""required"": [""action""]
}",

                // ==================== 调试请求上下文 ====================
                ["debug_request_context"] = @"{
  ""type"": ""object"",
  ""properties"": {}
}",

                // ==================== 设置活动实例 ====================
                ["set_active_instance"] = @"{
  ""type"": ""object"",
  ""properties"": {
    ""instance"": { ""type"": ""string"", ""description"": ""Target instance (Name@hash, hash prefix, or port number)."" }
  },
  ""required"": [""instance""]
}"
            };
        }
    }
}
