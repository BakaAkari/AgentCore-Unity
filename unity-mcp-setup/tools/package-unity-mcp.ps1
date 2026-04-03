# package-unity-mcp.ps1 - Unity MCP Offline Packager
# Run on a machine with internet access. Downloads Unity MCP packages from
# GitHub and creates .tgz files for offline / sandbox environments.
#
# Usage:
#   .\tools\package-unity-mcp.ps1                          # Package all versions
#   .\tools\package-unity-mcp.ps1 -Version "9.5.3"         # Package specific version
#   .\tools\package-unity-mcp.ps1 -OutputDir ".\packages"  # Custom output directory
#
# Prerequisites:
#   - Git (for cloning the repository)
#   - Internet access to GitHub
#
# Output:
#   packages/com.coplaydev.unity-mcp-9.5.3.tgz
#   packages/com.coplaydev.unity-mcp-9.6.2.tgz

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$ConfigPath,
    [switch]$SkipCleanup
)

# Force UTF-8 output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# ============================================================
# Load Config
# ============================================================

if (-not $ConfigPath) {
    $ConfigPath = Join-Path $PSScriptRoot "unity-mcp-config.json"
}

if (-not (Test-Path $ConfigPath)) {
    Write-Error "Config file not found: $ConfigPath"
    exit 1
}

$config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$gitBaseUrl = $config.git_base_url
# Extract repo URL (before ?path=)
$repoUrl = ($gitBaseUrl -split '\?')[0]
# Extract subpath (after ?path=)
$subPath = ""
if ($gitBaseUrl -match '\?path=([^#]+)') {
    $subPath = $Matches[1].TrimStart('/')
}

if (-not $OutputDir) {
    $OutputDir = Join-Path (Join-Path $PSScriptRoot "..") "packages"
}

# Create output directory
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}
$OutputDir = (Resolve-Path $OutputDir).Path

# ============================================================
# Determine versions to package
# ============================================================

$versionsToPackage = @()
if ($Version) {
    # Single version specified
    $mapping = $config.version_map | Where-Object { $_.mcp_version -eq $Version }
    if ($mapping) {
        $versionsToPackage += $mapping
    } else {
        # Create ad-hoc mapping
        $versionsToPackage += @{
            mcp_version = $Version
            mcp_tag     = "v$Version"
        }
    }
} else {
    # Package all versions from config
    $versionsToPackage = $config.version_map
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Unity MCP Offline Packager" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Repository: $repoUrl" -ForegroundColor DarkGray
Write-Host "  Sub-path:   $subPath" -ForegroundColor DarkGray
Write-Host "  Output:     $OutputDir" -ForegroundColor DarkGray
Write-Host "  Versions:   $($versionsToPackage | ForEach-Object { $_.mcp_version }) " -ForegroundColor DarkGray
Write-Host ""

# ============================================================
# Check prerequisites
# ============================================================

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCmd) {
    Write-Error "Git is not installed or not in PATH. Please install Git first."
    exit 1
}

# ============================================================
# Clone and package each version
# ============================================================

$tempBase = Join-Path ([System.IO.Path]::GetTempPath()) "unity-mcp-packager"
if (Test-Path $tempBase) {
    Remove-Item $tempBase -Recurse -Force
}
New-Item -ItemType Directory -Path $tempBase -Force | Out-Null

$results = @()

