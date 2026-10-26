using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Native.Specialized;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// manage_probuilder 的测试：反射/校验等纯函数始终运行；
    /// 依赖 ProBuilder 包的端到端用例在包不可用时自动忽略。
    /// </summary>
    [TestFixture]
    public class ManageProBuilderToolTests
    {
        #region 反射工具（不依赖 ProBuilder）

        public enum DummyTopology { Triangles = 0, Quads = 2 }

        public static class DummyOps
        {
            public static string Run(IEnumerable<string> items, float amount) => "strings";
            public static string Run(IList<int> items) => "ints";
            public static string Optional(int a, DummyTopology topology = DummyTopology.Quads, int extra = 5) => $"{a}:{topology}:{extra}";
        }

        public class DummyMesh
        {
            public int Calls;
            public DummyTopology Last;
            public void ToMesh(DummyTopology topology = DummyTopology.Quads) { Calls++; Last = topology; }
        }

        [Test]
        public void FindStaticMethod_SelectsOverloadByParameterTypes()
        {
            var forStrings = ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Run", typeof(string[]), typeof(float));
            var forInts = ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Run", typeof(int[]));

            Assert.IsNotNull(forStrings);
            Assert.IsNotNull(forInts);
            Assert.AreEqual("strings", forStrings.Invoke(null, new object[] { new string[0], 1f }));
            Assert.AreEqual("ints", forInts.Invoke(null, new object[] { new int[0] }));
        }

        [Test]
        public void FindStaticMethod_NoMatchOrNullType_ReturnsNull()
        {
            Assert.IsNull(ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Run", typeof(Vector3[])));
            Assert.IsNull(ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Missing", typeof(int)));
            Assert.IsNull(ManageProBuilderTool.FindStaticMethod(null, "Run", typeof(int[])));
            Assert.IsNull(ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Run", (Type)null));
        }

        [Test]
        public void FindInstanceMethod_MatchesMethodWithOnlyOptionalParameters()
        {
            // 与 ProBuilderMesh.ToMesh(MeshTopology = Triangles) 同形：按无参签名查找会失败，这里必须能找到
            var method = ManageProBuilderTool.FindInstanceMethod(typeof(DummyMesh), "ToMesh");
            Assert.IsNotNull(method);

            var target = new DummyMesh();
            ManageProBuilderTool.InvokeWithDefaults(method, target);
            Assert.AreEqual(1, target.Calls);
            Assert.AreEqual(DummyTopology.Quads, target.Last);
        }

        [Test]
        public void InvokeWithDefaults_FillsTrailingOptionalArguments()
        {
            var method = ManageProBuilderTool.FindStaticMethod(typeof(DummyOps), "Optional", typeof(int));
            Assert.IsNotNull(method);
            Assert.AreEqual("7:Quads:5", ManageProBuilderTool.InvokeWithDefaults(method, null, 7));
            Assert.AreEqual("7:Triangles:5", ManageProBuilderTool.InvokeWithDefaults(method, null, 7, DummyTopology.Triangles));
        }

        #endregion

        #region 输入校验与几何辅助（不依赖 ProBuilder）

        [Test]
        public void ValidateIndices_ReportsInvalidAndDeduplicates()
        {
            var items = new List<ToolItemResult>();
            var result = ManageProBuilderTool.ValidateIndices(new JArray(0, 2, 2, 9, -1, "x", 1.0), 3, "face", items);

            CollectionAssert.AreEqual(new[] { 0, 2, 1 }, result);
            Assert.AreEqual(3, items.Count(i => i.Ok));
            Assert.AreEqual(3, items.Count(i => !i.Ok));
            Assert.IsTrue(items.Any(i => !i.Ok && i.Target == "face 9"));
        }

        [Test]
        public void ValidateIndices_NullTokens_ReturnsEmpty()
        {
            var items = new List<ToolItemResult>();
            Assert.IsEmpty(ManageProBuilderTool.ValidateIndices(null, 3, "face", items));
            Assert.IsEmpty(items);
        }

        [Test]
        public void FindCoincidentByPosition_ReturnsAllSharedPositions()
        {
            var positions = new[] { Vector3.zero, Vector3.one, Vector3.zero, new Vector3(0f, 0f, 1e-3f) };
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, ManageProBuilderTool.FindCoincidentByPosition(positions, 2));
            CollectionAssert.AreEquivalent(new[] { 1 }, ManageProBuilderTool.FindCoincidentByPosition(positions, 1));
        }

        [Test]
        public void ComputeProjectedUVs_PlanarMapsBoundsToUnitSquare()
        {
            var positions = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
            var uvs = ManageProBuilderTool.ComputeProjectedUVs(positions, new[] { 0, 2 }, "planar");

            Assert.AreEqual(2, uvs.Length);
            Assert.That(Vector2.Distance(uvs[0], new Vector2(0, 0)), Is.LessThan(1e-5f));
            Assert.That(Vector2.Distance(uvs[1], new Vector2(1, 1)), Is.LessThan(1e-5f));
        }

        [Test]
        public void ComputeProjectedUVs_DegenerateBounds_ProducesFiniteValues()
        {
            var positions = new[] { Vector3.zero, Vector3.zero };
            foreach (var mode in new[] { "planar", "box", "spherical", "cylindrical" })
            {
                var uvs = ManageProBuilderTool.ComputeProjectedUVs(positions, new[] { 0, 1 }, mode);
                Assert.IsTrue(uvs.All(uv => !float.IsNaN(uv.x) && !float.IsNaN(uv.y) && !float.IsInfinity(uv.x) && !float.IsInfinity(uv.y)), mode);
            }
        }

        #endregion

        #region 端到端（需要 com.unity.probuilder）

        private const string TestPrefix = "AC_PBTest_";
        private readonly List<string> _created = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var name in _created)
            {
                var go = GameObject.Find(name);
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            _created.Clear();
        }

        private static ToolResult Run(JObject parameters)
        {
            return new ManageProBuilderTool().ExecuteAsync(parameters).Result;
        }

        private static JObject Json(ToolResult result)
        {
            return JObject.Parse(result.Output);
        }

        private string CreateCube()
        {
            var available = Run(new JObject { ["action"] = "check_available" });
            if (!available.Success || Json(available)["data"]?["available"]?.Value<bool>() != true)
                Assert.Ignore("com.unity.probuilder is not installed; skipping ProBuilder integration test.");

            var name = TestPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            _created.Add(name);
            var result = Run(new JObject { ["action"] = "create_shape", ["shape"] = "cube", ["name"] = name });
            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            return name;
        }

        private static Vector3[] GetVertices(string name)
        {
            var result = Run(new JObject { ["action"] = "get_vertices", ["name"] = name, ["max_count"] = 10000 });
            Assert.IsTrue(result.Success, result.Error);
            return Json(result)["data"]["vertices"].Select(v => ToolHelpers.ParseVector3(v["position"])).ToArray();
        }

        private static int GetFaceCount(string name)
        {
            var result = Run(new JObject { ["action"] = "get_info", ["name"] = name });
            Assert.IsTrue(result.Success, result.Error);
            return Json(result)["data"]["faces"].Value<int>();
        }

        private static Vector3[] GetUnityMeshVertices(string name)
        {
            return GameObject.Find(name).GetComponent<MeshFilter>().sharedMesh.vertices;
        }

        [Test]
        public void MoveVertices_UpdatesProBuilderDataAndUnityMesh_AndMovesCoincidentVertices()
        {
            var name = CreateCube();
            var before = GetVertices(name);
            // 偏移量不能是边长的整数倍，否则会落在另一个角点上，把那里原有的顶点也算进来
            var target = before[0] + new Vector3(0f, 0.37f, 0f);
            var coincidentBefore = before.Count(p => Vector3.Distance(p, before[0]) < 1e-5f);

            var result = Run(new JObject
            {
                ["action"] = "move_vertices",
                ["name"] = name,
                ["vertices"] = new JArray(new JObject { ["index"] = 0, ["offset"] = new JObject { ["x"] = 0, ["y"] = 0.37, ["z"] = 0 } })
            });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            var after = GetVertices(name);
            Assert.That(Vector3.Distance(after[0], target), Is.LessThan(1e-4f));
            // 共享顶点一起移动，网格不被撕开
            Assert.AreEqual(coincidentBefore, after.Count(p => Vector3.Distance(p, target) < 1e-5f));
            // ToMesh() 已把 ProBuilder 数据重建到 Unity Mesh
            Assert.That(Vector3.Distance(GetUnityMeshVertices(name)[0], target), Is.LessThan(1e-4f));
        }

        [Test]
        public void MoveVertices_MixedValidAndInvalidEntries_IsPartial()
        {
            var name = CreateCube();
            var result = Run(new JObject
            {
                ["action"] = "move_vertices",
                ["name"] = name,
                ["vertices"] = new JArray(
                    new JObject { ["index"] = 1, ["position"] = new JObject { ["x"] = 2, ["y"] = 2, ["z"] = 2 } },
                    new JObject { ["index"] = 9999, ["offset"] = new JObject { ["x"] = 1, ["y"] = 0, ["z"] = 0 } })
            });

            Assert.IsTrue(result.IsPartial, result.Error ?? result.Output);
            Assert.That(Vector3.Distance(GetVertices(name)[1], new Vector3(2, 2, 2)), Is.LessThan(1e-4f));
        }

        [Test]
        public void MoveVertices_AllEntriesInvalid_FailsWithoutChanges()
        {
            var name = CreateCube();
            var before = GetVertices(name);
            var result = Run(new JObject
            {
                ["action"] = "move_vertices",
                ["name"] = name,
                ["vertices"] = new JArray(new JObject { ["index"] = -5, ["offset"] = new JObject { ["x"] = 1, ["y"] = 0, ["z"] = 0 } })
            });

            Assert.IsFalse(result.Success);
            CollectionAssert.AreEqual(before, GetVertices(name));
        }

        [Test]
        public void ExtrudeFaces_IncreasesFaceCount()
        {
            var name = CreateCube();
            var facesBefore = GetFaceCount(name);
            var result = Run(new JObject { ["action"] = "extrude_faces", ["name"] = name, ["face_indices"] = new JArray(0), ["extrude_distance"] = 0.5 });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            Assert.Greater(GetFaceCount(name), facesBefore);
        }

        [Test]
        public void DeleteFaces_DecreasesFaceCount()
        {
            var name = CreateCube();
            var facesBefore = GetFaceCount(name);
            var result = Run(new JObject { ["action"] = "delete_faces", ["name"] = name, ["face_indices"] = new JArray(0) });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            Assert.AreEqual(facesBefore - 1, GetFaceCount(name));
        }

        [Test]
        public void DeleteFaces_WithOutOfRangeIndex_IsPartial()
        {
            var name = CreateCube();
            var result = Run(new JObject { ["action"] = "delete_faces", ["name"] = name, ["face_indices"] = new JArray(0, 500) });
            Assert.IsTrue(result.IsPartial, result.Error ?? result.Output);
        }

        [Test]
        public void Subdivide_IncreasesFaceCount()
        {
            var name = CreateCube();
            var facesBefore = GetFaceCount(name);
            var result = Run(new JObject { ["action"] = "subdivide", ["name"] = name });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            Assert.Greater(GetFaceCount(name), facesBefore);
        }

        [Test]
        public void Triangulate_SplitsQuads()
        {
            var name = CreateCube();
            var result = Run(new JObject { ["action"] = "triangulate", ["name"] = name });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            Assert.AreEqual(12, GetFaceCount(name));
        }

        [Test]
        public void FlipNormals_ChangesUnityMeshTriangles()
        {
            var name = CreateCube();
            var mesh = GameObject.Find(name).GetComponent<MeshFilter>().sharedMesh;
            var before = mesh.triangles;
            var result = Run(new JObject { ["action"] = "flip_normals", ["name"] = name });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            var after = GameObject.Find(name).GetComponent<MeshFilter>().sharedMesh.triangles;
            CollectionAssert.AreNotEqual(before, after);
        }

        [Test]
        public void CenterPivot_MovesTransformButKeepsWorldGeometry()
        {
            var name = CreateCube();
            var go = GameObject.Find(name);
            var worldBefore = GetVertices(name).Select(v => go.transform.TransformPoint(v)).ToArray();
            var pivot = new Vector3(3f, 1f, -2f);

            var result = Run(new JObject { ["action"] = "center_pivot", ["name"] = name, ["position"] = new JObject { ["x"] = pivot.x, ["y"] = pivot.y, ["z"] = pivot.z } });

            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
            Assert.That(Vector3.Distance(go.transform.position, pivot), Is.LessThan(1e-4f));
            var worldAfter = GetVertices(name).Select(v => go.transform.TransformPoint(v)).ToArray();
            for (int i = 0; i < worldBefore.Length; i++)
                Assert.That(Vector3.Distance(worldBefore[i], worldAfter[i]), Is.LessThan(1e-3f));
            var meshWorld = GetUnityMeshVertices(name).Select(v => go.transform.TransformPoint(v)).ToArray();
            Assert.That(Vector3.Distance(meshWorld[0], worldBefore[0]), Is.LessThan(1e-3f));
        }

        [Test]
        public void WeldVertices_ResolvesApiAndSucceeds()
        {
            var name = CreateCube();
            var result = Run(new JObject { ["action"] = "weld_vertices", ["name"] = name, ["weld_distance"] = 0.001 });
            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);
        }

        [Test]
        public void SetUVProjection_AllModes_ResolveAndSucceed()
        {
            var name = CreateCube();
            foreach (var mode in new[] { "planar", "box", "spherical", "cylindrical" })
            {
                var result = Run(new JObject { ["action"] = "set_uv_projection", ["name"] = name, ["uv_projection"] = mode });
                Assert.IsTrue(result.IsFullSuccess, $"{mode}: {result.Error ?? result.Output}");
            }
        }

        [Test]
        public void SetUVProjection_Channel1_IsPartialAndPointsToGenerateLightmapUVs()
        {
            var name = CreateCube();
            var result = Run(new JObject { ["action"] = "set_uv_projection", ["name"] = name, ["uv_channel"] = 1 });

            // UV2 照常写入，但 ProBuilder 不保存它，结果须如实标为部分完成
            Assert.IsTrue(result.Success, result.Error ?? result.Output);
            Assert.IsFalse(result.IsFullSuccess, result.Output);
            StringAssert.Contains("generate_lightmap_uvs", result.Output);
        }

        [Test]
        public void GenerateLightmapUVs_StoresUnwrapParametersAndBuildsUV2()
        {
            var name = CreateCube();
            var result = Run(new JObject
            {
                ["action"] = "generate_lightmap_uvs",
                ["name"] = name,
                ["hard_angle"] = 60,
                ["pack_margin"] = 12,
                ["contribute_gi"] = true
            });
            Assert.IsTrue(result.IsFullSuccess, result.Error ?? result.Output);

            var data = Json(result)["data"];
            Assert.AreEqual(60f, data["unwrap_parameters"]["hard_angle"].Value<float>(), 1e-4f);
            Assert.AreEqual(12f, data["unwrap_parameters"]["pack_margin"].Value<float>(), 1e-4f);
            Assert.IsTrue(data["contribute_gi"].Value<bool>());

            var go = GameObject.Find(name);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(mesh.uv2.Length, 0);
            Assert.AreEqual(mesh.vertexCount, mesh.uv2.Length);
            Assert.IsTrue((UnityEditor.GameObjectUtility.GetStaticEditorFlags(go) & UnityEditor.StaticEditorFlags.ContributeGI) != 0);
        }

        [Test]
        public void GenerateLightmapUVs_LaterEditKeepsUV2_WhenAutoLightmapUVsOn()
        {
            var name = CreateCube();
            var generate = Run(new JObject { ["action"] = "generate_lightmap_uvs", ["name"] = name, ["contribute_gi"] = true });
            Assert.IsTrue(generate.IsFullSuccess, generate.Error ?? generate.Output);
            if (Json(generate)["data"]["uv2_regenerates_after_edits"]?.Value<bool>() != true)
                Assert.Ignore("ProBuilder's Auto Lightmap UVs preference is off; later edits are expected to clear UV2.");

            // 之后经本工具的编辑须像 ProBuilder 编辑器一样按展开参数重新生成 UV2，而不是留下被 ToMesh() 清空的网格
            var extrude = Run(new JObject { ["action"] = "extrude_faces", ["name"] = name, ["face_indices"] = new JArray(0) });
            Assert.IsTrue(extrude.IsFullSuccess, extrude.Error ?? extrude.Output);

            var mesh = GameObject.Find(name).GetComponent<MeshFilter>().sharedMesh;
            Assert.Greater(mesh.uv2.Length, 0);
            Assert.AreEqual(mesh.vertexCount, mesh.uv2.Length);
        }

        [Test]
        public void BevelAndBridge_ResolveProBuilderApi()
        {
            var name = CreateCube();
            // 被拒绝的倒角/桥接会由 ProBuilder 自己输出日志，这里只验证 API 已正确解析
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            var faces = Json(Run(new JObject { ["action"] = "get_faces", ["name"] = name }))["data"]["faces"];
            var idx = faces[0]["indices"].Select(t => t.Value<int>()).ToArray();
            var bevel = Run(new JObject
            {
                ["action"] = "bevel_edges",
                ["name"] = name,
                ["edge_indices"] = new JArray(new JObject { ["a"] = idx[0], ["b"] = idx[1] })
            });
            // 不论该边能否倒角，都不应再出现"找不到 API"或重载歧义
            StringAssert.DoesNotContain("does not expose the expected API", bevel.Error ?? string.Empty);
            StringAssert.DoesNotContain("Ambiguous", bevel.Error ?? string.Empty);

            var bridge = Run(new JObject
            {
                ["action"] = "bridge_edges",
                ["name"] = name,
                ["edge_indices"] = new JArray(new JObject { ["a"] = 0, ["b"] = 1 }, new JObject { ["a"] = 2, ["b"] = 3 })
            });
            StringAssert.DoesNotContain("does not expose the expected API", bridge.Error ?? string.Empty);
            StringAssert.DoesNotContain("Ambiguous", bridge.Error ?? string.Empty);
        }

        #endregion
    }
}
