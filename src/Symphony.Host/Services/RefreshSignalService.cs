namespace Symphony.Host.Services;

public sealed class RefreshSignalService(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private TaskCompletionSource<bool> signal = CreateSignal();
    private bool pending;

    public RefreshRequestResult RequestRefresh()
    {
        lock (sync)
        {
            var coalesced = pending;
            pending = true;
            signal.TrySetResult(true);

            return new RefreshRequestResult(
                Queued: true,
                Coalesced: coalesced,
                RequestedAt: timeProvider.GetUtcNow());
        }
    }

    public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Task signalTask;
        lock (sync)
        {
            signalTask = signal.Task;
        }

        var completedTask = await Task.WhenAny(signalTask, Task.Delay(delay, cancellationToken));
        if (completedTask == signalTask)
        {
            lock (sync)
            {
                pending = false;
                if (ReferenceEquals(signalTask, signal.Task))
                {
                    signal = CreateSignal();
                }
            }

            return;
        }

        await completedTask;
    }

    private static TaskCompletionSource<bool> CreateSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public readonly record struct RefreshRequestResult(
    bool Queued,
    bool Coalesced,
    DateTimeOffset RequestedAt);
