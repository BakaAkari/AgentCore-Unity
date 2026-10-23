using System;
using System.IO;
using NUnit.Framework;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// SafePath 路径规范化与根约束的单元测试。
    /// </summary>
    [TestFixture]
    public class SafePathTests
    {
        private static string Root => SafePath.ProjectRoot;

        #region IsUnder

        [Test]
        public void IsUnder_SiblingWithSamePrefix_ReturnsFalse()
        {
            Assert.IsFalse(SafePath.IsUnder(@"D:\Proj2\file.txt", @"D:\Proj"));
        }

        [Test]
        public void IsUnder_ChildAndRootItself_ReturnsTrue()
        {
            Assert.IsTrue(SafePath.IsUnder(@"D:\Proj\Assets\a.cs", @"D:\Proj"));
            Assert.IsTrue(SafePath.IsUnder(@"D:\Proj", @"D:\Proj\"));
            Assert.IsTrue(SafePath.IsUnder("d:/proj/assets", @"D:\Proj"));
        }

        #endregion

        #region TryResolveUnder

        [Test]
        public void TryResolveUnder_DotDotEscape_Fails()
        {
            Assert.IsFalse(SafePath.TryResolveUnder(Root, "Assets/../../outside.txt", out _, out var error));
            StringAssert.Contains("outside", error);
        }

        [Test]
        public void TryResolveUnder_SiblingDirectoryWithSamePrefix_Fails()
        {
            var sibling = Root + "2/file.txt";
            Assert.IsFalse(SafePath.TryResolveUnder(Root, sibling, out _, out _));
        }

        [Test]
        public void TryResolveUnder_DotDotStayingInside_Succeeds()
        {
            Assert.IsTrue(SafePath.TryResolveUnder(Root, "Assets/Foo/../Bar.cs", out var full, out _));
            Assert.AreEqual(Path.GetFullPath(Path.Combine(Root, "Assets/Bar.cs")), full);
        }

        [TestCase("Assets/a.txt:hidden")]
        [TestCase("Assets/a\0.txt")]
        [TestCase(@"\\?\C:\Windows\win.ini")]
        [TestCase(@"\\.\PhysicalDrive0")]
        public void TryResolveUnder_IllegalSyntax_Fails(string input)
        {
            Assert.IsFalse(SafePath.TryResolveUnder(Root, input, out _, out _));
        }

        [TestCase(".env")]
        [TestCase("Assets/.env.local")]
        [TestCase("Assets/.env.")]
        [TestCase("Assets/keys/id_rsa")]
        [TestCase("Assets/cert.pem")]
        [TestCase(".git/config")]
        public void TryResolveUnder_SecretFiles_ResolveButAreFlagged(string input)
        {
            // 机密文件不再硬拒绝：可以解析，由 IsForbidden 标记后走确认
            Assert.IsTrue(SafePath.TryResolveUnder(Root, input, out var full, out _));
            Assert.IsTrue(SafePath.IsForbidden(full));
        }

        #endregion

        #region ToAssetPath / NormalizeAssetPath

        [TestCase("Scripts/Foo.cs", "Assets/Scripts/Foo.cs")]
        [TestCase("Assets/Scripts/Foo.cs", "Assets/Scripts/Foo.cs")]
        [TestCase(@"Assets\Scripts\Foo.cs", "Assets/Scripts/Foo.cs")]
        [TestCase("Assets", "Assets")]
        [TestCase("Packages/com.example/Runtime/A.cs", "Packages/com.example/Runtime/A.cs")]
        [TestCase("Assets/Foo/../Bar.cs", "Assets/Bar.cs")]
        public void NormalizeAssetPath_ValidInputs_ReturnsAssetPath(string input, string expected)
        {
            Assert.AreEqual(expected, ToolHelpers.NormalizeAssetPath(input));
        }

        [Test]
        public void NormalizeAssetPath_AbsolutePathInsideProject_ReturnsRelative()
        {
            Assert.AreEqual("Assets/Scripts/Foo.cs", ToolHelpers.NormalizeAssetPath(Root + "/Assets/Scripts/Foo.cs"));
        }

        [TestCase("Assets/../../outside.cs")]
        [TestCase("../outside.cs")]
        [TestCase("Assets/../ProjectSettings/ProjectSettings.asset")]
        [TestCase(@"C:\Windows\System32\drivers\etc\hosts")]
        public void NormalizeAssetPath_Escapes_Throw(string input)
        {
            Assert.Throws<ArgumentException>(() => ToolHelpers.NormalizeAssetPath(input));
        }

        [Test]
        public void ToAssetPath_PackagesDisallowed_Throws()
        {
            Assert.Throws<ArgumentException>(() => SafePath.ToAssetPath("Packages/com.example/a.uxml", allowPackages: false));
        }

        #endregion

        #region IsProtected

        [TestCase("ProjectSettings/ProjectSettings.asset", true)]
        [TestCase("Packages/manifest.json", true)]
        [TestCase("Assets/Game/Game.asmdef", true)]
        [TestCase("Assets/Game/Editor/Tool.cs", true)]
        [TestCase(".git/hooks/pre-commit", true)]
        [TestCase("Assets/Game/Player.cs", false)]
        [TestCase("Assets/Editor.cs", false)]
        public void IsProtected_ClassifiesPaths(string path, bool expected)
        {
            Assert.AreEqual(expected, SafePath.IsProtected(path));
        }

        #endregion
    }
}
