using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Infomatik.AutoCAD.Mcp.Tools;

// Sentinel-file pattern for synchronous-feeling execution.
//
// AutoCAD's SendStringToExecute is fire-and-forget: it queues a string into the
// command processor and returns immediately. There is no public hook for
// "queue drained" or "this is what the command printed".
//
// Workaround: append a small AutoLISP form to the queued stream that writes a
// temp file. AutoCAD evaluates LISP forms inline in the command stream, so the
// marker file only appears after every preceding input has been consumed.
// We poll for the file → the tool call blocks until AutoCAD is done.
//
// Caveat handled here: AutoLISP is also evaluated *transparently* at a command
// prompt. If the caller forgot the final ENTER, the marker runs while the command
// is still active. The marker therefore records CMDACTIVE so we can detect that
// case and report it (and by default cancel the dangling command).
//
// For eval_lisp the same trick captures the expression's value, wrapped in
// vl-catch-all-apply so runtime errors land in the file as "ERROR: ...".
internal static class AcadExecutor
{
    /// <summary>Two ESC characters: cancels any active command, like pressing ESC twice.</summary>
    private const string CancelSequence = "\x03\x03";

    public sealed record CommandResult(string DocName, TimeSpan Elapsed, bool StillActive, bool Cancelled);
    public sealed record LispResult(string Value, bool IsError);

