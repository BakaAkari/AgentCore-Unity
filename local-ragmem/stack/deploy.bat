@echo off
setlocal enabledelayedexpansion
REM ============================================
REM RagMem - Sandbox Deployment
REM
REM This script handles the FULL deployment:
REM   0. Remove Zone Identifier marks (Windows security)
REM   1. Check/install WSL2 + Ubuntu-24.04 + Docker
REM   2. Copy files into WSL2 (stack + mcp-server)
REM   3. Load Docker images from tar
REM   4. Start services via docker compose
REM   4.5 Install Python + uv + ragmem MCP in WSL2
REM   5. Access information + MCP client config
REM
REM For first-time setup, run as Administrator.
REM For subsequent runs, normal user is fine.
REM ============================================

echo ============================================
echo   RagMem - Sandbox Deployment
echo ============================================
echo.

set "SCRIPT_DIR=%~dp0"
set "IMAGES_DIR=%SCRIPT_DIR%..\images"
set "NEED_REBOOT=0"
set "ENV_OK=1"

REM ==========================================
REM Phase 0: Remove Zone Identifier marks
REM ==========================================
REM Files copied from network/external sources get Zone.Identifier ADS
REM which causes Windows security warnings and may block .bat execution.
REM PowerShell Unblock-File removes these marks silently.
echo [Phase 0] Removing Windows Zone Identifier marks...
powershell -NoProfile -Command "Get-ChildItem -Path '%SCRIPT_DIR%..' -Recurse -File | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1
echo   [OK] Done
echo.

REM ==========================================
REM Phase 1: Environment Check & Setup
REM ==========================================
echo [Phase 1] Checking environment...
echo.

REM --- 1.1 Check WSL2 ---
echo   [1.1] WSL2...
wsl --status >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo         NOT FOUND - attempting install...
    
    REM Check admin privileges
    net session >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo.
        echo   [ERROR] WSL2 is not installed and this script needs Administrator
        echo           privileges to install it.
        echo.
        echo   Please right-click this file and select "Run as administrator"
        echo.
        set "ENV_OK=0"
        goto :env_summary
    )
    
    echo         Installing WSL2 (this may take a few minutes^)...
    wsl --install --no-distribution
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] WSL2 installation failed.
        set "ENV_OK=0"
        goto :env_summary
    )
    set "NEED_REBOOT=1"
    echo         [OK] WSL2 installed (reboot may be required^)
) else (
    echo         [OK]
)

REM --- 1.2 Check Ubuntu-24.04 ---
echo   [1.2] Ubuntu-24.04...
if !NEED_REBOOT! EQU 1 (
    echo         [SKIP] Reboot required before installing Ubuntu
    set "ENV_OK=0"
    goto :env_summary
)

wsl -d Ubuntu-24.04 -- echo OK >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo         NOT FOUND - attempting install...
    
    net session >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo.
        echo   [ERROR] Ubuntu-24.04 not installed. Need Administrator privileges.
        echo   Please right-click this file and select "Run as administrator"
        set "ENV_OK=0"
        goto :env_summary
    )
    
    echo         Installing Ubuntu-24.04 (downloading ~600MB^)...
    wsl --install -d Ubuntu-24.04 --no-launch
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Ubuntu-24.04 installation failed.
        set "ENV_OK=0"
        goto :env_summary
    )
    
    echo.
    echo         Ubuntu-24.04 installed. You need to set up a Linux user.
    echo         A terminal will open - create a username and password, then type 'exit'.
    echo.
    pause
    wsl -d Ubuntu-24.04
    echo         [OK] Ubuntu-24.04 setup complete
) else (
    echo         [OK]
)

