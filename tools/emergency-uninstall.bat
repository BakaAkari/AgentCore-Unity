@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul 2>&1

REM ============================================================================
REM AgentCore Unity - Emergency Uninstall (Offline / No PowerShell required)
REM ============================================================================
REM Purpose:
REM   When a broken AgentCore version causes Unity to hang on startup, use this
REM   BAT script to remove AgentCore WITHOUT opening Unity Editor. Uses only
REM   built-in cmd.exe commands (no PowerShell, no Python, no external tools).
REM
REM What this script DOES automatically:
REM   1. Kill Unity.exe / UnityShaderCompiler.exe processes (release file locks)
REM   2. Delete <Project>\Packages\com.agentcore\  (embedded install)
REM   3. Delete <Project>\Library\PackageCache\com.agentcore.unity*\
REM   4. Delete <Project>\Library\AgentCore\
REM   5. Delete <Project>\Packages\packages-lock.json  (Unity rebuilds on next open)
REM   6. Optional: delete %APPDATA%\Unity\Editor-*\Preferences\AgentCore\
REM
REM What YOU must do BY HAND (BAT cannot safely edit JSON):
REM   Open <Project>\Packages\manifest.json in Notepad and DELETE the line:
REM       "com.agentcore.unity": "file:...",
REM   If the previous line ends with a comma, that trailing comma is fine.
REM   If the removed line was the LAST line before "}", remove the comma from
REM   the previous line so JSON stays valid.
REM
REM Usage:
REM   Double-click emergency-uninstall.bat
REM     -> auto-detects project if bat is inside <Project>\Packages\com.agentcore\tools\
REM     -> otherwise prompts for project path
REM   emergency-uninstall.bat "D:\Your\UnityProject"
REM     -> pass project root explicitly
REM   emergency-uninstall.bat "D:\Your\UnityProject" /prefs
REM     -> also wipe global %APPDATA%\Unity\Editor-*\Preferences\AgentCore\
REM   emergency-uninstall.bat "D:\Your\UnityProject" /prefs /yes
REM     -> skip all confirmation prompts (dangerous, test first)
REM ============================================================================

echo.
echo ====================================================================
echo   AgentCore Unity - Emergency Uninstall (BAT / no PowerShell)
echo ====================================================================
echo.

REM ---- Parse arguments -------------------------------------------------
set "PROJ=%~1"
set "OPT_PREFS=0"
set "OPT_YES=0"

REM Support /prefs, /yes on positions 2 and 3
for %%A in (%*) do (
    if /I "%%~A"=="/prefs" set "OPT_PREFS=1"
    if /I "%%~A"=="/yes"   set "OPT_YES=1"
    if /I "%%~A"=="-CleanPreferences" set "OPT_PREFS=1"
    if /I "%%~A"=="-Yes"   set "OPT_YES=1"
)

REM ---- Step 0: Locate Unity project -----------------------------------
if defined PROJ (
    if "%PROJ:~0,1%"=="/" set "PROJ="
)

if not defined PROJ (
    REM Try auto-detect: assume this bat lives in <Project>\Packages\com.agentcore\tools\
    set "AUTO_PROJ=%~dp0..\..\..\.."
    pushd "!AUTO_PROJ!" 2>nul
    if exist "ProjectSettings\ProjectVersion.txt" (
        set "PROJ=!CD!"
    )
    popd
)

if not defined PROJ (
    echo No project path provided and auto-detect failed.
    echo.
    set /p "PROJ=Enter Unity project root path (e.g. D:\MyGame): "
)

if not exist "%PROJ%\ProjectSettings\ProjectVersion.txt" (
    echo.
    echo [ERROR] "%PROJ%" does not look like a Unity project root
    echo         ^(missing ProjectSettings\ProjectVersion.txt^)
    echo.
    pause
    exit /b 2
)

echo Project root : %PROJ%
echo Clean prefs  : %OPT_PREFS%    (pass /prefs to enable)
echo Skip prompts : %OPT_YES%      (pass /yes  to enable)
echo.

if "%OPT_YES%"=="0" (
    choice /M "Proceed with uninstall"
    if errorlevel 2 (
        echo Cancelled.
        exit /b 2
    )
)

