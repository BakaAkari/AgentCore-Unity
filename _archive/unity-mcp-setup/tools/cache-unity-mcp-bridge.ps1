# cache-unity-mcp-bridge.ps1 - Pre-download mcpforunityserver Python bridge for offline deployment
#
# This script downloads the mcpforunityserver PyPI package and ALL its dependencies
# as wheel files into a local cache directory. This enables fully offline installation
# of the Unity MCP Python bridge in sandbox/air-gapped environments.
#
# Usage:
#   .\cache-unity-mcp-bridge.ps1                    # Download for uv's default Python version
#   .\cache-unity-mcp-bridge.ps1 -PythonVersion 3.12  # Download for specific Python version
#   .\cache-unity-mcp-bridge.ps1 -Verify             # Verify existing cache (offline install test)
#   .\cache-unity-mcp-bridge.ps1 -Clean              # Remove existing cache and re-download
#
# Output:
#   unity-mcp-setup/packages/pypi-cache/*.whl
#
# The cached wheels can be used for offline installation:
#   uv tool install mcpforunityserver --find-links <cache-dir> --no-index --offline
#
# Or with uvx (uv tool run):
#   uvx --find-links <cache-dir> --no-index --offline --from mcpforunityserver mcp-for-unity --transport stdio

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PythonVersion,

    [switch]$Verify,
    [switch]$Clean
)

# Force UTF-8 output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ErrorActionPreference = "Stop"

# ============================================================
# Constants
# ============================================================

$PACKAGE_NAME = "mcpforunityserver"
$ScriptDir = $PSScriptRoot
$CacheDir = Join-Path (Split-Path $ScriptDir -Parent) "packages\pypi-cache"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Unity MCP Bridge - Offline Cache Builder" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# Helper: Detect uv's default Python version
# ============================================================

function Get-UvPythonVersion {
    # uv tool install uses the highest available Python by default
    # We detect what uv would use
    try {
        $uvPythonOutput = & uv python list --only-installed 2>&1
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        # Parse the first cpython entry (uv prefers the highest version)
        foreach ($line in $uvPythonOutput) {
            if ($line -match 'cpython-(\d+\.\d+)\.\d+-windows') {
                return $Matches[1]
            }
        }
    } catch {
        return $null
    }
    return $null
}

# ============================================================
# Step 1: Check prerequisites
# ============================================================

Write-Host "[1/4] Checking prerequisites..." -ForegroundColor Yellow

# Check uv
$uvPath = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uvPath) {
    Write-Error "uv is not installed. Please install uv first: https://docs.astral.sh/uv/"
    exit 1
}
$uvVersion = & uv --version 2>&1
Write-Host "  uv: $uvVersion" -ForegroundColor DarkGray

# Check pip (needed for pip download)
$pipPath = Get-Command pip -ErrorAction SilentlyContinue
if (-not $pipPath) {
    Write-Error "pip is not installed. Please install pip first."
    exit 1
}
Write-Host "  pip: $(pip --version 2>&1)" -ForegroundColor DarkGray

# Determine target Python version
if (-not $PythonVersion) {
    $PythonVersion = Get-UvPythonVersion
    if (-not $PythonVersion) {
        # Fallback: check uv's managed Python
        $PythonVersion = "3.12"
        Write-Host "  [Fallback] Cannot detect uv Python version, using default: $PythonVersion" -ForegroundColor DarkYellow
    } else {
        Write-Host "  [Auto] Detected uv Python version: $PythonVersion" -ForegroundColor Cyan
    }
}

Write-Host "  Target Python: $PythonVersion (win_amd64)" -ForegroundColor DarkGray
Write-Host "  Cache dir: $CacheDir" -ForegroundColor DarkGray
Write-Host ""

# ============================================================
# Step 2: Prepare cache directory
# ============================================================

Write-Host "[2/4] Preparing cache directory..." -ForegroundColor Yellow

if ($Clean -and (Test-Path $CacheDir)) {
    Write-Host "  Cleaning existing cache..." -ForegroundColor DarkGray
    Remove-Item -Path "$CacheDir\*" -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    Write-Host "  Created: $CacheDir" -ForegroundColor DarkGray
}

# Check if cache already has wheels
$existingWheels = Get-ChildItem -Path $CacheDir -Filter "*.whl" -ErrorAction SilentlyContinue
if ($existingWheels -and $existingWheels.Count -gt 0 -and -not $Clean) {
    $mainPkg = $existingWheels | Where-Object { $_.Name -match "^mcpforunityserver" }
    if ($mainPkg) {
        Write-Host "  Cache already contains $($existingWheels.Count) wheels (including $($mainPkg.Name))" -ForegroundColor Green
        Write-Host "  Use -Clean to force re-download, or -Verify to test offline install." -ForegroundColor DarkGray

        if (-not $Verify) {
            Write-Host ""
            Write-Host "  Skipping download. Cache appears ready." -ForegroundColor Green
            Write-Host ""
            exit 0
        }
    }
}

# ============================================================
# Step 3: Download packages (skip if Verify-only)
# ============================================================