REM --- 1.3 Check Docker Engine ---
echo   [1.3] Docker Engine...
wsl -d Ubuntu-24.04 -- docker --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo         NOT FOUND - attempting install...
    
    REM Check if we have internet (try to reach Docker's install script)
    wsl -d Ubuntu-24.04 -- bash -c "curl -fsSL --connect-timeout 5 https://get.docker.com -o /dev/null 2>/dev/null"
    if !ERRORLEVEL! NEQ 0 (
        echo.
        echo   [ERROR] Docker is not installed and no internet access detected.
        echo.
        echo   OFFLINE INSTALL: Docker must be pre-installed before entering sandbox.
        echo   Run ragmem\stack\setup-environment.bat on a machine with internet first,
        echo   or ask your administrator to prepare the environment.
        echo.
        set "ENV_OK=0"
        goto :env_summary
    )
    
    echo         Installing Docker Engine (this may take a few minutes^)...
    wsl -d Ubuntu-24.04 -- bash -c "curl -fsSL https://get.docker.com | sudo sh 2>&1"
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Docker installation failed.
        set "ENV_OK=0"
        goto :env_summary
    )
    
    REM Add user to docker group
    wsl -d Ubuntu-24.04 -- bash -c "sudo usermod -aG docker $USER 2>&1"
    
    REM Need to restart WSL for group change to take effect
    echo         Restarting WSL2 for docker group to take effect...
    wsl --shutdown
    timeout /t 3 /nobreak >nul
    
    echo         [OK] Docker Engine installed
) else (
    for /f "tokens=*" %%v in ('wsl -d Ubuntu-24.04 -- docker --version 2^>^&1') do (
        echo         [OK] %%v
    )
)

REM --- 1.4 Check Docker daemon ---
echo   [1.4] Docker daemon...
wsl -d Ubuntu-24.04 -- docker info >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo         Not running - starting...
    wsl -d Ubuntu-24.04 -- bash -c "sudo service docker start 2>&1"
    timeout /t 3 /nobreak >nul
    
    wsl -d Ubuntu-24.04 -- docker info >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Docker daemon failed to start.
        echo   Try: wsl -d Ubuntu-24.04 -- sudo service docker start
        set "ENV_OK=0"
        goto :env_summary
    )
    echo         [OK] Started
) else (
    echo         [OK] Running
)

REM --- 1.5 Check Docker Compose ---
echo   [1.5] Docker Compose...
wsl -d Ubuntu-24.04 -- docker compose version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo         NOT FOUND - attempting install...
    wsl -d Ubuntu-24.04 -- bash -c "sudo apt-get update -qq && sudo apt-get install -y -qq docker-compose-plugin 2>&1"
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Docker Compose installation failed.
        set "ENV_OK=0"
        goto :env_summary
    )
    echo         [OK] Installed
) else (
    for /f "tokens=*" %%v in ('wsl -d Ubuntu-24.04 -- docker compose version 2^>^&1') do (
        echo         [OK] %%v
    )
)

:env_summary
echo.
if !NEED_REBOOT! EQU 1 (
    echo ============================================
    echo   REBOOT REQUIRED
    echo ============================================
    echo.
    echo   WSL2 was just installed. Please:
    echo     1. Reboot your computer
    echo     2. Run this script again
    echo.
    goto :end
)

if !ENV_OK! EQU 0 (
    echo ============================================
    echo   ENVIRONMENT SETUP INCOMPLETE
    echo ============================================
    echo.
    echo   Fix the errors above, then run this script again.
    echo   For first-time setup, run as Administrator.
    echo.
    goto :end
)

echo   [OK] All environment checks passed
echo.

REM ==========================================
REM Phase 2: Copy files to WSL2
REM ==========================================
echo [Phase 2] Copying deployment files to WSL2...

REM Create directory structure
wsl -d Ubuntu-24.04 --cd ~ -- bash -c "mkdir -p ~/ragmem/mem0 ~/ragmem/lightrag/data ~/ragmem/lightrag/documents ~/ragmem/mcp-server/src/ragmem_mcp"

REM Transfer files via pipe (sandbox drives may not be mounted in WSL2)
echo   docker-compose.yml...
type "%SCRIPT_DIR%docker-compose.yml" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/docker-compose.yml"

echo   start.sh...
type "%SCRIPT_DIR%start.sh" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/start.sh && chmod +x ~/ragmem/start.sh"

echo   mem0/config.yaml...
type "%SCRIPT_DIR%mem0\config.yaml" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mem0/config.yaml"

echo   mem0/main_override.py (runtime main.py override)...
type "%SCRIPT_DIR%mem0\main_override.py" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mem0/main_override.py"

echo   mem0/entrypoint.sh (runtime patches)...
type "%SCRIPT_DIR%mem0\entrypoint.sh" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mem0/entrypoint.sh && chmod +x ~/ragmem/mem0/entrypoint.sh"

