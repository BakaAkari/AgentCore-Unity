<#
.SYNOPSIS
    Deploy Unity Agent Rules to a Unity project directory.

.DESCRIPTION
    Copies AGENTS.md, .agents/, and tools/ (generate-snapshot.ps1 only) from the
    unity-agent-rules source directory to a target Unity project.

    This script is part of the LLM AI Toolkit and is designed to be run from the
    toolkit's unity-agent-rules/ directory.

.PARAMETER ProjectPath
    Path to the target Unity project root directory (must contain an Assets/ folder).
    If omitted, the script will attempt to auto-detect by searching upward from CWD.

.PARAMETER Force
    Overwrite existing files without prompting.

.PARAMETER DryRun
    Show what would be copied without actually copying.

.EXAMPLE
    .\deploy-agent-rules.ps1 -ProjectPath "D:\MyUnityProject"

.EXAMPLE
    .\deploy-agent-rules.ps1 -Force

.EXAMPLE
    .\deploy-agent-rules.ps1 -DryRun
#>

[CmdletBinding()]
param(
    [string]$ProjectPath,
    [switch]$Force,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resolve source directory (where this script lives) ──────────────────────
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$SourceRoot = Split-Path -Parent $ScriptDir  # unity-agent-rules/

# ── Validate source structure ────────────────────────────────────────────────
$requiredFiles = @(
    (Join-Path $SourceRoot "AGENTS.md"),
    (Join-Path $SourceRoot ".agents" "skills" "README.md"),
    (Join-Path $ScriptDir "generate-snapshot.ps1")
)

foreach ($f in $requiredFiles) {
    if (-not (Test-Path $f)) {
        Write-Error "Source file not found: $f`nIs this script located in unity-agent-rules/tools/?"
        exit 1
    }
}

# ── Resolve target Unity project ────────────────────────────────────────────
function Find-UnityProject {
    param([string]$StartPath)
    $current = (Resolve-Path $StartPath).Path
    while ($current) {
        if (Test-Path (Join-Path $current "Assets")) {
            return $current
        }
        $parent = Split-Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $null
}

if (-not $ProjectPath) {
    Write-Host "[Auto-detect] Searching for Unity project from current directory..." -ForegroundColor Cyan
    $ProjectPath = Find-UnityProject -StartPath (Get-Location).Path
    if (-not $ProjectPath) {
        Write-Error "Could not auto-detect a Unity project (no Assets/ directory found).`nPlease specify -ProjectPath explicitly."
        exit 1
    }
    Write-Host "[Auto-detect] Found Unity project: $ProjectPath" -ForegroundColor Green
}

# Validate target
if (-not (Test-Path (Join-Path $ProjectPath "Assets"))) {
    Write-Error "Target path does not appear to be a Unity project (no Assets/ directory): $ProjectPath"
    exit 1
}

$ProjectPath = (Resolve-Path $ProjectPath).Path

# ── Define copy manifest ────────────────────────────────────────────────────
# Each entry: [source_relative_to_SourceRoot, dest_relative_to_ProjectPath, is_directory]
$manifest = @(
    @{ Src = "AGENTS.md";                          Dest = "AGENTS.md";                          IsDir = $false },
    @{ Src = ".agents";                            Dest = ".agents";                             IsDir = $true  },
    @{ Src = "tools\generate-snapshot.ps1";        Dest = "tools\generate-snapshot.ps1";        IsDir = $false }
)

# ── Copy files ───────────────────────────────────────────────────────────────
$copied = 0
$skipped = 0

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Deploy Unity Agent Rules" -ForegroundColor Cyan
Write-Host "  Source: $SourceRoot" -ForegroundColor DarkGray
Write-Host "  Target: $ProjectPath" -ForegroundColor DarkGray
if ($DryRun) {
    Write-Host "  Mode:   DRY RUN (no files will be copied)" -ForegroundColor Yellow
}
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

foreach ($item in $manifest) {
    $srcPath = Join-Path $SourceRoot $item.Src
    $destPath = Join-Path $ProjectPath $item.Dest

    if (-not (Test-Path $srcPath)) {
        Write-Warning "Source not found, skipping: $($item.Src)"
        $skipped++
        continue
    }

    $exists = Test-Path $destPath

    if ($item.IsDir) {
        # Directory copy
        if ($exists -and -not $Force) {
            Write-Host "  [EXISTS] $($item.Dest)/ — use -Force to overwrite" -ForegroundColor Yellow
            $skipped++
            continue
        }

        if ($DryRun) {
            Write-Host "  [WOULD COPY] $($item.Src)/ → $($item.Dest)/" -ForegroundColor DarkGray
        } else {
            # Ensure parent directory exists
            $destParent = Split-Path $destPath -Parent
            if (-not (Test-Path $destParent)) {
                New-Item -ItemType Directory -Path $destParent -Force | Out-Null
            }
            # Remove existing and copy fresh
            if ($exists) {
                Remove-Item -Path $destPath -Recurse -Force
            }
            Copy-Item -Path $srcPath -Destination $destPath -Recurse -Force
            Write-Host "  [COPIED] $($item.Src)/ → $($item.Dest)/" -ForegroundColor Green
        }
        $copied++
    } else {
        # File copy
        if ($exists -and -not $Force) {
            Write-Host "  [EXISTS] $($item.Dest) — use -Force to overwrite" -ForegroundColor Yellow
            $skipped++
            continue
        }

        if ($DryRun) {
            Write-Host "  [WOULD COPY] $($item.Src) → $($item.Dest)" -ForegroundColor DarkGray
        } else {
            # Ensure parent directory exists
            $destParent = Split-Path $destPath -Parent
            if (-not (Test-Path $destParent)) {
                New-Item -ItemType Directory -Path $destParent -Force | Out-Null
            }
            Copy-Item -Path $srcPath -Destination $destPath -Force
            Write-Host "  [COPIED] $($item.Src) → $($item.Dest)" -ForegroundColor Green
        }
        $copied++
    }
}

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "───────────────────────────────────────────────────────" -ForegroundColor DarkGray
if ($DryRun) {
    Write-Host "  Dry run complete: $copied would be copied, $skipped skipped" -ForegroundColor Yellow
} else {
    Write-Host "  Deployment complete: $copied copied, $skipped skipped" -ForegroundColor Green
}
Write-Host "───────────────────────────────────────────────────────" -ForegroundColor DarkGray
Write-Host ""

if (-not $DryRun -and $copied -gt 0) {
    Write-Host "Next step: Run the project snapshot generator:" -ForegroundColor Cyan
    Write-Host "  cd `"$ProjectPath`"" -ForegroundColor White
    Write-Host "  .\tools\generate-snapshot.ps1" -ForegroundColor White
    Write-Host ""
}
