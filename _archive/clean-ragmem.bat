@echo off
setlocal
REM ============================================
REM  Clean ragmem deployment (keep searxng + openclaw)
REM  Run from Windows cmd
REM ============================================

echo ============================================
echo   RagMem Environment Cleanup
echo   KEEP: searxng, openclaw, system tools
echo ============================================
echo.

REM --- Step 1: Stop ragmem containers ---
echo [1/7] Stopping ragmem containers...
wsl -d Ubuntu-24.04 --cd ~/ragmem -- docker compose down 2>nul
if %errorlevel%==0 (
    echo       OK - containers stopped
) else (
    echo       SKIP - ragmem compose not found or already stopped
)
echo.

REM --- Step 2: Remove data volumes ---
echo [2/7] Removing ragmem data volumes...
wsl -d Ubuntu-24.04 -- bash -c "docker volume rm ragmem-mem0-data ragmem-pgvector-data 2>/dev/null; docker volume ls -q | grep -E '^[0-9a-f]{64}$' | xargs -r docker volume rm 2>/dev/null; echo done"
if %errorlevel%==0 (
    echo       OK - volumes removed
) else (
    echo       SKIP - volumes not found
)
echo.

REM --- Step 3: Remove Docker images ---
echo [3/7] Removing ragmem Docker images...
wsl -d Ubuntu-24.04 -- bash -c "docker rmi mem0-server:latest 2>/dev/null; docker rmi ghcr.io/hkuds/lightrag:latest 2>/dev/null; docker rmi ankane/pgvector:v0.5.1 2>/dev/null; docker image prune -f 2>/dev/null; echo done"
echo       OK - images cleaned
echo.

REM --- Step 4: Remove ragmem deploy directory ---
echo [4/7] Removing ~/ragmem directory...
wsl -d Ubuntu-24.04 -- sudo rm -rf ~/ragmem
echo       OK
echo.

REM --- Step 5: Remove old agent-memory-stack ---
echo [5/7] Removing ~/agent-memory-stack (old leftover)...
wsl -d Ubuntu-24.04 -- sudo rm -rf ~/agent-memory-stack
echo       OK
echo.

REM --- Step 6: Clear uvx ragmem MCP cache ---
echo [6/7] Clearing uvx ragmem cache...
wsl -d Ubuntu-24.04 -- bash -c "source ~/.local/bin/env 2>/dev/null; find ~/.cache/uv -name 'ragmem*' -exec rm -rf {} + 2>/dev/null; echo done"
echo       OK
echo.

REM --- Step 7: Verify ---
echo [7/7] Verifying environment...
echo.
echo   --- Docker containers (should only show searxng) ---
wsl -d Ubuntu-24.04 -- docker ps -a --format "  {{.Names}}	{{.Status}}"
echo.
echo   --- Docker images (should NOT contain mem0/lightrag/pgvector) ---
wsl -d Ubuntu-24.04 -- docker images --format "  {{.Repository}}:{{.Tag}}	{{.Size}}"
echo.
echo   --- Docker volumes (should be empty or only non-ragmem) ---
wsl -d Ubuntu-24.04 -- docker volume ls --format "  {{.Name}}"
echo.
echo   --- ~/ragmem should NOT exist ---
wsl -d Ubuntu-24.04 -- bash -c "test -d ~/ragmem && echo '  WARNING: ~/ragmem still exists!' || echo '  OK: ~/ragmem removed'"
echo.
echo   --- openclaw process (should still be running) ---
wsl -d Ubuntu-24.04 -- bash -c "ps aux | grep openclaw | grep -v grep | awk '{print \"  PID=\" $2 \" CMD=\" $11}'"
echo.
echo   --- home directory ---
wsl -d Ubuntu-24.04 -- ls ~/
echo.

echo ============================================
echo   Cleanup done!
echo   Now run deploy.bat to test from scratch
echo ============================================
pause