REM Handle .env file
if exist "%SCRIPT_DIR%.env" (
    echo   .env...
    type "%SCRIPT_DIR%.env" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/.env"
) else (
    echo   .env.example (no .env found^)...
    type "%SCRIPT_DIR%.env.example" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/.env.example"
    echo.
    echo   [WARNING] No .env file found. You must create one before starting:
    echo     1. Copy .env.example to .env:
    echo        wsl -d Ubuntu-24.04 --cd ~/ragmem -- cp .env.example .env
    echo     2. Edit .env with your LiteLLM settings:
    echo        wsl -d Ubuntu-24.04 --cd ~/ragmem -- nano .env
    echo.
    echo   Required settings:
    echo     LITELLM_BASE_URL=http://your-litellm-endpoint:port
    echo     LITELLM_API_KEY=your-api-key
    echo.
    choice /C YN /M "   Have you already configured .env? Continue deployment? (Y/N)"
    if !ERRORLEVEL! EQU 2 (
        echo.
        echo   Deployment paused. Configure .env and run this script again.
        goto :end
    )
)

REM Transfer mcp-server source (for WSL2-side MCP Server)
set "MCP_SRC=%SCRIPT_DIR%..\mcp-server"
if exist "%MCP_SRC%\pyproject.toml" (
    echo   mcp-server/pyproject.toml...
    type "%MCP_SRC%\pyproject.toml" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/pyproject.toml"
    echo   mcp-server/README.md...
    type "%MCP_SRC%\README.md" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/README.md"
    echo   mcp-server/src/ragmem_mcp/__init__.py...
    type "%MCP_SRC%\src\ragmem_mcp\__init__.py" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/src/ragmem_mcp/__init__.py"
    echo   mcp-server/src/ragmem_mcp/server.py...
    type "%MCP_SRC%\src\ragmem_mcp\server.py" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/src/ragmem_mcp/server.py"
    echo   mcp-server/src/ragmem_mcp/mem0_client.py...
    type "%MCP_SRC%\src\ragmem_mcp\mem0_client.py" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/src/ragmem_mcp/mem0_client.py"
    echo   mcp-server/src/ragmem_mcp/lightrag_client.py...
    type "%MCP_SRC%\src\ragmem_mcp\lightrag_client.py" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "tr -d '\r' > ~/ragmem/mcp-server/src/ragmem_mcp/lightrag_client.py"
    echo   [OK] MCP Server source transferred
) else (
    echo   [SKIP] mcp-server source not found at %MCP_SRC%
    echo          ragmem MCP Server will not be available in WSL2.
)

echo   Files transferred.
echo.

REM ==========================================
REM Phase 2.5: Confirm LLM model configuration
REM ==========================================
REM LLM model selection should already be done in DEPLOY.md B1 step
REM (AI Agent queries /v1/models API before writing .env).
REM Here we just display the current setting for confirmation.
REM To change the model later, run update-config.bat.
echo [Phase 2.5] LLM Model Configuration
if exist "%SCRIPT_DIR%.env" (
    for /f "tokens=1,* delims==" %%a in ('findstr /B "LLM_MODEL=" "%SCRIPT_DIR%.env"') do (
        echo   LLM_MODEL=%%b
    )
    echo   To change later, run: update-config.bat
) else (
    echo   [SKIP] No .env file found. LLM_MODEL will use default from docker-compose.yml.
)
echo.

REM ==========================================
REM Phase 3: Load Docker images
REM ==========================================
echo [Phase 3] Loading Docker images from tar files...

if exist "%IMAGES_DIR%\*.tar" (
    for %%T in ("%IMAGES_DIR%\*.tar") do (
        echo   Loading %%~nxT (this may take a moment^)...
        type "%%T" | wsl -d Ubuntu-24.04 --cd ~ -- bash -c "docker load"
    )
    echo   [OK] All images loaded
) else (
    echo   No tar files found in %IMAGES_DIR%
    echo   Checking if images already exist...
    wsl -d Ubuntu-24.04 --cd ~ -- bash -c "docker images --format '  {{.Repository}}:{{.Tag}} ({{.Size}})' | grep -E 'pgvector|mem0-server|lightrag'"
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] No Docker images found. Cannot deploy.
        echo   Make sure ragmem\images\ contains the tar files.
        goto :end
    )
)
echo.

