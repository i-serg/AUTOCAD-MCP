using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Infomatik.AutoCAD.Mcp.Plugin))]
[assembly: CommandClass(typeof(Infomatik.AutoCAD.Mcp.Plugin))]

namespace Infomatik.AutoCAD.Mcp;

public sealed class Plugin : IExtensionApplication
{
    private static McpServer? _server;

    public void Initialize()
    {
        try
        {
            AcadDispatcher.Start();
            _server = new McpServer(McpServerOptions.FromEnvironment());
            _server.Start();
            WriteLine($"[Infomatik.AutoCAD.Mcp] MCP server listening at {_server.Url}" +
                      (_server.RequiresToken ? " (bearer token required)" : " (no token: set INFOMATIK_ACAD_MCP_TOKEN to require one)"));
        }
        catch (System.Exception ex)
        {
            WriteLine($"[Infomatik.AutoCAD.Mcp] startup failed: {ex.Message}");
        }
    }

    public void Terminate()
    {
        try { _server?.Dispose(); } catch { }
        _server = null;
        AcadDispatcher.Stop();
    }

    [CommandMethod("MCPSTATUS")]
    public void McpStatus()
    {
        WriteLine(_server is { IsRunning: true }
            ? $"[Infomatik.AutoCAD.Mcp] running at {_server.Url}" + (_server.RequiresToken ? " (bearer token required)" : " (no token)")
            : "[Infomatik.AutoCAD.Mcp] not running");
    }

    [CommandMethod("MCPRESTART")]
    public void McpRestart()
    {
        Terminate();
        Initialize();
    }

    private static void WriteLine(string s)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        doc?.Editor.WriteMessage("\n" + s);
    }
}
