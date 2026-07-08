# ============================================================================
# AgentCore Unity — Tarball Structural Integrity Verifier
# ============================================================================
# Purpose:
#   Verify that a produced .tgz contains all critical code directories.
#   Complements tools/verify-meta.ps1 (which runs pre-pack against the source
#   tree) by running POST-pack against the actual tarball.
#
#   This exists because .npmignore glob semantics can silently exclude entire
#   critical directories. Historical incident (v1.4.6):
#     '.npmignore' had 'tools/' (no leading '/'), which minimatch expanded to
#     match ANY 'tools/' anywhere in the tree, including 'Editor/Tools/'
#     (case-insensitive on Windows). The resulting tarball was missing ~150
#     .cs files (Native/Cloud/FileSystem/Infrastructure/Safety tools) and
#     failed to compile in the target project. verify-meta.ps1 passed because
#     it scans the SOURCE tree, not the tarball.
#
# Usage:
#   powershell -File tools/verify-tarball.ps1
#   powershell -File tools/verify-tarball.ps1 -Tarball com.agentcore.unity-1.4.6.tgz
#
# Exit codes:
#   0  Tarball contains all required paths
#   1  One or more critical paths missing
#   2  Script argument / environment error
# ============================================================================

[CmdletBinding()]
param(
    [string]$Tarball = "",
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

# Auto-detect tarball if not specified: prefer the highest-version tgz matching
# the package name pattern. Users can override with -Tarball.
if ([string]::IsNullOrEmpty($Tarball)) {
    $candidates = Get-ChildItem -Path '.' -Filter 'com.agentcore.unity-*.tgz' -File `
        | Sort-Object -Property LastWriteTime -Descending
    if (-not $candidates -or $candidates.Count -eq 0) {
        Write-Error "No com.agentcore.unity-*.tgz found in current directory. Run 'npm pack' first or pass -Tarball explicitly."
        exit 2
    }
    $Tarball = $candidates[0].Name
}

if (-not (Test-Path -LiteralPath $Tarball -PathType Leaf)) {
    Write-Error "Tarball not found: $Tarball"
    exit 2
}

# Required top-level directories inside the tarball. Every entry must have at
# least one .cs (or .asmdef for asmdef-only dirs) file present.
#
# These are the load-bearing code trees: if any is missing, the package will
# not compile in the target project. Anchor points must remain stable across
# refactors — when moving code, update this list in the SAME commit.
$requiredPaths = @(
    @{ Path = 'package/Editor/AgentCore.Editor.asmdef'; MinCount = 1; Desc = 'Main asmdef' },
    @{ Path = 'package/Editor/Bootstrap/';              MinCount = 3; Desc = 'Bootstrap loader + resources' },
    @{ Path = 'package/Editor/Bootstrap/Resources/SOUL.md'; MinCount = 1; Desc = 'SOUL.md (embedded)' },
    @{ Path = 'package/Editor/Config/AgentCoreSettings.cs'; MinCount = 1; Desc = 'Settings core' },
    @{ Path = 'package/Editor/Config/Settings/Pages/';   MinCount = 5; Desc = 'Settings pages' },
    @{ Path = 'package/Editor/Core/AgentLoop';           MinCount = 5; Desc = 'AgentLoop partials' },
    @{ Path = 'package/Editor/Extensions/';              MinCount = 5; Desc = 'Extension host' },
    @{ Path = 'package/Editor/Extensions/OptionalComponentDefaultsBootstrap.cs.meta'; MinCount = 1; Desc = 'Bootstrap meta (v1.4.6 regression guard)' },
    @{ Path = 'package/Editor/LLM/';                     MinCount = 3; Desc = 'LLM clients' },
    @{ Path = 'package/Editor/Session/';                 MinCount = 3; Desc = 'Session subsystem' },
    @{ Path = 'package/Editor/Tools/IAgentTool.cs';      MinCount = 1; Desc = 'Tool interface' },
    @{ Path = 'package/Editor/Tools/Infrastructure/';    MinCount = 3; Desc = 'Tool infrastructure' },
    @{ Path = 'package/Editor/Tools/Native/';            MinCount = 20; Desc = 'Native tools (Unity API)' },
    @{ Path = 'package/Editor/Tools/Cloud/';             MinCount = 2; Desc = 'Cloud tools' },
    @{ Path = 'package/Editor/Tools/FileSystem/';        MinCount = 1; Desc = 'FileSystem tools' },
    @{ Path = 'package/Editor/Tools/Safety/';            MinCount = 3; Desc = 'Tool risk / policy layer' },
    @{ Path = 'package/Editor/UI/ChatWindow';            MinCount = 3; Desc = 'ChatWindow partials' },
    @{ Path = 'package/Editor/UI/Components/';           MinCount = 3; Desc = 'UI components' },
    @{ Path = 'package/Editor/VCS/';                     MinCount = 5; Desc = 'VCS optional component' },
    @{ Path = 'package/Editor/Indexing/';                MinCount = 5; Desc = 'Indexing optional component' },
    @{ Path = 'package/Editor/Workspace/';               MinCount = 5; Desc = 'Workspace infrastructure' },
    @{ Path = 'package/Editor/Utils/';                   MinCount = 3; Desc = 'Utilities' },
    @{ Path = 'package/package.json';                    MinCount = 1; Desc = 'Package manifest' },
    @{ Path = 'package/README.md';                       MinCount = 1; Desc = 'README' },
    @{ Path = 'package/CHANGELOG.md';                    MinCount = 1; Desc = 'CHANGELOG' },
    @{ Path = 'package/LICENSE.md';                      MinCount = 1; Desc = 'LICENSE' }
)

# Paths that MUST NOT appear in the tarball (development-only, would leak).
$forbiddenPaths = @(
    @{ Path = 'package/tools/';        Desc = 'Repo tooling scripts (should never ship)' },
    @{ Path = 'package/plans/';        Desc = 'Design docs (dev-only)' },
    @{ Path = 'package/AGENTS.md';     Desc = 'LLM dev rules (dev-only)' },
    @{ Path = 'package/.agents/';      Desc = 'AI tooling config (dev-only)' },
    @{ Path = 'package/.roo/';         Desc = 'AI tooling config (dev-only)' },
    @{ Path = 'package/_archive/';     Desc = 'Legacy archive (dev-only)' },
    @{ Path = 'package/PROJECT-ANALYSIS.md'; Desc = 'Dev-only analysis doc' }
)

# List tarball contents ONCE — repeated 'tar -tzf' invocations are expensive
# on Windows because tar shells out to bsdtar every time.
$entries = & tar -tzf $Tarball 2>$null
if ($LASTEXITCODE -ne 0 -or -not $entries) {
    Write-Error "Failed to read tarball: $Tarball"
    exit 2
}

$missing = @()
$leaked = @()

foreach ($req in $requiredPaths) {
    $pattern = [regex]::Escape($req.Path)
    $matched = $entries | Where-Object { $_ -match $pattern }
    $count = ($matched | Measure-Object).Count
    if ($count -lt $req.MinCount) {
        $missing += [PSCustomObject]@{
            Path     = $req.Path
            Expected = ">= $($req.MinCount)"
            Actual   = $count
            Desc     = $req.Desc
        }
    }
}

foreach ($fb in $forbiddenPaths) {
    $pattern = [regex]::Escape($fb.Path)
    $matched = $entries | Where-Object { $_ -match $pattern }
    $count = ($matched | Measure-Object).Count
    if ($count -gt 0) {
        $leaked += [PSCustomObject]@{
            Path  = $fb.Path
            Count = $count
            Desc  = $fb.Desc
        }
    }
}

if ($missing.Count -eq 0 -and $leaked.Count -eq 0) {
    if (-not $Quiet) {
        $total = ($entries | Measure-Object).Count
        Write-Host "[verify-tarball] OK — '$Tarball' passes all structural checks ($total entries total)" -ForegroundColor Green
        Write-Host "  $($requiredPaths.Count) required paths present, $($forbiddenPaths.Count) forbidden paths absent" -ForegroundColor Green
    }
    exit 0
}

Write-Host ""
Write-Host "[verify-tarball] FAIL — '$Tarball' has structural issues" -ForegroundColor Red
Write-Host ""

if ($missing.Count -gt 0) {
    Write-Host "  MISSING required paths ($($missing.Count)):" -ForegroundColor Yellow
    foreach ($m in $missing) {
        Write-Host "    - $($m.Path)" -ForegroundColor Red
        Write-Host "        Expected: $($m.Expected), Actual: $($m.Actual)  [$($m.Desc)]" -ForegroundColor DarkYellow
    }
    Write-Host ""
    Write-Host "  Likely cause: '.npmignore' pattern accidentally excludes these paths." -ForegroundColor Cyan
    Write-Host "  Common trap: 'foo/' (no leading '/') matches 'foo/' at ANY depth in the tree." -ForegroundColor Cyan
    Write-Host "  Fix: use '/foo/' to anchor to repo root only." -ForegroundColor Cyan
    Write-Host ""
}

if ($leaked.Count -gt 0) {
    Write-Host "  LEAKED forbidden paths ($($leaked.Count)):" -ForegroundColor Yellow
    foreach ($l in $leaked) {
        Write-Host "    - $($l.Path)  ($($l.Count) entries)" -ForegroundColor Red
        Write-Host "        [$($l.Desc)]" -ForegroundColor DarkYellow
    }
    Write-Host ""
    Write-Host "  Fix: add these paths to '.npmignore' with proper anchoring." -ForegroundColor Cyan
    Write-Host ""
}

exit 1
