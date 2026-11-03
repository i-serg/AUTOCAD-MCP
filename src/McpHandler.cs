using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Infomatik.AutoCAD.Mcp.Tools;

namespace Infomatik.AutoCAD.Mcp;

internal sealed class McpHandler
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };
    private readonly CommandLineTools _tools = new();

    public async Task<string?> HandleAsync(string body, CancellationToken ct)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (Exception ex) { return MakeError(null, -32700, "Parse error: " + ex.Message).ToJsonString(); }
        if (root is null) return MakeError(null, -32600, "Invalid Request").ToJsonString();

        if (root is JsonArray arr)
        {
            var responses = new JsonArray();
            foreach (var item in arr)
            {
                var r = await HandleOneAsync(item, ct).ConfigureAwait(false);
                if (r is not null) responses.Add(r);
            }
            return responses.Count == 0 ? null : responses.ToJsonString();
        }

        var single = await HandleOneAsync(root, ct).ConfigureAwait(false);
        return single?.ToJsonString();
    }

    private async Task<JsonNode?> HandleOneAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject obj) return MakeError(null, -32600, "Invalid Request");
        var idNode = obj["id"];
        var hasId = idNode is not null;
        var method = obj["method"]?.GetValue<string>();

        // No "method" → it's a response/error from the client. We don't issue requests, so drop it.
        if (method is null) return null;

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(obj["params"]),
                "notifications/initialized" => null,
                "notifications/cancelled" => null,
                "notifications/roots/list_changed" => null,
                "ping" => new JsonObject(),
                "tools/list" => _tools.List(),
                "tools/call" => await SafeToolCallAsync(obj["params"], ct).ConfigureAwait(false),
                _ => throw new NotSupportedException("Method not found: " + method)
            };

            if (!hasId) return null; // notifications must not be answered
            return MakeResult(idNode, result ?? new JsonObject());
        }
        catch (NotSupportedException nse)
        {
            return hasId ? MakeError(idNode, -32601, nse.Message) : null;
        }
        catch (ArgumentException aex)
        {
            return hasId ? MakeError(idNode, -32602, aex.Message) : null;
        }
        catch (Exception ex)
        {
            return hasId ? MakeError(idNode, -32603, "Internal error: " + ex.Message) : null;
        }
    }

    private async Task<JsonNode> SafeToolCallAsync(JsonNode? p, CancellationToken ct)
    {
        try { return await _tools.CallAsync(p, ct).ConfigureAwait(false); }
        catch (ArgumentException) { throw; } // protocol-level: bubble as -32602
        catch (Exception ex)
        {
            // MCP convention: tool execution failures are returned as a result with isError=true,
            // not as a JSON-RPC error.
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = ex.Message }
                },
                ["isError"] = true
            };
        }
    }

    private static JsonObject Initialize(JsonNode? @params)
    {
        // Echo the client's version only if we support it; otherwise offer our latest
        // and let the client decide whether to proceed (MCP version negotiation).
        var clientVersion = (@params?["protocolVersion"] as JsonValue)?.TryGetValue<string>(out var cv) == true ? cv : null;
        var version = clientVersion is not null && Array.IndexOf(SupportedVersions, clientVersion) >= 0
            ? clientVersion
            : ProtocolVersion;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "infomatik-autocad-mcp",
                ["title"] = "Infomatik AutoCAD MCP",
                ["version"] = typeof(McpHandler).Assembly.GetName().Version?.ToString() ?? "0.1.0"
            },
            ["instructions"] =
                "Exposes the AutoCAD 2026 command line of the host process. " +
                "Use send_command to enter input exactly as a user would type it; use \\n for ENTER. " +
                "Example: \"_LINE\\n0,0\\n100,100\\n\\n\". The call blocks until AutoCAD has consumed all input; " +
                "a command left waiting for input is cancelled and reported as an error. " +
                "Use eval_lisp to read drawing data (it returns structured values). " +
                "Prefer _-prefixed command names and '-' variants (e.g. -LAYER) to avoid dialogs."
        };
    }

    private static JsonObject MakeResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    };

    private static JsonObject MakeError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
