# install-unity-mcp.ps1 - Unity MCP Auto-Installer
# Detects Unity version, checks if Unity MCP is installed, and installs the correct version.
#
# Usage:
#   .\tools\install-unity-mcp.ps1                                    # Auto-detect project path (Git URL)
#   .\tools\install-unity-mcp.ps1 -ProjectPath "D:\My Unity Project" # Explicit path
#   .\tools\install-unity-mcp.ps1 -Force                             # Reinstall even if already installed
#   .\tools\install-unity-mcp.ps1 -Check                             # Check only, don't install
#   .\tools\install-unity-mcp.ps1 -McpVersion "9.5.3"                # Override MCP version
#   .\tools\install-unity-mcp.ps1 -Local                             # Install from local .tgz package (offline)
#   .\tools\install-unity-mcp.ps1 -Local -PackagePath ".\packages\com.coplaydev.unity-mcp-9.5.3.tgz"
#   .\tools\install-unity-mcp.ps1 -Embedded                          # Install as embedded package (offline)
#
# Offline Installation (sandbox / air-gapped environments):
#   1. On a machine with internet, run: .\tools\package-unity-mcp.ps1
#   2. Copy the generated .tgz files to the target machine
#   3. Run: .\tools\install-unity-mcp.ps1 -Local
#   Or use -Embedded to copy the package directly into Packages/ directory.
#
# Compatibility:
#   - Any Unity project (2021.3+) with standard Packages/manifest.json
#   - PowerShell 5.1 (Windows) or PowerShell 7+ (cross-platform)

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectPath,
    [switch]$Force,
    [switch]$Check,
    [string]$McpVersion,
    [string]$ConfigPath,
    [switch]$Local,
    [string]$PackagePath,
    [switch]$Embedded
)

# Force UTF-8 output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ============================================================
# Auto-detect Project Path (same logic as generate-snapshot.ps1)
# ============================================================

if (-not $ProjectPath) {
    $scriptDir = $PSScriptRoot
    if ($scriptDir) {
        $candidate = Split-Path $scriptDir -Parent
        if (Test-Path (Join-Path $candidate "Assets")) {
            $ProjectPath = $candidate
            Write-Host "  [Auto] Detected Unity project: $ProjectPath" -ForegroundColor Cyan
        }
    }

    if (-not $ProjectPath) {
        $searchDir = Get-Location
        for ($i = 0; $i -lt 5; $i++) {
            if (Test-Path (Join-Path $searchDir "Assets")) {
                $ProjectPath = $searchDir.ToString()
                Write-Host "  [Auto] Detected Unity project from working directory: $ProjectPath" -ForegroundColor Cyan
                break
            }
            $parent = Split-Path $searchDir -Parent
            if (-not $parent -or $parent -eq $searchDir) { break }
            $searchDir = $parent
        }
    }

    if (-not $ProjectPath) {
        Write-Error "Cannot auto-detect Unity project. Please specify -ProjectPath parameter."
        Write-Host ""
        Write-Host "  Usage: .\tools\install-unity-mcp.ps1 -ProjectPath ""D:\Your Unity Project""" -ForegroundColor Yellow
        exit 1
    }
}

$ProjectPath = (Resolve-Path $ProjectPath -ErrorAction SilentlyContinue).Path
if (-not $ProjectPath) {
    Write-Error "Project path does not exist."
    exit 1
}

# ============================================================
# Validation
# ============================================================

$assetsPath = Join-Path $ProjectPath "Assets"
$packagesPath = Join-Path $ProjectPath "Packages"
$manifestPath = Join-Path $packagesPath "manifest.json"

if (-not (Test-Path $assetsPath)) {
    Write-Error "Cannot find Assets/ directory: $assetsPath"
    exit 1
}

if (-not (Test-Path $manifestPath)) {
    Write-Error "Cannot find Packages/manifest.json: $manifestPath"
    exit 1
}

# ============================================================
# Load Config
# ============================================================

if (-not $ConfigPath) {
    # Look for config in same directory as script
    $ConfigPath = Join-Path $PSScriptRoot "unity-mcp-config.json"
    if (-not (Test-Path $ConfigPath)) {
        # Fallback: look in project's tools/ directory
        $ConfigPath = Join-Path (Join-Path $ProjectPath "tools") "unity-mcp-config.json"
    }
}

