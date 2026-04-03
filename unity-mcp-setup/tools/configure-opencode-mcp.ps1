# configure-opencode-mcp.ps1
# Automatically registers Unity MCP and RagMem MCP servers into OpenCode's global config.
# This script safely merges with existing opencode.json without overwriting other settings.
#
# Usage:
#   .\tools\configure-opencode-mcp.ps1
#   .\tools\configure-opencode-mcp.ps1 -Remove  # Remove the two MCP entries

[CmdletBinding()]
param(
    [switch]$Remove
)

# Force UTF-8 output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$configDir = Join-Path $env:USERPROFILE ".config\opencode"
$configPath = Join-Path $configDir "opencode.json"

# Ensure config directory exists
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Write-Host "Created directory: $configDir" -ForegroundColor Cyan
}

# Helper: Convert PSCustomObject (from ConvertFrom-Json) to ordered hashtable recursively.
# PowerShell 5.1 does not support -AsHashtable on ConvertFrom-Json.
function ConvertTo-OrderedHashtable($obj) {
    if ($obj -is [System.Collections.IDictionary]) { return $obj }
    if ($obj -is [System.Management.Automation.PSCustomObject]) {
        $ht = [ordered]@{}
        foreach ($prop in $obj.PSObject.Properties) {
            $ht[$prop.Name] = ConvertTo-OrderedHashtable $prop.Value
        }
        return $ht
    }
    if ($obj -is [System.Collections.IEnumerable] -and $obj -isnot [string]) {
        return @($obj | ForEach-Object { ConvertTo-OrderedHashtable $_ })
    }
    return $obj
}

# Load existing config or create a new one
$cfg = $null
if (Test-Path $configPath) {
    try {
        $raw = Get-Content $configPath -Raw -Encoding UTF8
        # Handle empty or malformed file gracefully
        if ($raw.Trim().Length -gt 0) {
            $parsed = $raw | ConvertFrom-Json
            $cfg = ConvertTo-OrderedHashtable $parsed
        }
    } catch {
        Write-Warning "Existing config is malformed. Starting fresh."
        $cfg = $null
    }
}

if (-not $cfg) {
    $cfg = [ordered]@{
        '$schema' = 'https://opencode.ai/config.json'
    }
}

# Ensure $schema is present
if (-not $cfg['$schema']) {
    $cfg['$schema'] = 'https://opencode.ai/config.json'
}

# Ensure mcp section exists
if (-not $cfg.mcp) {
    $cfg.mcp = [ordered]@{}
}

if ($Remove) {
    $cfg.mcp.Remove('unityMCP') | Out-Null
    $cfg.mcp.Remove('ragmem') | Out-Null
    Write-Host "Removed 'unityMCP' and 'ragmem' from OpenCode MCP config." -ForegroundColor Yellow
} else {
    # Merge / add unityMCP (local stdio)
    $cfg.mcp['unityMCP'] = [ordered]@{
        type    = 'local'
        command = @(
            'uvx',
            '--from', 'mcpforunityserver',
            'mcp-for-unity',
            '--transport', 'stdio'
        )
        enabled = $true
    }

    # Merge / add ragmem (local stdio via WSL)
    $cfg.mcp['ragmem'] = [ordered]@{
        type        = 'local'
        command     = @(
            'wsl',
            '-d', 'Ubuntu-24.04',
            '--',
            'bash', '-c',
            'source ~/.local/bin/env 2>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server'
        )
        enabled     = $true
    }

    Write-Host "Registered 'unityMCP' and 'ragmem' in OpenCode MCP config." -ForegroundColor Green
}

# Write back with nice formatting (UTF-8 without BOM to avoid parser issues)
$json = $cfg | ConvertTo-Json -Depth 10
# PowerShell escapes > as \u003e in JSON; restore it for shell readability
$json = $json -replace '\\u003e', '>'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($configPath, $json, $utf8NoBom)

Write-Host "Config written to: $configPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Ensure Unity Editor is open with the target project (Unity MCP auto-starts, no manual action needed)." -ForegroundColor White
Write-Host "  2. Ensure RagMem backend is up: wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose ps" -ForegroundColor White
Write-Host "  3. Ensure 'uvx' is available: uvx --version (install uv if missing: https://docs.astral.sh/uv/)" -ForegroundColor White
Write-Host "  4. Restart OpenCode or start a new session to pick up the MCP servers." -ForegroundColor White
