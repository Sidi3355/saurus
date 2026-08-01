using System.Collections.Concurrent;
using Saurus.Core;

namespace Saurus.Capture;

/// <summary>
/// Runs UI Automation work on a dedicated MTA thread with a hard time budget.
///
/// Two things make this necessary. First, UIA calls are cross-process COM calls into the
/// target application; against a busy or hung app a single property read can block for
/// seconds, and it must never do that on the UI thread. Second, a blocked COM call cannot
/// be cancelled — so when a job overruns its budget we abandon the whole thread and start
/// a fresh one rather than waiting for it. The abandoned thread is a background thread and
/// dies with the process.
/// </summary>
public sealed class UiaWorker : IDisposable
{
    private sealed class Job
    {
        public required Func<object?> Work;
        public readonly TaskCompletionSource<object?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private BlockingCollection<Job> _queue = new();
    private Thread _thread;
    private readonly object _swapGate = new();
    private volatile bool _disposed;
    private int _generation;

    public UiaWorker() => _thread = SpawnThread(_queue);

    private Thread SpawnThread(BlockingCollection<Job> queue)
    {
        var gen = Interlocked.Increment(ref _generation);
        var t = new Thread(() => Loop(queue))
        {
            IsBackground = true,
            Name = $"saurus-uia-{gen}"
        };
        // The managed UIA client wrapper wants MTA; an STA thread here deadlocks under load.
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        return t;
    }

    private static void Loop(BlockingCollection<Job> queue)
    {
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                try { job.Completion.TrySetResult(job.Work()); }
                catch (Exception ex) { job.Completion.TrySetException(ex); }
            }
        }
        catch (ObjectDisposedException) { /* queue retired under us; thread exits */ }
        catch (InvalidOperationException) { /* completed collection; thread exits */ }
    }

    /// <summary>
    /// Queues <paramref name="work"/> and waits at most <paramref name="budget"/>.
    /// Returns default on timeout, after replacing the worker thread.
    /// </summary>
    public async Task<T?> RunAsync<T>(Func<T> work, TimeSpan budget)
    {
        if (_disposed) return default;

        var job = new Job { Work = () => work() };

        try { _queue.Add(job); }
        catch (Exception) { return default; }   // queue retired mid-swap

        var completed = await Task.WhenAny(job.Completion.Task, Task.Delay(budget)).ConfigureAwait(false);

        if (completed != job.Completion.Task)
        {
            Log.Warn($"UIA call exceeded {budget.TotalMilliseconds:F0}ms budget; recycling worker thread");
            RecycleThread();
            return default;
        }

        try
        {
            var value = await job.Completion.Task.ConfigureAwait(false);
            return value is null ? default : (T)value;
        }
        catch (Exception ex) { Log.Error("UIA call failed", ex); return default; }
    }

    /// <summary>
    /// Abandons the stuck thread (it is still blocked inside COM and cannot be interrupted)
    /// and installs a fresh queue + thread so the next capture is not queued behind it.
    /// </summary>
    private void RecycleThread()
    {
        lock (_swapGate)
        {
            if (_disposed) return;
            var stale = _queue;
            _queue = new BlockingCollection<Job>();
            _thread = SpawnThread(_queue);

            // Retire the old queue. The stuck thread is still inside COM; when it returns it
            // drains whatever was behind it and then exits. Those callers have already timed
            // out and taken the default, so late results are simply dropped.
            try { stale.CompleteAdding(); } catch { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
    }
}
