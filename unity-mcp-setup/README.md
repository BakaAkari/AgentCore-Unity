# Unity MCP Setup — Install Unity MCP for AI-Powered Editor Operations

> Tools and packages for installing Unity MCP into Unity projects, enabling AI clients to directly operate the Unity Editor.
>
> For Agent Rules (AGENTS.md, .agents/ skills), see the separate `unity-agent-rules/` directory.

---

## What This Contains

| Component | Path | Description |
|-----------|------|-------------|
| Install script | `tools/install-unity-mcp.ps1` | Auto-detect Unity version and install matching MCP package |
| Package script | `tools/package-unity-mcp.ps1` | Package .tgz for offline installation |
| Cache script | `tools/cache-unity-mcp-bridge.ps1` | Cache Python bridge packages for offline use |
| Config script | `tools/configure-opencode-mcp.ps1` | Configure OpenCode MCP client settings |
| Version config | `tools/unity-mcp-config.json` | Unity version → MCP version mapping |
| Unity packages | `packages/*.tgz` | Pre-packaged Unity MCP .tgz files |
| Python cache | `packages/pypi-cache/*.whl` | Cached Python wheels for offline bridge setup |
| Deployment guide | `docs/unity-mcp-deployment-guide.md` | Comprehensive MCP deployment documentation |

---

## Quick Start

### 1. Install Unity MCP Package

```powershell
# Auto-detect Unity version and install (requires network)
.\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project"

# Check current status only
.\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Check

# Force reinstall
.\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Force
```

### 2. Offline Installation (Sandbox / No Network)

```powershell
# Install from local .tgz (file: protocol)
.\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Local

# Embedded install (extract to Packages/)
.\tools\install-unity-mcp.ps1 -ProjectPath "D:\Your Unity Project" -Embedded
```

### 3. Connect AI Client

After installing the Unity package, connect your AI client. See `docs/unity-mcp-deployment-guide.md` for detailed instructions for each client (Roo Code, Cursor, Claude Code, Codex CLI, OpenCode, etc.).

---

## Version Mapping

| Unity Version Range | Recommended MCP Version | Status |
|---------------------|------------------------|--------|
| 6000.0+ (Unity 6) | v9.6.2 | recommended |
| 2021.3 ~ 2023.x | v9.5.3 | tested |

> ⚠️ **Known Issue**: v9.6.x will fail to compile on Unity 2022.3 and below due to missing `BuildReport.SummarizeErrors()` API.

Edit `tools/unity-mcp-config.json` to add new version mappings.

---

## Caching Python Bridge for Offline Use

```powershell
# Download and cache all Python wheels needed by the MCP bridge
.\tools\cache-unity-mcp-bridge.ps1

# Cached wheels are stored in packages/pypi-cache/
```

---

## Related

- **Agent Rules**: `unity-agent-rules/` — AGENTS.md + .agents/ skills deployed to Unity projects
- **Deployment Guide**: `DEPLOY.md` — Full toolkit deployment instructions
- **Detailed MCP Guide**: `docs/unity-mcp-deployment-guide.md` — Comprehensive setup documentation

---

## License

MIT License
