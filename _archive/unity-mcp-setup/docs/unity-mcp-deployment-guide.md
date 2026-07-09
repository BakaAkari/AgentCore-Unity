# unity-mcp Local Deployment and Unified Access Guide

Document Date: 2026-03-26 (Updated)
Applicable Versions: `unity-mcp` v9.5.3 (Recommended) / v9.6.2 (Requires Unity 6000.0+)
Applicable Environment: Windows development machines, Unity 2021.3 LTS+
Objective: Deploy `unity-mcp` locally to provide unified access for all MCP clients.

> ** Version Compatibility Warning (2026-03-26 verified through testing)**
>
> `v9.6.x` (including the `#main` branch) has a compilation error on **Unity 2022.3**:
> `BuildReport.SummarizeErrors()` is an API introduced only in Unity 6000.0+ (Unity 6),
> but the conditional compilation macro was incorrectly written as `#if UNITY_2022_3_OR_NEWER` (should be `#if UNITY_6000_0_OR_NEWER`).
>
> - **Unity 6000.0+**: Can use `#main` or `#v9.6.2`
> - **Unity 2022.3 / 2021.3**: Should use **`#v9.5.3`** (does not include Build tools, all other features are complete)
> - This is an upstream bug, confirmed at commit `3dd5ac7742` (main = v9.6.2)

---

## 1. Recommended Approach

1. Install the `MCP for Unity` package in Unity (auto-starts, no manual action needed)
2. Configure AI clients to use **stdio mode** (recommended for all clients):
   ```
   uvx --from mcpforunityserver mcp-for-unity --transport stdio
   ```
3. The stdio bridge communicates with Unity Editor via socket port 6400 (automatic)

This document includes a PowerShell one-click environment check and configuration generation script (see Section 7).

---

## 2. Architecture Overview

```text
┌──────────────────────────────────────────────────────┐
│                   Unity Editor                        │
│  ┌────────────────────────────────────────────────┐  │
│  │  MCP for Unity Package (C# Editor Plugin)      │  │
│  │  - CommandRegistry: Discovers all tools via     │  │
│  │    reflection                                   │  │
│  │  - TransportCommandDispatcher: Main thread      │  │
│  │    dispatch                                     │  │
│  │  - Auto-starts socket listener on port 6400    │  │
│  └──────────────┬─────────────────────────────────┘  │
│                 │ Socket localhost:6400 (automatic)    │
└─────────────────┼────────────────────────────────────┘
                  │
┌─────────────────┼────────────────────────────────────┐
│  Python MCP Server (launched by uvx, stdio mode)      │
│  ┌──────────────┴─────────────────────────────────┐  │
│  │  mcpforunityserver (FastMCP)                   │  │
│  │  - Connects to Unity via socket :6400          │  │
│  │  - Communicates with AI client via stdio       │  │
│  │  - 36+ MCP Tools / 25+ Resources              │  │
│  └──────────────┬─────────────────────────────────┘  │
│                 │ stdio (stdin/stdout)                 │
└─────────────────┼────────────────────────────────────┘
                  │
    ┌─────────────┴─────────────┐
    │  MCP Clients              │
    │  Claude / Cursor / VSCode │
    │  Claude Code / Windsurf   │
    └───────────────────────────┘
```

**Key points:**

- MCP for Unity package auto-starts when installed — no manual "Start Server" needed
- AI clients launch the Python MCP Server via `uvx` in stdio mode
- The Python process connects to Unity Editor via socket port 6400 (automatic)
- If the Python process crashes, the AI client will restart it automatically on next tool call

---

## 3. Prerequisites

### 3.1 Install uv

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

### 3.2 Install Python (if missing)

```powershell
uv python install 3.12
```

### 3.3 Verify

```powershell
python --version    # Requires 3.10+
uv --version
```

---

## 4. Installation and Startup

### 4.1 Install the MCP for Unity Package

In Unity, open `Window > Package Manager`, select `Add package from git URL...`:

**Unity 2022.3 / 2021.3 (Recommended):**

```text
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v9.5.3
```