$config = $null
if (Test-Path $ConfigPath) {
    try {
        $config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Host "  [Config] Loaded: $ConfigPath" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Failed to parse config file: $ConfigPath. Using built-in defaults."
    }
}

# Built-in defaults (used if config file is missing or invalid)
if (-not $config) {
    Write-Host "  [Config] Using built-in defaults (no config file found)" -ForegroundColor DarkGray
    $config = @{
        package_name = "com.coplaydev.unity-mcp"
        git_base_url = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity"
        version_map = @(
            @{ unity_min = "6000.0.0"; unity_max = $null; mcp_version = "9.6.2"; mcp_tag = "v9.6.2"; status = "recommended" }
            @{ unity_min = "2021.3.0"; unity_max = "2023.9.99"; mcp_version = "9.5.3"; mcp_tag = "v9.5.3"; status = "tested" }
        )
    }
}

$packageName = $config.package_name
$gitBaseUrl = $config.git_base_url

# ============================================================
# Helper Functions
# ============================================================

function Get-UnityVersion {
    param([string]$ProjectPath)
    $versionFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectVersion.txt"
    if (Test-Path $versionFile) {
        $content = Get-Content $versionFile -Raw
        if ($content -match 'm_EditorVersion:\s*(.+)') {
            return $Matches[1].Trim()
        }
    }
    return $null
}

function Parse-UnityVersion {
    # Parses "2022.3.50f1" or "6000.0.25f1" into comparable components
    param([string]$VersionString)
    if ($VersionString -match '^(\d+)\.(\d+)\.(\d+)') {
        return @{
            Major = [int]$Matches[1]
            Minor = [int]$Matches[2]
            Patch = [int]$Matches[3]
            Full  = $VersionString
            Comparable = "$($Matches[1].PadLeft(5,'0')).$($Matches[2].PadLeft(3,'0')).$($Matches[3].PadLeft(3,'0'))"
        }
    }
    return $null
}

function Compare-UnityVersions {
    # Returns: -1 if A < B, 0 if A == B, 1 if A > B
    param([string]$VersionA, [string]$VersionB)
    $a = Parse-UnityVersion $VersionA
    $b = Parse-UnityVersion $VersionB
    if (-not $a -or -not $b) { return 0 }

    if ($a.Major -ne $b.Major) { return [Math]::Sign($a.Major - $b.Major) }
    if ($a.Minor -ne $b.Minor) { return [Math]::Sign($a.Minor - $b.Minor) }
    if ($a.Patch -ne $b.Patch) { return [Math]::Sign($a.Patch - $b.Patch) }
    return 0
}

function Find-McpVersion {
    param([string]$UnityVersion, [object]$Config)
    foreach ($mapping in $Config.version_map) {
        $minOk = $true
        $maxOk = $true

        if ($mapping.unity_min) {
            $cmp = Compare-UnityVersions $UnityVersion $mapping.unity_min
            if ($cmp -lt 0) { $minOk = $false }
        }

        if ($mapping.unity_max) {
            $cmp = Compare-UnityVersions $UnityVersion $mapping.unity_max
            if ($cmp -gt 0) { $maxOk = $false }
        }

        if ($minOk -and $maxOk) {
            return $mapping
        }
    }
    return $null
}

