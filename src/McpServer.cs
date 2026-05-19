using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Infomatik.AutoCAD.Mcp;

public sealed class McpServer : IDisposable
{
    private readonly McpServerOptions _options;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly McpHandler _handler = new();
    private Task? _loop;

    public McpServer(McpServerOptions options)
    {
        _options = options;
        _listener = new HttpListener();
        _listener.Prefixes.Add(options.Prefix);
    }

    public bool IsRunning => _listener.IsListening;
    public string Url => _options.Prefix;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var method = ctx.Request.HttpMethod;
            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                AddCors(ctx);
                ctx.Response.Close();
                return;
            }
            if (method == "GET" || method == "DELETE")
            {
                // No server-initiated streams and no session state to terminate.
                ctx.Response.StatusCode = 405;
                ctx.Response.AddHeader("Allow", "POST, OPTIONS");
                AddCors(ctx);
                ctx.Response.Close();
                return;
            }
            if (method != "POST")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.AddHeader("Allow", "POST, OPTIONS");
                AddCors(ctx);
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            var response = await _handler.HandleAsync(body, ct).ConfigureAwait(false);

            AddCors(ctx);
            if (response is null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(response);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.ContentType = "application/json";
                AddCors(ctx);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }));
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { /* connection already gone */ }
        }
    }

    private static void AddCors(HttpListenerContext ctx)
    {
        // Loopback-only listener, but some MCP clients still send preflights.
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, MCP-Protocol-Version, Mcp-Session-Id");
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
