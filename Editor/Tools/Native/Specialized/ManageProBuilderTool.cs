using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Creates and edits ProBuilder meshes through reflection so ProBuilder remains an optional package.
    /// </summary>
    [AgentTool("manage_probuilder",
        Description = "ProBuilder in-editor mesh modeling — create and edit meshes without external 3D tools. " +
                      "Actions: create_shape (cube/cylinder/sphere/plane/stairs/arch/door/pipe/cone/torus with dimensions), " +
                      "extrude_faces, bevel_edges, bridge_edges, weld_vertices, subdivide, merge_objects, " +
                      "set_pivot, flip_normals, conform_normals, triangulate, " +
                      "set_uv_projection (auto/box/planar), get_mesh_info (vertex/face/edge counts, bounds). " +
                      "USE FOR: rapid prototyping of level geometry, creating architectural elements (stairs, walls, doorways), " +
                      "in-Editor mesh editing without exporting to Blender/Maya, custom primitive shapes. " +
                      "NOT FOR: importing external 3D models (use manage_model_import), modifying imported meshes (those are read-only), " +
                      "skeletal animation or rigging, complex organic modeling. " +
                      "PREREQUISITE: com.unity.probuilder package must be installed. " +
                      "ACTIVATE WHEN: user mentions 'probuilder', 'mesh modeling', 'create shape', 'extrude', 'bevel', 'level prototype', 'geometry editing'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ReadProject | ToolCapability.ModifyScene,
        // 风险标注只用于确认流程，不改变 Play Mode 行为：本工具不受 Play Mode 写拦截（与标注前一致）
        PlaymodeRuntimeSafeActions = new[] { "*" },
        ReadOnlyActions = new[] { "check_available", "get_info", "get_vertices", "get_faces" })]
    public class ManageProBuilderTool : IAgentTool
    {
        private const string PackageRequiredMessage = "ProBuilder package/API is not available. Install 'com.unity.probuilder' via Unity Package Manager, then retry this action.";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""check_available"", ""create_shape"", ""get_info"", ""set_material"", ""center_pivot"", ""flip_normals"", ""subdivide"", ""combine_meshes"", ""get_vertices"", ""move_vertices"", ""get_faces"", ""extrude_faces"", ""delete_faces"", ""bevel_edges"", ""bridge_edges"", ""weld_vertices"", ""set_uv_projection"", ""triangulate""], ""description"": ""ProBuilder action to perform"" },
                ""shape"": { ""type"": ""string"", ""description"": ""Shape: cube, sphere, cylinder, cone, plane, stairs, door, arch, pipe, torus"" },
                ""name"": { ""type"": ""string"", ""description"": ""Target or new GameObject name"" },
                ""names"": { ""type"": ""string"", ""description"": ""Comma-separated GameObject names for combine_meshes"" },
                ""new_name"": { ""type"": ""string"", ""description"": ""New combined object name"" },
                ""position"": { ""type"": ""object"", ""description"": ""Vector3 {x,y,z}; create position, pivot position, or vertex absolute position"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""rotation"": { ""type"": ""object"", ""description"": ""Euler rotation {x,y,z} (degrees)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""size"": { ""type"": ""object"", ""description"": ""Vector3 size/scale {x,y,z}"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""material_path"": { ""type"": ""string"" },
                ""iterations"": { ""type"": ""integer"" },
                ""max_count"": { ""type"": ""integer"" },
                ""vertices"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""index"": { ""type"": ""integer"" }, ""position"": { ""type"": ""object"", ""description"": ""Absolute vertex position (Vector3)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } }, ""offset"": { ""type"": ""object"", ""description"": ""Vertex position offset (Vector3)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } } } } },
                ""face_indices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""Face indices for extrude_faces, delete_faces operations"" },
                ""edge_indices"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""a"": { ""type"": ""integer"" }, ""b"": { ""type"": ""integer"" } } }, ""description"": ""Edge vertex index pairs for bevel_edges, bridge_edges"" },
                ""extrude_distance"": { ""type"": ""number"", ""description"": ""Extrusion distance for extrude_faces (default: 0.5)"" },
                ""bevel_distance"": { ""type"": ""number"", ""description"": ""Bevel distance/amount for bevel_edges (default: 0.1)"" },
                ""weld_distance"": { ""type"": ""number"", ""description"": ""Maximum distance to weld vertices together (default: 0.01)"" },
                ""uv_projection"": { ""type"": ""string"", ""description"": ""UV projection mode: planar, box, spherical, cylindrical (default: planar)"" },
                ""uv_channel"": { ""type"": ""integer"", ""description"": ""UV channel index (default: 0)"" }
            },
            ""required"": [""action""]
        }");

        private static bool _reflectionInitialized;
        private static Type _proBuilderMeshType;
        private static Type _shapeGeneratorType;
        private static Type _shapeTypeType;
        private static Type _pivotLocationType;
        private static Type _extrudeMethodType;
        private static Type _bevelType;
        private static Type _bridgeEdgesType;
        private static Type _weldVerticesType;
        private static Type _uvProjectionType;
        private static Type _faceType;
        private static Type _edgeType;

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_probuilder",
            description: "Create and edit ProBuilder meshes using optional ProBuilder package APIs. Supports shape creation, vertex/face/edge editing, UV projection, mesh operations, and ProBuilder-specific tools like extrude, bevel, bridge, and weld.",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes the requested ProBuilder management action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                if (action != "check_available" && !IsAvailable())
                {
                    response = ToolResponse.Fail(PackageRequiredMessage);
                }
                else
                {
                    switch (action)
                    {
                        case "check_available": response = HandleCheckAvailable(); break;
                        case "create_shape": response = HandleCreateShape(parameters); break;
                        case "get_info": response = HandleGetInfo(parameters); break;
                        case "set_material": response = HandleSetMaterial(parameters); break;
                        case "center_pivot": response = HandleCenterPivot(parameters); break;
                        case "flip_normals": response = HandleFlipNormals(parameters); break;
                        case "subdivide": response = HandleSubdivide(parameters); break;
                        case "combine_meshes": response = HandleCombineMeshes(parameters); break;
                        case "get_vertices": response = HandleGetVertices(parameters); break;
                        case "move_vertices": response = HandleMoveVertices(parameters); break;
                        case "get_faces": response = HandleGetFaces(parameters); break;
                        case "extrude_faces": response = HandleExtrudeFaces(parameters); break;
                        case "delete_faces": response = HandleDeleteFaces(parameters); break;
                        case "bevel_edges": response = HandleBevelEdges(parameters); break;
                        case "bridge_edges": response = HandleBridgeEdges(parameters); break;
                        case "weld_vertices": response = HandleWeldVertices(parameters); break;
                        case "set_uv_projection": response = HandleSetUVProjection(parameters); break;
                        case "triangulate": response = HandleTriangulate(parameters); break;
                        default:
                            response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: check_available, create_shape, get_info, set_material, center_pivot, flip_normals, subdivide, combine_meshes, get_vertices, move_vertices, get_faces, extrude_faces, delete_faces, bevel_edges, bridge_edges, weld_vertices, set_uv_projection, triangulate");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private ToolResponse HandleCheckAvailable()
        {
            var available = IsAvailable();
            return ToolResponse.OkWithData(new
            {
                available,
                proBuilderMeshType = _proBuilderMeshType?.AssemblyQualifiedName,
                shapeGeneratorType = _shapeGeneratorType?.AssemblyQualifiedName,
                shapeTypeType = _shapeTypeType?.AssemblyQualifiedName
            }, available ? "ProBuilder package/API is available." : PackageRequiredMessage);
        }

        private ToolResponse HandleCreateShape(JObject parameters)
        {
            var shape = ToolHelpers.GetOptionalString(parameters, "shape", "cube");
            var name = ToolHelpers.GetOptionalString(parameters, "name", $"ProBuilder {shape}");
            var shapeValue = ParseShapeType(shape);
            if (shapeValue == null) return ToolResponse.Fail($"Unsupported shape '{shape}'. Try cube, sphere, cylinder, cone, plane, stairs, door, arch, pipe, or torus.");

            var pb = TryCreateShape(shapeValue);
            if (pb == null)
                return ToolResponse.Fail("Current ProBuilder version does not expose a compatible ShapeGenerator.CreateShape API for create_shape.");

            var component = pb as Component;
            if (component == null) return ToolResponse.Fail("ProBuilder CreateShape returned an unexpected object.");
            var go = component.gameObject;
            ToolHelpers.RegisterCreatedObject(go, "Create ProBuilder Shape");
            go.name = name;
            go.transform.position = ToolHelpers.ParseVector3(parameters["position"], Vector3.zero);
            go.transform.eulerAngles = ToolHelpers.ParseVector3(parameters["rotation"], Vector3.zero);
            go.transform.localScale = ToolHelpers.ParseVector3(parameters["size"], Vector3.one);
            RefreshProBuilderMesh(component);
            EditorUtility.SetDirty(go);

            // 校验：生成的网格必须有顶点，否则物体虽已创建但形状实际为空
            var created = TakeSnapshot(go, component);
            if (created.Positions == 0 && created.MeshVertices == 0)
                return ToolResponse.Fail($"Created GameObject '{name}' but the generated ProBuilder {shape} has no vertices (empty mesh). The empty GameObject was left in the scene.");

            return ToolResponse.OkWithData(SerializeMeshInfo(go, component), $"Created ProBuilder {shape} '{name}'.");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component), $"ProBuilder info for '{result.GameObject.name}'.");
        }

        private ToolResponse HandleSetMaterial(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var materialPath = ToolHelpers.GetRequiredString(parameters, "material_path");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return ToolResponse.Fail($"Material not found at path: {materialPath}");

            var renderer = result.GameObject.GetComponent<Renderer>();
            if (renderer == null) return ToolResponse.Fail($"'{result.GameObject.name}' has no Renderer component.");
            ToolHelpers.RecordUndo(renderer, "Set ProBuilder Material");
            renderer.sharedMaterial = material;
            EditorUtility.SetDirty(renderer);
            if (renderer.sharedMaterial != material)
                return ToolResponse.Fail($"Assigned material '{materialPath}' to '{result.GameObject.name}', but the renderer still reports '{(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "null")}'.");
            return ToolResponse.Ok($"Assigned material '{materialPath}' to '{result.GameObject.name}'.");
        }

        private ToolResponse HandleCenterPivot(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var mesh = GetMesh(result.GameObject);
            if (mesh == null) return ToolResponse.Fail($"'{result.GameObject.name}' has no editable MeshFilter mesh.");

            var newPivot = parameters["position"] != null ? ToolHelpers.ParseVector3(parameters["position"]) : mesh.bounds.center + result.GameObject.transform.position;
            var worldCenterBefore = result.GameObject.transform.TransformPoint(mesh.bounds.center);
            SetPivotPreserveGeometry(result.GameObject, mesh, newPivot);
            RefreshProBuilderMesh(result.Component);

            // 校验：pivot 应移到目标位置，且几何体在世界空间中保持不动。
            // ProBuilder 的 ToMesh() 会按自身 positions 重建网格，可能覆盖上面对 Unity Mesh 顶点的平移。
            var meshAfter = GetMesh(result.GameObject);
            var pivotError = Vector3.Distance(result.GameObject.transform.position, newPivot);
            var geometryShift = meshAfter != null
                ? Vector3.Distance(result.GameObject.transform.TransformPoint(meshAfter.bounds.center), worldCenterBefore)
                : 0f;
            if (pivotError > VerifyTolerance || geometryShift > VerifyTolerance)
            {
                return ToolResponse.Fail(
                    $"center_pivot on '{result.GameObject.name}' did not take effect as intended: pivot is {pivotError:F4} away from {newPivot}, " +
                    $"and the geometry moved {geometryShift:F4} units in world space (expected 0). " +
                    "ProBuilder likely rebuilt the mesh from its own vertex positions, discarding the compensation. The transform change was kept; use Undo to revert.");
            }
            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component), $"Set pivot for '{result.GameObject.name}' to {newPivot}.");
        }

        private ToolResponse HandleFlipNormals(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var mesh = GetMesh(result.GameObject);
            if (mesh == null) return ToolResponse.Fail($"'{result.GameObject.name}' has no MeshFilter mesh to flip.");

            ToolHelpers.RecordUndo(mesh, "Flip ProBuilder Normals");
            var flipBefore = TakeSnapshot(result.GameObject, result.Component);
            var triangles = mesh.triangles;
            for (var i = 0; i < triangles.Length; i += 3)
            {
                var tmp = triangles[i];
                triangles[i] = triangles[i + 1];
                triangles[i + 1] = tmp;
            }
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            var flipAfter = TakeSnapshot(result.GameObject, result.Component);
            if (flipBefore.MeshIndices > 0 && flipAfter.TrianglesHash == flipBefore.TrianglesHash)
                return ToolResponse.Fail($"Flip normals on '{result.GameObject.name}' did not change the mesh triangles.");
            return ToolResponse.Ok($"Flipped normals on '{result.GameObject.name}' (edited the Unity mesh directly; a later ProBuilder rebuild of this object may regenerate it from ProBuilder data).");
        }

        private ToolResponse HandleSubdivide(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            // Try ProBuilder's Subdivide API via reflection
            var subdivideType = FindType("UnityEngine.ProBuilder.MeshOperations.Subdivide, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.Subdivide", "Unity.ProBuilder.MeshOperations.Subdivide");
            if (subdivideType != null)
            {
                var method = subdivideType.GetMethod("SubdivideMesh",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { _proBuilderMeshType }, null);
                if (method != null)
                {
                    try
                    {
                        ToolHelpers.RecordUndo(result.Component, "Subdivide ProBuilder Mesh");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        method.Invoke(null, new object[] { result.Component });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return VerifyEdit("subdivide", result.GameObject, result.Component, before,
                            after => after.Faces > before.Faces || after.Positions > before.Positions,
                            "face or vertex count to increase",
                            $"Subdivided '{result.GameObject.name}'.");
                    }
                    catch (Exception ex)
                    {
                        return ToolResponse.Fail($"Subdivide failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            // Fallback: manual mesh subdivision via Unity Mesh API
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail($"'{result.GameObject.name}' has no MeshFilter mesh to subdivide.");

            ToolHelpers.RecordUndo(mesh, "Subdivide Mesh");
            var fallbackBefore = TakeSnapshot(result.GameObject, result.Component);
            SubdivideMeshFallback(mesh);
            RefreshProBuilderMesh(result.Component);
            EditorUtility.SetDirty(mesh);
            // 回退路径直接改 Unity Mesh，RefreshProBuilderMesh 可能按 ProBuilder 数据重建而把修改覆盖掉
            return VerifyEdit("subdivide (fallback)", result.GameObject, result.Component, fallbackBefore,
                after => after.MeshIndices > fallbackBefore.MeshIndices,
                "Unity mesh triangle count to increase (the fallback edit may have been overwritten by ProBuilder's mesh rebuild)",
                $"Subdivided '{result.GameObject.name}' using fallback method (each triangle split into 4).");
        }

        private ToolResponse HandleGetFaces(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            int maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", 100);
            maxCount = Mathf.Clamp(maxCount, 1, 500);

            var facesData = GetFacesData(result.Component, maxCount, out int totalFaces);

            return ToolResponse.OkWithData(new
            {
                name = result.GameObject.name,
                totalFaces,
                returned = facesData.Count,
                maxCount,
                truncated = totalFaces > maxCount,
                faces = facesData
            }, $"Read {facesData.Count} of {totalFaces} faces from '{result.GameObject.name}'.");
        }

        private ToolResponse HandleExtrudeFaces(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            float distance = ToolHelpers.GetOptionalFloat(parameters, "extrude_distance", 0.5f);
            var faceIndicesToken = ToolHelpers.GetOptionalArray(parameters, "face_indices");

            // Try ProBuilder Extrude API
            var extrudeType = FindType("UnityEngine.ProBuilder.MeshOperations.ExtrudeFaces, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.ExtrudeFaces", "Unity.ProBuilder.MeshOperations.ExtrudeFaces");

            if (extrudeType != null && _faceType != null)
            {
                try
                {
                    var faces = GetFaceObjects(result.Component, faceIndicesToken);
                    if (faces == null || faces.Length == 0)
                        return ToolResponse.Fail("No valid faces found. Provide face_indices or ensure the mesh has faces.");

                    var method = extrudeType.GetMethod("Extrude",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Extrude ProBuilder Faces");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        // Create typed array
                        var typedFaces = Array.CreateInstance(_faceType, faces.Length);
                        for (int i = 0; i < faces.Length; i++) typedFaces.SetValue(faces[i], i);

                        method.Invoke(null, new object[] { result.Component, typedFaces, distance });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return VerifyEdit("extrude_faces", result.GameObject, result.Component, before,
                            after => after.Faces > before.Faces || after.Positions > before.Positions,
                            "face or vertex count to increase",
                            $"Extruded {faces.Length} face(s) by {distance} on '{result.GameObject.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Extrude failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // Fallback: manual extrusion via mesh vertices
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail($"'{result.GameObject.name}' has no mesh. ProBuilder Extrude API not available.");

            return ToolResponse.Fail(
                "ProBuilder Extrude API not available in this version. Install ProBuilder 4.x+ and ensure the package is properly imported. " +
                "Alternatively, use move_vertices to manually offset face vertices.");
        }

        private ToolResponse HandleDeleteFaces(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            var faceIndicesToken = ToolHelpers.GetOptionalArray(parameters, "face_indices");
            if (faceIndicesToken == null || faceIndicesToken.Count == 0)
                return ToolResponse.Fail("face_indices array is required for delete_faces.");

            // Try ProBuilder DeleteFaces API
            var deleteType = FindType("UnityEngine.ProBuilder.MeshOperations.DeleteElements, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.DeleteElements", "Unity.ProBuilder.MeshOperations.DeleteElements");

            if (deleteType != null && _faceType != null)
            {
                try
                {
                    var faces = GetFaceObjects(result.Component, faceIndicesToken);
                    if (faces == null || faces.Length == 0)
                        return ToolResponse.Fail("No valid faces found at the specified indices.");

                    var method = deleteType.GetMethod("DeleteFaces",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Delete ProBuilder Faces");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        var typedFaces = Array.CreateInstance(_faceType, faces.Length);
                        for (int i = 0; i < faces.Length; i++) typedFaces.SetValue(faces[i], i);

                        method.Invoke(null, new object[] { result.Component, typedFaces });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return VerifyEdit("delete_faces", result.GameObject, result.Component, before,
                            after => after.Faces < before.Faces,
                            "face count to decrease",
                            $"Deleted {faces.Length} face(s) from '{result.GameObject.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Delete faces failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // Fallback: remove triangles from Unity mesh
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail("ProBuilder DeleteFaces API not available and no mesh found.");

            var faceIndices = faceIndicesToken.Select(t => t.Value<int>()).ToList();
            ToolHelpers.RecordUndo(mesh, "Delete Mesh Faces");
            var fallbackBefore = TakeSnapshot(result.GameObject, result.Component);
            DeleteMeshFacesFallback(mesh, faceIndices);
            RefreshProBuilderMesh(result.Component);
            EditorUtility.SetDirty(mesh);
            // 回退路径按三角形删除；索引越界或被 ProBuilder 重建覆盖时三角形数量不会减少
            return VerifyEdit("delete_faces (fallback)", result.GameObject, result.Component, fallbackBefore,
                after => after.MeshIndices < fallbackBefore.MeshIndices,
                "Unity mesh triangle count to decrease (indices may be out of range, or the fallback edit was overwritten by ProBuilder's mesh rebuild)",
                $"Deleted {faceIndices.Count} face(s) from '{result.GameObject.name}' using fallback method.");
        }

        private ToolResponse HandleBevelEdges(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            float distance = ToolHelpers.GetOptionalFloat(parameters, "bevel_distance", 0.1f);
            var edgeIndicesToken = ToolHelpers.GetOptionalArray(parameters, "edge_indices");

            // Try ProBuilder Bevel API
            var bevelType = FindType("UnityEngine.ProBuilder.MeshOperations.Bevel, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.Bevel", "Unity.ProBuilder.MeshOperations.Bevel");

            if (bevelType != null && _edgeType != null)
            {
                try
                {
                    var edges = GetEdgeObjects(result.Component, edgeIndicesToken);
                    if (edges == null || edges.Length == 0)
                        return ToolResponse.Fail("No valid edges found. Provide edge_indices with {a, b} vertex index pairs.");

                    var method = bevelType.GetMethod("BevelEdges",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Bevel ProBuilder Edges");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        var typedEdges = Array.CreateInstance(_edgeType, edges.Length);
                        for (int i = 0; i < edges.Length; i++) typedEdges.SetValue(edges[i], i);

                        method.Invoke(null, new object[] { result.Component, typedEdges, distance });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return VerifyEdit("bevel_edges", result.GameObject, result.Component, before,
                            after => after.Faces > before.Faces || after.Positions > before.Positions,
                            "face or vertex count to increase (the given vertex pairs may not form edges of this mesh)",
                            $"Beveled {edges.Length} edge(s) by {distance} on '{result.GameObject.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Bevel failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            return ToolResponse.Fail(
                "ProBuilder Bevel API not available in this version. Install ProBuilder 4.x+ and ensure the package is properly imported.");
        }

        private ToolResponse HandleBridgeEdges(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            var edgeIndicesToken = ToolHelpers.GetOptionalArray(parameters, "edge_indices");
            if (edgeIndicesToken == null || edgeIndicesToken.Count < 2)
                return ToolResponse.Fail("edge_indices must contain at least 2 edges to bridge.");

            // Try ProBuilder Bridge API
            var connectType = FindType("UnityEngine.ProBuilder.MeshOperations.ConnectElements, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.ConnectElements", "Unity.ProBuilder.MeshOperations.ConnectElements");

            if (connectType != null && _edgeType != null)
            {
                try
                {
                    var edges = GetEdgeObjects(result.Component, edgeIndicesToken);
                    if (edges == null || edges.Length < 2)
                        return ToolResponse.Fail("Need at least 2 valid edges to bridge.");

                    var method = connectType.GetMethod("Bridge",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Bridge ProBuilder Edges");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        var typedEdges = Array.CreateInstance(_edgeType, edges.Length);
                        for (int i = 0; i < edges.Length; i++) typedEdges.SetValue(edges[i], i);

                        method.Invoke(null, new object[] { result.Component, typedEdges });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return VerifyEdit("bridge_edges", result.GameObject, result.Component, before,
                            after => after.Faces > before.Faces,
                            "a new bridging face (face count to increase)",
                            $"Bridged {edges.Length} edge(s) on '{result.GameObject.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Bridge edges failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            return ToolResponse.Fail(
                "ProBuilder Bridge API not available in this version. Install ProBuilder 4.x+ and ensure the package is properly imported.");
        }

        private ToolResponse HandleWeldVertices(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            float weldDistance = ToolHelpers.GetOptionalFloat(parameters, "weld_distance", 0.01f);

            // Try ProBuilder WeldVertices API
            var weldType = FindType("UnityEngine.ProBuilder.MeshOperations.MergeElements, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.MergeElements", "Unity.ProBuilder.MeshOperations.MergeElements");

            if (weldType != null)
            {
                try
                {
                    // Get all vertex indices
                    var positions = GetLocalVertices(result.Component, result.GameObject).ToList();
                    if (positions.Count == 0)
                        return ToolResponse.Fail($"'{result.GameObject.name}' has no vertices to weld.");

                    var allIndices = Enumerable.Range(0, positions.Count).ToArray();

                    var method = weldType.GetMethod("WeldVertices",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Weld ProBuilder Vertices");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        method.Invoke(null, new object[] { result.Component, allIndices, weldDistance });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        // 焊接可能合法地什么都不改（没有足够近的顶点），因此不判失败，只如实报告前后计数
                        var after = TakeSnapshot(result.GameObject, result.Component);
                        var weldMessage = after.Positions != before.Positions || after.TrianglesHash != before.TrianglesHash
                            ? $"Welded vertices within {weldDistance} distance on '{result.GameObject.name}' (vertices {before.Positions} -> {after.Positions})."
                            : $"Weld ran on '{result.GameObject.name}' but no vertices were merged (none within {weldDistance}); mesh unchanged.";
                        return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component), weldMessage);
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Weld vertices failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // Fallback: weld via Unity Mesh API
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail("ProBuilder WeldVertices API not available and no mesh found.");

            ToolHelpers.RecordUndo(mesh, "Weld Mesh Vertices");
            var fallbackBefore = TakeSnapshot(result.GameObject, result.Component);
            int welded = WeldMeshVerticesFallback(mesh, weldDistance);
            RefreshProBuilderMesh(result.Component);
            EditorUtility.SetDirty(mesh);
            if (welded == 0)
            {
                return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component),
                    $"No vertex pairs within {weldDistance} distance on '{result.GameObject.name}'; mesh unchanged (fallback method).");
            }
            // 回退路径重映射了三角形索引；若被 ProBuilder 重建覆盖，三角形会恢复原样
            return VerifyEdit("weld_vertices (fallback)", result.GameObject, result.Component, fallbackBefore,
                after => after.TrianglesHash != fallbackBefore.TrianglesHash,
                "Unity mesh triangles to be remapped (the fallback edit may have been overwritten by ProBuilder's mesh rebuild)",
                $"Welded {welded} vertex pair(s) within {weldDistance} distance on '{result.GameObject.name}' using fallback method.");
        }

        private ToolResponse HandleSetUVProjection(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            string projectionMode = ToolHelpers.GetOptionalString(parameters, "uv_projection", "planar").ToLowerInvariant();
            int uvChannel = ToolHelpers.GetOptionalInt(parameters, "uv_channel", 0);
            var faceIndicesToken = ToolHelpers.GetOptionalArray(parameters, "face_indices");

            // Try ProBuilder UV projection API
            var uvType = FindType("UnityEngine.ProBuilder.MeshOperations.UVEditing, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.UVEditing", "Unity.ProBuilder.MeshOperations.UVEditing");

            if (uvType != null && _faceType != null)
            {
                try
                {
                    var faces = GetFaceObjects(result.Component, faceIndicesToken);
                    if (faces == null || faces.Length == 0)
                    {
                        // Use all faces if none specified
                        faces = GetAllFaceObjects(result.Component);
                    }

                    if (faces == null || faces.Length == 0)
                        return ToolResponse.Fail($"No faces found on '{result.GameObject.name}'.");

                    // Try ProjectFacesBox, ProjectFacesPlanar, etc.
                    string methodName = projectionMode switch
                    {
                        "box" => "ProjectFacesBox",
                        "spherical" => "ProjectFacesSphere",
                        "cylindrical" => "ProjectFacesCylinder",
                        _ => "ProjectFacesPlanar"
                    };

                    var method = uvType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Set ProBuilder UV Projection");
                        var typedFaces = Array.CreateInstance(_faceType, faces.Length);
                        for (int i = 0; i < faces.Length; i++) typedFaces.SetValue(faces[i], i);

                        method.Invoke(null, new object[] { result.Component, typedFaces });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component),
                            $"Applied {projectionMode} UV projection to {faces.Length} face(s) on '{result.GameObject.name}'.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"UV projection failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // Fallback: generate UVs via Unity Mesh API
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail("ProBuilder UV API not available and no mesh found.");

            ToolHelpers.RecordUndo(mesh, "Generate Mesh UVs");
            GenerateUVsFallback(mesh, projectionMode, uvChannel);
            EditorUtility.SetDirty(mesh);
            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component),
                $"Applied {projectionMode} UV projection to '{result.GameObject.name}' using fallback method (UV channel {uvChannel}).");
        }

        private ToolResponse HandleTriangulate(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null)
                return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");

            // Try ProBuilder Triangulate API
            var triangulateType = FindType("UnityEngine.ProBuilder.MeshOperations.TriangulateQuads, Unity.ProBuilder",
                "UnityEngine.ProBuilder.MeshOperations.TriangulateQuads", "Unity.ProBuilder.MeshOperations.TriangulateQuads");

            if (triangulateType != null)
            {
                try
                {
                    var method = triangulateType.GetMethod("Triangulate",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        ToolHelpers.RecordUndo(result.Component, "Triangulate ProBuilder Mesh");
                        var before = TakeSnapshot(result.GameObject, result.Component);
                        method.Invoke(null, new object[] { result.Component });
                        RefreshProBuilderMesh(result.Component);
                        EditorUtility.SetDirty(result.Component);
                        // 已全是三角形的网格合法地不会变化，因此只如实报告前后面数
                        var after = TakeSnapshot(result.GameObject, result.Component);
                        return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component),
                            after.Faces != before.Faces
                                ? $"Triangulated '{result.GameObject.name}' (faces {before.Faces} -> {after.Faces})."
                                : $"Triangulate ran on '{result.GameObject.name}' but the face count did not change ({after.Faces}); the mesh may already be all triangles.");
                    }
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"Triangulate failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            // Fallback: mesh is already triangulated in Unity
            var mesh = GetMesh(result.GameObject);
            if (mesh == null)
                return ToolResponse.Fail("No mesh found to triangulate.");

            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component),
                $"'{result.GameObject.name}' mesh is already stored as triangles in Unity's mesh format. No changes needed.");
        }

        private ToolResponse HandleCombineMeshes(JObject parameters)
        {
            var names = ToolHelpers.GetRequiredString(parameters, "names")
                .Split(',')
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            var newName = ToolHelpers.GetOptionalString(parameters, "new_name", "Combined ProBuilder Mesh");

            // 逐个名字记录能否参与合并（筛选规则与以前一致），被跳过的名字如实回报
            var items = new List<ToolItemResult>();
            var filterList = new List<MeshFilter>();
            foreach (var n in names)
            {
                var go = ToolHelpers.FindGameObject(n);
                if (go == null) { items.Add(ToolItemResult.Failed(n, "GameObject not found; skipped")); continue; }
                if (FindProBuilderMesh(go) == null) { items.Add(ToolItemResult.Failed(n, "no ProBuilderMesh component; skipped")); continue; }
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) { items.Add(ToolItemResult.Failed(n, "no MeshFilter mesh; skipped")); continue; }
                filterList.Add(mf);
                items.Add(ToolItemResult.Succeeded(n));
            }
            var filters = filterList.ToArray();

            if (filters.Length < 2)
                return ToolResponse.Fail("Need at least two ProBuilder meshes to combine." +
                    (items.Any(i => !i.Ok) ? " Skipped: " + string.Join("; ", items.Where(i => !i.Ok).Select(i => $"{i.Target} ({i.Error})")) : string.Empty));

            var combines = filters.Select(mf => new CombineInstance { mesh = mf.sharedMesh, transform = mf.transform.localToWorldMatrix }).ToArray();
            var goNew = new GameObject(newName);
            ToolHelpers.RegisterCreatedObject(goNew, "Combine ProBuilder Meshes");
            var meshFilter = goNew.AddComponent<MeshFilter>();
            var meshRenderer = goNew.AddComponent<MeshRenderer>();
            var combinedMesh = new Mesh { name = newName + " Mesh" };
            combinedMesh.CombineMeshes(combines, true, true);
            meshFilter.sharedMesh = combinedMesh;
            meshRenderer.sharedMaterial = filters[0].GetComponent<Renderer>()?.sharedMaterial;
            EditorUtility.SetDirty(goNew);

            var combineData = new { name = goNew.name, sourceCount = filters.Length, vertexCount = combinedMesh.vertexCount, note = "Combined as a standard Unity mesh because ProBuilder combine editor APIs vary by version." };
            if (combinedMesh.vertexCount == 0)
                return ToolResponse.Fail($"Created '{newName}' but the combined mesh has no vertices; the empty GameObject was left in the scene.");
            return ToolResponse.FromItems($"Combined {filters.Length} ProBuilder meshes into '{newName}'", items, combineData);
        }

        private ToolResponse HandleGetVertices(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", 200);
            var vertices = GetLocalVertices(result.Component, result.GameObject).ToList();
            return ToolResponse.OkWithData(new
            {
                name = result.GameObject.name,
                count = vertices.Count,
                maxCount,
                truncated = vertices.Count > maxCount,
                vertices = vertices.Take(maxCount).Select((v, i) => new { index = i, position = ToolHelpers.Vector3ToJson(v) }).ToArray()
            }, $"Read {Math.Min(vertices.Count, maxCount)} of {vertices.Count} vertices from '{result.GameObject.name}'.");
        }

        private ToolResponse HandleMoveVertices(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var verticesArray = ToolHelpers.GetOptionalArray(parameters, "vertices");
            if (verticesArray == null || verticesArray.Count == 0) return ToolResponse.Fail("vertices array is required and must not be empty.");

            var positions = GetMutablePositions(result.Component, result.GameObject, out var source);
            if (positions == null) return ToolResponse.Fail($"Could not access mutable vertex positions on '{result.GameObject.name}'.");

            ToolHelpers.RecordUndo(result.Component, "Move ProBuilder Vertices");
            var moved = 0;
            // 逐个顶点记录：无效索引 / 缺少 position|offset 的条目以前被静默跳过，现在如实列出
            var items = new List<ToolItemResult>();
            var expected = new Dictionary<int, Vector3>();
            var requestedIndices = new List<int>();
            int entry = 0;
            foreach (var token in verticesArray.OfType<JObject>())
            {
                var label = $"vertex entry #{entry++}";
                var index = token["index"]?.Value<int>() ?? -1;
                if (index < 0 || index >= positions.Count)
                {
                    items.Add(ToolItemResult.Failed(label, $"index {index} out of range (mesh has {positions.Count} vertices)"));
                    continue;
                }
                var current = (Vector3)positions[index];
                if (token["position"] != null)
                    positions[index] = ToolHelpers.ParseVector3(token["position"], current);
                else if (token["offset"] != null)
                    positions[index] = current + ToolHelpers.ParseVector3(token["offset"], Vector3.zero);
                else
                {
                    items.Add(ToolItemResult.Failed(label, $"index {index}: neither 'position' nor 'offset' given"));
                    continue;
                }
                expected[index] = (Vector3)positions[index];
                requestedIndices.Add(index);
                items.Add(null); // 占位，写入后按实际顶点位置校验
                moved++;
            }

            WriteMutablePositions(result.Component, result.GameObject, positions, source);
            RefreshProBuilderMesh(result.Component);
            EditorUtility.SetDirty(result.Component);

            // 校验：重新读取顶点，确认每个顶点确实到达目标位置（写入可能只改了副本，或被网格重建覆盖）
            var actual = GetLocalVertices(result.Component, result.GameObject).ToList();
            int verified = 0, slot = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] != null) continue;
                var index = requestedIndices[slot++];
                var target = expected[index];
                var label = $"vertex {index}";
                if (index < actual.Count && Vector3.Distance(actual[index], target) <= VerifyTolerance)
                {
                    items[i] = ToolItemResult.Succeeded(label);
                    verified++;
                }
                else
                {
                    var now = index < actual.Count ? actual[index].ToString("F4") : "missing";
                    var want = target.ToString("F4");
                    items[i] = ToolItemResult.Failed(label, $"position did not take effect (expected {want}, actual {now})");
                }
            }

            return ToolResponse.FromItems($"Moved {verified} vertices on '{result.GameObject.name}'", items,
                new { name = result.GameObject.name, moved = verified, requested = verticesArray.Count, source });
        }

        /// <summary>编辑结果校验的位置容差（本地/世界单位）</summary>
        private const float VerifyTolerance = 1e-3f;

        /// <summary>
        /// 编辑前后的网格计数快照，用于确认修改是否真正生效。
        /// </summary>
        private struct MeshSnapshot
        {
            public int Positions;
            public int Faces;
            public int MeshVertices;
            public int MeshIndices;
            public int TrianglesHash;

            public override string ToString()
            {
                return $"positions={Positions}, faces={Faces}, meshVertices={MeshVertices}, meshTriangleIndices={MeshIndices}";
            }
        }

        private static MeshSnapshot TakeSnapshot(GameObject go, Component pb)
        {
            var mesh = GetMesh(go);
            var tris = mesh != null ? mesh.triangles : Array.Empty<int>();
            int hash = 17;
            unchecked
            {
                for (int i = 0; i < tris.Length; i++) hash = hash * 31 + tris[i];
            }
            return new MeshSnapshot
            {
                Positions = pb != null ? GetLocalVertices(pb, go).Count() : 0,
                Faces = pb != null ? GetCountProperty(pb, "faceCount", "faces") : 0,
                MeshVertices = mesh != null ? mesh.vertexCount : 0,
                MeshIndices = tris.Length,
                TrianglesHash = hash
            };
        }

        /// <summary>
        /// 编辑后重新读取网格：符合预期则返回成功（附网格信息），否则如实返回失败。
        /// 只做报告，不回滚、不阻止已经执行的修改（需要时可用 Undo 撤销）。
        /// </summary>
        private static ToolResponse VerifyEdit(string operation, GameObject go, Component pb, MeshSnapshot before,
            Func<MeshSnapshot, bool> tookEffect, string expectation, string successMessage)
        {
            var after = TakeSnapshot(go, pb);
            if (tookEffect(after))
                return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), successMessage);

            return ToolResponse.Fail(
                $"{operation} on '{go.name}' ran without an error but did not take effect: expected {expectation}. " +
                $"Before: {before}. After: {after}.");
        }

        private static bool IsAvailable()
        {
            if (_reflectionInitialized) return _proBuilderMeshType != null;
            _reflectionInitialized = true;
            _proBuilderMeshType = FindType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder", "UnityEngine.ProBuilder.ProBuilderMesh", "Unity.ProBuilder.ProBuilderMesh");
            _shapeGeneratorType = FindType("UnityEngine.ProBuilder.ShapeGenerator, Unity.ProBuilder", "UnityEngine.ProBuilder.ShapeGenerator", "Unity.ProBuilder.ShapeGenerator");
            _shapeTypeType = FindType("UnityEngine.ProBuilder.ShapeType, Unity.ProBuilder", "UnityEngine.ProBuilder.ShapeType", "Unity.ProBuilder.ShapeType");
            _pivotLocationType = FindType("UnityEngine.ProBuilder.PivotLocation, Unity.ProBuilder", "UnityEngine.ProBuilder.PivotLocation", "Unity.ProBuilder.PivotLocation");
            _faceType = FindType("UnityEngine.ProBuilder.Face, Unity.ProBuilder", "UnityEngine.ProBuilder.Face", "Unity.ProBuilder.Face");
            _edgeType = FindType("UnityEngine.ProBuilder.Edge, Unity.ProBuilder", "UnityEngine.ProBuilder.Edge", "Unity.ProBuilder.Edge");
            return _proBuilderMeshType != null;
        }

        private static Type FindType(params string[] names)
        {
            foreach (var name in names)
            {
                var type = Type.GetType(name);
                if (type != null) return type;
            }
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            foreach (var name in names.Select(n => n.Split(',')[0].Trim()))
            {
                var type = asm.GetType(name);
                if (type != null) return type;
            }
            return null;
        }

        private static Component FindProBuilderMesh(GameObject go)
        {
            return go != null && _proBuilderMeshType != null ? go.GetComponent(_proBuilderMeshType) : null;
        }

        private static (string Name, GameObject GameObject, Component Component) ResolveProBuilder(string name)
        {
            var go = ToolHelpers.FindGameObject(name);
            return (name, go, FindProBuilderMesh(go));
        }

        private static object ParseShapeType(string shape)
        {
            if (_shapeTypeType == null) return null;
            var mapped = shape.ToLowerInvariant() == "stairs" ? "Stair" : char.ToUpperInvariant(shape[0]) + shape.Substring(1).ToLowerInvariant();
            return Enum.TryParse(_shapeTypeType, mapped, true, out var value) ? value : null;
        }

        private static object TryCreateShape(object shapeValue)
        {
            if (_shapeGeneratorType == null) return null;
            var methods = _shapeGeneratorType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateShape").ToArray();
            foreach (var method in methods)
            {
                var ps = method.GetParameters();
                try
                {
                    if (ps.Length == 1 && ps[0].ParameterType == _shapeTypeType)
                        return method.Invoke(null, new[] { shapeValue });
                    if (ps.Length == 2 && ps[0].ParameterType == _shapeTypeType && _pivotLocationType != null)
                    {
                        var pivot = Enum.GetValues(_pivotLocationType).GetValue(0);
                        return method.Invoke(null, new[] { shapeValue, pivot });
                    }
                }
                catch (TargetInvocationException) { }
                catch (ArgumentException) { }
                catch (TargetParameterCountException) { }
            }
            return null;
        }

        private static object SerializeMeshInfo(GameObject go, Component pb)
        {
            var mesh = GetMesh(go);
            var positions = GetLocalVertices(pb, go).ToList();
            var faces = GetCountProperty(pb, "faceCount", "faces");
            var edges = GetCountProperty(pb, "edgeCount", "edges");
            var renderer = go.GetComponent<Renderer>();
            return new
            {
                name = go.name,
                componentType = pb.GetType().FullName,
                position = ToolHelpers.Vector3ToJson(go.transform.position),
                rotation = ToolHelpers.QuaternionToJson(go.transform.rotation),
                scale = ToolHelpers.Vector3ToJson(go.transform.localScale),
                vertices = positions.Count > 0 ? positions.Count : mesh?.vertexCount ?? 0,
                faces,
                edges,
                materials = renderer != null ? renderer.sharedMaterials.Select(m => m != null ? m.name : null).ToArray() : Array.Empty<string>(),
                bounds = mesh != null ? new { center = ToolHelpers.Vector3ToJson(mesh.bounds.center), size = ToolHelpers.Vector3ToJson(mesh.bounds.size) } : null
            };
        }

        private static int GetCountProperty(Component pb, string countName, string collectionName)
        {
            var prop = pb.GetType().GetProperty(countName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(int)) return (int)prop.GetValue(pb);
            var collection = pb.GetType().GetProperty(collectionName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(pb) as ICollection;
            return collection?.Count ?? 0;
        }

        private static Mesh GetMesh(GameObject go)
        {
            return go.GetComponent<MeshFilter>()?.sharedMesh;
        }

        private static IEnumerable<Vector3> GetLocalVertices(Component pb, GameObject go)
        {
            var positions = pb.GetType().GetProperty("positions", BindingFlags.Public | BindingFlags.Instance)?.GetValue(pb) as IEnumerable;
            if (positions != null)
            {
                foreach (var item in positions)
                    if (item is Vector3 v) yield return v;
                yield break;
            }

            var mesh = GetMesh(go);
            if (mesh == null) yield break;
            foreach (var v in mesh.vertices) yield return v;
        }

        private static IList GetMutablePositions(Component pb, GameObject go, out string source)
        {
            var prop = pb.GetType().GetProperty("positions", BindingFlags.Public | BindingFlags.Instance);
            var value = prop?.GetValue(pb) as IList;
            if (value != null)
            {
                source = "ProBuilderMesh.positions";
                return value;
            }

            var mesh = GetMesh(go);
            if (mesh != null)
            {
                source = "MeshFilter.sharedMesh.vertices";
                return mesh.vertices.Cast<object>().ToList();
            }

            source = null;
            return null;
        }

        private static void WriteMutablePositions(Component pb, GameObject go, IList positions, string source)
        {
            if (source == "MeshFilter.sharedMesh.vertices")
            {
                var mesh = GetMesh(go);
                mesh.vertices = positions.Cast<Vector3>().ToArray();
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
            }
        }

        private static void RefreshProBuilderMesh(Component pb)
        {
            var type = pb.GetType();
            type.GetMethod("ToMesh", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(pb, null);
            type.GetMethod("Refresh", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(pb, null);
        }

        private static void SetPivotPreserveGeometry(GameObject go, Mesh mesh, Vector3 worldPivot)
        {
            ToolHelpers.RecordUndo(go.transform, "Set ProBuilder Pivot");
            ToolHelpers.RecordUndo(mesh, "Adjust ProBuilder Vertices For Pivot");
            var oldWorld = go.transform.position;
            var deltaLocal = go.transform.InverseTransformVector(worldPivot - oldWorld);
            var vertices = mesh.vertices;
            for (var i = 0; i < vertices.Length; i++) vertices[i] -= deltaLocal;
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
            go.transform.position = worldPivot;
            EditorUtility.SetDirty(mesh);
            EditorUtility.SetDirty(go.transform);
        }

        /// <summary>
        /// Gets face data from a ProBuilderMesh component.
        /// </summary>
        private static List<object> GetFacesData(Component pb, int maxCount, out int totalFaces)
        {
            totalFaces = 0;
            var result = new List<object>();

            var facesProperty = pb.GetType().GetProperty("faces", BindingFlags.Public | BindingFlags.Instance);
            if (facesProperty == null)
            {
                // Try faceCount
                totalFaces = GetCountProperty(pb, "faceCount", "faces");
                return result;
            }

            var facesValue = facesProperty.GetValue(pb) as System.Collections.IEnumerable;
            if (facesValue == null) return result;

            int index = 0;
            foreach (var face in facesValue)
            {
                totalFaces++;
                if (index >= maxCount) continue;

                var faceObj = new JObject { ["index"] = index };

                // Try to get indices/triangles from face
                var indicesProp = face.GetType().GetProperty("indexes", BindingFlags.Public | BindingFlags.Instance)
                               ?? face.GetType().GetProperty("indices", BindingFlags.Public | BindingFlags.Instance);
                if (indicesProp != null)
                {
                    var indices = indicesProp.GetValue(face) as System.Collections.IEnumerable;
                    if (indices != null)
                    {
                        var arr = new JArray();
                        foreach (var idx in indices) arr.Add(idx);
                        faceObj["indices"] = arr;
                    }
                }

                // Try to get material index
                var matProp = face.GetType().GetProperty("submeshIndex", BindingFlags.Public | BindingFlags.Instance)
                           ?? face.GetType().GetProperty("materialIndex", BindingFlags.Public | BindingFlags.Instance);
                if (matProp != null)
                    faceObj["submeshIndex"] = JToken.FromObject(matProp.GetValue(face));

                result.Add(faceObj);
                index++;
            }

            return result;
        }

        /// <summary>
        /// Gets face objects from a ProBuilderMesh for use in API calls.
        /// </summary>
        private static object[] GetFaceObjects(Component pb, JArray faceIndicesToken)
        {
            var facesProperty = pb.GetType().GetProperty("faces", BindingFlags.Public | BindingFlags.Instance);
            if (facesProperty == null) return null;

            var facesValue = facesProperty.GetValue(pb) as System.Collections.IList;
            if (facesValue == null) return null;

            if (faceIndicesToken == null || faceIndicesToken.Count == 0)
                return null;

            var result = new List<object>();
            foreach (var token in faceIndicesToken)
            {
                int idx = token.Value<int>();
                if (idx >= 0 && idx < facesValue.Count)
                    result.Add(facesValue[idx]);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Gets all face objects from a ProBuilderMesh.
        /// </summary>
        private static object[] GetAllFaceObjects(Component pb)
        {
            var facesProperty = pb.GetType().GetProperty("faces", BindingFlags.Public | BindingFlags.Instance);
            if (facesProperty == null) return null;

            var facesValue = facesProperty.GetValue(pb) as System.Collections.IEnumerable;
            if (facesValue == null) return null;

            return facesValue.Cast<object>().ToArray();
        }

        /// <summary>
        /// Gets edge objects from a ProBuilderMesh for use in API calls.
        /// </summary>
        private static object[] GetEdgeObjects(Component pb, JArray edgeIndicesToken)
        {
            if (edgeIndicesToken == null || edgeIndicesToken.Count == 0) return null;
            if (_edgeType == null) return null;

            var result = new List<object>();
            foreach (var token in edgeIndicesToken.OfType<JObject>())
            {
                int a = token["a"]?.Value<int>() ?? -1;
                int b = token["b"]?.Value<int>() ?? -1;
                if (a < 0 || b < 0) continue;

                // Create Edge(int a, int b) via constructor
                var ctor = _edgeType.GetConstructor(new[] { typeof(int), typeof(int) });
                if (ctor != null)
                    result.Add(ctor.Invoke(new object[] { a, b }));
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        /// <summary>
        /// Fallback mesh subdivision: splits each triangle into 4 smaller triangles.
        /// </summary>
        private static void SubdivideMeshFallback(Mesh mesh)
        {
            var oldVerts = mesh.vertices;
            var oldTris = mesh.triangles;
            var oldUVs = mesh.uv;
            var oldNormals = mesh.normals;

            var newVerts = new List<Vector3>(oldVerts);
            var newUVs = new List<Vector2>(oldUVs.Length > 0 ? oldUVs : new Vector2[oldVerts.Length]);
            var newNormals = new List<Vector3>(oldNormals.Length > 0 ? oldNormals : new Vector3[oldVerts.Length]);
            var newTris = new List<int>();

            var midpointCache = new Dictionary<long, int>();

            for (int i = 0; i < oldTris.Length; i += 3)
            {
                int i0 = oldTris[i], i1 = oldTris[i + 1], i2 = oldTris[i + 2];
                int m01 = GetMidpoint(i0, i1, newVerts, newUVs, newNormals, midpointCache);
                int m12 = GetMidpoint(i1, i2, newVerts, newUVs, newNormals, midpointCache);
                int m20 = GetMidpoint(i2, i0, newVerts, newUVs, newNormals, midpointCache);

                newTris.AddRange(new[] { i0, m01, m20 });
                newTris.AddRange(new[] { i1, m12, m01 });
                newTris.AddRange(new[] { i2, m20, m12 });
                newTris.AddRange(new[] { m01, m12, m20 });
            }

            mesh.vertices = newVerts.ToArray();
            mesh.triangles = newTris.ToArray();
            if (oldUVs.Length > 0) mesh.uv = newUVs.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        private static int GetMidpoint(int a, int b, List<Vector3> verts, List<Vector2> uvs, List<Vector3> normals, Dictionary<long, int> cache)
        {
            long key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
            if (cache.TryGetValue(key, out int existing)) return existing;

            int idx = verts.Count;
            verts.Add((verts[a] + verts[b]) * 0.5f);
            if (uvs.Count > a && uvs.Count > b)
                uvs.Add((uvs[a] + uvs[b]) * 0.5f);
            else
                uvs.Add(Vector2.zero);
            if (normals.Count > a && normals.Count > b)
                normals.Add(Vector3.Normalize(normals[a] + normals[b]));
            else
                normals.Add(Vector3.up);

            cache[key] = idx;
            return idx;
        }

        /// <summary>
        /// Fallback: removes triangles at the specified face indices from a Unity Mesh.
        /// </summary>
        private static void DeleteMeshFacesFallback(Mesh mesh, List<int> faceIndices)
        {
            var tris = mesh.triangles.ToList();
            var toRemove = new HashSet<int>(faceIndices);
            var newTris = new List<int>();

            for (int i = 0; i < tris.Count / 3; i++)
            {
                if (!toRemove.Contains(i))
                {
                    newTris.Add(tris[i * 3]);
                    newTris.Add(tris[i * 3 + 1]);
                    newTris.Add(tris[i * 3 + 2]);
                }
            }

            mesh.triangles = newTris.ToArray();
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
        }

        /// <summary>
        /// Fallback: welds vertices within a given distance using Unity Mesh API.
        /// Returns the number of vertex pairs welded.
        /// </summary>
        private static int WeldMeshVerticesFallback(Mesh mesh, float distance)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            var remap = new int[verts.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = i;

            int welded = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                if (remap[i] != i) continue;
                for (int j = i + 1; j < verts.Length; j++)
                {
                    if (remap[j] != j) continue;
                    if (Vector3.Distance(verts[i], verts[j]) <= distance)
                    {
                        remap[j] = i;
                        welded++;
                    }
                }
            }

            if (welded == 0) return 0;

            for (int i = 0; i < tris.Length; i++)
                tris[i] = remap[tris[i]];

            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return welded;
        }

        /// <summary>
        /// Fallback: generates UVs for a mesh using a simple projection method.
        /// </summary>
        private static void GenerateUVsFallback(Mesh mesh, string projectionMode, int uvChannel)
        {
            var verts = mesh.vertices;
            var uvs = new Vector2[verts.Length];

            switch (projectionMode)
            {
                case "spherical":
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var n = verts[i].normalized;
                        uvs[i] = new Vector2(
                            0.5f + Mathf.Atan2(n.z, n.x) / (2f * Mathf.PI),
                            0.5f - Mathf.Asin(n.y) / Mathf.PI);
                    }
                    break;
                case "cylindrical":
                    var bounds = mesh.bounds;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var local = verts[i] - bounds.center;
                        uvs[i] = new Vector2(
                            0.5f + Mathf.Atan2(local.z, local.x) / (2f * Mathf.PI),
                            (local.y / bounds.size.y) + 0.5f);
                    }
                    break;
                case "box":
                    var b = mesh.bounds;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var local = verts[i] - b.center;
                        var abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                        if (abs.x >= abs.y && abs.x >= abs.z)
                            uvs[i] = new Vector2(local.z / b.size.z + 0.5f, local.y / b.size.y + 0.5f);
                        else if (abs.y >= abs.x && abs.y >= abs.z)
                            uvs[i] = new Vector2(local.x / b.size.x + 0.5f, local.z / b.size.z + 0.5f);
                        else
                            uvs[i] = new Vector2(local.x / b.size.x + 0.5f, local.y / b.size.y + 0.5f);
                    }
                    break;
                default: // planar
                    var pb = mesh.bounds;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        uvs[i] = new Vector2(
                            (verts[i].x - pb.min.x) / pb.size.x,
                            (verts[i].z - pb.min.z) / pb.size.z);
                    }
                    break;
            }

            if (uvChannel == 0)
                mesh.uv = uvs;
            else if (uvChannel == 1)
                mesh.uv2 = uvs;
            else if (uvChannel == 2)
                mesh.uv3 = uvs;
            else if (uvChannel == 3)
                mesh.uv4 = uvs;
        }
    }
}
