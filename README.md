# AUTOCAD-MCP

An in-process [Model Context Protocol](https://modelcontextprotocol.io) server for
AutoCAD 2026. The .NET plugin is loaded into AutoCAD via `NETLOAD` and hosts a JSON-RPC
endpoint on `http://localhost:7410/` that exposes AutoCAD's command line and AutoLISP
runtime to any MCP client (Claude Code, Claude Desktop, ...).

- **One 40 KB DLL**, no external dependencies — uses `System.Net.HttpListener` and
  `System.Text.Json` from the .NET 8 BCL only. No ASP.NET Core shipped with the plugin.
- **Synchronous command execution** — `send_command` blocks until AutoCAD has actually
  drained the command queue, not just queued it.
- **Drawing data readback via AutoLISP** — `eval_lisp` returns any LISP expression's value
  as a string (system variables, DXF group codes, COM properties, ...).
- **Thread-safe** — every tool call marshals onto AutoCAD's main thread through the
  `Application.Idle` event before touching the API.

## Repo layout

```
AUTOCAD-MCP/
├── README.md
├── .gitignore
└── src/
    ├── Infomatik.AutoCAD.Mcp.csproj
    ├── Plugin.cs
    ├── AcadDispatcher.cs
    ├── McpServer.cs
    ├── McpServerOptions.cs
    ├── McpHandler.cs
    └── Tools/
        ├── AcadExecutor.cs
        └── CommandLineTools.cs
```

## Build

Requires the .NET 8 SDK and AutoCAD 2026 installed at the default path
(`C:\Program Files\Autodesk\AutoCAD 2026`).

```powershell
dotnet build src/Infomatik.AutoCAD.Mcp.csproj -c Release
```

Output: `src/bin/Release/Infomatik.AutoCAD.Mcp.dll` (~40 KB, no shipped dependencies).

The project references `acmgd.dll`, `accoremgd.dll`, `acdbmgd.dll` from AutoCAD's
install folder with `Private=false`, so those DLLs are **not** copied to the output —
they come from the AutoCAD host process at runtime.

If AutoCAD is installed elsewhere:
```powershell
dotnet build src/Infomatik.AutoCAD.Mcp.csproj -c Release -p:AcadRefPath="<path>"
```

You can also download a pre-built DLL from the [Releases](../../releases) page.

## Load into AutoCAD

1. Start AutoCAD 2026 and open any drawing.
2. At the command line: `NETLOAD` → pick `src/bin/Release/Infomatik.AutoCAD.Mcp.dll`.
3. The command line prints:
   ```
   [Infomatik.AutoCAD.Mcp] MCP server listening at http://localhost:7410/
   ```

In-AutoCAD commands provided by the plugin:

| Command       | Description                                            |
|---------------|--------------------------------------------------------|
| `MCPSTATUS`   | Print whether the MCP server is running and its URL.   |
| `MCPRESTART`  | Stop and restart the MCP server (e.g. after port bump).|

> **Re-loading after a rebuild:** AutoCAD never unloads a .NET assembly once loaded.
> To pick up code changes you must close and restart AutoCAD before `NETLOAD`-ing the new DLL.

## Configuration

| Env var                       | Default | Purpose            |
|-------------------------------|---------|--------------------|
| `INFOMATIK_ACAD_MCP_PORT`     | `7410`  | TCP port to bind.  |

Set the variable before AutoCAD starts (e.g. via `setx`, then relaunch from a new shell).

## Tools exposed via MCP

### `send_command`

Runs a command sequence on the active document's command line and **waits until AutoCAD
has finished processing it**. Pass the input exactly as a user would type it, with `\n`
for ENTER between arguments.

```json
{
  "name": "send_command",
  "arguments": {
    "command": "_LINE\n0,0\n100,100\n\n",
    "timeoutSeconds": 30
  }
}
```

A trailing `\n` is appended automatically if missing. Returns
`"executed in '<doc>' (<seconds>s)"`. Options: `activate` (foreground window),
`echo` (default `true`), `timeoutSeconds` (default 30, max 600).

### `eval_lisp`

Evaluates an AutoLISP expression and returns its value as a string. This is the right
tool for **reading drawing data**.

```json
{ "name": "eval_lisp", "arguments": { "expression": "(getvar \"DWGNAME\")" } }
```

Useful expressions:
- `(getvar "DWGNAME")` — current drawing filename
- `(getvar "CLAYER")` — current layer
- `(entget (entlast))` — DXF group codes of the most recent entity
- `(entget (car (entsel)))` — DXF data of an interactively selected entity
- `(vla-get-Count (vla-get-ModelSpace (vla-get-ActiveDocument (vlax-get-acad-object))))` — model-space entity count

LISP runtime errors come back as `"ERROR: <message>"` (the form is wrapped in
`vl-catch-all-apply`). Result is serialized with `vl-prin1-to-string`.

### `list_documents`

Returns the filenames of all open documents.

### `get_active_document`

Returns the active document's filename, or empty if none.

## How it works internally

Both `send_command` and `eval_lisp` use a **sentinel-file pattern**:

1. A short AutoLISP form is appended to the command stream that opens and closes
   (`eval_lisp`: writes the value to) a temp file.
2. Because AutoCAD evaluates LISP forms inline with the command stream, the marker file
   only appears after every preceding command has finished.
3. The tool polls for the file — so the HTTP call blocks until AutoCAD is actually done.

All tool work is marshalled onto AutoCAD's main thread through the `Application.Idle`
event before touching the API (AutoCAD's managed API is single-threaded).

## Quick sanity check

With AutoCAD running and the plugin loaded:

```powershell
# 1. Handshake
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}'
Invoke-RestMethod -Method Post -Uri http://localhost:7410/ -ContentType 'application/json' -Body $body

# 2. Draw a circle (blocks until AutoCAD finishes the command)
$draw = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"send_command","arguments":{"command":"_CIRCLE\n0,0\n50\n"}}}'
Invoke-RestMethod -Method Post -Uri http://localhost:7410/ -ContentType 'application/json' -Body $draw

# 3. Read the drawing name back
$read = '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"eval_lisp","arguments":{"expression":"(getvar \"DWGNAME\")"}}}'
Invoke-RestMethod -Method Post -Uri http://localhost:7410/ -ContentType 'application/json' -Body $read

# 4. Get the DXF data of the circle you just drew
$last = '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"eval_lisp","arguments":{"expression":"(entget (entlast))"}}}'
Invoke-RestMethod -Method Post -Uri http://localhost:7410/ -ContentType 'application/json' -Body $last
```

## Wiring into Claude Code

```bash
claude mcp add --transport http autocad http://localhost:7410/
```

Or in `~/.claude.json`:

```json
{
  "mcpServers": {
    "autocad": {
      "type": "http",
      "url": "http://localhost:7410/"
    }
  }
}
```

Restart Claude Code; `mcp__autocad__send_command`, `mcp__autocad__eval_lisp`,
`mcp__autocad__list_documents` and `mcp__autocad__get_active_document` will appear in
the tool list.

## Notes & limitations

- **Transport:** plain JSON-RPC over HTTP POST. No SSE / server-initiated messages, no
  session state — the simplest subset of the MCP "Streamable HTTP" spec that works for a
  tool-only server.
- **No raw command-line output for `send_command`.** AutoCAD's .NET API does not expose
  a hook for "what was just written to the command line". For reading drawing data, use
  `eval_lisp` — it returns structured values (DXF group codes, system variable values,
  COM property results), which are richer than parsing the command-line text.
- **Loopback only.** The listener binds to `localhost`, which avoids Windows URL-ACL
  registration. Do not expose it to other machines without adding authentication.
- **AutoCAD 2026 only.** The plugin targets the `.NETCoreApp,Version=v8.0` runtime that
  ships with AutoCAD 2025+. It will not load into AutoCAD 2024 or earlier (which use
  .NET Framework 4.8).

## License

MIT.
