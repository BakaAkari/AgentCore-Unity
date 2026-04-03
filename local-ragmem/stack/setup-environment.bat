@echo off
REM ============================================
REM RagMem - Environment Setup
REM Checks and installs: WSL2 + Ubuntu-24.04 + Docker Engine
REM 
REM Run as Administrator (right-click → Run as administrator)
REM Works both inside and outside sandbox
REM ============================================

setlocal enabledelayedexpansion

echo ============================================
echo   RagMem - Environment Setup
echo ============================================
echo.

REM ------------------------------------------
REM Check admin privileges
REM ------------------------------------------
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] This script requires Administrator privileges.
    echo         Right-click this file and select "Run as administrator"
    echo.
    goto :end
)
echo [OK] Running as Administrator
echo.

REM ------------------------------------------
REM Step 1: Check WSL2
REM ------------------------------------------
echo [Step 1/5] Checking WSL2...

wsl --status >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo   WSL2 not found. Installing...
    echo.
    echo   This will enable WSL2 Windows feature.
    echo   A REBOOT may be required after this step.
    echo.
    
    wsl --install --no-distribution
    
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] WSL2 installation failed.
        echo   Try manually: Open PowerShell as Admin and run:
        echo     wsl --install --no-distribution
        goto :end
    )
    
    echo.
    echo   [WARNING] WSL2 installed. You may need to REBOOT your computer.
    echo   After reboot, run this script again to continue setup.
    echo.
    choice /C YN /M "Has the system already been rebooted (or no reboot needed)? (Y/N)"
    if !ERRORLEVEL! EQU 2 (
        echo.
        echo   Please reboot and run this script again.
        goto :end
    )
) else (
    echo   [OK] WSL2 is available
)
echo.

REM ------------------------------------------
REM Step 2: Check Ubuntu-24.04
REM ------------------------------------------
echo [Step 2/5] Checking Ubuntu-24.04 distribution...

wsl -d Ubuntu-24.04 -- echo "Ubuntu-24.04 OK" >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo   Ubuntu-24.04 not found. Installing...
    echo   This may take a few minutes (downloading ~600MB^)...
    echo.
    
    wsl --install -d Ubuntu-24.04 --no-launch
    
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Ubuntu-24.04 installation failed.
        echo   Try manually:
        echo     wsl --install -d Ubuntu-24.04
        goto :end
    )
    
    echo.
    echo   Ubuntu-24.04 installed. Setting up default user...
    echo   You will be asked to create a username and password.
    echo   (This is the Linux user inside WSL2, not your Windows account^)
    echo.
    
    wsl -d Ubuntu-24.04
    
    echo.
    echo   [OK] Ubuntu-24.04 setup complete
) else (
    echo   [OK] Ubuntu-24.04 is available
)
echo.

REM ------------------------------------------
REM Step 3: Check Docker Engine inside WSL2
REM ------------------------------------------
echo [Step 3/5] Checking Docker Engine in WSL2...

wsl -d Ubuntu-24.04 -- docker --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo   Docker not found in WSL2. Installing Docker Engine...
    echo   This may take a few minutes...
    echo.
    
    REM Install Docker Engine using official convenience script
    wsl -d Ubuntu-24.04 -- bash -c "curl -fsSL https://get.docker.com | sudo sh 2>&1"
    
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Docker installation failed.
        echo   Try manually inside WSL2:
        echo     wsl -d Ubuntu-24.04
        echo     curl -fsSL https://get.docker.com ^| sudo sh
        goto :end
    )
    
    REM Add current user to docker group (avoid sudo for docker commands)
    echo   Adding user to docker group...
    wsl -d Ubuntu-24.04 -- bash -c "sudo usermod -aG docker $USER 2>&1"
    
    echo   [OK] Docker Engine installed
) else (
    for /f "tokens=*" %%v in ('wsl -d Ubuntu-24.04 -- docker --version 2^>^&1') do set "DOCKER_VER=%%v"
    echo   [OK] !DOCKER_VER!
)
echo.

REM ------------------------------------------
REM Step 4: Check Docker daemon is running
REM ------------------------------------------
echo [Step 4/5] Checking Docker daemon...

wsl -d Ubuntu-24.04 -- docker info >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo   Docker daemon not running. Starting...
    
    wsl -d Ubuntu-24.04 -- bash -c "sudo service docker start 2>&1"
    
    REM Wait a moment for daemon to start
    timeout /t 3 /nobreak >nul
    
    wsl -d Ubuntu-24.04 -- docker info >nul 2>&1
    if !ERRORLEVEL! NEQ 0 (
        echo   [WARNING] Docker daemon may not have started.
        echo   Try manually:
        echo     wsl -d Ubuntu-24.04 -- sudo service docker start
        echo.
        echo   If this persists, the docker group change may need a WSL restart:
        echo     wsl --shutdown
        echo     Then run this script again.
    ) else (
        echo   [OK] Docker daemon started
    )
) else (
    echo   [OK] Docker daemon is running
)
echo.

REM ------------------------------------------
REM Step 5: Check Docker Compose
REM ------------------------------------------
echo [Step 5/5] Checking Docker Compose...

wsl -d Ubuntu-24.04 -- docker compose version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo   Docker Compose not found. Installing...
    
    wsl -d Ubuntu-24.04 -- bash -c "sudo apt-get update && sudo apt-get install -y docker-compose-plugin 2>&1"
    
    if !ERRORLEVEL! NEQ 0 (
        echo   [ERROR] Docker Compose installation failed.
        echo   Try manually inside WSL2:
        echo     sudo apt-get install -y docker-compose-plugin
        goto :end
    )
    
    echo   [OK] Docker Compose installed
) else (
    for /f "tokens=*" %%v in ('wsl -d Ubuntu-24.04 -- docker compose version 2^>^&1') do set "COMPOSE_VER=%%v"
    echo   [OK] !COMPOSE_VER!
)
echo.

REM ------------------------------------------
REM Summary
REM ------------------------------------------
echo ============================================
echo   Environment Check Summary
echo ============================================
echo.

REM Final verification
echo Verifying all components...
echo.

wsl -d Ubuntu-24.04 -- bash -c "echo '  WSL2:           OK' && echo \"  Docker Engine:  $(docker --version 2>/dev/null || echo 'NOT FOUND')\" && echo \"  Docker Compose: $(docker compose version 2>/dev/null || echo 'NOT FOUND')\" && echo \"  Docker Daemon:  $(docker info --format '{{.ServerVersion}}' 2>/dev/null && echo 'running' || echo 'NOT RUNNING')\" && echo \"  User in docker group: $(groups | grep -q docker && echo 'YES' || echo 'NO - run: sudo usermod -aG docker $USER && wsl --shutdown')\""

echo.
echo ============================================
echo.
echo If all checks show OK, your environment is ready.
echo.
echo Next steps:
echo   - To BUILD images (admin, needs internet):
echo       Double-click ragmem\prepare-images.bat
echo.
echo   - To DEPLOY in sandbox (user, offline):
echo       1. Copy ragmem\ directory into sandbox
echo       2. Edit ragmem\stack\.env
echo       3. Double-click ragmem\stack\deploy.bat
echo.

:end
echo.
pause