REM ---- Step 1: Kill Unity processes -----------------------------------
echo.
echo [Step 1/6] Killing Unity processes ^(release file locks^)...
for %%P in (Unity.exe UnityShaderCompiler.exe UnityHelper.exe UnityCrashHandler64.exe UnityCrashHandler32.exe UnityDataTools.exe) do (
    taskkill /F /IM "%%P" >nul 2>&1
    if not errorlevel 1 (
        echo   - killed %%P
    )
)
timeout /t 1 /nobreak >nul 2>&1

REM ---- Step 2: Delete embedded package --------------------------------
echo.
echo [Step 2/6] Removing embedded Packages\com.agentcore\ ...
set "TARGET=%PROJ%\Packages\com.agentcore"
if exist "%TARGET%" (
    rmdir /s /q "%TARGET%"
    if exist "%TARGET%.meta" del /f /q "%TARGET%.meta"
    echo   - removed
) else (
    echo   - not present, skip
)

REM ---- Step 3: Delete Library\PackageCache\com.agentcore.unity* -------
echo.
echo [Step 3/6] Removing Library\PackageCache\com.agentcore.unity*\ ...
set "CACHE=%PROJ%\Library\PackageCache"
if exist "%CACHE%" (
    for /d %%D in ("%CACHE%\com.agentcore.unity*") do (
        echo   - deleting %%~fD
        rmdir /s /q "%%~fD"
    )
) else (
    echo   - Library\PackageCache not present, skip
)

REM ---- Step 4: Delete Library\AgentCore -------------------------------
echo.
echo [Step 4/6] Removing Library\AgentCore\ ...
set "LIBAG=%PROJ%\Library\AgentCore"
if exist "%LIBAG%" (
    rmdir /s /q "%LIBAG%"
    echo   - removed
) else (
    echo   - not present, skip
)

REM ---- Step 5: Delete packages-lock.json ------------------------------
echo.
echo [Step 5/6] Removing Packages\packages-lock.json ^(Unity will rebuild^)...
set "LOCK=%PROJ%\Packages\packages-lock.json"
if exist "%LOCK%" (
    del /f /q "%LOCK%"
    echo   - removed
) else (
    echo   - not present, skip
)

REM ---- Step 6 (opt): global Preferences -------------------------------
echo.
echo [Step 6/6] Global %%APPDATA%%\Unity\Editor-*\Preferences\AgentCore\ ...
if "%OPT_PREFS%"=="1" (
    if defined APPDATA (
        for /d %%R in ("%APPDATA%\Unity\Editor-*") do (
            if exist "%%~fR\Preferences\AgentCore" (
                echo   - deleting %%~fR\Preferences\AgentCore
                rmdir /s /q "%%~fR\Preferences\AgentCore"
            )
        )
        echo   - global preferences cleaned
    )
) else (
    echo   - skipped ^(pass /prefs to enable; recommended if new version also hangs^)
)

REM ---- Manual step: manifest.json -------------------------------------
echo.
echo ====================================================================
echo   MANUAL STEP REQUIRED
echo ====================================================================
echo.
echo   Open this file in Notepad and DELETE the line containing
echo   "com.agentcore.unity":
echo.
echo       %PROJ%\Packages\manifest.json
echo.
echo   Example - remove the marked line, keep JSON valid:
echo       {
echo         "dependencies": {
echo   -^>    "com.agentcore.unity": "file:../_local/com.agentcore.unity-1.5.6.tgz",
echo           "com.unity.render-pipelines.universal": "17.0.3",
echo           ...
echo         }
echo       }
echo.
echo   If the removed line was the LAST entry before "}", also remove the
echo   trailing comma from the previous line so JSON stays valid.
echo.

if "%OPT_YES%"=="0" (
    choice /M "Open manifest.json in Notepad now"
    if not errorlevel 2 (
        start "" notepad "%PROJ%\Packages\manifest.json"
    )
)

echo.
echo ====================================================================
echo   DONE
echo ====================================================================
echo.
echo Next steps:
echo   1. Confirm you have edited manifest.json to remove the com.agentcore.unity line.
echo   2. Open Unity Hub, load the project. It should start normally.
echo   3. To reinstall the fixed version 1.5.6+:
echo        - Put com.agentcore.unity-1.5.6.tgz somewhere stable,
echo          e.g. ^<Project^>\_local\
echo        - Add this line to manifest.json dependencies:
echo            "com.agentcore.unity": "file:../_local/com.agentcore.unity-1.5.6.tgz"
echo        - Reopen Unity.
echo.

if "%OPT_YES%"=="0" pause
exit /b 0