foreach ($ver in $versionsToPackage) {
    $mcpVersion = $ver.mcp_version
    $mcpTag = $ver.mcp_tag
    $outputFile = Join-Path $OutputDir "com.coplaydev.unity-mcp-$mcpVersion.tgz"

    Write-Host "  [$mcpVersion] Packaging..." -ForegroundColor Yellow

    # Clone specific tag (shallow)
    $cloneDir = Join-Path $tempBase "clone-$mcpVersion"
    Write-Host "    Cloning tag $mcpTag..." -ForegroundColor DarkGray

    $gitArgs = @("clone", "--depth", "1", "--branch", $mcpTag, $repoUrl, $cloneDir)
    $gitResult = & git @gitArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "  [$mcpVersion] Failed to clone: $gitResult"
        $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "Clone failed" }
        continue
    }

    # Locate package directory
    $packageDir = Join-Path $cloneDir $subPath
    if (-not (Test-Path $packageDir)) {
        Write-Warning "  [$mcpVersion] Package directory not found: $subPath"
        $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "Sub-path not found" }
        continue
    }

    # Verify package.json exists
    $packageJsonPath = Join-Path $packageDir "package.json"
    if (-not (Test-Path $packageJsonPath)) {
        Write-Warning "  [$mcpVersion] No package.json found in $packageDir"
        $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "No package.json" }
        continue
    }

    # Read package.json to verify
    $packageJson = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
    Write-Host "    Package: $($packageJson.name) v$($packageJson.version)" -ForegroundColor DarkGray

    # Create tgz using npm pack (if available) or manual tar
    # Unity expects the tgz to contain a "package/" root directory
    $stagingDir = Join-Path $tempBase "staging-$mcpVersion"
    $packageStagingDir = Join-Path $stagingDir "package"
    New-Item -ItemType Directory -Path $packageStagingDir -Force | Out-Null

    # Copy package contents to staging/package/
    Copy-Item -Path "$packageDir\*" -Destination $packageStagingDir -Recurse -Force

    # Remove .git and other unnecessary files
    $cleanupPaths = @(".git", ".github", ".gitignore", ".gitattributes", "*.md~", "*.bak")
    foreach ($pattern in $cleanupPaths) {
        $toRemove = Get-ChildItem -Path $packageStagingDir -Filter $pattern -Recurse -Force -ErrorAction SilentlyContinue
        foreach ($item in $toRemove) {
            Remove-Item $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    # Create .tgz
    Write-Host "    Creating tgz..." -ForegroundColor DarkGray

    # Try using tar (available on Windows 10+)
    $tarCmd = Get-Command tar -ErrorAction SilentlyContinue
    if ($tarCmd) {
        Push-Location $stagingDir
        try {
            & tar -czf $outputFile "package"
            if ($LASTEXITCODE -ne 0) {
                throw "tar failed with exit code $LASTEXITCODE"
            }
        } catch {
            Write-Warning "  [$mcpVersion] tar failed: $_"
            Pop-Location
            $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "tar failed: $_" }
            continue
        }
        Pop-Location
    } else {
        # Fallback: use .NET compression
        Write-Host "    Using .NET compression (tar not available)..." -ForegroundColor DarkGray
        try {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            # Create zip first, then we'll note it's actually a zip (Unity also accepts this)
            $zipPath = $outputFile -replace '\.tgz$', '.zip'
            [System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $zipPath)
            Move-Item $zipPath $outputFile -Force
            Write-Warning "  [$mcpVersion] Created as zip (tar unavailable). May need manual conversion."
        } catch {
            Write-Warning "  [$mcpVersion] Compression failed: $_"
            $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "Compression failed: $_" }
            continue
        }
    }

    if (Test-Path $outputFile) {
        $fileSize = (Get-Item $outputFile).Length
        $fileSizeKB = [math]::Round($fileSize / 1024, 1)
        Write-Host "    ✓ Created: $outputFile ($fileSizeKB KB)" -ForegroundColor Green
        $results += @{ Version = $mcpVersion; Status = "OK"; Path = $outputFile; Size = "$fileSizeKB KB" }
    } else {
        Write-Warning "  [$mcpVersion] Output file not created"
        $results += @{ Version = $mcpVersion; Status = "FAILED"; Error = "Output not created" }
    }
}

# ============================================================
# Cleanup
# ============================================================

if (-not $SkipCleanup) {
    Write-Host ""
    Write-Host "  Cleaning up temp files..." -ForegroundColor DarkGray
    Remove-Item $tempBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ============================================================
# Summary
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Packaging Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

foreach ($r in $results) {
    if ($r.Status -eq "OK") {
        Write-Host "  ✓ v$($r.Version): $($r.Path) ($($r.Size))" -ForegroundColor Green
    } else {
        Write-Host "  ✗ v$($r.Version): $($r.Error)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "  Next steps:" -ForegroundColor Yellow
Write-Host "    1. Copy the .tgz files to the target machine" -ForegroundColor White
Write-Host "    2. Place them in the Unity project's tools/ or packages/ directory" -ForegroundColor White
Write-Host "    3. Run install-unity-mcp.ps1 -Local to install from local package" -ForegroundColor White
Write-Host ""

# Output machine-readable result
$summary = @{
    output_dir = $OutputDir
    results    = $results
}
Write-Host "  [Result JSON]" -ForegroundColor DarkGray
Write-Host "  $($summary | ConvertTo-Json -Compress -Depth 3)" -ForegroundColor DarkGray
Write-Host ""
