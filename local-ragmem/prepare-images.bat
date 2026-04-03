@echo off
REM ============================================
REM Prepare Docker images for offline deployment
REM Run this OUTSIDE the sandbox (with internet)
REM Requires: WSL2 Ubuntu-24.04 with Docker
REM ============================================

echo ============================================
echo   Prepare Docker Images for Sandbox
echo ============================================
echo.
echo This script builds/pulls Docker images and exports them as tar files.
echo Requires internet access and WSL2 with Docker.
echo.

REM Get the directory where this script lives and convert to WSL path
set "SCRIPT_DIR=%~dp0"

REM Remove trailing backslash
set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"

REM Convert D:\path\to\ragmem to /mnt/d/path/to/ragmem
set "DRIVE_LETTER=%SCRIPT_DIR:~0,1%"
set "REST_PATH=%SCRIPT_DIR:~2%"
set "REST_PATH=%REST_PATH:\=/%"

REM Convert drive letter to lowercase for WSL
for %%a in (a b c d e f g h i j k l m n o p q r s t u v w x y z) do (
    call set "DRIVE_LETTER=%%DRIVE_LETTER:%%a=%%a%%"
)

set "WSL_PATH=/mnt/%DRIVE_LETTER%%REST_PATH%"

echo WSL path: %WSL_PATH%
echo.
echo Running prepare-images.sh via WSL2...
echo.

wsl -d Ubuntu-24.04 -- bash "%WSL_PATH%/prepare-images.sh"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ERROR: Script failed. Check output above.
) else (
    echo.
    echo SUCCESS: Images exported to ragmem\images\
)

echo.
pause