**Unity 6000.0+ (Unity 6):**

```text
https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v9.6.2
```

>  **Do not use `#main`**: The `main` branch is equivalent to v9.6.2 and will fail to compile on Unity 2022.3 and below due to the missing `BuildReport.SummarizeErrors()` API. See the version compatibility warning at the top of this document.

Other installation methods:

| Method | Operation |
|--------|-----------|
| **OpenUPM** | `openupm add com.coplaydev.unity-mcp` |
| **Asset Store** | Search for "MCP for Unity" and import |
| **Lock version** | Use tags like `#v9.5.3` (Unity 2022.3) or `#v9.6.2` (Unity 6) |

#### Offline Installation (Sandbox / No Network Environment)

Sandbox and other network-isolated environments cannot access GitHub and require offline installation:

**Step 1: Package on a machine with network access**

```powershell
# Package all versions
.\tools\package-unity-mcp.ps1

# Or package a specific version
.\tools\package-unity-mcp.ps1 -Version "9.5.3"
```

Generated `.tgz` files are located in the `packages/` directory.

**Step 2: Copy the .tgz file to the target machine**

Copy `com.coplaydev.unity-mcp-9.5.3.tgz` (or the corresponding version) to the Unity project's `tools/` or `packages/` directory.

**Step 3: Install using offline mode**

```powershell
# Method A: Local tgz reference (recommended, manifest.json uses file: protocol)
.\tools\install-unity-mcp.ps1 -Local

# Method B: Embedded package (extract directly to Packages/ directory)
.\tools\install-unity-mcp.ps1 -Embedded

# Specify tgz file path
.\tools\install-unity-mcp.ps1 -Local -PackagePath ".\packages\com.coplaydev.unity-mcp-9.5.3.tgz"
```

| Mode | manifest.json Reference | Applicable Scenario |
|------|------------------------|---------------------|
| `-Local` | `"file:com.coplaydev.unity-mcp-9.5.3.tgz"` | Sandbox environment, maintains package manager management |
| `-Embedded` | Reference auto-removed, package in `Packages/` directory | Simplest, no network needed, source code directly editable |

### 4.2 Start the Service

After installing the Unity MCP package, it auto-starts when Unity Editor opens. No manual action needed.

All AI clients connect via **stdio mode** using:

```text
uvx --from mcpforunityserver mcp-for-unity --transport stdio
```

> The stdio bridge communicates with Unity Editor via socket port 6400 (automatic, no configuration needed).

---

## 5. Client Configuration

### 5.1 Claude Desktop

Configuration file: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

### 5.2 Cursor

Configuration file: `%USERPROFILE%\.cursor\mcp.json`

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

### 5.3 VS Code Copilot

Project-level configuration (recommended), create `.vscode/mcp.json`:

```json
{
  "servers": {
    "unityMCP": {
      "type": "stdio",
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

Global configuration, add to VS Code `settings.json`:

```json
{
  "mcp": {
    "servers": {
      "unityMCP": {
        "type": "stdio",
        "command": "uvx",
        "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
      }
    }
  }
}
```

### 5.4 Roo Code (VS Code Extension)

Configuration file: `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json`

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"],
      "disabled": false
    }
  }
}
```

> After modification, use `Ctrl+Shift+P` → `Reload Window` for the configuration to take effect.

### 5.5 Claude Code

```bash
claude mcp add unityMCP -- uvx --from mcpforunityserver mcp-for-unity --transport stdio
```

### 5.6 Windsurf

Search for MCP in Settings, then add:

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

### 5.7 Codex CLI (stdio only)

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "uvx",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

On Windows, if `uvx` is not in PATH, use the full path:

```json
{
  "mcpServers": {
    "unityMCP": {
      "command": "C:/Users/YOUR_USERNAME/AppData/Local/Microsoft/WinGet/Links/uvx.exe",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
```

> Use `where uvx` or `Get-Command uvx` to find the actual path.

### 5.8 Quick Reference Table

