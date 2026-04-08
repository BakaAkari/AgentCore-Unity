@echo off
setlocal
chcp 65001 >nul 2>nul
title OpenCode CLI - 自动安装与配置

echo ============================================================
echo   OpenCode CLI 自动安装与配置工具
echo ============================================================
echo.

:: ============================================================
:: [1/5] 检查 opencode 是否已安装
:: ============================================================
echo [1/5] 检查 opencode CLI 是否已安装...

where opencode >nul 2>nul
if %errorlevel%==0 (
    echo       √ opencode CLI 已安装
    for /f "tokens=*" %%v in ('opencode --version 2^>nul') do echo       版本: %%v
    goto :configure
)

echo       × 未检测到 opencode CLI，准备安装...
echo.

:: ============================================================
:: [2/5] 检测可用的安装方式
:: ============================================================
echo [2/5] 检测可用的安装方式...

:: 优先检查 scoop
where scoop >nul 2>nul
if %errorlevel%==0 (
    echo       √ 检测到 Scoop 包管理器，使用 Scoop 安装...
    goto :install_scoop
)

:: 检查 chocolatey
where choco >nul 2>nul
if %errorlevel%==0 (
    echo       √ 检测到 Chocolatey 包管理器，使用 Chocolatey 安装...
    goto :install_choco
)

:: 检查 npm
where npm >nul 2>nul
if %errorlevel%==0 (
    echo       √ 检测到 npm，使用 npm 安装...
    goto :install_npm
)

:: 检查 Git Bash (可以运行 curl | bash)
if exist "C:\Program Files\Git\bin\bash.exe" (
    echo       √ 检测到 Git Bash，使用 curl 脚本安装...
    goto :install_curl_gitbash
)

:: 检查 WSL2
where wsl >nul 2>nul
if %errorlevel%==0 (
    wsl --status >nul 2>nul
    if %errorlevel%==0 (
        echo       √ 检测到 WSL2，使用 curl 脚本安装...
        goto :install_curl_wsl
    )
)

:: 最后方案：使用 PowerShell 直接下载二进制文件
echo       未检测到包管理器，使用 PowerShell 下载二进制文件...
goto :install_binary

:: ============================================================
:: 安装方式: Scoop
:: ============================================================
:install_scoop
echo.
echo [3/5] 使用 Scoop 安装 opencode...
scoop install opencode
if %errorlevel% neq 0 (
    echo       × Scoop 安装失败，尝试下载二进制文件...
    goto :install_binary
)
echo       √ Scoop 安装完成
goto :verify_install

:: ============================================================
:: 安装方式: Chocolatey
:: ============================================================
:install_choco
echo.
echo [3/5] 使用 Chocolatey 安装 opencode...
choco install opencode -y
if %errorlevel% neq 0 (
    echo       × Chocolatey 安装失败，尝试下载二进制文件...
    goto :install_binary
)
echo       √ Chocolatey 安装完成
goto :verify_install

:: ============================================================
:: 安装方式: npm
:: ============================================================
:install_npm
echo.
echo [3/5] 使用 npm 安装 opencode...
npm install -g opencode-ai
if %errorlevel% neq 0 (
    echo       × npm 安装失败，尝试下载二进制文件...
    goto :install_binary
)
echo       √ npm 安装完成
goto :verify_install

:: ============================================================
:: 安装方式: curl via Git Bash
:: ============================================================
:install_curl_gitbash
echo.
echo [3/5] 使用 Git Bash + curl 安装 opencode...
"C:\Program Files\Git\bin\bash.exe" -c "curl -fsSL https://opencode.ai/install | bash"
if %errorlevel% neq 0 (
    echo       × curl 安装失败，尝试下载二进制文件...
    goto :install_binary
)
echo       √ curl 安装完成
goto :verify_install

:: ============================================================
:: 安装方式: curl via WSL2
:: ============================================================
:install_curl_wsl
echo.
echo [3/5] 使用 WSL2 + curl 安装 opencode...
echo       注意: WSL2 安装的 opencode 仅在 WSL 环境中可用
wsl bash -c "curl -fsSL https://opencode.ai/install | bash"
if %errorlevel% neq 0 (
    echo       × WSL2 curl 安装失败，尝试下载二进制文件...
    goto :install_binary
)
echo       √ WSL2 curl 安装完成
goto :verify_install