function Get-InstalledMcpInfo {
    param([string]$ManifestPath, [string]$PackageName)
    try {
        $manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
        $deps = $manifest.dependencies
        if ($deps) {
            $entry = $deps.PSObject.Properties | Where-Object { $_.Name -eq $PackageName }
            if ($entry) {
                $value = $entry.Value
                $version = $null
                $tag = $null

                # Parse version from git URL: ...#v9.5.3
                if ($value -match '#v?([\d.]+)$') {
                    $version = $Matches[1]
                    $tag = $value -replace '.*#', ''
                }
                # Parse version from semver: "9.5.3"
                elseif ($value -match '^\d+\.\d+\.\d+') {
                    $version = $value
                }

                return @{
                    Installed = $true
                    Value     = $value
                    Version   = $version
                    Tag       = $tag
                }
            }
        }
    } catch { }
    
    # Also check packages-lock.json for resolved version
    $lockPath = Join-Path (Split-Path $ManifestPath -Parent) "packages-lock.json"
    if (Test-Path $lockPath) {
        try {
            $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
            $lockDeps = $lock.dependencies
            if ($lockDeps) {
                $lockEntry = $lockDeps.PSObject.Properties | Where-Object { $_.Name -eq $PackageName }
                if ($lockEntry) {
                    $lockValue = $lockEntry.Value
                    $resolvedVersion = $lockValue.version
                    return @{
                        Installed = $true
                        Value     = "resolved: $resolvedVersion"
                        Version   = $resolvedVersion
                        Tag       = $null
                    }
                }
            }
        } catch { }
    }

    return @{ Installed = $false; Value = $null; Version = $null; Tag = $null }
}

function Add-PackageToManifest {
    param([string]$ManifestPath, [string]$PackageName, [string]$PackageUrl)

    # Read raw content to preserve formatting
    $content = Get-Content $ManifestPath -Raw

    # Check if package already exists
    if ($content -match [regex]::Escape("`"$PackageName`"")) {
        # Replace existing entry
        $pattern = "`"$([regex]::Escape($PackageName))`"\s*:\s*`"[^`"]*`""
        $replacement = "`"$PackageName`": `"$PackageUrl`""
        $newContent = [regex]::Replace($content, $pattern, $replacement)
    } else {
        # Add new entry after "dependencies": {
        $pattern = '("dependencies"\s*:\s*\{)'
        $replacement = "`$1`n    `"$PackageName`": `"$PackageUrl`","
        $newContent = [regex]::Replace($content, $pattern, $replacement)
    }

    # Write back with UTF-8 BOM (Unity standard)
    $utf8Bom = New-Object System.Text.UTF8Encoding $true
    try {
        [System.IO.File]::WriteAllText($ManifestPath, $newContent, $utf8Bom)
    } catch {
        $newContent | Out-File -FilePath $ManifestPath -Encoding utf8 -Force
    }
}

function Remove-PackageFromManifest {
    # Remove a package entry from manifest.json (used before embedded install)
    param([string]$ManifestPath, [string]$PackageName)

    $content = Get-Content $ManifestPath -Raw
    if ($content -match [regex]::Escape("`"$PackageName`"")) {
        # Remove the line containing the package (and trailing comma if present)
        $pattern = "\s*`"$([regex]::Escape($PackageName))`"\s*:\s*`"[^`"]*`"\s*,?"
        $newContent = [regex]::Replace($content, $pattern, "")
        # Clean up double commas or trailing comma before }
        $newContent = $newContent -replace ',(\s*\})', '$1'

        $utf8Bom = New-Object System.Text.UTF8Encoding $true
        try {
            [System.IO.File]::WriteAllText($ManifestPath, $newContent, $utf8Bom)
        } catch {
            $newContent | Out-File -FilePath $ManifestPath -Encoding utf8 -Force
        }
    }
}

function Find-LocalPackage {
    # Search for a local .tgz package file matching the target version
    param([string]$McpVersion, [string]$PackageName, [string]$ProjectPath, [string]$ScriptRoot)

    $tgzName = "$PackageName-$McpVersion.tgz"
    $searchDirs = @(
        # 1. Explicit PackagePath (handled by caller)
        # 2. packages/ directory next to tools/
        (Join-Path (Split-Path $ScriptRoot -Parent) "packages")
        # 3. tools/ directory itself
        $ScriptRoot
        # 4. Project root
        $ProjectPath
        # 5. Project's Packages/ directory
        (Join-Path $ProjectPath "Packages")
    )

    foreach ($dir in $searchDirs) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }
        $candidate = Join-Path $dir $tgzName
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    # Also search for any tgz matching the package name (version-agnostic fallback)
    foreach ($dir in $searchDirs) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }
        $candidates = Get-ChildItem -Path $dir -Filter "$PackageName-*.tgz" -ErrorAction SilentlyContinue
        if ($candidates) {
            # Return the first match (ideally sorted by version)
            return $candidates[0].FullName
        }
    }

    return $null
}