| Client | Mode | Configuration File |
|--------|------|--------------------|
| Claude Desktop | stdio | `%APPDATA%\Claude\claude_desktop_config.json` |
| Cursor | stdio | `%USERPROFILE%\.cursor\mcp.json` |
| VS Code Copilot | stdio | `.vscode/mcp.json` |
| Roo Code | stdio | `%APPDATA%\Code\User\globalStorage\rooveterinaryinc.roo-cline\settings\mcp_settings.json` |
| Claude Code | stdio | CLI command or `~/.claude/settings.json` |
| Windsurf | stdio | Settings UI |
| Codex CLI | stdio | Configuration file |
| Qwen Code | stdio | Refer to official documentation |
| Gemini CLI | stdio | Refer to official documentation |

---

## 6. Important Notes

### 6.1 Multiple Instances

When multiple Unity projects are open on the same machine:

1. Read the `unity_instances` resource to view connected instances
2. Use `set_active_instance` to specify the target instance
3. Then execute operations

Each instance is uniquely identified by `project_hash` (a hash of the project path).

### 6.2 Concurrent Writes

Multiple clients concurrently modifying the same Unity project is not recommended. Unity Editor API does not support concurrent writes; write operations should be serialized.

### 6.3 Network Security

- stdio mode communicates via local process pipes, no network port exposure
- Unity Editor socket (port 6400) only listens on `localhost`
- No additional network configuration needed for standard usage

### 6.4 Telemetry

Telemetry is enabled by default in the project. To disable:

```powershell
# Environment variable
$env:UNITY_MCP_TELEMETRY_ENABLED = "false"
```

Or disable in the Advanced settings of `Window > MCP for Unity`.

### 6.5 Compilation and Domain Reload

- During Unity compilation, tool calls will be blocked by preflight checks, waiting for compilation to complete
- Excessively long compilation times may cause timeouts
- WebSocket will disconnect during domain reload and automatically reconnect

### 6.6 Version Pinning

For production projects, it is recommended to pin Unity version, Python version, and package versions. Do not use `#main` or `#beta`.

---

## 7. One-Click Deployment Script

Save the following script as `setup-unity-mcp.ps1`:

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    Unity MCP Environment Check and Client Configuration Generation
.DESCRIPTION
    Checks Python and uv environment, verifies MCP Server status,
    generates configuration files for mainstream clients.
#>

param(
    [switch]$GenerateConfigs,
    [switch]$Force
)

$ErrorActionPreference = "Continue"
$script:AllPassed = $true

function Write-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail = "")
    $icon = if ($Passed) { "" } else { ""; $script:AllPassed = $false }
    $msg = "$icon $Name"
    if ($Detail) { $msg += " — $Detail" }
    Write-Host $msg
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Unity MCP Environment Check" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 1. Python
$pythonVersion = $null
try {
    $pythonVersion = & python --version 2>&1 | Select-Object -First 1
    $versionMatch = [regex]::Match($pythonVersion, '(\d+)\.(\d+)')
    $major = [int]$versionMatch.Groups[1].Value
    $minor = [int]$versionMatch.Groups[2].Value
    $pythonOk = ($major -eq 3 -and $minor -ge 10) -or ($major -gt 3)
    Write-Check "Python" $pythonOk $pythonVersion
} catch {
    Write-Check "Python" $false "Not found, please install Python 3.10+"
}

# 2. uv
try {
    $uvVersion = & uv --version 2>&1 | Select-Object -First 1
    Write-Check "uv" $true $uvVersion
} catch {
    Write-Check "uv" $false "Not found, please run: powershell -c 'irm https://astral.sh/uv/install.ps1 | iex'"
}

# 3. uvx
$uvxPath = $null
try {
    $uvxCmd = Get-Command uvx -ErrorAction Stop
    $uvxPath = $uvxCmd.Source
    Write-Check "uvx" $true $uvxPath
} catch {
    $commonPaths = @(
        "$env:USERPROFILE\.local\bin\uvx.exe",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links\uvx.exe",
        "$env:USERPROFILE\.cargo\bin\uvx.exe"
    )
    foreach ($p in $commonPaths) {
        if (Test-Path $p) { $uvxPath = $p; break }
    }
    if ($uvxPath) {
        Write-Check "uvx" $true "Found at $uvxPath (not in PATH)"
    } else {
        Write-Check "uvx" $false "Not found (required for stdio mode)"
    }
}

