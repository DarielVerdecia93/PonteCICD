namespace EnseCI;

/// <summary>Signals "something changed" to the panel (Server-Sent Events) so it refreshes at once.</summary>
public sealed class Notifier
{
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Changed()
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) =>
        Task.WhenAny(_changed.Task, Task.Delay(timeout, ct));
}
