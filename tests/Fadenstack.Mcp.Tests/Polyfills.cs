#if !NET
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// For init accessors on .NET Framework. The SDK's assemblies each hold one, internal and visible to these tests, so
    /// the compiler would not know which to take; this project's own decides.
    /// </summary>
    internal static class IsExternalInit
    {
    }
}

namespace Fadenstack.Agents.Tests
{
    /// <summary>Task.WaitAsync(TimeSpan) of .NET 6+, for the net48 run.</summary>
    internal static class TaskPolyfills
    {
        public static async Task WaitAsync(this Task task, TimeSpan timeout)
        {
            if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task)
            {
                throw new TimeoutException();
            }
            await task.ConfigureAwait(false);
        }

        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
        {
            await ((Task)task).WaitAsync(timeout).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
#endif