# 4. MCP Server (uvx package check)
Write-Host ""
Write-Host "--- MCP Server Package ---" -ForegroundColor Yellow
try {
    $uvxTest = & uvx --from mcpforunityserver mcp-for-unity --help 2>&1
    Write-Check "MCP Server Package" $true "mcpforunityserver is available via uvx"
} catch {
    Write-Check "MCP Server Package" $false "Cannot run mcpforunityserver — run: uvx --from mcpforunityserver mcp-for-unity --transport stdio"
}

# 5. Generate Configurations (all stdio mode)
if ($GenerateConfigs) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Generate Client Configurations (stdio)" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    $uvxCommand = if ($uvxPath) { $uvxPath.Replace('\', '/') } else { "uvx" }
    $stdioConfig = @"
{
  "mcpServers": {
    "unityMCP": {
      "command": "$uvxCommand",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
"@

    $vscodeStdioConfig = @"
{
  "servers": {
    "unityMCP": {
      "type": "stdio",
      "command": "$uvxCommand",
      "args": ["--from", "mcpforunityserver", "mcp-for-unity", "--transport", "stdio"]
    }
  }
}
"@

    # Claude Desktop
    $claudeDesktopPath = "$env:APPDATA\Claude\claude_desktop_config.json"
    if ($Force -or -not (Test-Path $claudeDesktopPath)) {
        $claudeDir = Split-Path $claudeDesktopPath
        if (-not (Test-Path $claudeDir)) { New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null }
        $stdioConfig | Out-File -FilePath $claudeDesktopPath -Encoding utf8
        Write-Host "   Claude Desktop: $claudeDesktopPath" -ForegroundColor Green
    } else {
        Write-Host "    Claude Desktop: Already exists, skipped (-Force to overwrite)" -ForegroundColor DarkYellow
    }

    # Cursor
    $cursorPath = "$env:USERPROFILE\.cursor\mcp.json"
    if ($Force -or -not (Test-Path $cursorPath)) {
        $cursorDir = Split-Path $cursorPath
        if (-not (Test-Path $cursorDir)) { New-Item -ItemType Directory -Path $cursorDir -Force | Out-Null }
        $stdioConfig | Out-File -FilePath $cursorPath -Encoding utf8
        Write-Host "   Cursor: $cursorPath" -ForegroundColor Green
    } else {
        Write-Host "    Cursor: Already exists, skipped (-Force to overwrite)" -ForegroundColor DarkYellow
    }

    # VS Code
    $vscodePath = ".\.vscode\mcp.json"
    if ($Force -or -not (Test-Path $vscodePath)) {
        $vscodeDir = Split-Path $vscodePath
        if (-not (Test-Path $vscodeDir)) { New-Item -ItemType Directory -Path $vscodeDir -Force | Out-Null }
        $vscodeStdioConfig | Out-File -FilePath $vscodePath -Encoding utf8
        Write-Host "   VS Code (project-level): $vscodePath" -ForegroundColor Green
    } else {
        Write-Host "    VS Code: Already exists, skipped (-Force to overwrite)" -ForegroundColor DarkYellow
    }

    Write-Host ""
    Write-Host "All configurations use stdio mode (uvx → mcpforunityserver)." -ForegroundColor Green
}

# 6. Summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
if ($script:AllPassed) {
    Write-Host "   All checks passed" -ForegroundColor Green
} else {
    Write-Host "    Some checks failed, please fix according to the prompts" -ForegroundColor Yellow
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Ensure Unity Editor is open with MCP for Unity package installed"
Write-Host "  2. Run .\setup-unity-mcp.ps1 -GenerateConfigs to generate client configurations"
Write-Host "  3. Verify the connection in the client (stdio auto-starts, no manual action needed)"
Write-Host ""
```

Usage:

```powershell
# Check environment only
.\setup-unity-mcp.ps1

# Check environment + generate configurations
.\setup-unity-mcp.ps1 -GenerateConfigs

# Force overwrite existing configurations
.\setup-unity-mcp.ps1 -GenerateConfigs -Force
```

Script coverage:

| Feature | Description |
|---------|-------------|
| Python Check | Verify >= 3.10 |
| uv Check | Verify available |
| uvx Path Discovery | Auto-find including common installation locations |
| MCP Server Package Check | Verifies mcpforunityserver is available via uvx |
| Claude Desktop Config | Auto-generated (stdio) |
| Cursor Config | Auto-generated (stdio) |
| VS Code Config | Auto-generated (stdio, project-level) |

> Unity package installation still requires manual operation in the Unity Editor and cannot be done via external scripts.

---

## 8. Verification

### 8.1 Environment

```powershell
python --version    # 3.10+
uv --version
```

### 8.2 Service

1. In Unity, `Window > MCP for Unity` status shows  **Connected **
2. Verify uvx can launch the server:
   ```powershell
   uvx --from mcpforunityserver mcp-for-unity --help
   ```

### 8.3 Client

1. Open your AI client with the stdio configuration
2. Read the `unity_instances` resource, confirm the current project is visible
3. Execute a read operation (e.g., get hierarchy information)
4. Execute a write operation (e.g., create a test GameObject)
5. Confirm the result in Unity

---

## 9. Common Issues

### 9.1 Unity MCP Not Connecting

```powershell
# Check if uvx can find the package
uvx --from mcpforunityserver mcp-for-unity --help

# Check if Unity socket port is in use
netstat -ano | findstr :6400

# Check Python processes
Get-Process python* | Format-Table Id, ProcessName, Path
```

Common causes: Python not installed, `uv`/`uvx` missing, Unity Editor not running, MCP for Unity package not installed.

### 9.2 Tool Call Timeout

- Unity is compiling (domain reload), wait for compilation to complete
- Unity window is not focused, causing reduced update frequency — click the Unity window

### 9.3 stdio Mode Handshake Failure

Unity package version and Python Server version mismatch. Update both to the same version. Ensure Unity Editor is open and the MCP for Unity package is installed.

### 9.4 Multi-Instance Command Routing Error

1. Read `unity_instances` to confirm the instance list
2. Use `set_active_instance` to specify the target
3. Or pass the `unity_instance` parameter in the tool call

### 9.5 v9.6.x Compilation Failure on Unity 2022.3 (CS1061)

**Symptom:**

```text
BuildRunner.cs(115,47): error CS1061: 'BuildReport' does not contain a definition for 'SummarizeErrors'
```

**Cause:**

`BuildReport.SummarizeErrors()` is an API introduced only in Unity 6000.0+ (Unity 6).
The conditional compilation macro in v9.6.x's `BuildRunner.cs` was incorrectly written as `#if UNITY_2022_3_OR_NEWER`,
and should be `#if UNITY_6000_0_OR_NEWER`. This is an upstream bug (commit `3dd5ac7742`).

**Solutions (in recommended order):**

1. **Downgrade to v9.5.3** (Recommended): In `manifest.json`, change `#main` or `#v9.6.2` to `#v9.5.3`.
   v9.5.3 does not include Build tools (added in v9.6.x), but all other features are complete.
2. **Embed the package and manually fix**: Copy the package from `Library/PackageCache` to the `Packages/` directory,
   modify line 114 of `BuildRunner.cs` from `#if UNITY_2022_3_OR_NEWER` to `#if UNITY_6000_0_OR_NEWER`.
3. **Upgrade Unity to 6000.0+**: If the project allows, upgrading the Unity version enables direct use of v9.6.x.

---

## 10. References

- unity-mcp repository: <https://github.com/CoplayDev/unity-mcp>
- unity-mcp architecture analysis: [`unity-mcp-architecture-analysis.md`](unity-mcp-architecture-analysis.md)
- uv documentation: <https://docs.astral.sh/uv/>
- MCP protocol: <https://modelcontextprotocol.io/introduction>
- FastMCP: <https://github.com/jlowin/fastmcp>