function Install-EmbeddedPackage {
    # Extract a .tgz package into Packages/<package-name>/ as an embedded package
    param([string]$TgzPath, [string]$PackagesDir, [string]$PackageName)

    $embeddedDir = Join-Path $PackagesDir $PackageName

    # Remove existing embedded package if present
    if (Test-Path $embeddedDir) {
        Write-Host "         Removing existing embedded package..." -ForegroundColor DarkGray
        Remove-Item $embeddedDir -Recurse -Force
    }

    # Extract tgz
    $tempExtract = Join-Path ([System.IO.Path]::GetTempPath()) "unity-mcp-extract-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null

    try {
        $tarCmd = Get-Command tar -ErrorAction SilentlyContinue
        if ($tarCmd) {
            & tar -xzf $TgzPath -C $tempExtract 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "tar extraction failed"
            }
        } else {
            throw "tar command not available. Please install tar or extract manually."
        }

        # The tgz should contain a "package/" directory
        $extractedPackageDir = Join-Path $tempExtract "package"
        if (-not (Test-Path $extractedPackageDir)) {
            # Try finding any directory with package.json
            $dirs = Get-ChildItem -Path $tempExtract -Directory
            foreach ($d in $dirs) {
                if (Test-Path (Join-Path $d.FullName "package.json")) {
                    $extractedPackageDir = $d.FullName
                    break
                }
            }
        }

        if (-not (Test-Path (Join-Path $extractedPackageDir "package.json"))) {
            throw "Cannot find package.json in extracted archive"
        }

        # Move to Packages/<package-name>/
        Copy-Item -Path $extractedPackageDir -Destination $embeddedDir -Recurse -Force
        Write-Host "         Embedded at: $embeddedDir" -ForegroundColor Green
    } finally {
        Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue
    }

    return $embeddedDir
}

# ============================================================
# Main
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Unity MCP Auto-Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Detect Unity version
$unityVersionStr = Get-UnityVersion -ProjectPath $ProjectPath
if (-not $unityVersionStr) {
    Write-Error "Cannot detect Unity version. Missing ProjectSettings/ProjectVersion.txt"
    exit 1
}

$unityVersion = Parse-UnityVersion $unityVersionStr
if (-not $unityVersion) {
    Write-Error "Cannot parse Unity version: $unityVersionStr"
    exit 1
}

Write-Host "  [1/4] Unity Version: $unityVersionStr" -ForegroundColor Green

# Step 2: Determine target MCP version
$targetMapping = $null
if ($McpVersion) {
    # User override
    Write-Host "  [2/4] MCP Version: $McpVersion (user override)" -ForegroundColor Green
    $targetMcpVersion = $McpVersion
    $targetMcpTag = "v$McpVersion"
} else {
    $targetMapping = Find-McpVersion -UnityVersion $unityVersionStr -Config $config
    if (-not $targetMapping) {
        Write-Error "No compatible Unity MCP version found for Unity $unityVersionStr"
        Write-Host ""
        Write-Host "  Known compatible ranges:" -ForegroundColor Yellow
        foreach ($m in $config.version_map) {
            $max = if ($m.unity_max) { $m.unity_max } else { "latest" }
            Write-Host "    Unity $($m.unity_min) ~ $max  ->  MCP v$($m.mcp_version) ($($m.status))" -ForegroundColor Yellow
        }
        Write-Host ""
        Write-Host "  You can manually specify: -McpVersion ""9.5.3""" -ForegroundColor Yellow
        exit 1
    }
    $targetMcpVersion = $targetMapping.mcp_version
    $targetMcpTag = $targetMapping.mcp_tag
    $statusLabel = if ($targetMapping.status) { " ($($targetMapping.status))" } else { "" }
    Write-Host "  [2/4] Target MCP: v$targetMcpVersion$statusLabel" -ForegroundColor Green
    if ($targetMapping.notes) {
        Write-Host "         Note: $($targetMapping.notes)" -ForegroundColor DarkGray
    }
}

# Step 3: Check current installation
$mcpInfo = Get-InstalledMcpInfo -ManifestPath $manifestPath -PackageName $packageName

