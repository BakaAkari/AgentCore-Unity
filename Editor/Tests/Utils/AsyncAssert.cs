using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AgentCore.Tests.Editor
{
    /// <summary>
    /// 可 await 的异常断言。NUnit 的 Assert.ThrowsAsync/CatchAsync 会在主线程同步阻塞等待，
    /// 被测代码的 await 续延投递到 UnitySynchronizationContext 后永远得不到执行，测试死锁。
    /// 测试方法改为 async Task 并 await 这里的断言即可。语义与 NUnit 一致：Throws 要求精确类型，Catch 接受派生类型。
    /// </summary>
    public static class AsyncAssert
    {
        public static async Task<T> ThrowsAsync<T>(Func<Task> code) where T : Exception
        {
            var ex = await Capture(code);
            Assert.IsNotNull(ex, $"Expected {typeof(T).Name} but no exception was thrown");
            Assert.AreEqual(typeof(T), ex.GetType(), $"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
            return (T)ex;
        }

        public static async Task<T> CatchAsync<T>(Func<Task> code) where T : Exception
        {
            var ex = await Capture(code);
            Assert.IsNotNull(ex, $"Expected {typeof(T).Name} but no exception was thrown");
            Assert.IsInstanceOf<T>(ex, $"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
            return (T)ex;
        }

        private static async Task<Exception> Capture(Func<Task> code)
        {
            try
            {
                await code();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
