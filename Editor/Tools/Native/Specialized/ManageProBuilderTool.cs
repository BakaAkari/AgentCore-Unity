using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Creates and edits ProBuilder meshes through reflection so ProBuilder remains an optional package.
    /// </summary>
    [AgentTool("manage_probuilder",
        Description = "Create and edit ProBuilder meshes using optional ProBuilder package APIs",
        Category = "Specialized",
        RequiresMainThread = true)]
    public class ManageProBuilderTool : IAgentTool
    {
        private const string PackageRequiredMessage = "ProBuilder package/API is not available. Install 'com.unity.probuilder' via Unity Package Manager, then retry this action.";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""check_available"", ""create_shape"", ""get_info"", ""set_material"", ""center_pivot"", ""flip_normals"", ""subdivide"", ""combine_meshes"", ""get_vertices"", ""move_vertices""], ""description"": ""ProBuilder action to perform"" },
                ""shape"": { ""type"": ""string"", ""description"": ""Shape: cube, sphere, cylinder, cone, plane, stairs, door, arch, pipe, torus"" },
                ""name"": { ""type"": ""string"", ""description"": ""Target or new GameObject name"" },
                ""names"": { ""type"": ""string"", ""description"": ""Comma-separated GameObject names for combine_meshes"" },
                ""new_name"": { ""type"": ""string"", ""description"": ""New combined object name"" },
                ""position"": { ""type"": ""object"", ""description"": ""Vector3 {x,y,z}; create position, pivot position, or vertex absolute position"" },
                ""rotation"": { ""type"": ""object"", ""description"": ""Euler rotation {x,y,z}"" },
                ""size"": { ""type"": ""object"", ""description"": ""Vector3 size/scale {x,y,z}"" },
                ""material_path"": { ""type"": ""string"" },
                ""iterations"": { ""type"": ""integer"" },
                ""max_count"": { ""type"": ""integer"" },
                ""vertices"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""index"": { ""type"": ""integer"" }, ""position"": { ""type"": ""object"" }, ""offset"": { ""type"": ""object"" } } } }
            },
            ""required"": [""action""]
        }");

        private static bool _reflectionInitialized;
        private static Type _proBuilderMeshType;
        private static Type _shapeGeneratorType;
        private static Type _shapeTypeType;
        private static Type _pivotLocationType;

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_probuilder",
            description: "Create and edit ProBuilder meshes using optional ProBuilder package APIs",
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
                        default:
                            response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: check_available, create_shape, get_info, set_material, center_pivot, flip_normals, subdivide, combine_meshes, get_vertices, move_vertices");
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
            return ToolResponse.Ok($"Assigned material '{materialPath}' to '{result.GameObject.name}'.");
        }

        private ToolResponse HandleCenterPivot(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var mesh = GetMesh(result.GameObject);
            if (mesh == null) return ToolResponse.Fail($"'{result.GameObject.name}' has no editable MeshFilter mesh.");

            var newPivot = parameters["position"] != null ? ToolHelpers.ParseVector3(parameters["position"]) : mesh.bounds.center + result.GameObject.transform.position;
            SetPivotPreserveGeometry(result.GameObject, mesh, newPivot);
            RefreshProBuilderMesh(result.Component);
            return ToolResponse.OkWithData(SerializeMeshInfo(result.GameObject, result.Component), $"Set pivot for '{result.GameObject.name}' to {newPivot}.");
        }

        private ToolResponse HandleFlipNormals(JObject parameters)
        {
            var result = ResolveProBuilder(ToolHelpers.GetRequiredString(parameters, "name"));
            if (result.Component == null) return ToolResponse.Fail($"GameObject '{result.Name}' does not have a ProBuilderMesh component.");
            var mesh = GetMesh(result.GameObject);
            if (mesh == null) return ToolResponse.Fail($"'{result.GameObject.name}' has no MeshFilter mesh to flip.");

            ToolHelpers.RecordUndo(mesh, "Flip ProBuilder Normals");
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
            return ToolResponse.Ok($"Flipped normals on '{result.GameObject.name}'.");
        }

        private ToolResponse HandleSubdivide(JObject parameters)
        {
            var name = ToolHelpers.GetRequiredString(parameters, "name");
            return ToolResponse.Fail($"Current ProBuilder reflection bridge does not support subdivide for '{name}'. Use a ProBuilder version exposing editor subdivision APIs or subdivide manually in the ProBuilder window.");
        }

        private ToolResponse HandleCombineMeshes(JObject parameters)
        {
            var names = ToolHelpers.GetRequiredString(parameters, "names")
                .Split(',')
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            var newName = ToolHelpers.GetOptionalString(parameters, "new_name", "Combined ProBuilder Mesh");
            var filters = names.Select(ToolHelpers.FindGameObject)
                .Where(go => go != null && FindProBuilderMesh(go) != null)
                .Select(go => go.GetComponent<MeshFilter>())
                .Where(mf => mf != null && mf.sharedMesh != null)
                .ToArray();

            if (filters.Length < 2) return ToolResponse.Fail("Need at least two ProBuilder meshes to combine.");

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

            return ToolResponse.OkWithData(new { name = goNew.name, sourceCount = filters.Length, vertexCount = combinedMesh.vertexCount, note = "Combined as a standard Unity mesh because ProBuilder combine editor APIs vary by version." }, $"Combined {filters.Length} ProBuilder meshes into '{newName}'.");
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
            foreach (var token in verticesArray.OfType<JObject>())
            {
                var index = token["index"]?.Value<int>() ?? -1;
                if (index < 0 || index >= positions.Count) continue;
                var current = (Vector3)positions[index];
                if (token["position"] != null)
                    positions[index] = ToolHelpers.ParseVector3(token["position"], current);
                else if (token["offset"] != null)
                    positions[index] = current + ToolHelpers.ParseVector3(token["offset"], Vector3.zero);
                else
                    continue;
                moved++;
            }

            WriteMutablePositions(result.Component, result.GameObject, positions, source);
            RefreshProBuilderMesh(result.Component);
            EditorUtility.SetDirty(result.Component);
            return ToolResponse.OkWithData(new { name = result.GameObject.name, moved, source }, $"Moved {moved} vertices on '{result.GameObject.name}'.");
        }

        private static bool IsAvailable()
        {
            if (_reflectionInitialized) return _proBuilderMeshType != null;
            _reflectionInitialized = true;
            _proBuilderMeshType = FindType("UnityEngine.ProBuilder.ProBuilderMesh, Unity.ProBuilder", "UnityEngine.ProBuilder.ProBuilderMesh", "Unity.ProBuilder.ProBuilderMesh");
            _shapeGeneratorType = FindType("UnityEngine.ProBuilder.ShapeGenerator, Unity.ProBuilder", "UnityEngine.ProBuilder.ShapeGenerator", "Unity.ProBuilder.ShapeGenerator");
            _shapeTypeType = FindType("UnityEngine.ProBuilder.ShapeType, Unity.ProBuilder", "UnityEngine.ProBuilder.ShapeType", "Unity.ProBuilder.ShapeType");
            _pivotLocationType = FindType("UnityEngine.ProBuilder.PivotLocation, Unity.ProBuilder", "UnityEngine.ProBuilder.PivotLocation", "Unity.ProBuilder.PivotLocation");
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
    }
}