REM ==========================================
REM Phase 4: Deploy services
REM ==========================================
echo [Phase 4] Starting services...
wsl -d Ubuntu-24.04 --cd ~/ragmem -- bash start.sh
echo.

REM ==========================================
REM Phase 4.5: Install Python + uv for ragmem MCP Server
REM ==========================================
REM In sandbox environments, Windows cannot reach WSL2 containers via localhost.
REM The ragmem MCP Server must run INSIDE WSL2 to access mem0/LightRAG.
REM AI Agent clients invoke it via: wsl -d Ubuntu-24.04 -- uvx ...
echo [Phase 4.5] Setting up ragmem MCP Server in WSL2...

REM Check if Python3 is available
wsl -d Ubuntu-24.04 --cd ~ -- bash -c "command -v python3 >/dev/null 2>&1" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo   Installing Python3...
    wsl -d Ubuntu-24.04 --cd ~ -- bash -c "sudo apt-get update -qq && sudo apt-get install -y -qq python3 python3-pip python3-venv >/dev/null 2>&1"
    if !ERRORLEVEL! NEQ 0 (
        echo   [WARNING] Failed to install Python3. ragmem MCP Server will not work.
        echo            Install Python3 manually: wsl -d Ubuntu-24.04 -- sudo apt install python3
        goto :skip_mcp_setup
    )
    echo   [OK] Python3 installed
) else (
    echo   [OK] Python3 found
)

REM Check if uv is available
wsl -d Ubuntu-24.04 --cd ~ -- bash -c "command -v uv >/dev/null 2>&1" >nul 2>&1
if !ERRORLEVEL! NEQ 0 (
    echo   Installing uv...
    wsl -d Ubuntu-24.04 --cd ~ -- bash -c "curl -LsSf https://astral.sh/uv/install.sh 2>/dev/null | sh >/dev/null 2>&1 || pip3 install uv >/dev/null 2>&1"
    if !ERRORLEVEL! NEQ 0 (
        echo   [WARNING] Failed to install uv. ragmem MCP Server will not work.
        echo            Install uv manually: wsl -d Ubuntu-24.04 -- curl -LsSf https://astral.sh/uv/install.sh ^| sh
        goto :skip_mcp_setup
    )
    echo   [OK] uv installed
) else (
    echo   [OK] uv found
)

REM Verify ragmem MCP Server can be resolved
wsl -d Ubuntu-24.04 --cd ~ -- bash -c "source ~/.local/bin/env 2>/dev/null; uvx --from ~/ragmem/mcp-server ragmem-mcp-server --help >/dev/null 2>&1"
if !ERRORLEVEL! EQU 0 (
    echo   [OK] ragmem MCP Server verified
) else (
    echo   [INFO] ragmem MCP Server will be installed on first use via uvx.
)

:skip_mcp_setup
echo.

REM ==========================================
REM Phase 5: Access information
REM ==========================================
echo [Phase 5] Access Information
echo ============================================
echo.
echo   Docker services are running inside WSL2.
echo.
echo   --- Service Health Check ---
echo.
echo   Check all services:
echo     wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose ps
echo.
echo   mem0 API (Swagger UI):
echo     wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18910/docs
echo.
echo   LightRAG health:
echo     wsl -d Ubuntu-24.04 --cd ~ -- curl -s http://localhost:18920/health
echo.
echo   pgvector status:
echo     wsl -d Ubuntu-24.04 --cd ~ -- docker exec ragmem-pgvector pg_isready -U mem0
echo.
echo   --- Manage Services ---
echo.
echo   View logs:
echo     wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose logs -f
echo.
echo   Stop services:
echo     wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose down
echo.
echo   --- MCP Client Configuration ---
echo.
echo   ragmem MCP Server runs inside WSL2 (required for sandbox network access).
echo   Add to your AI client's MCP config (.vscode/mcp.json):
echo.
echo   {
echo     "servers": {
echo       "ragmem": {
echo         "command": "wsl",
echo         "args": ["-d", "Ubuntu-24.04", "--",
echo                  "bash", "-c",
echo                  "source ~/.local/bin/env 2^>/dev/null; MEM0_URL=http://localhost:18910 LIGHTRAG_URL=http://localhost:18920 uvx --from ~/ragmem/mcp-server ragmem-mcp-server"]
echo       }
echo     }
echo   }
echo.

:end
echo Done.
pause
