namespace Fadenstack.Agents.Delegation;

internal static class TaskExtensions
{
    /// <summary>Waits for the task for at most <paramref name="limit"/>; throws <see cref="TimeoutException"/> after that.</summary>
    public static async Task WaitAsyncWithin(this Task task, TimeSpan limit)
    {
        var finished = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (finished != task)
        {
            throw new TimeoutException();
        }
        await task.ConfigureAwait(false);
    }
}