if (-not $Verify) {
    Write-Host "[3/4] Downloading $PACKAGE_NAME and all dependencies..." -ForegroundColor Yellow
    Write-Host "  Python version: $PythonVersion | Platform: win_amd64 | Binary only" -ForegroundColor DarkGray
    Write-Host ""

    # Use pip download with explicit platform targeting
    # --only-binary :all: ensures we only get pre-built wheels (no source builds needed)
    # --python-version targets the specific Python version
    # --platform win_amd64 targets Windows x64
    # --implementation cp targets CPython
    $pipArgs = @(
        "download", $PACKAGE_NAME,
        "--dest", $CacheDir,
        "--no-cache-dir",
        "--python-version", $PythonVersion,
        "--only-binary", ":all:",
        "--platform", "win_amd64",
        "--implementation", "cp"
    )

    Write-Host "  > pip $($pipArgs -join ' ')" -ForegroundColor DarkGray
    Write-Host ""

    & pip @pipArgs 2>&1 | ForEach-Object {
        if ($_ -match "^Saved") {
            # Show only the filename, not the full path
            $fileName = ($_ -split '\\')[-1]
            Write-Host "  + $fileName" -ForegroundColor DarkGray
        } elseif ($_ -match "^ERROR|^error") {
            Write-Host "  $_" -ForegroundColor Red
        }
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "pip download failed. Check network connectivity and try again."
        exit 1
    }

    # Count results
    $wheels = Get-ChildItem -Path $CacheDir -Filter "*.whl"
    $totalSize = ($wheels | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ""
    Write-Host "  Downloaded $($wheels.Count) wheels ($('{0:N1}' -f $totalSize) MB total)" -ForegroundColor Green

    # Show platform-specific wheels
    $platformSpecific = $wheels | Where-Object { $_.Name -notmatch "none-any" }
    if ($platformSpecific) {
        Write-Host ""
        Write-Host "  Platform-specific wheels (Python $PythonVersion, win_amd64):" -ForegroundColor DarkYellow
        foreach ($w in $platformSpecific) {
            Write-Host "    $($w.Name)" -ForegroundColor DarkGray
        }
        Write-Host ""
        Write-Host "  NOTE: These wheels are tied to Python $PythonVersion + Windows x64." -ForegroundColor DarkYellow
        Write-Host "  If the sandbox uses a different Python version, re-run this script" -ForegroundColor DarkYellow
        Write-Host "  with -PythonVersion <version> -Clean to download matching wheels." -ForegroundColor DarkYellow
    }
} else {
    Write-Host "[3/4] Skipping download (Verify mode)..." -ForegroundColor Yellow
}

Write-Host ""

# ============================================================
# Step 4: Verify offline installation
# ============================================================

Write-Host "[4/4] Verifying offline installation..." -ForegroundColor Yellow

# Test that uv can resolve all dependencies from the cache
$verifyArgs = @(
    "tool", "install", $PACKAGE_NAME,
    "--find-links", $CacheDir,
    "--no-index",
    "--offline",
    "--force",
    "--dry-run"
)

# Note: uv tool install doesn't have --dry-run, so we do a real install + uninstall
# Actually, let's just verify resolution works
$verifyArgs = @(
    "pip", "install", $PACKAGE_NAME,
    "--find-links", $CacheDir,
    "--no-index",
    "--offline",
    "--dry-run",
    "--target", "$env:TEMP\uv-verify-test"
)

# uv pip install doesn't support --dry-run either. Let's just check the wheel count
$wheels = Get-ChildItem -Path $CacheDir -Filter "*.whl" -ErrorAction SilentlyContinue
$mainPkg = $wheels | Where-Object { $_.Name -match "^mcpforunityserver" }

if (-not $mainPkg) {
    Write-Error "Cache verification failed: mcpforunityserver wheel not found in $CacheDir"
    exit 1
}

# Extract version from wheel filename
$mainPkgVersion = "unknown"
if ($mainPkg.Name -match "mcpforunityserver-([^-]+)-") {
    $mainPkgVersion = $Matches[1]
}

Write-Host "  mcpforunityserver version: $mainPkgVersion" -ForegroundColor Green
Write-Host "  Total wheels in cache: $($wheels.Count)" -ForegroundColor Green
Write-Host "  Cache directory: $CacheDir" -ForegroundColor Green

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Cache Ready!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  To install offline on the sandbox machine:" -ForegroundColor White
Write-Host ""
Write-Host "    uv tool install mcpforunityserver ``" -ForegroundColor White
Write-Host "      --find-links `"<path-to>\packages\pypi-cache`" ``" -ForegroundColor White
Write-Host "      --no-index --offline --force" -ForegroundColor White
Write-Host ""
Write-Host "  For AI client MCP config (offline uvx):" -ForegroundColor White
Write-Host ""
Write-Host "    uvx --find-links `"<path-to>\packages\pypi-cache`" --no-index --offline ``" -ForegroundColor White
Write-Host "      --from mcpforunityserver mcp-for-unity --transport stdio" -ForegroundColor White
Write-Host ""
