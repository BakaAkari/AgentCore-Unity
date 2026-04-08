@echo off
setlocal enabledelayedexpansion
REM ============================================
REM  Build Distribution Package
REM  Copies all distributable files into a
REM  timestamped output directory for packaging.
REM ============================================

set "SRC=%~dp0"
set "TIMESTAMP=%date:~0,4%%date:~5,2%%date:~8,2%"
set "DIST=%SRC%dist\llm-ai-toolkit-%TIMESTAMP%"

echo ============================================
echo   Build Distribution Package
echo   Output: %DIST%
echo ============================================
echo.

REM Clean previous build with same timestamp
if exist "%DIST%" (
    echo Removing previous build...
    rmdir /s /q "%DIST%"
)

REM ==========================================
REM 1. Root documentation
REM ==========================================
echo [1/6] Copying root documentation...
mkdir "%DIST%" >nul 2>nul
copy /Y "%SRC%DEPLOY.md" "%DIST%\DEPLOY.md" >nul
copy /Y "%SRC%MCP-MANUAL-CONNECT.md" "%DIST%\MCP-MANUAL-CONNECT.md" >nul
copy /Y "%SRC%Fully_DEPLOY.bat" "%DIST%\Fully_DEPLOY.bat" >nul
echo       OK
echo.

REM ==========================================
REM 2. local-ragmem (full directory)
REM ==========================================
echo [2/6] Copying local-ragmem...

REM --- mcp-server ---
mkdir "%DIST%\local-ragmem\mcp-server\src\ragmem_mcp" >nul 2>nul
copy /Y "%SRC%local-ragmem\mcp-server\pyproject.toml"                "%DIST%\local-ragmem\mcp-server\" >nul
copy /Y "%SRC%local-ragmem\mcp-server\README.md"                     "%DIST%\local-ragmem\mcp-server\" >nul
copy /Y "%SRC%local-ragmem\mcp-server\src\ragmem_mcp\__init__.py"    "%DIST%\local-ragmem\mcp-server\src\ragmem_mcp\" >nul
copy /Y "%SRC%local-ragmem\mcp-server\src\ragmem_mcp\server.py"      "%DIST%\local-ragmem\mcp-server\src\ragmem_mcp\" >nul
copy /Y "%SRC%local-ragmem\mcp-server\src\ragmem_mcp\mem0_client.py" "%DIST%\local-ragmem\mcp-server\src\ragmem_mcp\" >nul
copy /Y "%SRC%local-ragmem\mcp-server\src\ragmem_mcp\lightrag_client.py" "%DIST%\local-ragmem\mcp-server\src\ragmem_mcp\" >nul

REM --- stack ---
mkdir "%DIST%\local-ragmem\stack\mem0" >nul 2>nul
mkdir "%DIST%\local-ragmem\stack\lightrag\data\inputs" >nul 2>nul
mkdir "%DIST%\local-ragmem\stack\lightrag\data\rag_storage" >nul 2>nul
mkdir "%DIST%\local-ragmem\stack\lightrag\data\tiktoken" >nul 2>nul
mkdir "%DIST%\local-ragmem\stack\lightrag\documents" >nul 2>nul
copy /Y "%SRC%local-ragmem\stack\.env.example"        "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\deploy.bat"           "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\docker-compose.yml"   "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\start.sh"             "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\setup-environment.bat" "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\update-config.bat"    "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\select-llm-model.ps1" "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\README.md"            "%DIST%\local-ragmem\stack\" >nul
copy /Y "%SRC%local-ragmem\stack\mem0\config.yaml"     "%DIST%\local-ragmem\stack\mem0\" >nul
copy /Y "%SRC%local-ragmem\stack\mem0\main_override.py" "%DIST%\local-ragmem\stack\mem0\" >nul
copy /Y "%SRC%local-ragmem\stack\mem0\entrypoint.sh"   "%DIST%\local-ragmem\stack\mem0\" >nul

REM --- prepare-images scripts ---
copy /Y "%SRC%local-ragmem\prepare-images.sh"  "%DIST%\local-ragmem\" >nul
copy /Y "%SRC%local-ragmem\prepare-images.bat" "%DIST%\local-ragmem\" >nul

REM --- images directory (copy .tar files if they exist) ---
mkdir "%DIST%\local-ragmem\images" >nul 2>nul
if exist "%SRC%local-ragmem\images\*.tar" (
    copy /Y "%SRC%local-ragmem\images\*.tar" "%DIST%\local-ragmem\images\" >nul
    echo       OK (with Docker images^)
) else (
    echo       OK (no .tar images found - run prepare-images first^)
)

REM --- .gitattributes / .gitignore ---
if exist "%SRC%local-ragmem\.gitattributes" copy /Y "%SRC%local-ragmem\.gitattributes" "%DIST%\local-ragmem\" >nul
if exist "%SRC%local-ragmem\.gitignore"     copy /Y "%SRC%local-ragmem\.gitignore"     "%DIST%\local-ragmem\" >nul
echo.

REM ==========================================
REM 3. unity-agent-rules (Agent Rules deployed to Unity projects)
REM ==========================================
echo [3/6] Copying unity-agent-rules...

