using System;
using System.Text.Json;
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
                    "Prefer underscore-prefixed (language-independent) command names, and '-' prefixed variants " +
                    "(e.g. \"-LAYER\") to avoid dialogs. " +
                    "The call blocks until AutoCAD has consumed all input. If the command is still waiting for input " +
                    "afterwards, it is cancelled and an error is returned (set cancelIfIncomplete=false to keep it open " +
                    "and continue it in the next call with cancelPending=false). " +
                    "For reading drawing data (system variables, entity properties, layers, ...), use `eval_lisp` instead.",
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
                        ["cancelPending"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Press ESC twice before sending, cancelling any command left open earlier.",
                            ["default"] = true
                        },
                        ["cancelIfIncomplete"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "If the command is still waiting for input after all input was consumed, cancel it and return an error.",
                            ["default"] = true
                        },
                        ["suppressDialogs"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Temporarily set FILEDIA=0 and CMDDIA=0 so commands prompt on the command line instead of opening modal dialogs. Restored afterwards.",
                            ["default"] = true
                        },
                        ["timeoutSeconds"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] = "Maximum time to wait for AutoCAD to finish. Default 30, max 600. On timeout the active command is cancelled.",
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
                    "This is the right tool for reading drawing data. Non-ASCII text (e.g. Cyrillic) is returned correctly. Examples:\n" +
                    "  - \"(getvar \\\"DWGNAME\\\")\" → current filename\n" +
                    "  - \"(getvar \\\"CLAYER\\\")\" → current layer\n" +
                    "  - \"(entget (entlast))\" → DXF group codes of the most recent entity\n" +
                    "  - \"(vla-get-Count (vla-get-ModelSpace (vla-get-ActiveDocument (vlax-get-acad-object))))\" → model-space entity count\n" +
                    "The expression runs inside vl-catch-all-apply; LISP runtime errors are returned as a tool error " +
                    "\"ERROR: <message>\". Result is serialized with vl-prin1-to-string.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("expression"),
                    ["properties"] = new JsonObject
                    {
                        ["expression"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "AutoLISP form(s), e.g. \"(getvar \\\"DWGNAME\\\")\". Several forms are allowed; the value of the last one is returned."
                        },
                        ["cancelPending"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Press ESC twice first, cancelling any active command. Needed if the expression calls (command ...). Default false, so data can be read while a command is in progress.",
                            ["default"] = false
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
        var command = GetString(args, "command")
            ?? throw new ArgumentException("send_command requires a 'command' string.");

        var r = await AcadExecutor.ExecuteCommandAsync(
                command,
                activate: GetBool(args, "activate", false),
                echo: GetBool(args, "echo", true),
                cancelPending: GetBool(args, "cancelPending", true),
                suppressDialogs: GetBool(args, "suppressDialogs", true),
                cancelIfIncomplete: GetBool(args, "cancelIfIncomplete", true),
                ParseTimeout(args), ct)
            .ConfigureAwait(false);

        if (r.StillActive && r.Cancelled)
            return TextResult(
                $"Input consumed in '{r.DocName}' ({r.Elapsed.TotalSeconds:F2}s), but the command was still waiting for " +
                "more input and has been cancelled (ESC). Supply all prompts' answers and finish with an extra '\\n'.",
                isError: true);

        if (r.StillActive)
            return TextResult(
                $"Input consumed in '{r.DocName}' ({r.Elapsed.TotalSeconds:F2}s); the command is still active and waiting " +
                "for input. Continue with send_command and cancelPending=false.");

        return TextResult($"executed in '{r.DocName}' ({r.Elapsed.TotalSeconds:F2}s)");
    }

    private static async Task<JsonObject> EvalLispAsync(JsonObject args, CancellationToken ct)
    {
        var expr = GetString(args, "expression")
            ?? throw new ArgumentException("eval_lisp requires an 'expression' string.");

        var r = await AcadExecutor
            .EvaluateLispAsync(expr, GetBool(args, "cancelPending", false), ParseTimeout(args), ct)
            .ConfigureAwait(false);
        return TextResult(r.Value, r.IsError);
    }

    private static async Task<JsonObject> ListDocumentsAsync(CancellationToken ct)
    {
        var arr = await AcadDispatcher.InvokeAsync(() =>
        {
            var a = new JsonArray();
            foreach (Document d in AcadApp.DocumentManager) a.Add(d.Name);
            return a;
        }, ct).ConfigureAwait(false);

        return TextResult(arr.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }

    private static async Task<JsonObject> GetActiveDocumentAsync(CancellationToken ct)
    {
        var name = await AcadDispatcher.InvokeAsync(
            () => AcadApp.DocumentManager.MdiActiveDocument?.Name ?? string.Empty,
            ct).ConfigureAwait(false);

        return TextResult(name);
    }

    // ---- argument helpers: wrong types become -32602 (invalid params), not crashes ----

    private static string? GetString(JsonObject args, string key)
    {
        var node = args[key];
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        throw new ArgumentException($"'{key}' must be a string.");
    }

    private static bool GetBool(JsonObject args, string key, bool fallback)
    {
        var node = args[key];
        if (node is null) return fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        }
        throw new ArgumentException($"'{key}' must be a boolean.");
    }

    internal static TimeSpan ParseTimeout(JsonObject args)
    {
        var node = args["timeoutSeconds"];
        double seconds = 30.0;
        if (node is JsonValue v)
        {
            if (!v.TryGetValue(out seconds))
            {
                if (v.TryGetValue<string>(out var s) && double.TryParse(s,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    seconds = parsed;
                else
                    throw new ArgumentException("'timeoutSeconds' must be a number.");
            }
        }
        else if (node is not null)
            throw new ArgumentException("'timeoutSeconds' must be a number.");

        if (double.IsNaN(seconds)) seconds = 30.0;
        seconds = Math.Clamp(seconds, 1.0, 600.0);
        return TimeSpan.FromSeconds(seconds);
    }

    private static JsonObject TextResult(string text, bool isError = false)
    {
        var o = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            }
        };
        if (isError) o["isError"] = true;
        return o;
    }
}