    static AcadExecutor()
    {
        // Needed for legacy ANSI code pages (e.g. 1251) when LISPSYS = 0.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<CommandResult> ExecuteCommandAsync(
        string command, bool activate, bool echo, bool cancelPending, bool suppressDialogs,
        bool cancelIfIncomplete, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command))
            throw new ArgumentException("command must not be empty.");

        var markerPath = NewTempPath();
        var stream = new StringBuilder();
        if (cancelPending) stream.Append(CancelSequence);
        if (suppressDialogs) stream.Append(DialogsOffLisp);
        stream.Append(EnsureTerminated(command));
        stream.Append(BuildMarkerLisp(markerPath));

        var docName = await SendAsync(stream.ToString(), activate, echo, ct).ConfigureAwait(false);

        var elapsed = await WaitForFileOrCancelAsync(markerPath, timeout, ct).ConfigureAwait(false);
        var marker = ReadAllText(markerPath).Trim();
        TryDelete(markerPath);

        // CMDACTIVE bit 1 = an ordinary command is still running (waiting for input).
        var stillActive = int.TryParse(marker, out var cmdActive) && (cmdActive & 1) != 0;
        var cancelled = false;
        if (stillActive && cancelIfIncomplete)
        {
            await SendAsync(CancelSequence + DialogsRestoreLisp, activate: false, echo: false, ct).ConfigureAwait(false);
            cancelled = true;
        }
        return new CommandResult(docName, elapsed, stillActive, cancelled);
    }

    public static async Task<LispResult> EvaluateLispAsync(
        string expression, bool cancelPending, TimeSpan timeout, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ArgumentException("expression must not be empty.");

        var resultPath = NewTempPath();
        var stream = (cancelPending ? CancelSequence : string.Empty) + BuildLispEvaluator(expression, resultPath);

        await SendAsync(stream, activate: false, echo: false, ct).ConfigureAwait(false);
        _ = await WaitForFileOrCancelAsync(resultPath, timeout, ct).ConfigureAwait(false);

        string content;
        try { content = ReadAllText(resultPath); }
        finally { TryDelete(resultPath); }

        // A successful result is serialized with vl-prin1-to-string, so a returned
        // string starts with a quote — "ERROR: " at the start is unambiguous.
        return new LispResult(content, content.StartsWith("ERROR: ", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------

    private static Task<string> SendAsync(string stream, bool activate, bool echo, CancellationToken ct)
        => AcadDispatcher.InvokeAsync(() =>
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument
                ?? throw new InvalidOperationException("No active AutoCAD document.");
            doc.SendStringToExecute(stream, activate, wrapUpInactiveDoc: false, echoCommand: echo);
            return doc.Name;
        }, ct);

    private static string EnsureTerminated(string command)
        => command.EndsWith("\n", StringComparison.Ordinal) ? command : command + "\n";

    // Save FILEDIA/CMDDIA once (guarded, so a previous run that never restored them
    // can't make us "restore" the zeros), then switch dialogs off.
    private const string DialogsOffLisp =
        "(progn (if (not #mcpdlg) (setq #mcpdlg (list (getvar \"FILEDIA\") (getvar \"CMDDIA\")))) " +
        "(setvar \"FILEDIA\" 0) (setvar \"CMDDIA\" 0) (princ))\n";

    private const string DialogsRestoreLisp =
        "(progn (if #mcpdlg (progn (setvar \"FILEDIA\" (car #mcpdlg)) (setvar \"CMDDIA\" (cadr #mcpdlg)) (setq #mcpdlg nil))) (princ))\n";

    private static string BuildMarkerLisp(string markerPath)
    {
        var p = EscapeLispString(markerPath);
        // Writes CMDACTIVE (plain digits → no encoding concerns). (princ) at the tail
        // suppresses the trailing 'nil' echo from the progn.
        return
            "(progn (setq #mcpca (getvar \"CMDACTIVE\")) " +
            "(if #mcpdlg (progn (setvar \"FILEDIA\" (car #mcpdlg)) (setvar \"CMDDIA\" (cadr #mcpdlg)) (setq #mcpdlg nil))) " +
            $"(setq #mcpf (open \"{p}\" \"w\")) (princ #mcpca #mcpf) (close #mcpf) (princ))\n";
    }

    private static string BuildLispEvaluator(string userExpression, string outPath)
    {
        var p = EscapeLispString(outPath);
        // With LISPSYS 1/2 (Unicode AutoLISP, the default since AutoCAD 2021) write UTF-8
        // with BOM so non-ASCII text (e.g. Cyrillic layer names) survives. With legacy
        // LISPSYS 0 `open` has no encoding argument; the file is then in the system ANSI
        // code page, which ReadAllText detects by the missing BOM.
        return
            "(progn (vl-load-com) " +
            $"(setq #mcpr (vl-catch-all-apply (function (lambda () {userExpression})))) " +
            $"(setq #mcpf (if (= (getvar \"LISPSYS\") 0) (open \"{p}\" \"w\") (open \"{p}\" \"w\" \"utf8-bom\"))) " +
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

    /// <summary>Reads a file written by AutoLISP: UTF-8 if it has a BOM, otherwise the system ANSI code page.</summary>
    private static string ReadAllText(string path)
    {
        byte[] bytes;
        // FileShare.ReadWrite in case AutoCAD still holds a handle for a few ms.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        return AnsiEncoding.GetString(bytes);
    }

    private static Encoding AnsiEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(GetACP()); }
            catch { return Encoding.UTF8; }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetACP();

    private static async Task<TimeSpan> WaitForFileOrCancelAsync(string path, TimeSpan timeout, CancellationToken ct)
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

        // Timed out: AutoCAD is busy, blocked in a modal dialog, or our marker was eaten
        // as input by a prompt (e.g. getstring). Cancel whatever is running so the next
        // call doesn't feed into a dangling prompt, and clean up the late marker file.
        _ = AcadDispatcherCancelBestEffort();
        _ = DeleteWhenItAppearsAsync(path, TimeSpan.FromMinutes(2));

        throw new TimeoutException(
            $"AutoCAD did not finish within {timeout.TotalSeconds:F1}s; the active command was cancelled (ESC ESC). " +
            "Typical causes: a command still waiting for input (add the missing input or a final '\\n'), " +
            "a modal dialog, or a long-running operation (raise `timeoutSeconds`).");
    }

    private static Task AcadDispatcherCancelBestEffort()
        => SendAsync(CancelSequence + DialogsRestoreLisp, activate: false, echo: false, CancellationToken.None)
            .ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe, never throw

    private static async Task DeleteWhenItAppearsAsync(string path, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) { await Task.Delay(100).ConfigureAwait(false); TryDelete(path); return; }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leftovers in %TEMP% are harmless */ }
    }
}
