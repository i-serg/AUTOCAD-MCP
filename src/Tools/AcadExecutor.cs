using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Infomatik.AutoCAD.Mcp.Tools;

// Sentinel-file pattern for synchronous-feeling execution.
//
// AutoCAD's SendStringToExecute is fire-and-forget: it queues a string into the
// command processor and returns immediately. There is no public hook for
// "queue drained" or "this is what the command printed".
//
// Workaround: append a small AutoLISP form to the queued stream that opens a
// temp file and closes it. AutoCAD evaluates LISP forms inline in the command
// stream, so the marker file only appears after every preceding command has
// finished. We poll for the file → the tool call blocks until AutoCAD is done.
//
// For eval_lisp the same trick captures the expression's value, wrapped in
// vl-catch-all-apply so runtime errors land in the file as "ERROR: ...".
internal static class AcadExecutor
{
    public static async Task<(string DocName, TimeSpan Elapsed)> ExecuteCommandAsync(
        string command, bool activate, bool echo, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command))
            throw new ArgumentException("command must not be empty.");

        var markerPath = NewTempPath();
        var stream = EnsureTerminated(command) + BuildMarkerLisp(markerPath);

        var docName = await AcadDispatcher.InvokeAsync(() =>
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("No active AutoCAD document.");
            doc.SendStringToExecute(stream, activate, wrapUpInactiveDoc: false, echoCommand: echo);
            return doc.Name;
        }, ct).ConfigureAwait(false);

        var elapsed = await WaitForFileAsync(markerPath, timeout, ct).ConfigureAwait(false);
        TryDelete(markerPath);
        return (docName, elapsed);
    }

    public static async Task<string> EvaluateLispAsync(
        string expression, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("expression must not be empty.");

        var resultPath = NewTempPath();
        var stream = BuildLispEvaluator(expression, resultPath);

        await AcadDispatcher.InvokeAsync(() =>
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("No active AutoCAD document.");
            doc.SendStringToExecute(stream, activate: false, wrapUpInactiveDoc: false, echoCommand: false);
            return 0;
        }, ct).ConfigureAwait(false);

        _ = await WaitForFileAsync(resultPath, timeout, ct).ConfigureAwait(false);

        string content;
        try
        {
            // FileShare.ReadWrite in case AutoCAD still holds a handle for a few ms.
            using var fs = new FileStream(resultPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            content = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        finally { TryDelete(resultPath); }
        return content;
    }

    private static string EnsureTerminated(string command)
        => command.EndsWith("\n", StringComparison.Ordinal) ? command : command + "\n";

    private static string BuildMarkerLisp(string markerPath)
    {
        var p = EscapeLispString(markerPath);
        // (princ) at the tail suppresses the trailing 'nil' echo from the progn.
        return $"(progn (setq #mcpf (open \"{p}\" \"w\")) (close #mcpf) (princ))\n";
    }

    private static string BuildLispEvaluator(string userExpression, string outPath)
    {
        var p = EscapeLispString(outPath);
        return
            "(progn (vl-load-com) " +
            $"(setq #mcpr (vl-catch-all-apply (function (lambda () {userExpression})))) " +
            $"(setq #mcpf (open \"{p}\" \"w\")) " +
            "(princ (if (vl-catch-all-error-p #mcpr) " +
            "          (strcat \"ERROR: \" (vl-catch-all-error-message #mcpr)) " +
            "          (vl-prin1-to-string #mcpr)) " +
            "       #mcpf) " +
            "(close #mcpf) (princ))\n";
    }

    private static string EscapeLispString(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string NewTempPath()
        => Path.Combine(Path.GetTempPath(), $"acadmcp_{Guid.NewGuid():N}.txt");

    private static async Task<TimeSpan> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var deadline = start + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                // Tiny grace period so AutoCAD's (close ...) finishes flushing.
                await Task.Delay(25, ct).ConfigureAwait(false);
                return DateTime.UtcNow - start;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"AutoCAD did not finish within {timeout.TotalSeconds:F1}s. " +
            "A command is probably waiting for further input — terminate it with an additional '\\n' " +
            "in your `command` argument, or raise `timeoutSeconds`.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leftovers in %TEMP% are harmless */ }
    }
}
