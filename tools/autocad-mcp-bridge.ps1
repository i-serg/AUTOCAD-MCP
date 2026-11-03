<#
  stdio <-> HTTP bridge for the AutoCAD MCP plugin.

  Claude Desktop (and other MCP clients) start local servers as processes that speak
  JSON-RPC over stdin/stdout. The plugin listens on http://localhost:7410/ inside
  AutoCAD instead. This script forwards each line from stdin to the plugin and writes
  the plugin's answer to stdout. Windows PowerShell 5.1 only - no Node.js or Python.

  claude_desktop_config.json:
    "autocad": {
      "command": "powershell.exe",
      "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
               "C:\\path\\to\\autocad-mcp-bridge.ps1"]
    }

  Optional environment variables:
    INFOMATIK_ACAD_MCP_URL    default http://localhost:7410/
    INFOMATIK_ACAD_MCP_TOKEN  bearer token, if the plugin was started with one
#>
$ErrorActionPreference = 'Stop'

$url   = if ($env:INFOMATIK_ACAD_MCP_URL) { $env:INFOMATIK_ACAD_MCP_URL } else { 'http://localhost:7410/' }
$token = $env:INFOMATIK_ACAD_MCP_TOKEN

# UTF-8 without BOM on both pipes; Cyrillic must survive in both directions.
$utf8 = New-Object System.Text.UTF8Encoding $false
$stdin  = New-Object System.IO.StreamReader ([Console]::OpenStandardInput()), $utf8
$stdout = New-Object System.IO.StreamWriter ([Console]::OpenStandardOutput()), $utf8
$stdout.AutoFlush = $true
$stderr = [Console]::Error

# Loopback calls must not go through a system proxy.
[System.Net.WebRequest]::DefaultWebProxy = $null
[System.Net.ServicePointManager]::Expect100Continue = $false

function Write-Log([string]$msg) { $stderr.WriteLine("[autocad-mcp-bridge] $msg") }

