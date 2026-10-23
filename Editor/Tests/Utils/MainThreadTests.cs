using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using AgentCore.Editor.Utils;

namespace AgentCore.Tests.Editor.Utils
{
    /// <summary>
    /// MainThread 调度器的单元测试（EditMode 测试本身跑在主线程）。
    /// </summary>
    [TestFixture]
    public class MainThreadTests
    {
        [Test]
        public void IsMainThread_OnTestThread_IsTrue()
        {
            Assert.IsTrue(MainThread.IsMainThread);
        }

        [Test]
        public void RunAsync_OnMainThread_ExecutesInline()
        {
            var task = MainThread.RunAsync(() => 42);
            Assert.IsTrue(task.IsCompleted);
            Assert.AreEqual(42, task.Result);
        }

        [Test]
        public void RunAsync_OnMainThread_ExceptionPropagatesToTask()
        {
            var task = MainThread.RunAsync<int>(() => throw new InvalidOperationException("boom"));
            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOf<InvalidOperationException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator RunAsync_FromBackgroundThread_RunsOnMainThreadAndPropagatesException()
        {
            var ok = Task.Run(() => MainThread.RunAsync(() => MainThread.IsMainThread));
            var bad = Task.Run(() => MainThread.RunAsync<int>(() => throw new InvalidOperationException("bg")));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((!ok.IsCompleted || !bad.IsCompleted) && DateTime.UtcNow < deadline)
                yield return null;

            Assert.IsTrue(ok.IsCompleted, "background RunAsync was not pumped");
            Assert.IsTrue(ok.Result, "work did not run on main thread");
            Assert.IsTrue(bad.IsFaulted);
            Assert.IsInstanceOf<InvalidOperationException>(bad.Exception.InnerException);
        }
    }
}