:: ============================================================
:: 安装方式: 直接下载二进制文件 (最终回退方案)
:: ============================================================
:install_binary
echo.
echo [3/5] 使用 PowerShell 下载 opencode 二进制文件...

set "INSTALL_DIR=%LOCALAPPDATA%\Programs\opencode"
set "EXE_NAME=opencode.exe"

:: 创建安装目录
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

:: 使用 PowerShell 从 GitHub releases 下载最新版本
echo       正在从 GitHub 下载最新版本...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$ErrorActionPreference = 'Stop'; " ^
    "try { " ^
    "  $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/anomalyco/opencode/releases/latest' -Headers @{'User-Agent'='OpenCode-Installer'}; " ^
    "  $asset = $release.assets | Where-Object { $_.name -like '*windows*amd64*' -and $_.name -like '*.exe' } | Select-Object -First 1; " ^
    "  if (-not $asset) { $asset = $release.assets | Where-Object { $_.name -like '*windows*' -and $_.name -like '*.exe' } | Select-Object -First 1; } " ^
    "  if (-not $asset) { Write-Error 'No Windows binary found in latest release'; exit 1; } " ^
    "  Write-Host ('       下载: ' + $asset.name + ' (' + [math]::Round($asset.size/1MB, 1) + ' MB)'); " ^
    "  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile '%INSTALL_DIR%\%EXE_NAME%' -UseBasicParsing; " ^
    "  Write-Host '       √ 下载完成'; " ^
    "} catch { " ^
    "  Write-Error $_.Exception.Message; exit 1; " ^
    "}"

if %errorlevel% neq 0 (
    echo.
    echo       × 下载失败！请检查网络连接或手动下载：
    echo         https://github.com/anomalyco/opencode/releases/latest
    echo         下载 opencode-cli-windows-amd64.exe 并放入 PATH 目录
    goto :error_exit
)

:: 添加到用户 PATH
echo       正在添加到系统 PATH...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$installDir = '%INSTALL_DIR%'; " ^
    "$userPath = [Environment]::GetEnvironmentVariable('Path', 'User'); " ^
    "if ($userPath -notlike \"*$installDir*\") { " ^
    "  [Environment]::SetEnvironmentVariable('Path', $userPath + ';' + $installDir, 'User'); " ^
    "  Write-Host '       √ 已添加到用户 PATH'; " ^
    "} else { " ^
    "  Write-Host '       √ PATH 中已存在'; " ^
    "}"

:: 更新当前会话的 PATH
set "PATH=%PATH%;%INSTALL_DIR%"

echo       √ 二进制文件安装完成
echo       安装位置: %INSTALL_DIR%\%EXE_NAME%

:: ============================================================
:: [4/5] 验证安装
:: ============================================================
:verify_install
echo.
echo [4/5] 验证安装...

where opencode >nul 2>nul
if %errorlevel%==0 (
    echo       √ opencode CLI 安装成功
    for /f "tokens=*" %%v in ('opencode --version 2^>nul') do echo       版本: %%v
) else (
    :: 检查二进制安装路径
    if exist "%LOCALAPPDATA%\Programs\opencode\opencode.exe" (
        echo       √ opencode 已安装到 %LOCALAPPDATA%\Programs\opencode\
        echo       注意: 请重新打开终端以使 PATH 生效
    ) else (
        echo       × 安装验证失败，请手动检查
        goto :error_exit
    )
)

:: ============================================================
:: [5/5] 配置 Provider
:: ============================================================
:configure
echo.
echo [5/5] 配置 opencode Provider...

:: 配置文件路径: %USERPROFILE%\.config\opencode\opencode.json
set "CONFIG_DIR=%USERPROFILE%\.config\opencode"
set "CONFIG_FILE=%CONFIG_DIR%\opencode.json"

:: 创建配置目录
if not exist "%CONFIG_DIR%" (
    mkdir "%CONFIG_DIR%"
    echo       √ 创建配置目录: %CONFIG_DIR%
)

:: 检查是否已有配置文件
if exist "%CONFIG_FILE%" goto :merge_config
goto :write_new_config

:merge_config
echo       发现已有配置文件: %CONFIG_FILE%
echo       将备份为 opencode.json.bak 并更新 provider 配置...
copy /y "%CONFIG_FILE%" "%CONFIG_FILE%.bak" >nul 2>nul

