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
        Write-Error-Response $rawId ("AutoCAD is not reachable at $url. Start AutoCAD, open a drawing and " +
            "load the plugin (NETLOAD Infomatik.AutoCAD.Mcp.dll). Details: " + $_.Exception.Message)
    }
}
