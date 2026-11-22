using System;
using System.Collections.Generic;
using NUnit.Framework;
using AgentCore.Editor.Extensions;
using UnityEditor;

namespace AgentCore.Tests.Editor.Extensions
{
    /// <summary>
    /// OptionalComponentManager 的单元测试：按工程隔离的 EditorPrefs 开关、默认值、状态变更事件，
    /// 以及切换组件不改写 ProjectSettings 的 scripting defines。
    /// </summary>
    [TestFixture]
    public class OptionalComponentManagerTests
    {
        private static readonly string[] ComponentIds =
        {
            OptionalComponentManager.VcsComponentId,
            OptionalComponentManager.IndexingComponentId,
            OptionalComponentManager.McpComponentId
        };

        private readonly List<string> _prefsKeysToDelete = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (var key in _prefsKeysToDelete)
                EditorPrefs.DeleteKey(key);
            _prefsKeysToDelete.Clear();
        }

        // ── 默认值 ──

        [Test]
        public void Defaults_VcsOn_IndexingOff_McpOff()
        {
            Assert.IsTrue(OptionalComponentManager.GetDefaultEnabled(OptionalComponentManager.VcsComponentId));
            Assert.IsFalse(OptionalComponentManager.GetDefaultEnabled(OptionalComponentManager.IndexingComponentId));
            Assert.IsFalse(OptionalComponentManager.GetDefaultEnabled(OptionalComponentManager.McpComponentId));
        }

