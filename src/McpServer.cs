using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
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
    public bool RequiresToken => _options.Token is not null;

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
            var req = ctx.Request;
            var method = req.HttpMethod;

            // ---- Security gate -------------------------------------------------------------
            // A browser page on any site can send requests to localhost. Without these checks
            // any web page could call eval_lisp and run arbitrary code inside AutoCAD
            // (MCP spec: servers MUST validate the Origin header on HTTP transports).
            var origin = req.Headers["Origin"];
            if (origin is not null && !IsAllowedOrigin(origin))
            {
                await RejectAsync(ctx, 403, "Forbidden: origin not allowed.").ConfigureAwait(false);
                return;
            }

            if (method == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                AddCors(ctx, origin);
                ctx.Response.Close();
                return;
            }

            if (method != "POST")
            {
                // No server-initiated streams (GET) and no session state to terminate (DELETE).
                ctx.Response.StatusCode = 405;
                ctx.Response.AddHeader("Allow", "POST, OPTIONS");
                AddCors(ctx, origin);
                ctx.Response.Close();
                return;
            }

            if (!IsAuthorized(req))
            {
                ctx.Response.AddHeader("WWW-Authenticate", "Bearer");
                await RejectAsync(ctx, 401, "Unauthorized: missing or invalid bearer token.", origin).ConfigureAwait(false);
                return;
            }

            // Requiring application/json means a browser can never reach us with a CORS
            // "simple request" (text/plain form post) that skips the preflight.
            var contentType = req.ContentType ?? string.Empty;
            if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(ctx, 415, "Unsupported Media Type: Content-Type must be application/json.", origin).ConfigureAwait(false);
                return;
            }

            if (req.ContentLength64 > McpServerOptions.MaxRequestBytes)
            {
                await RejectAsync(ctx, 413, "Request body too large.", origin).ConfigureAwait(false);
                return;
            }

            // ---- JSON-RPC ------------------------------------------------------------------
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                body = await ReadLimitedAsync(reader, McpServerOptions.MaxRequestBytes, ct).ConfigureAwait(false);

            var response = await _handler.HandleAsync(body, ct).ConfigureAwait(false);

            AddCors(ctx, origin);
            if (response is null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(response);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentEncoding = Encoding.UTF8;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            try { await RejectAsync(ctx, 500, ex.Message).ConfigureAwait(false); }
            catch { /* connection already gone */ }
        }
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        if (_options.Token is null) return true;

        var header = req.Headers["Authorization"];
        const string prefix = "Bearer ";
        if (header is null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var given = Encoding.UTF8.GetBytes(header.Substring(prefix.Length).Trim());
        var expected = Encoding.UTF8.GetBytes(_options.Token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>Only pages served from the local machine may talk to us from a browser.</summary>
    internal static bool IsAllowedOrigin(string origin)
    {
        // "null" is sent by file://, sandboxed iframes and data: URLs — never trust it.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host == "127.0.0.1"
            || host == "::1";
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, long maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, n);
            if (sb.Length > maxChars) throw new InvalidDataException("Request body too large.");
        }
        return sb.ToString();
    }

    private static async Task RejectAsync(HttpListenerContext ctx, int status, string message, string? origin = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (origin is not null && IsAllowedOrigin(origin)) AddCors(ctx, origin);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static void AddCors(HttpListenerContext ctx, string? origin)
    {
        // Echo back only an origin that already passed IsAllowedOrigin — never "*".
        if (origin is null) return;
        ctx.Response.AddHeader("Access-Control-Allow-Origin", origin);
        ctx.Response.AddHeader("Vary", "Origin");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers",
            "Content-Type, Authorization, MCP-Protocol-Version, Mcp-Session-Id");
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
