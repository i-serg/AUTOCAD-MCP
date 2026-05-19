using System;

namespace Infomatik.AutoCAD.Mcp;

public sealed record McpServerOptions(string Prefix)
{
    public const int DefaultPort = 7410;

    public static McpServerOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("INFOMATIK_ACAD_MCP_PORT");
        var port = (int.TryParse(raw, out var p) && p > 0 && p < 65536) ? p : DefaultPort;
        // HttpListener prefixes must end with '/'. Binding to "localhost" avoids the
        // Windows URL-ACL requirement and keeps the listener loopback-only.
        return new McpServerOptions($"http://localhost:{port}/");
    }
}