:: 使用 PowerShell 合并配置（保留已有配置，添加/更新 Recreate provider + agent 权限）
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$ErrorActionPreference = 'Stop'; " ^
    "try { " ^
    "  $configPath = '%CONFIG_FILE%'; " ^
    "  $json = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json; " ^
    "  if (-not $json.provider) { $json | Add-Member -NotePropertyName 'provider' -NotePropertyValue ([PSCustomObject]@{}) -Force; } " ^
    "  $recreate = [PSCustomObject]@{ " ^
    "    'name' = 'Recreate'; " ^
    "    'npm' = '@ai-sdk/openai-compatible'; " ^
    "    'options' = [PSCustomObject]@{ 'baseURL' = 'http://172.16.249.43:8000/v1' }; " ^
    "    'models' = [PSCustomObject]@{ 'claude-opus-4-6' = [PSCustomObject]@{ 'name' = 'Claude-Opus4.6' } } " ^
    "  }; " ^
    "  $json.provider | Add-Member -NotePropertyName 'Recreate' -NotePropertyValue $recreate -Force; " ^
    "  if (-not $json.agent) { $json | Add-Member -NotePropertyName 'agent' -NotePropertyValue ([PSCustomObject]@{}) -Force; } " ^
    "  if (-not $json.agent.build) { $json.agent | Add-Member -NotePropertyName 'build' -NotePropertyValue ([PSCustomObject]@{}) -Force; } " ^
    "  $readPerm = [PSCustomObject]@{ '*.env' = 'allow'; '*.env.*' = 'allow' }; " ^
    "  $perm = [PSCustomObject]@{ 'read' = $readPerm }; " ^
    "  $json.agent.build | Add-Member -NotePropertyName 'permission' -NotePropertyValue $perm -Force; " ^
    "  $json | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8; " ^
    "  Write-Host '       √ 已合并 Recreate provider 和 agent 权限到现有配置'; " ^
    "} catch { " ^
    "  Write-Error $_.Exception.Message; exit 1; " ^
    "}"

if %errorlevel% neq 0 (
    echo       × 合并配置失败，将创建新配置文件...
    goto :write_new_config
)
goto :config_done

