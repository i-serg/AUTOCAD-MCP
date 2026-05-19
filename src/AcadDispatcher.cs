using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Infomatik.AutoCAD.Mcp;

// AutoCAD's managed API is single-threaded — every call must run on the main
// (document) thread. HTTP requests arrive on thread-pool threads, so we queue
// work here and drain it from AcadApp.Idle, which fires on the main thread.
internal static class AcadDispatcher
{
    private static readonly ConcurrentQueue<Action> _queue = new();
    private static int _running;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        AcadApp.Idle += OnIdle;
    }

    public static void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        AcadApp.Idle -= OnIdle;
        while (_queue.TryDequeue(out _)) { }
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        // Drain a small batch per Idle tick so we never starve AutoCAD.
        for (int i = 0; i < 16; i++)
        {
            if (!_queue.TryDequeue(out var action)) break;
            try { action(); } catch { /* errors are surfaced via the TaskCompletionSource */ }
        }
    }

    public static Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(() =>
        {
            if (tcs.Task.IsCompleted) return;
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
