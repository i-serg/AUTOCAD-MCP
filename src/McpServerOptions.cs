using System;

namespace Infomatik.AutoCAD.Mcp;

public sealed record McpServerOptions(string Prefix, int Port, string? Token)
{
    public const int DefaultPort = 7410;

    /// <summary>Maximum accepted request body size (bytes).</summary>
    public const long MaxRequestBytes = 4 * 1024 * 1024;

    public static McpServerOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("INFOMATIK_ACAD_MCP_PORT");
        var port = (int.TryParse(raw, out var p) && p > 0 && p < 65536) ? p : DefaultPort;

        // Optional shared secret. When set, every request must carry
        // "Authorization: Bearer <token>".
        var token = Environment.GetEnvironmentVariable("INFOMATIK_ACAD_MCP_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(token)) token = null;

        // HttpListener prefixes must end with '/'. Binding to "localhost" avoids the
        // Windows URL-ACL requirement and keeps the listener loopback-only.
        return new McpServerOptions($"http://localhost:{port}/", port, token);
    }
}