        [Test]
        public void ResolveInitialState_NoLegacyState_UsesDefaults()
        {
            Assert.IsTrue(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.VcsComponentId, false, false));
            Assert.IsFalse(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.IndexingComponentId, false, false));
            Assert.IsFalse(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.McpComponentId, false, false));
        }

        [Test]
        public void ResolveInitialState_LegacyState_IsCarriedOver()
        {
            Assert.IsFalse(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.VcsComponentId, false, legacyVcsUserDisabled: true));
            Assert.IsTrue(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.IndexingComponentId, legacyDefinePresent: true, false));
            Assert.IsTrue(OptionalComponentManager.ResolveInitialState(OptionalComponentManager.McpComponentId, legacyDefinePresent: true, false));
        }

        [Test]
        public void UnstoredPrefs_ReadBackAsDefaults()
        {
            var projectKey = OptionalComponentManager.ComputeProjectKey("Z:/agentcore-tests/" + Guid.NewGuid().ToString("N") + "/Assets");
            foreach (var id in ComponentIds)
            {
                var key = OptionalComponentManager.GetPrefsKey(id, projectKey);
                _prefsKeysToDelete.Add(key);
                Assert.IsFalse(EditorPrefs.HasKey(key));
                Assert.AreEqual(OptionalComponentManager.GetDefaultEnabled(id), EditorPrefs.GetBool(key, OptionalComponentManager.GetDefaultEnabled(id)), id);
            }
        }

        // ── 按工程隔离 ──

        [Test]
        public void ProjectKey_IsStableAndDiffersPerProject()
        {
            var a1 = OptionalComponentManager.ComputeProjectKey("C:/Projects/A/Assets");
            var a2 = OptionalComponentManager.ComputeProjectKey("C:/Projects/A/Assets");
            var b = OptionalComponentManager.ComputeProjectKey("C:/Projects/B/Assets");

            Assert.AreEqual(a1, a2);
            Assert.AreNotEqual(a1, b);
            Assert.AreEqual(16, a1.Length);
            StringAssert.IsMatch("^[0-9a-f]{16}$", a1);
        }

        [Test]
        public void PrefsKeys_UseExpectedPrefixes()
        {
            Assert.AreEqual("AgentCore.Component.Vcs.k", OptionalComponentManager.GetPrefsKey(OptionalComponentManager.VcsComponentId, "k"));
            Assert.AreEqual("AgentCore.Component.Indexing.k", OptionalComponentManager.GetPrefsKey(OptionalComponentManager.IndexingComponentId, "k"));
            Assert.AreEqual("AgentCore.Component.Mcp.k", OptionalComponentManager.GetPrefsKey(OptionalComponentManager.McpComponentId, "k"));
            Assert.IsNull(OptionalComponentManager.GetPrefsKey("unknown", "k"));
        }

        [Test]
        public void PrefsWrite_ForOneProject_DoesNotAffectAnother()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var keyA = OptionalComponentManager.ComputeProjectKey("Z:/agentcore-tests/A-" + suffix + "/Assets");
            var keyB = OptionalComponentManager.ComputeProjectKey("Z:/agentcore-tests/B-" + suffix + "/Assets");
            var prefsA = OptionalComponentManager.GetPrefsKey(OptionalComponentManager.McpComponentId, keyA);
            var prefsB = OptionalComponentManager.GetPrefsKey(OptionalComponentManager.McpComponentId, keyB);
            _prefsKeysToDelete.Add(prefsA);
            _prefsKeysToDelete.Add(prefsB);

            EditorPrefs.SetBool(prefsA, true);

            Assert.AreNotEqual(prefsA, prefsB);
            Assert.IsTrue(EditorPrefs.GetBool(prefsA, false));
            Assert.IsFalse(EditorPrefs.HasKey(prefsB));
        }

        // ── 程序集映射 / 未知组件 ──

        [Test]
        public void AssemblyNames_MapToComponentIds()
        {
            Assert.AreEqual(OptionalComponentManager.VcsComponentId, OptionalComponentManager.GetComponentIdForAssembly("AgentCore.VCS.Editor"));
            Assert.AreEqual(OptionalComponentManager.IndexingComponentId, OptionalComponentManager.GetComponentIdForAssembly("AgentCore.Indexing.Editor"));
            Assert.AreEqual(OptionalComponentManager.McpComponentId, OptionalComponentManager.GetComponentIdForAssembly("AgentCore.MCP.Editor"));
            Assert.IsNull(OptionalComponentManager.GetComponentIdForAssembly("AgentCore.Editor"));
            Assert.IsNull(OptionalComponentManager.GetComponentIdForAssembly(null));
            Assert.IsTrue(OptionalComponentManager.IsAssemblyEnabled(typeof(OptionalComponentManager).Assembly));
        }

        [Test]
        public void UnknownComponent_IsDisabled_AndCannotBeSet()
        {
            Assert.IsFalse(OptionalComponentManager.IsComponentEnabled("unknown"));
            Assert.Throws<ArgumentException>(() => OptionalComponentManager.SetComponentEnabled("unknown", true));
        }

        // ── 切换：事件 + 不改写 ProjectSettings ──

        [Test]
        public void Toggle_RaisesComponentStateChanged_AndLeavesScriptingDefinesUntouched()
        {
            var definesBefore = CaptureDefines();
            var original = new Dictionary<string, bool>();
            foreach (var id in ComponentIds)
                original[id] = OptionalComponentManager.IsComponentEnabled(id);

            var events = new List<(string id, bool enabled)>();
            Action<string, bool> handler = (id, enabled) => events.Add((id, enabled));
            OptionalComponentManager.ComponentStateChanged += handler;
            try
            {
                foreach (var id in ComponentIds)
                {
                    events.Clear();
                    OptionalComponentManager.SetComponentEnabled(id, !original[id]);
                    Assert.AreEqual(!original[id], OptionalComponentManager.IsComponentEnabled(id), id);
                    CollectionAssert.AreEqual(new[] { (id, !original[id]) }, events, id);

                    events.Clear();
                    OptionalComponentManager.SetComponentEnabled(id, original[id]);
                    Assert.AreEqual(original[id], OptionalComponentManager.IsComponentEnabled(id), id);
                    CollectionAssert.AreEqual(new[] { (id, original[id]) }, events, id);

                    // 已是目标状态：不重复触发
                    events.Clear();
                    OptionalComponentManager.SetComponentEnabled(id, original[id]);
                    CollectionAssert.IsEmpty(events, id);
                }
            }
            finally
            {
                OptionalComponentManager.ComponentStateChanged -= handler;
                foreach (var kvp in original)
                    OptionalComponentManager.SetComponentEnabled(kvp.Key, kvp.Value);
            }

            CollectionAssert.AreEquivalent(definesBefore, CaptureDefines());
        }

        private static Dictionary<BuildTargetGroup, string> CaptureDefines()
        {
            var result = new Dictionary<BuildTargetGroup, string>();
            foreach (var group in OptionalComponentManager.GetValidBuildTargetGroups())
            {
                try
                {
                    result[group] = ScriptingDefineHelper.GetDefines(group) ?? string.Empty;
                }
                catch (Exception)
                {
                    // 当前 Unity 不支持的分组：无 defines 可比较
                }
            }
            return result;
        }

        // ── 旧版宏清理（纯函数） ──

        [Test]
        public void RemoveLegacyDefines_NothingToRemove_ReturnsSameInstance()
        {
            const string raw = "FOO;BAR";
            Assert.AreSame(raw, OptionalComponentManager.RemoveLegacyDefines(raw));
            Assert.AreEqual(string.Empty, OptionalComponentManager.RemoveLegacyDefines(string.Empty));
            Assert.IsNull(OptionalComponentManager.RemoveLegacyDefines(null));
        }

        [Test]
        public void RemoveLegacyDefines_RemovesOnlyLegacySymbols_KeepingOrder()
        {
            var result = OptionalComponentManager.RemoveLegacyDefines("ZED;AGENTCORE_VCS;ALPHA; AGENTCORE_INDEXING ;AGENTCORE_MCP;AGENTCORE_OTHER");
            Assert.AreEqual("ZED;ALPHA;AGENTCORE_OTHER", result);
        }

        [Test]
        public void RemoveLegacyDefines_OnlyLegacySymbols_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, OptionalComponentManager.RemoveLegacyDefines("AGENTCORE_VCS;AGENTCORE_MCP"));
        }
    }
}
