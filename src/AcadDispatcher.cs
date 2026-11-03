using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Infomatik.AutoCAD.Mcp;

// AutoCAD's managed API is single-threaded — every call must run on the main
// (document) thread. HTTP requests arrive on thread-pool threads, so we queue
// work here and drain it from AcadApp.Idle, which fires on the main thread.
//
// Note: Idle does not fire while AutoCAD shows a modal dialog or is busy, so
// queued work waits until AutoCAD is responsive again.
internal static class AcadDispatcher
{
    private sealed record WorkItem(Action Run, Action<Exception> Fail);

    private static readonly ConcurrentQueue<WorkItem> _queue = new();
    private static int _running;

    public static bool IsRunning => Volatile.Read(ref _running) == 1;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        AcadApp.Idle += OnIdle;
    }

    public static void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        AcadApp.Idle -= OnIdle;
        // Fail pending work instead of leaving callers waiting forever.
        var stopped = new ObjectDisposedException(nameof(AcadDispatcher), "The MCP dispatcher was stopped.");
        while (_queue.TryDequeue(out var item))
        {
            try { item.Fail(stopped); } catch { }
        }
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        // Drain a small batch per Idle tick so we never starve AutoCAD.
        for (int i = 0; i < 16; i++)
        {
            if (!_queue.TryDequeue(out var item)) break;
            try { item.Run(); } catch { /* errors are surfaced via the TaskCompletionSource */ }
        }
    }

    public static Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (!IsRunning)
            return Task.FromException<T>(new InvalidOperationException("The MCP dispatcher is not running."));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(new WorkItem(
            Run: () =>
            {
                if (tcs.Task.IsCompleted) return;
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            Fail: ex => tcs.TrySetException(ex)));
        return tcs.Task;
    }
}
