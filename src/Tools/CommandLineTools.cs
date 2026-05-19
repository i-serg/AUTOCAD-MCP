using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Infomatik.AutoCAD.Mcp.Tools;

internal sealed class CommandLineTools
{
    public JsonObject List() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "send_command",
                ["description"] =
                    "Execute a command sequence on the active AutoCAD document's command line. " +
                    "Pass the input exactly as a user would type it. Use '\\n' for ENTER between arguments. " +
                    "Example: \"_LINE\\n0,0\\n100,100\\n\\n\" draws a line and exits the LINE command. " +
                    "The call blocks until AutoCAD has finished processing the input. " +
                    "For reading drawing data (system variables, entity properties, layers, ...), use `eval_lisp` instead — " +
                    "it returns structured values, whereas send_command returns only an 'executed' status.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("command"),
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Raw command-line input. A trailing '\\n' is appended automatically if missing."
                        },
                        ["activate"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Bring the AutoCAD window to the foreground before sending.",
                            ["default"] = false
                        },
                        ["echo"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Echo the input on the command line.",
                            ["default"] = true
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Maximum time to wait for AutoCAD to finish. Default 30, max 600.",
                            ["default"] = 30
                        }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "eval_lisp",
                ["description"] =
                    "Evaluate an AutoLISP expression in the active document and return its value as a string. " +
                    "This is the right tool for reading drawing data. Examples:\n" +
                    "  - \"(getvar \\\"DWGNAME\\\")\" → current filename\n" +
                    "  - \"(getvar \\\"CLAYER\\\")\" → current layer\n" +
                    "  - \"(entget (entlast))\" → DXF group codes of the most recent entity\n" +
                    "  - \"(vla-get-Count (vla-get-ModelSpace (vla-get-ActiveDocument (vlax-get-acad-object))))\" → model-space entity count\n" +
                    "The expression runs inside vl-catch-all-apply, so LISP runtime errors return as \"ERROR: <message>\" " +
                    "instead of breaking the command stream. Result is serialized with vl-prin1-to-string.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("expression"),
                    ["properties"] = new JsonObject
                    {
                        ["expression"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "A single AutoLISP form, e.g. \"(getvar \\\"DWGNAME\\\")\"."
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Maximum time to wait. Default 30, max 600.",
                            ["default"] = 30
                        }
                    }
                }
            },
            new JsonObject
            {
                ["name"] = "list_documents",
                ["description"] = "List all open AutoCAD documents with their filenames.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["name"] = "get_active_document",
                ["description"] = "Get the filename of the currently active AutoCAD document, or empty if none.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject()
                }
            }
        }
    };

    public async Task<JsonObject> CallAsync(JsonNode? @params, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>()
            ?? throw new ArgumentException("tools/call requires a 'name' parameter.");
        var args = @params?["arguments"] as JsonObject ?? new JsonObject();

        return name switch
        {
            "send_command" => await SendCommandAsync(args, ct).ConfigureAwait(false),
            "eval_lisp" => await EvalLispAsync(args, ct).ConfigureAwait(false),
            "list_documents" => await ListDocumentsAsync(ct).ConfigureAwait(false),
            "get_active_document" => await GetActiveDocumentAsync(ct).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown tool: " + name)
        };
    }

    private static async Task<JsonObject> SendCommandAsync(JsonObject args, CancellationToken ct)
    {
        var command = args["command"]?.GetValue<string>()
            ?? throw new ArgumentException("send_command requires a 'command' string.");
        var activate = args["activate"]?.GetValue<bool>() ?? false;
        var echo = args["echo"]?.GetValue<bool>() ?? true;
        var timeout = ParseTimeout(args);

        var (docName, elapsed) = await AcadExecutor
            .ExecuteCommandAsync(command, activate, echo, timeout, ct)
            .ConfigureAwait(false);

        return TextResult($"executed in '{docName}' ({elapsed.TotalSeconds:F2}s)");
    }

    private static async Task<JsonObject> EvalLispAsync(JsonObject args, CancellationToken ct)
    {
        var expr = args["expression"]?.GetValue<string>()
            ?? throw new ArgumentException("eval_lisp requires an 'expression' string.");
        var timeout = ParseTimeout(args);

        var result = await AcadExecutor.EvaluateLispAsync(expr, timeout, ct).ConfigureAwait(false);
        return TextResult(result);
    }

    private static async Task<JsonObject> ListDocumentsAsync(CancellationToken ct)
    {
        var arr = await AcadDispatcher.InvokeAsync(() =>
        {
            var a = new JsonArray();
            foreach (Document d in AcadApp.DocumentManager) a.Add(d.Name);
            return a;
        }, ct).ConfigureAwait(false);

        return TextResult(arr.ToJsonString());
    }

    private static async Task<JsonObject> GetActiveDocumentAsync(CancellationToken ct)
    {
        var name = await AcadDispatcher.InvokeAsync(
            () => AcadApp.DocumentManager.MdiActiveDocument?.Name ?? string.Empty,
            ct).ConfigureAwait(false);

        return TextResult(name);
    }

    private static TimeSpan ParseTimeout(JsonObject args)
    {
        var seconds = args["timeoutSeconds"]?.GetValue<double>() ?? 30.0;
        seconds = Math.Clamp(seconds, 1.0, 600.0);
        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonObject TextResult(string text) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text }
        }
    };
}