:write_new_config
:: 写入全新配置文件
echo       创建新配置文件: %CONFIG_FILE%

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$config = @{ " ^
    "  provider = @{ " ^
    "    Recreate = @{ " ^
    "      name = 'Recreate'; " ^
    "      npm = '@ai-sdk/openai-compatible'; " ^
    "      options = @{ baseURL = 'http://172.16.249.43:8000/v1' }; " ^
    "      models = @{ 'claude-opus-4-6' = @{ name = 'Claude-Opus4.6' } } " ^
    "    } " ^
    "  }; " ^
    "  agent = @{ " ^
    "    build = @{ " ^
    "      permission = @{ " ^
    "        read = @{ '*.env' = 'allow'; '*.env.*' = 'allow' } " ^
    "      } " ^
    "    } " ^
    "  } " ^
    "}; " ^
    "$config | ConvertTo-Json -Depth 10 | Set-Content '%CONFIG_FILE%' -Encoding UTF8; " ^
    "Write-Host '       √ 配置文件已创建';"

if %errorlevel% neq 0 (
    echo       × PowerShell 写入失败，使用 echo 方式写入...
    > "%CONFIG_FILE%" echo {
    >> "%CONFIG_FILE%" echo   "provider": {
    >> "%CONFIG_FILE%" echo     "Recreate": {
    >> "%CONFIG_FILE%" echo       "name": "Recreate",
    >> "%CONFIG_FILE%" echo       "npm": "@ai-sdk/openai-compatible",
    >> "%CONFIG_FILE%" echo       "options": {
    >> "%CONFIG_FILE%" echo         "baseURL": "http://172.16.249.43:8000/v1"
    >> "%CONFIG_FILE%" echo       },
    >> "%CONFIG_FILE%" echo       "models": {
    >> "%CONFIG_FILE%" echo         "claude-opus-4-6": {
    >> "%CONFIG_FILE%" echo           "name": "Claude-Opus4.6"
    >> "%CONFIG_FILE%" echo         }
    >> "%CONFIG_FILE%" echo       }
    >> "%CONFIG_FILE%" echo     }
    >> "%CONFIG_FILE%" echo   },
    >> "%CONFIG_FILE%" echo   "agent": {
    >> "%CONFIG_FILE%" echo     "build": {
    >> "%CONFIG_FILE%" echo       "permission": {
    >> "%CONFIG_FILE%" echo         "read": {
    >> "%CONFIG_FILE%" echo           "*.env": "allow",
    >> "%CONFIG_FILE%" echo           "*.env.*": "allow"
    >> "%CONFIG_FILE%" echo         }
    >> "%CONFIG_FILE%" echo       }
    >> "%CONFIG_FILE%" echo     }
    >> "%CONFIG_FILE%" echo   }
    >> "%CONFIG_FILE%" echo }
    echo       √ 配置文件已创建（echo 方式）
)

:config_done
echo.
echo ============================================================
echo   配置完成！
echo ============================================================
echo.
echo   配置文件位置: %CONFIG_FILE%
echo.
echo   Provider 信息:
echo     名称:    Recreate
echo     Base URL: http://172.16.249.43:8000/v1
echo     SDK:     @ai-sdk/openai-compatible
echo     模型:    claude-opus-4-6 (Claude-Opus4.6)
echo.
echo ============================================================
echo.

:: ============================================================
:: [6] 询问是否立即部署 RagMem 系统
:: ============================================================
echo.
echo   是否立即启动 opencode 并自动部署 RagMem 系统？
echo.
echo     [Y] 是 - 启动 opencode 并自动执行 DEPLOY.md 部署流程
echo     [N] 否 - 稍后手动部署
echo.
set /p "DEPLOY_CHOICE=  请输入选择 (Y/N): "

if /i "%DEPLOY_CHOICE%"=="Y" goto :start_deploy
if /i "%DEPLOY_CHOICE%"=="y" goto :start_deploy
if /i "%DEPLOY_CHOICE%"=="yes" goto :start_deploy

echo.
echo   已跳过自动部署。你可以稍后手动执行：
echo.
echo     1. 打开终端，cd 到本项目目录
echo     2. 运行: opencode
echo     3. 在 opencode 中输入: 请按照 DEPLOY.md 执行部署
echo.
goto :end

:start_deploy
echo.
echo ============================================================
echo   正在启动 opencode 自动部署...
echo ============================================================
echo.

:: 获取本脚本所在目录作为项目根目录
:: Fully_DEPLOY.bat 位于项目根目录
:: 使用 %~dp0 获取脚本目录（末尾带反斜杠）
set "PROJECT_ROOT=%~dp0"
:: 去掉末尾反斜杠
if "%PROJECT_ROOT:~-1%"=="\" set "PROJECT_ROOT=%PROJECT_ROOT:~0,-1%"

:: 检查 DEPLOY.md 是否存在
if not exist "%PROJECT_ROOT%\DEPLOY.md" (
    echo   [X] DEPLOY.md not found
    echo       Expected: %PROJECT_ROOT%\DEPLOY.md
    echo.
    echo   Please make sure this script is in the LLM AI Toolkit root directory.
    echo.
    echo   You can also start opencode manually:
    echo     cd "your project directory"
    echo     opencode
    echo     Then type: follow DEPLOY.md to deploy
    goto :end
)

echo   Project root: %PROJECT_ROOT%
echo   DEPLOY.md:   %PROJECT_ROOT%\DEPLOY.md
echo.

cd /d "%PROJECT_ROOT%"

echo   ============================================================
echo   选择部署模式:
echo.
echo     [1] 自动模式 - opencode 自动读取 DEPLOY.md 并执行部署
echo                    (非交互式，执行完毕后退出)
echo.
echo     [2] 交互模式 - 打开 opencode TUI，手动输入指令
echo                    (可随时干预、暂停、调整)
echo.
echo   ============================================================
set /p "DEPLOY_MODE=  请选择 (1/2): "

if "%DEPLOY_MODE%"=="1" goto :deploy_auto
if "%DEPLOY_MODE%"=="2" goto :deploy_interactive
:: 默认交互模式
goto :deploy_interactive

:deploy_auto
echo.
echo   正在以自动模式启动 opencode...
echo   opencode 将读取 DEPLOY.md 并自动执行部署步骤。
echo.
echo   将在新窗口中运行，请勿关闭该窗口。
echo.

:: 写入临时启动脚本，避免 start 命令中的引号嵌套问题
set "TEMP_SCRIPT=%TEMP%\opencode_deploy_auto.bat"

> "%TEMP_SCRIPT%" echo @echo off
>> "%TEMP_SCRIPT%" echo chcp 65001 ^>nul 2^>nul
>> "%TEMP_SCRIPT%" echo cd /d "%PROJECT_ROOT%"
>> "%TEMP_SCRIPT%" echo echo.
>> "%TEMP_SCRIPT%" echo echo   Working directory: %PROJECT_ROOT%
>> "%TEMP_SCRIPT%" echo echo   Starting opencode auto-deploy...
>> "%TEMP_SCRIPT%" echo echo.
>> "%TEMP_SCRIPT%" echo opencode run -m Recreate/claude-opus-4-6 "Please read the DEPLOY.md file in the current directory carefully, then follow the deployment steps for Phase B (sandbox deployment) only - Phase A (online preparation) has already been completed. Execute steps B1 through B5 in order and verify each step succeeds before moving to the next. Do NOT attempt to install Unity MCP (steps U1-U6) - those are optional and require user interaction. After B5 validation passes, report the deployment result and remind the user they can optionally install Unity MCP later by asking an LLM for help."
>> "%TEMP_SCRIPT%" echo echo.
>> "%TEMP_SCRIPT%" echo echo ============================================================
>> "%TEMP_SCRIPT%" echo echo   opencode has exited. You may close this window.
>> "%TEMP_SCRIPT%" echo echo   To resume: opencode --continue
>> "%TEMP_SCRIPT%" echo echo ============================================================
>> "%TEMP_SCRIPT%" echo pause

:: 在新窗口中运行临时脚本，/wait 等待窗口关闭
start "OpenCode - Auto Deploy" /wait cmd /k "%TEMP_SCRIPT%"

:: 清理临时脚本
del "%TEMP_SCRIPT%" >nul 2>nul

echo.
echo   ============================================================
echo   opencode 部署窗口已关闭。
echo   如需继续或检查部署状态，运行:
echo     cd "%PROJECT_ROOT%"
echo     opencode --continue
echo   ============================================================

goto :end

:deploy_interactive
echo.
echo   正在以交互模式启动 opencode TUI...
echo.
echo   ============================================================
echo   opencode 启动后，请输入以下指令开始部署:
echo.
echo     请按照 DEPLOY.md 执行部署
echo.
echo   按 Ctrl+C 可随时退出 opencode。
echo   ============================================================
echo.

:: 写入临时启动脚本，避免 start 命令中的引号嵌套问题
set "TEMP_SCRIPT=%TEMP%\opencode_deploy_tui.bat"

> "%TEMP_SCRIPT%" echo @echo off
>> "%TEMP_SCRIPT%" echo chcp 65001 ^>nul 2^>nul
>> "%TEMP_SCRIPT%" echo cd /d "%PROJECT_ROOT%"
>> "%TEMP_SCRIPT%" echo opencode
>> "%TEMP_SCRIPT%" echo echo.
>> "%TEMP_SCRIPT%" echo echo   opencode has exited. You may close this window.
>> "%TEMP_SCRIPT%" echo echo   To resume: opencode --continue
>> "%TEMP_SCRIPT%" echo pause

:: 在新窗口中运行临时脚本，/wait 等待窗口关闭
start "OpenCode" /wait cmd /k "%TEMP_SCRIPT%"

:: 清理临时脚本
del "%TEMP_SCRIPT%" >nul 2>nul

echo.
echo   ============================================================
echo   opencode 已退出。
echo   如需恢复部署，运行: opencode --continue
echo   ============================================================

goto :end

:error_exit
echo.
echo ============================================================
echo   安装失败！请尝试以下方式手动安装：
echo.
echo   方式 1 (Scoop):
echo     scoop install opencode
echo.
echo   方式 2 (npm):
echo     npm install -g opencode-ai
echo.
echo   方式 3 (手动下载):
echo     https://github.com/anomalyco/opencode/releases/latest
echo     下载 opencode-cli-windows-amd64.exe
echo     重命名为 opencode.exe 并放入 PATH 目录
echo.
echo   安装后重新运行本脚本即可自动配置
echo ============================================================
echo.
pause
exit /b 1

:end
echo.
pause
endlocal