if ($mcpInfo.Installed) {
    Write-Host "  [3/4] Current: v$($mcpInfo.Version) ($($mcpInfo.Value))" -ForegroundColor Green

    $versionMatch = $mcpInfo.Version -eq $targetMcpVersion

    if ($versionMatch -and -not $Force) {
        Write-Host ""
        Write-Host "  ✓ Unity MCP v$targetMcpVersion is already installed. No action needed." -ForegroundColor Green
        Write-Host ""
        if ($Check) {
            Write-Host "  Status: INSTALLED_CORRECT" -ForegroundColor Green
        }
        exit 0
    }

    if (-not $versionMatch) {
        Write-Host "         Version mismatch: installed v$($mcpInfo.Version), target v$targetMcpVersion" -ForegroundColor Yellow
    }

    if ($Force) {
        Write-Host "         Force reinstall requested" -ForegroundColor Yellow
    }
} else {
    Write-Host "  [3/4] Current: Not installed" -ForegroundColor Yellow
}

if ($Check) {
    Write-Host ""
    if ($mcpInfo.Installed) {
        if ($mcpInfo.Version -eq $targetMcpVersion) {
            Write-Host "  Status: INSTALLED_CORRECT" -ForegroundColor Green
        } else {
            Write-Host "  Status: INSTALLED_WRONG_VERSION (have v$($mcpInfo.Version), need v$targetMcpVersion)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  Status: NOT_INSTALLED (need v$targetMcpVersion)" -ForegroundColor Yellow
    }
    exit 0
}

# Step 4: Install / Update
Write-Host "  [4/4] Installing Unity MCP v$targetMcpVersion..." -ForegroundColor Yellow

# Determine installation mode
$installMode = "git"  # default
$installSource = $null

if ($Embedded) {
    $installMode = "embedded"
    Write-Host "         Mode: Embedded package (offline)" -ForegroundColor Cyan
} elseif ($Local) {
    $installMode = "local"
    Write-Host "         Mode: Local .tgz package (offline)" -ForegroundColor Cyan
} else {
    Write-Host "         Mode: Git URL (requires network)" -ForegroundColor Cyan
}

# --- Mode: Local tgz ---
if ($installMode -eq "local") {
    # Find the .tgz file
    if ($PackagePath -and (Test-Path $PackagePath)) {
        $tgzPath = (Resolve-Path $PackagePath).Path
    } else {
        if ($PackagePath) {
            Write-Warning "Specified PackagePath not found: $PackagePath"
            Write-Host "         Searching for local package..." -ForegroundColor DarkGray
        }
        $tgzPath = Find-LocalPackage -McpVersion $targetMcpVersion -PackageName $packageName `
                                      -ProjectPath $ProjectPath -ScriptRoot $PSScriptRoot
    }

    if (-not $tgzPath) {
        Write-Error "Cannot find local package file for v$targetMcpVersion"
        Write-Host ""
        Write-Host "  Expected file: $packageName-$targetMcpVersion.tgz" -ForegroundColor Yellow
        Write-Host "  Searched in:" -ForegroundColor Yellow
        Write-Host "    - $(Join-Path (Split-Path $PSScriptRoot -Parent) 'packages')" -ForegroundColor Yellow
        Write-Host "    - $PSScriptRoot" -ForegroundColor Yellow
        Write-Host "    - $ProjectPath" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  To create the package, run on a machine with internet:" -ForegroundColor Yellow
        Write-Host "    .\tools\package-unity-mcp.ps1 -Version ""$targetMcpVersion""" -ForegroundColor White
        exit 1
    }

    Write-Host "         Package: $tgzPath" -ForegroundColor DarkGray

    # Copy tgz to project's Packages/ directory for reliable file: reference
    $localTgzName = Split-Path $tgzPath -Leaf
    $projectTgzPath = Join-Path $packagesPath $localTgzName
    if ($tgzPath -ne $projectTgzPath) {
        Copy-Item $tgzPath $projectTgzPath -Force
        Write-Host "         Copied to: $projectTgzPath" -ForegroundColor DarkGray
    }

    # Use file: protocol with relative path (Unity supports this)
    $installSource = "file:$localTgzName"
}

# --- Mode: Embedded ---
if ($installMode -eq "embedded") {
    # Find the .tgz file first (same search logic)
    if ($PackagePath -and (Test-Path $PackagePath)) {
        $tgzPath = (Resolve-Path $PackagePath).Path
    } else {
        $tgzPath = Find-LocalPackage -McpVersion $targetMcpVersion -PackageName $packageName `
                                      -ProjectPath $ProjectPath -ScriptRoot $PSScriptRoot
    }

    if (-not $tgzPath) {
        # Check if already embedded
        $embeddedDir = Join-Path $packagesPath $packageName
        if (Test-Path (Join-Path $embeddedDir "package.json")) {
            Write-Host "         Already embedded at: $embeddedDir" -ForegroundColor Green
            $installSource = "embedded_exists"
        } else {
            Write-Error "Cannot find local package file for v$targetMcpVersion and no embedded package exists."
            Write-Host ""
            Write-Host "  To create the package, run on a machine with internet:" -ForegroundColor Yellow
            Write-Host "    .\tools\package-unity-mcp.ps1 -Version ""$targetMcpVersion""" -ForegroundColor White
            exit 1
        }
    } else {
        Write-Host "         Package: $tgzPath" -ForegroundColor DarkGray
    }
}

# --- Mode: Git URL ---
if ($installMode -eq "git") {
    $gitUrl = "$gitBaseUrl#$targetMcpTag"
    $installSource = $gitUrl
    Write-Host "         URL: $gitUrl" -ForegroundColor DarkGray
}

# Backup manifest
$backupPath = "$manifestPath.bak"
Copy-Item $manifestPath $backupPath -Force
Write-Host "         Backup: $backupPath" -ForegroundColor DarkGray

# Perform installation
try {
    if ($installMode -eq "embedded") {
        if ($installSource -ne "embedded_exists") {
            # Extract tgz to Packages/<package-name>/
            Install-EmbeddedPackage -TgzPath $tgzPath -PackagesDir $packagesPath -PackageName $packageName
        }
        # Remove any git/tgz reference from manifest.json (embedded packages are auto-detected)
        Remove-PackageFromManifest -ManifestPath $manifestPath -PackageName $packageName
    } elseif ($installMode -eq "local") {
        # Use file: protocol in manifest.json
        Add-PackageToManifest -ManifestPath $manifestPath -PackageName $packageName -PackageUrl $installSource
    } else {
        # Git URL in manifest.json
        Add-PackageToManifest -ManifestPath $manifestPath -PackageName $packageName -PackageUrl $installSource
    }
} catch {
    Write-Error "Failed to install: $_"
    Write-Host "  Restoring backup..." -ForegroundColor Red
    Copy-Item $backupPath $manifestPath -Force
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Done! Unity MCP v$targetMcpVersion configured." -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Install mode: $installMode" -ForegroundColor Cyan
Write-Host "  Modified: $manifestPath" -ForegroundColor Cyan
Write-Host "  Backup:   $backupPath" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor Yellow
Write-Host "    1. Open Unity Editor (or switch to it if already open)" -ForegroundColor White
Write-Host "    2. Unity will auto-detect manifest change and import the package" -ForegroundColor White
Write-Host "    3. Wait for compilation to finish" -ForegroundColor White
Write-Host "    4. Open Window > MCP For Unity and verify status is 'Healthy'" -ForegroundColor White
Write-Host ""
Write-Host "  If Unity is already open, it should auto-refresh." -ForegroundColor DarkGray
Write-Host "  If not, you can trigger refresh via Assets > Refresh (Ctrl+R)." -ForegroundColor DarkGray
Write-Host ""

# Output machine-readable result for AI agents
$result = @{
    action           = if ($mcpInfo.Installed) { "updated" } else { "installed" }
    install_mode     = $installMode
    package          = $packageName
    version          = $targetMcpVersion
    tag              = $targetMcpTag
    source           = $installSource
    unity_version    = $unityVersionStr
    manifest_path    = $manifestPath
    backup_path      = $backupPath
    previous_version = $mcpInfo.Version
}
# Write result as JSON comment for script consumers
Write-Host "  [Result JSON]" -ForegroundColor DarkGray
Write-Host "  $($result | ConvertTo-Json -Compress)" -ForegroundColor DarkGray
Write-Host ""