REM --- Root files ---
mkdir "%DIST%\unity-agent-rules" >nul 2>nul
copy /Y "%SRC%unity-agent-rules\AGENTS.md"  "%DIST%\unity-agent-rules\" >nul
copy /Y "%SRC%unity-agent-rules\README.md"  "%DIST%\unity-agent-rules\" >nul
if exist "%SRC%unity-agent-rules\.gitignore" copy /Y "%SRC%unity-agent-rules\.gitignore" "%DIST%\unity-agent-rules\" >nul

REM --- .agents/ (skills + context templates) ---
REM Use xcopy to recursively copy the entire .agents directory
xcopy /E /I /Y /Q "%SRC%unity-agent-rules\.agents" "%DIST%\unity-agent-rules\.agents" >nul

REM --- .vscode/ (MCP config template) ---
mkdir "%DIST%\unity-agent-rules\.vscode" >nul 2>nul
if exist "%SRC%unity-agent-rules\.vscode\mcp.json" (
    copy /Y "%SRC%unity-agent-rules\.vscode\mcp.json" "%DIST%\unity-agent-rules\.vscode\" >nul
)

REM --- tools (only generate-snapshot.ps1 + deploy-agent-rules.ps1) ---
mkdir "%DIST%\unity-agent-rules\tools" >nul 2>nul
copy /Y "%SRC%unity-agent-rules\tools\generate-snapshot.ps1"    "%DIST%\unity-agent-rules\tools\" >nul
copy /Y "%SRC%unity-agent-rules\tools\deploy-agent-rules.ps1"   "%DIST%\unity-agent-rules\tools\" >nul
echo       OK
echo.

REM ==========================================
REM 4. unity-mcp-setup (MCP installation tools)
REM ==========================================
echo [4/6] Copying unity-mcp-setup...

REM --- Root files ---
mkdir "%DIST%\unity-mcp-setup" >nul 2>nul
copy /Y "%SRC%unity-mcp-setup\README.md"  "%DIST%\unity-mcp-setup\" >nul

REM --- packages ---
mkdir "%DIST%\unity-mcp-setup\packages" >nul 2>nul
if exist "%SRC%unity-mcp-setup\packages\*.tgz" (
    copy /Y "%SRC%unity-mcp-setup\packages\*.tgz" "%DIST%\unity-mcp-setup\packages\" >nul
)

REM --- packages/pypi-cache (offline Unity MCP Python bridge wheels) ---
mkdir "%DIST%\unity-mcp-setup\packages\pypi-cache" >nul 2>nul
if exist "%SRC%unity-mcp-setup\packages\pypi-cache\*.whl" (
    copy /Y "%SRC%unity-mcp-setup\packages\pypi-cache\*.whl" "%DIST%\unity-mcp-setup\packages\pypi-cache\" >nul
    echo       pypi-cache: OK (with wheel files^)
) else (
    echo       pypi-cache: EMPTY (run cache-unity-mcp-bridge.ps1 first^)
)

REM --- tools ---
mkdir "%DIST%\unity-mcp-setup\tools" >nul 2>nul
copy /Y "%SRC%unity-mcp-setup\tools\install-unity-mcp.ps1"       "%DIST%\unity-mcp-setup\tools\" >nul
copy /Y "%SRC%unity-mcp-setup\tools\package-unity-mcp.ps1"       "%DIST%\unity-mcp-setup\tools\" >nul
copy /Y "%SRC%unity-mcp-setup\tools\configure-opencode-mcp.ps1"  "%DIST%\unity-mcp-setup\tools\" >nul
copy /Y "%SRC%unity-mcp-setup\tools\cache-unity-mcp-bridge.ps1"  "%DIST%\unity-mcp-setup\tools\" >nul
copy /Y "%SRC%unity-mcp-setup\tools\unity-mcp-config.json"       "%DIST%\unity-mcp-setup\tools\" >nul
copy /Y "%SRC%unity-mcp-setup\tools\setup-opencode.bat"           "%DIST%\unity-mcp-setup\tools\" >nul

REM --- docs ---
mkdir "%DIST%\unity-mcp-setup\docs" >nul 2>nul
copy /Y "%SRC%unity-mcp-setup\docs\unity-mcp-deployment-guide.md"  "%DIST%\unity-mcp-setup\docs\" >nul
copy /Y "%SRC%unity-mcp-setup\docs\agent-enhancement-research.md"  "%DIST%\unity-mcp-setup\docs\" >nul
echo       OK
echo.

REM ==========================================
REM 5. Verify
REM ==========================================
echo [5/6] Verifying distribution...
echo.
echo   Directory structure:
echo   ---
dir /s /b "%DIST%" 2>nul | findstr /v /i "\\$" | sort
echo   ---
echo.

REM Count files
set "FILE_COUNT=0"
for /r "%DIST%" %%f in (*) do set /a FILE_COUNT+=1
echo   Total files: !FILE_COUNT!
echo.

REM ==========================================
REM 5. Summary
REM ==========================================
echo [6/6] Done!
echo.
echo ============================================
echo   Distribution ready at:
echo   %DIST%
echo.
echo   Next steps:
echo   1. Review the contents
echo   2. Compress: right-click the folder ^> Send to ^> Compressed (zipped) folder
echo      Or use: powershell Compress-Archive -Path "%DIST%\*" -DestinationPath "%DIST%.zip"
echo ============================================
pause
