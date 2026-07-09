@echo off
setlocal enabledelayedexpansion

echo ============================================
echo   RagMem - Config Update
echo ============================================
echo.

set "SCRIPT_DIR=%~dp0"
set "WSL_DISTRO=Ubuntu-24.04"
set "WSL_DEPLOY_DIR=~/ragmem"

REM ------------------------------------------
REM 1. Check .env file exists
REM ------------------------------------------
if not exist "%SCRIPT_DIR%.env" (
    echo [ERROR] .env file not found at %SCRIPT_DIR%.env
    echo.
    echo   Please create .env first:
    echo     copy .env.example .env
    echo     notepad .env
    echo.
    goto :end
)

REM ------------------------------------------
REM 2. Check WSL2 distro is available
REM ------------------------------------------
wsl -d %WSL_DISTRO% --cd ~ -- echo ok >nul 2>&1
if errorlevel 1 (
    echo [ERROR] WSL2 distro '%WSL_DISTRO%' not found.
    echo   Please run deploy.bat first to set up the environment.
    goto :end
)

REM ------------------------------------------
REM 2.5. Auto-discover LLM models from LiteLLM
REM ------------------------------------------
echo [0/4] Discovering available LLM models...
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%select-llm-model.ps1" -EnvFile "%SCRIPT_DIR%.env"
echo.

REM ------------------------------------------
REM 3. Push .env to WSL2
REM ------------------------------------------
echo [1/4] Pushing .env to WSL2...
type "%SCRIPT_DIR%.env" | wsl -d %WSL_DISTRO% --cd ~ -- bash -c "cat > %WSL_DEPLOY_DIR%/.env"
if errorlevel 1 (
    echo   [FAILED] Could not copy .env to WSL2.
    goto :end
)
echo   [OK]

REM ------------------------------------------
REM 3.5. Push mem0 runtime files (main_override.py + entrypoint.sh)
REM ------------------------------------------
echo [2/4] Pushing mem0 runtime files to WSL2...
type "%SCRIPT_DIR%mem0\main_override.py" | wsl -d %WSL_DISTRO% --cd ~ -- bash -c "tr -d '\r' > %WSL_DEPLOY_DIR%/mem0/main_override.py"
type "%SCRIPT_DIR%mem0\entrypoint.sh" | wsl -d %WSL_DISTRO% --cd ~ -- bash -c "tr -d '\r' > %WSL_DEPLOY_DIR%/mem0/entrypoint.sh && chmod +x %WSL_DEPLOY_DIR%/mem0/entrypoint.sh"
echo   [OK]

REM ------------------------------------------
REM 4. Restart services (docker compose detects env changes)
REM ------------------------------------------
echo [3/4] Restarting services with new config...
wsl -d %WSL_DISTRO% --cd %WSL_DEPLOY_DIR% -- docker compose up -d
if errorlevel 1 (
    echo   [FAILED] docker compose up failed.
    echo   Check logs: wsl -d %WSL_DISTRO% --cd %WSL_DEPLOY_DIR% -- docker compose logs
    goto :end
)
echo   [OK]

REM ------------------------------------------
REM 5. Show service status
REM ------------------------------------------
echo [4/4] Service status:
echo.
wsl -d %WSL_DISTRO% --cd %WSL_DEPLOY_DIR% -- docker compose ps
echo.
echo ============================================
echo   Config update complete!
echo   mem0:     http://localhost:18910/docs
echo   LightRAG: http://localhost:18920/health
echo ============================================

:end
echo.
pause