# Returns the raw JSON "id" of a request (or $null for notifications) without
# re-serializing it, so numbers and strings round-trip exactly.
function Get-RawId([string]$json) {
    $m = [regex]::Match($json, '"id"\s*:\s*("(?:[^"\\]|\\.)*"|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|null)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Write-Error-Response([string]$rawId, [string]$message) {
    if ($null -eq $rawId) { return }   # notifications get no answer
    $escaped = $message.Replace('\', '\\').Replace('"', '\"').Replace("`r", ' ').Replace("`n", ' ')
    $stdout.WriteLine('{"jsonrpc":"2.0","id":' + $rawId + ',"error":{"code":-32000,"message":"' + $escaped + '"}}')
}

function Invoke-Plugin([string]$body) {
    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = 'POST'
    $req.ContentType = 'application/json; charset=utf-8'
    $req.Accept = 'application/json'
    $req.Timeout = 660000            # > max tool timeout (600 s)
    $req.ReadWriteTimeout = 660000
    if ($token) { $req.Headers.Add('Authorization', "Bearer $token") }

    $bytes = $utf8.GetBytes($body)
    $req.ContentLength = $bytes.Length
    $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()

    try { $resp = $req.GetResponse() }
    catch {
        # PowerShell wraps .NET exceptions in MethodInvocationException; dig out the WebException.
        $ex = $_.Exception
        while ($ex -and -not ($ex -is [System.Net.WebException])) { $ex = $ex.InnerException }
        if ($ex -and $ex.Response) { $resp = $ex.Response } else { throw }
    }
    try {
        $code = [int]$resp.StatusCode
        $reader = New-Object System.IO.StreamReader ($resp.GetResponseStream()), $utf8
        $text = $reader.ReadToEnd()
        return @{ Code = $code; Body = $text }
    } finally { $resp.Close() }
}

# Offline answers, used while AutoCAD isn't running yet (e.g. Claude Desktop started at
# logon before AutoCAD). Without them the client would mark the server as failed at
# startup; with them the tools are listed and start working once AutoCAD is up.
# Kept in sync with the plugin's tools/list (ASCII-escaped for PowerShell 5.1).
$OfflineInit  = '{"capabilities":{"tools":{"listChanged":false}},"serverInfo":{"name":"infomatik-autocad-mcp","title":"Infomatik AutoCAD MCP","version":"1.0.0.0"},"instructions":"Exposes the command line of the AutoCAD instance hosting this plugin. Use send_command to enter input exactly as a user would type it; use \\n for ENTER. Example: \"_LINE\\n0,0\\n100,100\\n\\n\". The call blocks until AutoCAD has consumed all input; a command left waiting for input is cancelled and reported as an error. Use eval_lisp to read drawing data (it returns structured values). Prefer _-prefixed command names and ''-'' variants (e.g. -LAYER) to avoid dialogs."}'
$OfflineTools = '{"tools":[{"name":"send_command","description":"Execute a command sequence on the active AutoCAD document''s command line. Pass the input exactly as a user would type it. Use ''\\n'' for ENTER between arguments. Example: \"_LINE\\n0,0\\n100,100\\n\\n\" draws a line and exits the LINE command. Prefer underscore-prefixed (language-independent) command names, and ''-'' prefixed variants (e.g. \"-LAYER\") to avoid dialogs. The call blocks until AutoCAD has consumed all input. If the command is still waiting for input afterwards, it is cancelled and an error is returned (set cancelIfIncomplete=false to keep it open and continue it in the next call with cancelPending=false). For reading drawing data (system variables, entity properties, layers, ...), use `eval_lisp` instead.","inputSchema":{"type":"object","required":["command"],"properties":{"command":{"type":"string","description":"Raw command-line input. A trailing ''\\n'' is appended automatically if missing."},"activate":{"type":"boolean","description":"Bring the AutoCAD window to the foreground before sending.","default":false},"echo":{"type":"boolean","description":"Echo the input on the command line.","default":true},"cancelPending":{"type":"boolean","description":"Press ESC twice before sending, cancelling any command left open earlier.","default":true},"cancelIfIncomplete":{"type":"boolean","description":"If the command is still waiting for input after all input was consumed, cancel it and return an error.","default":true},"suppressDialogs":{"type":"boolean","description":"Temporarily set FILEDIA=0 and CMDDIA=0 so commands prompt on the command line instead of opening modal dialogs. Restored afterwards.","default":true},"timeoutSeconds":{"type":"number","description":"Maximum time to wait for AutoCAD to finish. Default 30, max 600. On timeout the active command is cancelled.","default":30}}}},{"name":"eval_lisp","description":"Evaluate an AutoLISP expression in the active document and return its value as a string. This is the right tool for reading drawing data. Non-ASCII text (e.g. Cyrillic) is returned correctly. Examples:\n  - \"(getvar \\\"DWGNAME\\\")\" \u2192 current filename\n  - \"(getvar \\\"CLAYER\\\")\" \u2192 current layer\n  - \"(entget (entlast))\" \u2192 DXF group codes of the most recent entity\n  - \"(vla-get-Count (vla-get-ModelSpace (vla-get-ActiveDocument (vlax-get-acad-object))))\" \u2192 model-space entity count\nThe expression runs inside vl-catch-all-apply; LISP runtime errors are returned as a tool error \"ERROR: <message>\". Result is serialized with vl-prin1-to-string.","inputSchema":{"type":"object","required":["expression"],"properties":{"expression":{"type":"string","description":"AutoLISP form(s), e.g. \"(getvar \\\"DWGNAME\\\")\". Several forms are allowed; the value of the last one is returned."},"cancelPending":{"type":"boolean","description":"Press ESC twice first, cancelling any active command. Needed if the expression calls (command ...). Default false, so data can be read while a command is in progress.","default":false},"timeoutSeconds":{"type":"number","description":"Maximum time to wait. Default 30, max 600.","default":30}}}},{"name":"list_documents","description":"List all open AutoCAD documents with their filenames.","inputSchema":{"type":"object","properties":{}}},{"name":"get_active_document","description":"Get the filename of the currently active AutoCAD document, or empty if none.","inputSchema":{"type":"object","properties":{}}}]}'

function Get-Method([string]$json) {
    $m = [regex]::Match($json, '"method"\s*:\s*"([^"\\]*)"')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-ClientProtocolVersion([string]$json) {
    $m = [regex]::Match($json, '"protocolVersion"\s*:\s*"([0-9-]+)"')
    if ($m.Success -and @('2025-06-18', '2025-03-26', '2024-11-05') -contains $m.Groups[1].Value) { return $m.Groups[1].Value }
    return '2025-06-18'
}

# Answers initialize / tools/list / ping locally. Returns $true if the request was handled.
function Write-Offline-Response([string]$line, [string]$rawId) {
    if (-not $rawId) { return $true }   # notifications need no answer
    $method = Get-Method $line
    if ($method -eq 'initialize') {
        $init = $OfflineInit.Substring(0, $OfflineInit.Length - 1) +
            ',"protocolVersion":"' + (Get-ClientProtocolVersion $line) + '"}'
        $stdout.WriteLine('{"jsonrpc":"2.0","id":' + $rawId + ',"result":' + $init + '}')
        return $true
    }
    if ($method -eq 'tools/list') {
        $stdout.WriteLine('{"jsonrpc":"2.0","id":' + $rawId + ',"result":' + $OfflineTools + '}')
        return $true
    }
    if ($method -eq 'ping') {
        $stdout.WriteLine('{"jsonrpc":"2.0","id":' + $rawId + ',"result":{}}')
        return $true
    }
    return $false
}

Write-Log "forwarding stdio to $url"

while ($true) {
    $line = $stdin.ReadLine()
    if ($null -eq $line) { break }           # client closed stdin -> exit
    if ($line.Trim().Length -eq 0) { continue }

    $rawId = Get-RawId $line
    try {
        $r = Invoke-Plugin $line
        if ($r.Code -eq 202 -or $r.Body.Length -eq 0) { continue }   # notification accepted
        if ($r.Code -ge 400 -and -not $r.Body.TrimStart().StartsWith('{"jsonrpc"')) {
            Write-Error-Response $rawId "AutoCAD MCP plugin returned HTTP $($r.Code): $($r.Body)"
            continue
        }
        # The plugin emits compact single-line JSON, as stdio framing requires.
        $stdout.WriteLine($r.Body.Replace("`r", '').Replace("`n", ''))
    }
    catch {
        Write-Log $_.Exception.Message
        if (Write-Offline-Response $line $rawId) { continue }
        Write-Error-Response $rawId ("AutoCAD is not reachable at $url. Start AutoCAD (the plugin loads automatically " +
            "when installed as InfomatikAutoCADMcp.bundle, otherwise NETLOAD it). Details: " + $_.Exception.Message)
    }
}
