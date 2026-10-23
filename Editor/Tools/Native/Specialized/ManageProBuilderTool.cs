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
    /// <remarks>
    /// 所有编辑都只改 ProBuilder 自身数据（positions / faces / UV），随后 ToMesh() + Refresh() 重建 Unity Mesh。
    /// 不再直接改 MeshFilter.sharedMesh：ProBuilder 下一次 ToMesh() 会按自身数据重建网格，直接改 sharedMesh 的结果会被静默丢弃，
    /// 或让 Unity Mesh 与 ProBuilder 数据失去同步。API 签名以 ProBuilder 5.x（com.unity.probuilder 5.2.4）为准；
    /// 反射找不到对应 API 时如实返回失败，不再退回到会被覆盖的 sharedMesh 修改。
    /// </remarks>
    [AgentTool("manage_probuilder",
        Description = "ProBuilder in-editor mesh modeling — create and edit meshes without external 3D tools. " +
                      "Actions: create_shape (cube/cylinder/sphere/plane/stairs/arch/door/pipe/cone/torus/prism), " +
                      "get_info, get_vertices, get_faces, move_vertices, extrude_faces, delete_faces, bevel_edges, bridge_edges, " +
                      "weld_vertices, subdivide, triangulate, flip_normals, center_pivot, set_material, " +
                      "set_uv_projection (planar/box/spherical/cylindrical), combine_meshes. " +
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
                ""shape"": { ""type"": ""string"", ""description"": ""Shape: cube, sphere, cylinder, cone, plane, stairs, door, arch, pipe, torus, prism"" },
                ""name"": { ""type"": ""string"", ""description"": ""Target or new GameObject name"" },
                ""names"": { ""type"": ""string"", ""description"": ""Comma-separated GameObject names for combine_meshes"" },
                ""new_name"": { ""type"": ""string"", ""description"": ""New combined object name"" },
                ""position"": { ""type"": ""object"", ""description"": ""Vector3 {x,y,z}; create position, or world-space pivot position for center_pivot"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""rotation"": { ""type"": ""object"", ""description"": ""Euler rotation {x,y,z} (degrees)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""size"": { ""type"": ""object"", ""description"": ""Vector3 size/scale {x,y,z}"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""material_path"": { ""type"": ""string"" },
                ""iterations"": { ""type"": ""integer"" },
                ""max_count"": { ""type"": ""integer"" },
                ""vertices"": { ""type"": ""array"", ""description"": ""move_vertices entries. index is a get_vertices index; vertices sharing that position (coincident vertices of adjacent faces) move together so the surface stays connected."", ""items"": { ""type"": ""object"", ""properties"": { ""index"": { ""type"": ""integer"" }, ""position"": { ""type"": ""object"", ""description"": ""Absolute local-space vertex position (Vector3)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } }, ""offset"": { ""type"": ""object"", ""description"": ""Local-space vertex position offset (Vector3)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } } } } } },
                ""face_indices"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" }, ""description"": ""Face indices (from get_faces) for extrude_faces, delete_faces, set_uv_projection, subdivide (optional: all faces)"" },
                ""edge_indices"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""a"": { ""type"": ""integer"" }, ""b"": { ""type"": ""integer"" } } }, ""description"": ""Edge vertex index pairs for bevel_edges; bridge_edges bridges consecutive pairs of edges (0-1, 2-3, ...)"" },
                ""extrude_distance"": { ""type"": ""number"", ""description"": ""Extrusion distance for extrude_faces (default: 0.5)"" },
                ""extrude_method"": { ""type"": ""string"", ""description"": ""extrude_faces method: face_normal (default), vertex_normal, individual"" },
                ""bevel_distance"": { ""type"": ""number"", ""description"": ""Bevel distance/amount for bevel_edges (default: 0.1)"" },
                ""weld_distance"": { ""type"": ""number"", ""description"": ""Maximum distance to weld vertices together (default: 0.01)"" },
                ""uv_projection"": { ""type"": ""string"", ""description"": ""UV projection mode: planar, box, spherical, cylindrical (default: planar)"" },
                ""uv_channel"": { ""type"": ""integer"", ""description"": ""UV channel index 0-3 (default: 0)"" }
            },
            ""required"": [""action""]
        }");

        private static bool _reflectionInitialized;
        private static Type _proBuilderMeshType;
        private static Type _shapeGeneratorType;
        private static Type _shapeTypeType;
        private static Type _pivotLocationType;
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
                // 反射调用抛出的是 TargetInvocationException，真正原因在 InnerException
                response = ToolResponse.Fail($"Error: {Unwrap(ex).Message}");
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
            if (shapeValue == null) return ToolResponse.Fail($"Unsupported shape '{shape}'. Try cube, sphere, cylinder, cone, plane, stairs, door, arch, pipe, torus, or prism.");

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
            // CreateShape 自身已构建网格；这里重建失败只影响刷新，不影响形状数据，因此只附注不判失败
            var rebuilt = TryRebuildMesh(component, out var rebuildError);
            EditorUtility.SetDirty(go);

            // 校验：生成的网格必须有顶点，否则物体虽已创建但形状实际为空
            var created = TakeSnapshot(go, component);
            if (created.Positions == 0 && created.MeshVertices == 0)
                return ToolResponse.Fail($"Created GameObject '{name}' but the generated ProBuilder {shape} has no vertices (empty mesh). The empty GameObject was left in the scene.");

            return ToolResponse.OkWithData(SerializeMeshInfo(go, component),
                $"Created ProBuilder {shape} '{name}'." + (rebuilt ? string.Empty : $" Note: mesh refresh after placement failed ({rebuildError})."));
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component), $"ProBuilder info for '{result.GameObject.name}'.");
        }

        private ToolResponse HandleSetMaterial(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var materialPath = ToolHelpers.GetRequiredString(parameters, "material_path");
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) return ToolResponse.Fail($"Material not found at path: {materialPath}");

            var go = result.GameObject;
            var pb = result.Component;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return ToolResponse.Fail($"'{go.name}' has no Renderer component.");

            // ProBuilder 按面记录 submeshIndex，ToMesh() 依此重排子网格与材质槽。
            // 只改 renderer.sharedMaterial 在多材质网格上只会换掉第 0 槽，因此优先用 ProBuilderMesh.SetMaterial(faces, material)
            var faces = GetAllFaceObjects(pb);
            var setMaterial = FindInstanceMethod(_proBuilderMeshType, "SetMaterial", FaceArrayType, typeof(Material));
            if (setMaterial != null && faces != null && faces.Length > 0)
            {
                ToolHelpers.RecordUndo(pb, "Set ProBuilder Material");
                ToolHelpers.RecordUndo(renderer, "Set ProBuilder Material");
                try
                {
                    InvokeWithDefaults(setMaterial, pb, ToTypedArray(faces, _faceType), material);
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"set_material failed inside ProBuilder (ProBuilderMesh.SetMaterial): {Unwrap(ex).Message}");
                }
                if (!TryRebuildMesh(pb, out var rebuildError))
                    return RebuildFailed("set_material", go, pb, rebuildError);
                EditorUtility.SetDirty(pb);
                EditorUtility.SetDirty(renderer);
            }
            else
            {
                ToolHelpers.RecordUndo(renderer, "Set ProBuilder Material");
                renderer.sharedMaterial = material;
                EditorUtility.SetDirty(renderer);
            }

            // 校验：每个面所映射的材质槽都应是目标材质
            var mats = renderer.sharedMaterials;
            var wrongFaces = 0;
            if (faces != null && mats.Length > 0)
            {
                foreach (var face in faces)
                {
                    var submesh = GetIntProperty(face, "submeshIndex", 0);
                    if (mats[Mathf.Clamp(submesh, 0, mats.Length - 1)] != material) wrongFaces++;
                }
            }
            if (mats.Length == 0 || mats[0] != material && (faces == null || wrongFaces > 0))
                return ToolResponse.Fail($"Assigned material '{materialPath}' to '{go.name}', but the renderer still reports [{string.Join(", ", mats.Select(m => m != null ? m.name : "null"))}].");
            if (wrongFaces > 0)
                return ToolResponse.Partial($"Assigned material '{materialPath}' to '{go.name}'",
                    new[]
                    {
                        ToolItemResult.Succeeded("material slot 0"),
                        ToolItemResult.Failed("other submeshes", $"{wrongFaces} of {faces.Length} face(s) still use a different material slot (ProBuilderMesh.SetMaterial was unavailable)")
                    }, SerializeMeshInfo(go, pb));
            return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), $"Assigned material '{materialPath}' to '{go.name}'.");
        }

        private ToolResponse HandleCenterPivot(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;
            var positionsProp = GetPositionsProperty();
            var localBefore = ReadPositions(pb);
            if (positionsProp == null || !positionsProp.CanWrite || localBefore == null)
                return ApiMissing("center_pivot", "ProBuilderMesh.positions { get; set; }");
            if (localBefore.Length == 0)
                return ToolResponse.Fail($"'{go.name}' has no vertices; cannot compute a pivot.");

            // 以 ProBuilder positions 为准：先记下每个顶点的世界坐标，移动 Transform 后再换算回新的本地坐标，
            // 写回 positions 后 ToMesh()/Refresh()。旧实现改 sharedMesh 顶点，会被 ToMesh() 按旧 positions 覆盖。
            var t = go.transform;
            var localBounds = ComputeBounds(localBefore);
            var newPivot = parameters["position"] != null
                ? ToolHelpers.ParseVector3(parameters["position"])
                : t.TransformPoint(localBounds.center);
            var worldBefore = localBefore.Select(p => t.TransformPoint(p)).ToArray();

            ToolHelpers.RecordUndo(t, "Set ProBuilder Pivot");
            ToolHelpers.RecordUndo(pb, "Set ProBuilder Pivot");
            t.position = newPivot;
            var localAfter = worldBefore.Select(p => t.InverseTransformPoint(p)).ToArray();
            try
            {
                positionsProp.SetValue(pb, localAfter);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"center_pivot moved the transform of '{go.name}' but could not write ProBuilderMesh.positions: {Unwrap(ex).Message}. Use Undo to revert.");
            }
            if (!TryRebuildMesh(pb, out var rebuildError))
                return RebuildFailed("center_pivot", go, pb, rebuildError);
            EditorUtility.SetDirty(pb);
            EditorUtility.SetDirty(t);

            // 校验：pivot 到达目标位置，且所有顶点在世界空间中保持不动
            var pivotError = Vector3.Distance(t.position, newPivot);
            var actual = ReadPositions(pb) ?? Array.Empty<Vector3>();
            var geometryShift = actual.Length == worldBefore.Length
                ? actual.Select((p, i) => Vector3.Distance(t.TransformPoint(p), worldBefore[i])).DefaultIfEmpty(0f).Max()
                : float.PositiveInfinity;
            if (pivotError > VerifyTolerance || geometryShift > VerifyTolerance)
            {
                return ToolResponse.Fail(
                    $"center_pivot on '{go.name}' did not take effect as intended: pivot is {pivotError:F4} away from {newPivot}, " +
                    $"and vertices moved up to {geometryShift:F4} units in world space (expected 0). Use Undo to revert.");
            }
            return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), $"Set pivot for '{go.name}' to {newPivot} (geometry unchanged in world space).");
        }

        private ToolResponse HandleFlipNormals(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            // 反转 ProBuilder 面的三角形环绕顺序（Face.Reverse()），再重建网格；
            // 旧实现直接改 sharedMesh.triangles，下次 ToMesh() 就会被还原
            var reverse = _faceType?.GetMethod("Reverse", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (reverse == null) return ApiMissing("flip_normals", "Face.Reverse()");
            var faces = GetAllFaceObjects(pb);
            if (faces == null || faces.Length == 0) return ToolResponse.Fail($"'{go.name}' has no faces to flip.");

            ToolHelpers.RecordUndo(pb, "Flip ProBuilder Normals");
            var before = TakeSnapshot(go, pb);
            try
            {
                foreach (var face in faces) reverse.Invoke(face, null);
            }
            catch (Exception ex)
            {
                TryRebuildMesh(pb, out _);
                return ToolResponse.Fail($"flip_normals failed inside ProBuilder (Face.Reverse): {Unwrap(ex).Message}. Use Undo to revert any partial change.");
            }
            if (!TryRebuildMesh(pb, out var rebuildError))
                return RebuildFailed("flip_normals", go, pb, rebuildError);
            EditorUtility.SetDirty(pb);

            var after = TakeSnapshot(go, pb);
            if (before.MeshIndices > 0 && after.TrianglesHash == before.TrianglesHash)
                return ToolResponse.Fail($"flip_normals on '{go.name}' ran without an error but the mesh triangles did not change. Before: {before}. After: {after}.");
            return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), $"Flipped normals of {faces.Length} face(s) on '{go.name}'.");
        }

        private ToolResponse HandleSubdivide(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            // ProBuilder 5.x 的 Subdivision 是 internal 类，其实现就是 ConnectElements.Connect(mesh, faces)（公开 API）
            var items = new List<ToolItemResult>();
            var faces = SelectFacesOrAll(pb, ToolHelpers.GetOptionalArray(parameters, "face_indices"), items);
            if (faces.Length == 0) return NoValidFaces("subdivide", go, items);

            var method = FindStaticMethod(FindProBuilderType("MeshOperations.ConnectElements"), "Connect", _proBuilderMeshType, FaceArrayType);
            var error = InvokeAndRebuild("subdivide", go, pb, method, "ConnectElements.Connect(ProBuilderMesh, IEnumerable<Face>)",
                new object[] { pb, ToTypedArray(faces, _faceType) }, out var returned, out var before, out var after);
            if (error != null) return error;
            if (returned == null || !(after.Faces > before.Faces || after.Positions > before.Positions))
                return NotApplied("subdivide", go, "face or vertex count to increase", before, after);
            return Finish($"Subdivided {faces.Length} face(s) on '{go.name}' (faces {before.Faces} -> {after.Faces})", items, go, pb);
        }

        private ToolResponse HandleGetFaces(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);

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
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            float distance = ToolHelpers.GetOptionalFloat(parameters, "extrude_distance", 0.5f);
            var faceIndicesToken = ToolHelpers.GetOptionalArray(parameters, "face_indices");
            if (faceIndicesToken == null || faceIndicesToken.Count == 0)
                return ToolResponse.Fail("face_indices array is required for extrude_faces (use get_faces to list face indices).");

            var methodName = ToolHelpers.GetOptionalString(parameters, "extrude_method", "face_normal");
            var extrudeMethodType = FindProBuilderType("ExtrudeMethod");
            var extrudeMethod = ParseExtrudeMethod(extrudeMethodType, methodName);
            if (extrudeMethodType != null && extrudeMethod == null)
                return ToolResponse.Fail($"Unknown extrude_method '{methodName}'. Use face_normal, vertex_normal, or individual.");

            var items = new List<ToolItemResult>();
            var faces = SelectFaces(pb, faceIndicesToken, items);
            if (faces.Length == 0) return NoValidFaces("extrude_faces", go, items);

            // ProBuilder 5.x：ExtrudeElements.Extrude(ProBuilderMesh, IEnumerable<Face>, ExtrudeMethod, float)
            // （旧实现找的是不存在的 ExtrudeFaces 类型，且同名重载会让 GetMethod 抛 AmbiguousMatchException）
            var method = FindStaticMethod(FindProBuilderType("MeshOperations.ExtrudeElements"), "Extrude",
                _proBuilderMeshType, FaceArrayType, extrudeMethodType, typeof(float));
            var error = InvokeAndRebuild("extrude_faces", go, pb, method, "ExtrudeElements.Extrude(ProBuilderMesh, IEnumerable<Face>, ExtrudeMethod, float)",
                new[] { pb, ToTypedArray(faces, _faceType), extrudeMethod, distance }, out var returned, out var before, out var after);
            if (error != null) return error;
            if (returned == null || !(after.Faces > before.Faces || after.Positions > before.Positions))
                return NotApplied("extrude_faces", go, "face or vertex count to increase", before, after);
            return Finish($"Extruded {faces.Length} face(s) by {distance} ({methodName}) on '{go.name}' (faces {before.Faces} -> {after.Faces})", items, go, pb);
        }

        private ToolResponse HandleDeleteFaces(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            var faceIndicesToken = ToolHelpers.GetOptionalArray(parameters, "face_indices");
            if (faceIndicesToken == null || faceIndicesToken.Count == 0)
                return ToolResponse.Fail("face_indices array is required for delete_faces.");

            var items = new List<ToolItemResult>();
            var faces = SelectFaces(pb, faceIndicesToken, items);
            if (faces.Length == 0) return NoValidFaces("delete_faces", go, items);

            // DeleteFaces 有 IEnumerable<Face> / IList<int> 两个重载，按参数类型精确选择
            var method = FindStaticMethod(FindProBuilderType("MeshOperations.DeleteElements"), "DeleteFaces", _proBuilderMeshType, FaceArrayType);
            var error = InvokeAndRebuild("delete_faces", go, pb, method, "DeleteElements.DeleteFaces(ProBuilderMesh, IEnumerable<Face>)",
                new object[] { pb, ToTypedArray(faces, _faceType) }, out _, out var before, out var after);
            if (error != null) return error;
            if (!(after.Faces < before.Faces))
                return NotApplied("delete_faces", go, "face count to decrease", before, after);
            return Finish($"Deleted {before.Faces - after.Faces} face(s) from '{go.name}' (faces {before.Faces} -> {after.Faces})", items, go, pb);
        }

        private ToolResponse HandleBevelEdges(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            float distance = ToolHelpers.GetOptionalFloat(parameters, "bevel_distance", 0.1f);
            var edgeIndicesToken = ToolHelpers.GetOptionalArray(parameters, "edge_indices");
            if (edgeIndicesToken == null || edgeIndicesToken.Count == 0)
                return ToolResponse.Fail("edge_indices array is required for bevel_edges ({a, b} vertex index pairs from get_vertices/get_faces).");
            if (_edgeType == null) return ApiMissing("bevel_edges", "UnityEngine.ProBuilder.Edge");

            var items = new List<ToolItemResult>();
            var edges = SelectEdges(pb, edgeIndicesToken, items);
            if (edges.Count == 0) return NoValidEdges("bevel_edges", go, items);

            var method = FindStaticMethod(FindProBuilderType("MeshOperations.Bevel"), "BevelEdges", _proBuilderMeshType, EdgeArrayType, typeof(float));
            var error = InvokeAndRebuild("bevel_edges", go, pb, method, "Bevel.BevelEdges(ProBuilderMesh, IList<Edge>, float)",
                new object[] { pb, ToTypedArray(edges.Select(e => e.Edge).ToArray(), _edgeType), distance }, out var returned, out var before, out var after);
            if (error != null) return error;
            // BevelEdges 失败时返回 null（例如顶点对不是该网格的边）
            if (returned == null || !(after.Faces > before.Faces || after.Positions > before.Positions))
                return NotApplied("bevel_edges", go, "face or vertex count to increase (the given vertex pairs may not form edges of this mesh)", before, after);
            return Finish($"Beveled {edges.Count} edge(s) by {distance} on '{go.name}' (faces {before.Faces} -> {after.Faces})", items, go, pb);
        }

        private ToolResponse HandleBridgeEdges(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            var edgeIndicesToken = ToolHelpers.GetOptionalArray(parameters, "edge_indices");
            if (edgeIndicesToken == null || edgeIndicesToken.Count < 2)
                return ToolResponse.Fail("edge_indices must contain at least 2 edges to bridge.");
            if (_edgeType == null) return ApiMissing("bridge_edges", "UnityEngine.ProBuilder.Edge");

            // ProBuilder 5.x：AppendElements.Bridge(ProBuilderMesh, Edge, Edge, bool allowNonManifoldGeometry = false)
            // 一次只桥接两条边；旧实现找的是不存在的 ConnectElements.Bridge(mesh, Edge[])
            var method = FindStaticMethod(FindProBuilderType("MeshOperations.AppendElements"), "Bridge", _proBuilderMeshType, _edgeType, _edgeType);
            if (method == null) return ApiMissing("bridge_edges", "AppendElements.Bridge(ProBuilderMesh, Edge, Edge, bool)");

            var items = new List<ToolItemResult>();
            var edges = SelectEdges(pb, edgeIndicesToken, items, reportValid: false);
            ToolHelpers.RecordUndo(pb, "Bridge ProBuilder Edges");
            var before = TakeSnapshot(go, pb);
            int bridged = 0;
            for (int i = 0; i < edges.Count; i += 2)
            {
                if (i + 1 >= edges.Count)
                {
                    items.Add(ToolItemResult.Failed(edges[i].Label, "no partner edge to bridge with (edges are bridged in consecutive pairs)"));
                    continue;
                }
                var label = $"{edges[i].Label} <-> {edges[i + 1].Label}";
                try
                {
                    var face = InvokeWithDefaults(method, null, pb, edges[i].Edge, edges[i + 1].Edge);
                    if (face != null) { items.Add(ToolItemResult.Succeeded(label)); bridged++; }
                    else items.Add(ToolItemResult.Failed(label, "ProBuilder rejected the bridge (edges must be open border edges that do not share a face; see the Console for ProBuilder's warning)"));
                }
                catch (Exception ex)
                {
                    items.Add(ToolItemResult.Failed(label, Unwrap(ex).Message));
                }
            }

            if (!TryRebuildMesh(pb, out var rebuildError))
                return bridged > 0 ? RebuildFailed("bridge_edges", go, pb, rebuildError) : ToolResponse.FromItems($"Bridged 0 edge pair(s) on '{go.name}'", items);
            EditorUtility.SetDirty(pb);
            var after = TakeSnapshot(go, pb);
            if (bridged > 0 && !(after.Faces > before.Faces))
                return NotApplied("bridge_edges", go, "a new bridging face (face count to increase)", before, after);
            return ToolResponse.FromItems($"Bridged {bridged} edge pair(s) on '{go.name}' (faces {before.Faces} -> {after.Faces})", items,
                bridged > 0 ? SerializeMeshInfo(go, pb) : null);
        }

        private ToolResponse HandleWeldVertices(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            float weldDistance = ToolHelpers.GetOptionalFloat(parameters, "weld_distance", 0.01f);
            var positions = ReadPositions(pb);
            if (positions == null || positions.Length == 0)
                return ToolResponse.Fail($"'{go.name}' has no vertices to weld.");

            // ProBuilder 5.x：VertexEditing.WeldVertices(ProBuilderMesh, IEnumerable<int>, float)
            // （旧实现找的是 MergeElements.WeldVertices，不存在，于是退回改 sharedMesh，结果会被 ToMesh() 覆盖）
            var allIndices = Enumerable.Range(0, positions.Length).ToArray();
            var method = FindStaticMethod(FindProBuilderType("MeshOperations.VertexEditing"), "WeldVertices", _proBuilderMeshType, typeof(int[]), typeof(float));
            var error = InvokeAndRebuild("weld_vertices", go, pb, method, "VertexEditing.WeldVertices(ProBuilderMesh, IEnumerable<int>, float)",
                new object[] { pb, allIndices, weldDistance }, out _, out var before, out var after);
            if (error != null) return error;

            // 焊接可能合法地什么都不改（没有足够近的顶点），因此不判失败，只如实报告前后计数
            var weldMessage = after.Positions != before.Positions || after.TrianglesHash != before.TrianglesHash
                ? $"Welded vertices within {weldDistance} distance on '{go.name}' (vertices {before.Positions} -> {after.Positions})."
                : $"Weld ran on '{go.name}' but no vertices were merged (none within {weldDistance}); mesh unchanged.";
            return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), weldMessage);
        }

        private ToolResponse HandleSetUVProjection(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            string projectionMode = ToolHelpers.GetOptionalString(parameters, "uv_projection", "planar").ToLowerInvariant();
            if (projectionMode != "planar" && projectionMode != "box" && projectionMode != "spherical" && projectionMode != "cylindrical")
                return ToolResponse.Fail($"Unknown uv_projection '{projectionMode}'. Use planar, box, spherical, or cylindrical.");
            int uvChannel = ToolHelpers.GetOptionalInt(parameters, "uv_channel", 0);
            if (uvChannel < 0 || uvChannel > 3)
                return ToolResponse.Fail($"uv_channel must be 0-3 (got {uvChannel}).");

            var items = new List<ToolItemResult>();
            var faces = SelectFacesOrAll(pb, ToolHelpers.GetOptionalArray(parameters, "face_indices"), items);
            if (faces.Length == 0) return NoValidFaces("set_uv_projection", go, items);
            var distinct = faces.SelectMany(f => GetIntCollection(f, "distinctIndexes", "indexes")).Distinct().ToArray();
            var positions = ReadPositions(pb);
            if (positions == null || distinct.Length == 0)
                return ToolResponse.Fail($"Could not read ProBuilder vertex positions / face indexes on '{go.name}'.");

            var uvType = FindProBuilderType("MeshOperations.UVEditing");
            var manualUvProp = _faceType?.GetProperty("manualUV", BindingFlags.Public | BindingFlags.Instance);

            ToolHelpers.RecordUndo(pb, "Set ProBuilder UV Projection");
            var meshForUndo = GetMesh(go);
            if (meshForUndo != null) ToolHelpers.RecordUndo(meshForUndo, "Set ProBuilder UV Projection");

            // 先 ToMesh() 同步一次：UV2 只存在于 Unity Mesh 上，而 ToMesh() 会清空 UV2，所以写 UV2 必须在 ToMesh() 之后
            if (!TryRebuildMesh(pb, out var syncError, refresh: false))
                return ToolResponse.Fail($"set_uv_projection could not sync '{go.name}' before projecting (ToMesh): {syncError}");

            string api;
            Vector2[] expectedUVs = null;
            try
            {
                var boxMethod = projectionMode == "box" ? FindStaticMethod(uvType, "ProjectFacesBox", _proBuilderMeshType, FaceArrayType, typeof(int)) : null;
                var sphereMethod = projectionMode == "spherical" ? FindStaticMethod(uvType, "ProjectFacesSphere", _proBuilderMeshType, typeof(int[]), typeof(int)) : null;
                if (boxMethod != null)
                {
                    // ProBuilder 自带的盒状投影（按面法线选轴，会把面标为 manualUV）
                    InvokeWithDefaults(boxMethod, null, pb, ToTypedArray(faces, _faceType), uvChannel);
                    api = "UVEditing.ProjectFacesBox";
                }
                else if (sphereMethod != null)
                {
                    InvokeWithDefaults(sphereMethod, null, pb, distinct, uvChannel);
                    api = "UVEditing.ProjectFacesSphere";
                }
                else
                {
                    // planar 优先用公开的 Projection.PlanarProject（按最佳拟合平面投影）；其余情况用本地计算
                    var planar = projectionMode == "planar"
                        ? FindStaticMethod(FindProBuilderType("Projection"), "PlanarProject", typeof(Vector3[]), typeof(int[]))
                        : null;
                    if (planar != null)
                    {
                        expectedUVs = InvokeWithDefaults(planar, null, positions, distinct) as Vector2[];
                        api = "Projection.PlanarProject";
                    }
                    else
                    {
                        api = "AgentCore projection";
                    }
                    if (expectedUVs == null || expectedUVs.Length != distinct.Length)
                    {
                        expectedUVs = ComputeProjectedUVs(positions, distinct, projectionMode);
                        api = "AgentCore projection";
                    }
                    // 通道 0 的自动 UV 面会在 Refresh() 里被重新展开，必须标为 manualUV 才能保留投影结果
                    if (uvChannel == 0 && manualUvProp != null && manualUvProp.CanWrite)
                        foreach (var face in faces) manualUvProp.SetValue(face, true);
                    if (!WriteUVChannel(pb, go, uvChannel, distinct, expectedUVs, out var writeError))
                        return ToolResponse.Fail($"set_uv_projection could not write UV channel {uvChannel} on '{go.name}': {writeError}");
                }
            }
            catch (Exception ex)
            {
                TryRebuildMesh(pb, out _);
                return ToolResponse.Fail($"set_uv_projection failed inside ProBuilder: {Unwrap(ex).Message}. Use Undo to revert any partial change.");
            }

            if (!TryRebuildMesh(pb, out var rebuildError, toMesh: uvChannel != 1))
                return RebuildFailed("set_uv_projection", go, pb, rebuildError);
            EditorUtility.SetDirty(pb);

            // 校验（本地计算的投影）：重新读取该通道 UV，确认写入值没有被 Refresh() 的自动展开覆盖
            if (expectedUVs != null)
            {
                var actual = ReadUVChannel(pb, go, uvChannel);
                int mismatched = 0;
                for (int i = 0; i < distinct.Length; i++)
                {
                    if (actual == null || distinct[i] >= actual.Length || (actual[distinct[i]] - expectedUVs[i]).sqrMagnitude > VerifyTolerance * VerifyTolerance)
                        mismatched++;
                }
                if (mismatched == distinct.Length)
                    return ToolResponse.Fail($"set_uv_projection on '{go.name}' wrote UV channel {uvChannel}, but none of the {distinct.Length} UVs survived the mesh refresh (ProBuilder auto-UV likely regenerated them).");
                if (mismatched > 0)
                    items.Add(ToolItemResult.Failed($"UV channel {uvChannel}", $"{mismatched} of {distinct.Length} UV(s) did not keep the projected value after refresh"));
            }

            var note = uvChannel == 1
                ? " Note: UV2 lives only on the Unity mesh; ProBuilder clears it on its next rebuild (it regenerates lightmap UVs)."
                : string.Empty;
            return Finish($"Applied {projectionMode} UV projection ({api}) to {faces.Length} face(s) on '{go.name}' (UV channel {uvChannel}).{note}", items, go, pb);
        }

        private ToolResponse HandleTriangulate(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;

            var faces = GetAllFaceObjects(pb);
            if (faces == null || faces.Length == 0) return ToolResponse.Fail($"'{go.name}' has no faces to triangulate.");
            // 已全是三角形的网格合法地不需要任何修改
            if (faces.All(f => GetIntCollection(f, "indexes").Length == 3))
                return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), $"'{go.name}' already consists only of triangle faces ({faces.Length}); nothing to triangulate.");

            // ProBuilder 5.x：SurfaceTopology.ToTriangles(ProBuilderMesh, IList<Face>)
            // （旧实现找的是不存在的 TriangulateQuads，再退回"Unity 网格本来就是三角形"的成功回报，实际什么也没做）
            var method = FindStaticMethod(FindProBuilderType("MeshOperations.SurfaceTopology"), "ToTriangles", _proBuilderMeshType, FaceArrayType);
            var error = InvokeAndRebuild("triangulate", go, pb, method, "SurfaceTopology.ToTriangles(ProBuilderMesh, IList<Face>)",
                new object[] { pb, ToTypedArray(faces, _faceType) }, out _, out var before, out var after);
            if (error != null) return error;
            if (!(after.Faces > before.Faces))
                return NotApplied("triangulate", go, "face count to increase (quads/polygons split into triangles)", before, after);
            return ToolResponse.OkWithData(SerializeMeshInfo(go, pb), $"Triangulated '{go.name}' (faces {before.Faces} -> {after.Faces}).");
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

            var combineData = new { name = goNew.name, sourceCount = filters.Length, vertexCount = combinedMesh.vertexCount, note = "Combined as a standard Unity mesh (not a ProBuilderMesh) because ProBuilder combine editor APIs vary by version; the source objects were left unchanged." };
            if (combinedMesh.vertexCount == 0)
                return ToolResponse.Fail($"Created '{newName}' but the combined mesh has no vertices; the empty GameObject was left in the scene.");
            return ToolResponse.FromItems($"Combined {filters.Length} ProBuilder meshes into '{newName}'", items, combineData);
        }

        private ToolResponse HandleGetVertices(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var maxCount = Mathf.Max(1, ToolHelpers.GetOptionalInt(parameters, "max_count", 200));
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
            if (result.Component == null) return NoProBuilderMesh(result.Name, result.GameObject);
            var go = result.GameObject;
            var pb = result.Component;
            var verticesArray = ToolHelpers.GetOptionalArray(parameters, "vertices");
            if (verticesArray == null || verticesArray.Count == 0) return ToolResponse.Fail("vertices array is required and must not be empty.");

            // ProBuilderMesh.positions 的 getter 返回 ReadOnlyCollection：旧实现对它按索引赋值会抛 NotSupportedException。
            // 正确做法：复制成数组修改，再通过 positions 的 setter 整体写回，然后 ToMesh() + Refresh()。
            var positionsProp = GetPositionsProperty();
            var positions = ReadPositions(pb);
            if (positionsProp == null || !positionsProp.CanWrite || positions == null)
                return ApiMissing("move_vertices", "ProBuilderMesh.positions { get; set; }");

            var coincidentMethod = FindInstanceMethod(_proBuilderMeshType, "GetCoincidentVertices", typeof(int), typeof(List<int>));
            var items = new List<ToolItemResult>();
            var expected = new Dictionary<int, Vector3>();
            var pending = new List<(int Slot, int Index, int GroupSize)>();
            int entry = 0;
            foreach (var token in verticesArray)
            {
                var label = $"vertex entry #{entry++}";
                if (!(token is JObject obj))
                {
                    items.Add(ToolItemResult.Failed(label, "entry is not an object {index, position|offset}"));
                    continue;
                }
                var index = obj["index"] != null && (obj["index"].Type == JTokenType.Integer) ? obj["index"].Value<int>() : -1;
                if (index < 0 || index >= positions.Length)
                {
                    items.Add(ToolItemResult.Failed(label, $"index {obj["index"]} out of range (mesh has {positions.Length} vertices)"));
                    continue;
                }
                var current = positions[index];
                Vector3 target;
                if (obj["position"] != null) target = ToolHelpers.ParseVector3(obj["position"], current);
                else if (obj["offset"] != null) target = current + ToolHelpers.ParseVector3(obj["offset"], Vector3.zero);
                else
                {
                    items.Add(ToolItemResult.Failed(label, $"index {index}: neither 'position' nor 'offset' given"));
                    continue;
                }

                // 与 ProBuilder 编辑器一致：同一位置的共享顶点（相邻面各自的副本）一起移动，否则网格会被撕开
                var group = GetCoincidentGroup(pb, coincidentMethod, positions, index);
                var delta = target - current;
                foreach (var g in group) positions[g] += delta;
                foreach (var key in expected.Keys.ToList())
                    if (group.Contains(key)) expected[key] += delta;
                expected[index] = target;
                pending.Add((items.Count, index, group.Count));
                items.Add(null); // 占位，写入后按实际顶点位置校验
            }

            if (pending.Count == 0)
                return ToolResponse.FromItems($"Moved 0 vertices on '{go.name}'", items);

            ToolHelpers.RecordUndo(pb, "Move ProBuilder Vertices");
            try
            {
                positionsProp.SetValue(pb, positions);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"move_vertices could not write ProBuilderMesh.positions on '{go.name}': {Unwrap(ex).Message}");
            }
            if (!TryRebuildMesh(pb, out var rebuildError))
                return RebuildFailed("move_vertices", go, pb, rebuildError);
            EditorUtility.SetDirty(pb);

            // 校验：重新读取 ProBuilder positions 与重建后的 Unity Mesh，确认每个顶点确实到达目标位置
            var actual = ReadPositions(pb) ?? Array.Empty<Vector3>();
            var mesh = GetMesh(go);
            var meshVertices = mesh != null && mesh.vertexCount == actual.Length ? mesh.vertices : null;
            int verified = 0, coincidentMoved = 0;
            foreach (var p in pending)
            {
                var target = expected[p.Index];
                var label = $"vertex {p.Index}";
                bool dataOk = p.Index < actual.Length && Vector3.Distance(actual[p.Index], target) <= VerifyTolerance;
                bool meshOk = meshVertices == null || Vector3.Distance(meshVertices[p.Index], target) <= VerifyTolerance;
                if (dataOk && meshOk)
                {
                    items[p.Slot] = ToolItemResult.Succeeded(label, p.GroupSize > 1 ? new { coincidentMoved = p.GroupSize - 1 } : null);
                    verified++;
                    coincidentMoved += p.GroupSize - 1;
                }
                else
                {
                    var now = p.Index < actual.Length ? actual[p.Index].ToString("F4") : "missing";
                    var why = dataOk ? "Unity mesh was not updated by ToMesh()" : $"expected {target.ToString("F4")}, actual {now}";
                    items[p.Slot] = ToolItemResult.Failed(label, $"position did not take effect ({why})");
                }
            }

            return ToolResponse.FromItems($"Moved {verified} vertices on '{go.name}'", items,
                new { name = go.name, moved = verified, coincidentMoved, requested = verticesArray.Count, source = "ProBuilderMesh.positions" });
        }

        #region 结果与校验

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
        /// 调用一个 ProBuilder 网格操作，然后 ToMesh() + Refresh()。成功返回 null，否则返回如实的失败 / 部分成功结果。
        /// 只做报告，不回滚已经执行的修改（需要时可用 Undo 撤销）。
        /// </summary>
        private static ToolResponse InvokeAndRebuild(string operation, GameObject go, Component pb, MethodInfo method, string api,
            object[] args, out object returned, out MeshSnapshot before, out MeshSnapshot after)
        {
            returned = null;
            before = after = default;
            if (method == null) return ApiMissing(operation, api);

            ToolHelpers.RecordUndo(pb, $"ProBuilder {operation}");
            before = TakeSnapshot(go, pb);
            try
            {
                returned = InvokeWithDefaults(method, null, args);
            }
            catch (Exception ex)
            {
                // 操作中途抛出时 ProBuilder 数据可能已部分修改，尽量重建一次保持 Unity Mesh 同步
                TryRebuildMesh(pb, out _);
                return ToolResponse.Fail($"{operation} failed inside ProBuilder ({api}): {Unwrap(ex).Message}. Use Undo to revert any partial change.");
            }

            if (!TryRebuildMesh(pb, out var rebuildError))
                return RebuildFailed(operation, go, pb, rebuildError);
            EditorUtility.SetDirty(pb);
            after = TakeSnapshot(go, pb);
            return null;
        }

        /// <summary>操作已执行但没有产生预期变化 → failed（附前后快照）</summary>
        private static ToolResponse NotApplied(string operation, GameObject go, string expectation, MeshSnapshot before, MeshSnapshot after)
        {
            return ToolResponse.Fail(
                $"{operation} on '{go.name}' ran without an error but did not take effect: expected {expectation}. " +
                $"Before: {before}. After: {after}.");
        }

        /// <summary>操作生效后汇总：有被跳过的输入项（无效索引等）时为 partial，否则 ok</summary>
        private static ToolResponse Finish(string message, List<ToolItemResult> items, GameObject go, Component pb)
        {
            var data = SerializeMeshInfo(go, pb);
            if (items.Any(i => !i.Ok)) return ToolResponse.Partial(message, items, data);
            return ToolResponse.OkWithData(data, message + ".");
        }

        /// <summary>ProBuilder 数据已修改，但 ToMesh()/Refresh() 失败 → partial</summary>
        private static ToolResponse RebuildFailed(string operation, GameObject go, Component pb, string error)
        {
            return ToolResponse.Partial(
                $"{operation} on '{go.name}' changed the ProBuilder data, but the Unity mesh was not rebuilt, so the change may not be visible",
                new[]
                {
                    ToolItemResult.Succeeded("ProBuilder data"),
                    ToolItemResult.Failed("mesh rebuild (ToMesh/Refresh)", error)
                },
                SerializeMeshInfo(go, pb));
        }

        private static ToolResponse ApiMissing(string operation, string api)
        {
            return ToolResponse.Fail(
                $"{operation}: the installed ProBuilder version does not expose the expected API {api}. Nothing was changed.");
        }

        private static ToolResponse NoProBuilderMesh(string name, GameObject go)
        {
            return ToolResponse.Fail(go == null
                ? $"GameObject '{name}' not found."
                : $"GameObject '{name}' does not have a ProBuilderMesh component.");
        }

        private static ToolResponse NoValidFaces(string operation, GameObject go, List<ToolItemResult> items)
        {
            var invalid = items.Where(i => !i.Ok).Select(i => $"{i.Target} ({i.Error})").ToArray();
            return ToolResponse.Fail($"{operation}: no valid faces on '{go.name}'." +
                (invalid.Length > 0 ? " " + string.Join("; ", invalid) : string.Empty) + " Use get_faces to list face indices.");
        }

        private static ToolResponse NoValidEdges(string operation, GameObject go, List<ToolItemResult> items)
        {
            var invalid = items.Where(i => !i.Ok).Select(i => $"{i.Target} ({i.Error})").ToArray();
            return ToolResponse.Fail($"{operation}: no valid edges on '{go.name}'." +
                (invalid.Length > 0 ? " " + string.Join("; ", invalid) : string.Empty));
        }

        #endregion

        #region 反射工具

        private static bool IsAvailable()
        {
            if (_reflectionInitialized) return _proBuilderMeshType != null;
            _reflectionInitialized = true;
            _proBuilderMeshType = FindProBuilderType("ProBuilderMesh");
            _shapeGeneratorType = FindProBuilderType("ShapeGenerator");
            _shapeTypeType = FindProBuilderType("ShapeType");
            _pivotLocationType = FindProBuilderType("PivotLocation");
            _faceType = FindProBuilderType("Face");
            _edgeType = FindProBuilderType("Edge");
            return _proBuilderMeshType != null;
        }

        /// <summary>Face[]（可传给 IEnumerable&lt;Face&gt; / IList&lt;Face&gt; 参数）</summary>
        private static Type FaceArrayType => _faceType?.MakeArrayType();

        /// <summary>Edge[]（可传给 IList&lt;Edge&gt; 参数）</summary>
        private static Type EdgeArrayType => _edgeType?.MakeArrayType();

        /// <summary>
        /// 按 "UnityEngine.ProBuilder.{relativeName}" 查找 ProBuilder 类型（含 internal 类型，如 5.x 的 UVEditing）。
        /// </summary>
        private static Type FindProBuilderType(string relativeName)
        {
            return FindType($"UnityEngine.ProBuilder.{relativeName}, Unity.ProBuilder",
                $"UnityEngine.ProBuilder.{relativeName}", $"Unity.ProBuilder.{relativeName}");
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

        /// <summary>
        /// 按名称与前置参数类型查找公开静态方法：前 N 个参数须能接收给定类型，其余参数必须是可选参数。
        /// 用于在同名重载间精确选择（GetMethod(name) 遇到重载会抛 AmbiguousMatchException），
        /// 也能匹配 ToMesh(MeshTopology = Triangles) 这类只有可选参数的方法。任一类型为 null 时返回 null。
        /// </summary>
        internal static MethodInfo FindStaticMethod(Type type, string name, params Type[] leading)
        {
            return FindMethod(type, name, BindingFlags.Public | BindingFlags.Static, leading);
        }

        /// <summary>同 <see cref="FindStaticMethod"/>，查找公开实例方法。</summary>
        internal static MethodInfo FindInstanceMethod(Type type, string name, params Type[] leading)
        {
            return FindMethod(type, name, BindingFlags.Public | BindingFlags.Instance, leading);
        }

        private static MethodInfo FindMethod(Type type, string name, BindingFlags flags, Type[] leading)
        {
            if (type == null || leading == null || leading.Any(t => t == null)) return null;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name || method.IsGenericMethodDefinition) continue;
                var ps = method.GetParameters();
                if (ps.Length < leading.Length) continue;
                bool match = true;
                for (int i = 0; i < ps.Length && match; i++)
                    match = i < leading.Length ? ps[i].ParameterType.IsAssignableFrom(leading[i]) : ps[i].IsOptional;
                if (match) return method;
            }
            return null;
        }

        /// <summary>调用方法，未给出的尾部可选参数按声明的默认值补齐。</summary>
        internal static object InvokeWithDefaults(MethodInfo method, object target, params object[] args)
        {
            var ps = method.GetParameters();
            var full = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                full[i] = i < args.Length ? args[i] : GetDefaultArgument(ps[i]);
            return method.Invoke(target, full);
        }

        /// <summary>可选参数的默认值；枚举默认值在元数据里可能是底层整数，需转换回枚举类型。</summary>
        internal static object GetDefaultArgument(ParameterInfo parameter)
        {
            var type = parameter.ParameterType;
            if (parameter.HasDefaultValue && parameter.DefaultValue != null && !(parameter.DefaultValue is DBNull))
                return type.IsEnum ? Enum.ToObject(type, parameter.DefaultValue) : parameter.DefaultValue;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        /// <summary>
        /// 把 ProBuilder 数据同步到 Unity Mesh：ToMesh() 然后 Refresh()。
        /// ProBuilder 4.x/5.x 中两者只有带可选参数的重载（ToMesh(MeshTopology = Triangles)、Refresh(RefreshMask = All)），
        /// 旧实现按无参签名查找会返回 null 而静默跳过，导致所有编辑都没有重建网格。找不到或调用失败时返回 false 并给出原因。
        /// </summary>
        private static bool TryRebuildMesh(Component pb, out string error, bool toMesh = true, bool refresh = true)
        {
            error = null;
            var type = pb.GetType();
            try
            {
                if (toMesh)
                {
                    var toMeshMethod = FindInstanceMethod(type, "ToMesh");
                    if (toMeshMethod == null) { error = "ProBuilderMesh.ToMesh() not found"; return false; }
                    InvokeWithDefaults(toMeshMethod, pb);
                }
                if (refresh)
                {
                    var refreshMethod = FindInstanceMethod(type, "Refresh");
                    if (refreshMethod == null) { error = "ProBuilderMesh.Refresh() not found"; return false; }
                    InvokeWithDefaults(refreshMethod, pb);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex).Message;
                return false;
            }
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
            if (_shapeTypeType == null || string.IsNullOrEmpty(shape)) return null;
            var mapped = shape.ToLowerInvariant() == "stairs" ? "Stair" : char.ToUpperInvariant(shape[0]) + shape.Substring(1).ToLowerInvariant();
            return Enum.TryParse(_shapeTypeType, mapped, true, out var value) ? value : null;
        }

        private static object ParseExtrudeMethod(Type extrudeMethodType, string value)
        {
            if (extrudeMethodType == null) return null;
            string name;
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "": case "face_normal": case "facenormal": case "group": name = "FaceNormal"; break;
                case "vertex_normal": case "vertexnormal": name = "VertexNormal"; break;
                case "individual": case "individual_faces": case "individualfaces": name = "IndividualFaces"; break;
                default: return null;
            }
            return Enum.IsDefined(extrudeMethodType, name) ? Enum.Parse(extrudeMethodType, name) : null;
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

        #endregion

        #region ProBuilder 数据读写

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

        private static int GetIntProperty(object obj, string name, int fallback)
        {
            var prop = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return prop != null && prop.PropertyType == typeof(int) ? (int)prop.GetValue(obj) : fallback;
        }

        /// <summary>读取对象上第一个存在的 int 集合属性（如 Face.distinctIndexes / Face.indexes）</summary>
        private static int[] GetIntCollection(object obj, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                var value = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj) as IEnumerable;
                if (value != null) return value.OfType<int>().ToArray();
            }
            return Array.Empty<int>();
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

        private static PropertyInfo GetPositionsProperty()
        {
            return _proBuilderMeshType?.GetProperty("positions", BindingFlags.Public | BindingFlags.Instance);
        }

        /// <summary>把 ProBuilderMesh.positions（只读集合）复制成可修改的数组；不可读时返回 null。</summary>
        private static Vector3[] ReadPositions(Component pb)
        {
            var value = pb.GetType().GetProperty("positions", BindingFlags.Public | BindingFlags.Instance)?.GetValue(pb) as IEnumerable;
            return value?.OfType<Vector3>().ToArray();
        }

        /// <summary>
        /// 取与 index 共享位置的全部顶点（含自身）。优先用 ProBuilderMesh.GetCoincidentVertices(int, List&lt;int&gt;)，
        /// 不可用时按位置相等近似。
        /// </summary>
        private static List<int> GetCoincidentGroup(Component pb, MethodInfo coincidentMethod, Vector3[] positions, int index)
        {
            if (coincidentMethod != null)
            {
                try
                {
                    var list = new List<int>();
                    InvokeWithDefaults(coincidentMethod, pb, index, list);
                    if (list.Contains(index) && list.All(i => i >= 0 && i < positions.Length)) return list;
                }
                catch (Exception)
                {
                    // 共享顶点表损坏等情况，退回按位置匹配
                }
            }
            return FindCoincidentByPosition(positions, index);
        }

        /// <summary>按位置相等（容差 1e-6）查找与 index 重合的顶点（含自身）。</summary>
        internal static List<int> FindCoincidentByPosition(IList<Vector3> positions, int index)
        {
            var result = new List<int>();
            var p = positions[index];
            for (int i = 0; i < positions.Count; i++)
                if ((positions[i] - p).sqrMagnitude <= 1e-12f) result.Add(i);
            return result;
        }

        internal static Bounds ComputeBounds(IList<Vector3> points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Count; i++) bounds.Encapsulate(points[i]);
            return bounds;
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
        /// 按 face_indices 取 Face 对象。越界 / 非整数的索引逐项记为失败（旧实现静默丢弃），重复索引只取一次。
        /// </summary>
        private static object[] SelectFaces(Component pb, JArray faceIndicesToken, List<ToolItemResult> items)
        {
            var all = GetAllFaceObjects(pb) ?? Array.Empty<object>();
            return ValidateIndices(faceIndicesToken, all.Length, "face", items).Select(i => all[i]).ToArray();
        }

        /// <summary>face_indices 为空时取全部面</summary>
        private static object[] SelectFacesOrAll(Component pb, JArray faceIndicesToken, List<ToolItemResult> items)
        {
            if (faceIndicesToken == null || faceIndicesToken.Count == 0)
                return GetAllFaceObjects(pb) ?? Array.Empty<object>();
            return SelectFaces(pb, faceIndicesToken, items);
        }

        /// <summary>
        /// 校验索引数组：合法索引记为成功项并返回（去重、保序），越界 / 非整数索引记为失败项。
        /// </summary>
        internal static List<int> ValidateIndices(JArray tokens, int count, string kind, List<ToolItemResult> items)
        {
            var result = new List<int>();
            if (tokens == null) return result;
            var seen = new HashSet<int>();
            foreach (var token in tokens)
            {
                if (!TryGetInt(token, out var index))
                {
                    items.Add(ToolItemResult.Failed($"{kind} {token}", "not an integer index"));
                    continue;
                }
                if (index < 0 || index >= count)
                {
                    items.Add(ToolItemResult.Failed($"{kind} {index}", $"index out of range ({kind} count is {count})"));
                    continue;
                }
                if (!seen.Add(index)) continue;
                result.Add(index);
                items.Add(ToolItemResult.Succeeded($"{kind} {index}"));
            }
            return result;
        }

        private static bool TryGetInt(JToken token, out int value)
        {
            value = 0;
            if (token == null) return false;
            if (token.Type == JTokenType.Integer) { value = token.Value<int>(); return true; }
            if (token.Type == JTokenType.Float)
            {
                var d = token.Value<double>();
                if (Math.Abs(d - Math.Round(d)) > 1e-9) return false;
                value = (int)Math.Round(d);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 按 edge_indices 构造 Edge(a, b)。缺少 a/b、越界或 a == b 的条目逐项记为失败。
        /// </summary>
        private static List<(string Label, object Edge)> SelectEdges(Component pb, JArray edgeIndicesToken, List<ToolItemResult> items, bool reportValid = true)
        {
            var result = new List<(string, object)>();
            var vertexCount = ReadPositions(pb)?.Length ?? 0;
            var ctor = _edgeType?.GetConstructor(new[] { typeof(int), typeof(int) });
            int entry = 0;
            foreach (var token in edgeIndicesToken)
            {
                var obj = token as JObject;
                var label = obj != null ? $"edge ({obj["a"]}, {obj["b"]})" : $"edge entry #{entry}";
                entry++;
                if (obj == null || !TryGetInt(obj["a"], out var a) || !TryGetInt(obj["b"], out var b))
                {
                    items.Add(ToolItemResult.Failed(label, "edge entry must be {a, b} with integer vertex indices"));
                    continue;
                }
                if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount || a == b)
                {
                    items.Add(ToolItemResult.Failed(label, $"invalid vertex pair (mesh has {vertexCount} vertices; a and b must differ)"));
                    continue;
                }
                if (ctor == null)
                {
                    items.Add(ToolItemResult.Failed(label, "Edge(int, int) constructor not found in this ProBuilder version"));
                    continue;
                }
                result.Add((label, ctor.Invoke(new object[] { a, b })));
                if (reportValid) items.Add(ToolItemResult.Succeeded(label));
            }
            return result;
        }

        private static Array ToTypedArray(IList<object> values, Type elementType)
        {
            var typed = Array.CreateInstance(elementType, values.Count);
            for (int i = 0; i < values.Count; i++) typed.SetValue(values[i], i);
            return typed;
        }

        /// <summary>
        /// 读取 ProBuilder 指定 UV 通道（0/2/3 来自 ProBuilder 数据，1 来自 Unity Mesh 的 uv2），长度与顶点数一致；不可读时返回 null。
        /// </summary>
        private static Vector2[] ReadUVChannel(Component pb, GameObject go, int channel)
        {
            var vertexCount = ReadPositions(pb)?.Length ?? 0;
            if (channel == 0)
            {
                var textures = pb.GetType().GetProperty("textures", BindingFlags.Public | BindingFlags.Instance)?.GetValue(pb) as IEnumerable;
                var arr = textures?.OfType<Vector2>().ToArray() ?? new Vector2[0];
                return arr.Length == vertexCount ? arr : PadUVs(arr, vertexCount);
            }
            if (channel == 1)
            {
                var mesh = GetMesh(go);
                return mesh != null ? PadUVs(mesh.uv2, vertexCount) : null;
            }
            var getUVs = FindInstanceMethod(pb.GetType(), "GetUVs", typeof(int), typeof(List<Vector4>));
            if (getUVs == null) return null;
            var list = new List<Vector4>();
            InvokeWithDefaults(getUVs, pb, channel, list);
            return PadUVs(list.Select(v => (Vector2)v).ToArray(), vertexCount);
        }

        /// <summary>
        /// 把 uvs[i] 写到 ProBuilder UV 通道的 indexes[i] 处：通道 0 用 textures setter，2/3 用 SetUVs，
        /// 通道 1（UV2）只存在于 Unity Mesh 上，直接写 mesh.uv2（须在 ToMesh() 之后，ToMesh() 会清空 UV2）。
        /// </summary>
        private static bool WriteUVChannel(Component pb, GameObject go, int channel, int[] indexes, Vector2[] uvs, out string error)
        {
            error = null;
            var current = ReadUVChannel(pb, go, channel);
            if (current == null) { error = $"cannot read UV channel {channel}"; return false; }
            for (int i = 0; i < indexes.Length; i++)
                if (indexes[i] >= 0 && indexes[i] < current.Length) current[indexes[i]] = uvs[i];

            if (channel == 0)
            {
                var prop = pb.GetType().GetProperty("textures", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanWrite) { error = "ProBuilderMesh.textures setter not found"; return false; }
                prop.SetValue(pb, current);
                return true;
            }
            if (channel == 1)
            {
                var mesh = GetMesh(go);
                if (mesh == null) { error = "no Unity mesh to hold UV2"; return false; }
                mesh.uv2 = current;
                EditorUtility.SetDirty(mesh);
                return true;
            }
            var setUVs = FindInstanceMethod(pb.GetType(), "SetUVs", typeof(int), typeof(List<Vector4>));
            if (setUVs == null) { error = "ProBuilderMesh.SetUVs(int, List<Vector4>) not found"; return false; }
            InvokeWithDefaults(setUVs, pb, channel, current.Select(v => (Vector4)v).ToList());
            return true;
        }

        private static Vector2[] PadUVs(Vector2[] uvs, int count)
        {
            var result = new Vector2[count];
            if (uvs != null) Array.Copy(uvs, result, Math.Min(uvs.Length, count));
            return result;
        }

        /// <summary>
        /// 本地计算的 UV 投影（ProBuilder 没有对应公开 API 时使用）：返回值与 indexes 一一对应。
        /// planar 投到 XZ 平面，box 按顶点相对中心的主轴选择投影面，spherical / cylindrical 绕包围盒中心展开。
        /// </summary>
        internal static Vector2[] ComputeProjectedUVs(IList<Vector3> positions, IList<int> indexes, string projectionMode)
        {
            var pts = indexes.Select(i => positions[i]).ToArray();
            var uvs = new Vector2[pts.Length];
            if (pts.Length == 0) return uvs;
            var b = ComputeBounds(pts);
            Func<float, float> safe = v => Mathf.Approximately(v, 0f) ? 1f : v;
            var size = new Vector3(safe(b.size.x), safe(b.size.y), safe(b.size.z));

            for (int i = 0; i < pts.Length; i++)
            {
                var local = pts[i] - b.center;
                switch (projectionMode)
                {
                    case "spherical":
                        var n = local.sqrMagnitude > 0f ? local.normalized : Vector3.up;
                        uvs[i] = new Vector2(0.5f + Mathf.Atan2(n.z, n.x) / (2f * Mathf.PI), 0.5f - Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) / Mathf.PI);
                        break;
                    case "cylindrical":
                        uvs[i] = new Vector2(0.5f + Mathf.Atan2(local.z, local.x) / (2f * Mathf.PI), local.y / size.y + 0.5f);
                        break;
                    case "box":
                        var abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                        if (abs.x >= abs.y && abs.x >= abs.z)
                            uvs[i] = new Vector2(local.z / size.z + 0.5f, local.y / size.y + 0.5f);
                        else if (abs.y >= abs.z)
                            uvs[i] = new Vector2(local.x / size.x + 0.5f, local.z / size.z + 0.5f);
                        else
                            uvs[i] = new Vector2(local.x / size.x + 0.5f, local.y / size.y + 0.5f);
                        break;
                    default: // planar
                        uvs[i] = new Vector2((pts[i].x - b.min.x) / size.x, (pts[i].z - b.min.z) / size.z);
                        break;
                }
            }
            return uvs;
        }

        #endregion
    }
}
