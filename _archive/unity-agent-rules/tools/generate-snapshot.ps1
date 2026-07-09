# generate-snapshot.ps1 - Unity Project Architecture Snapshot Generator
# Generates project-overview.md + architecture-snapshot.md for AI assistants.
#
# Usage:
#   .\tools\generate-snapshot.ps1                                    # Auto-detect project path
#   .\tools\generate-snapshot.ps1 -ProjectPath "D:\My Unity Project" # Explicit path
#   .\tools\generate-snapshot.ps1 -IncludePackageScripts             # Also scan Packages/
#   .\tools\generate-snapshot.ps1 -ExcludeDirs @("Assets\Vendor")    # Extra exclude dirs
#
# Compatibility:
#   - Any Unity project (2018.4+) with standard Assets/ + ProjectSettings/ + Packages/ structure
#   - PowerShell 5.1 (Windows) or PowerShell 7+ (cross-platform)
#   - No hardcoded assumptions about Assets/ subdirectory layout

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectPath,
    [int]$MaxDepth = 3,
    [int]$TopScripts = 30,
    [string]$OutputDir = ".agents\context",
    [switch]$IncludePackageScripts,
    [string[]]$ExcludeDirs = @()
)

$startTime = Get-Date

# Force UTF-8 output
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'

# ============================================================
# Auto-detect Project Path
# ============================================================

if (-not $ProjectPath) {
    # Strategy 1: Script is inside a Unity project (e.g., <project>/tools/generate-snapshot.ps1)
    $scriptDir = $PSScriptRoot
    if ($scriptDir) {
        $candidate = Split-Path $scriptDir -Parent
        if (Test-Path (Join-Path $candidate "Assets")) {
            $ProjectPath = $candidate
            Write-Host "  [Auto] Detected Unity project from script location: $ProjectPath" -ForegroundColor Cyan
        }
    }

    # Strategy 2: Current working directory or its ancestors contain Assets/
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
        Write-Host "  Usage: .\tools\generate-snapshot.ps1 -ProjectPath ""D:\Your Unity Project""" -ForegroundColor Yellow
        exit 1
    }
}

# Normalize path
$ProjectPath = (Resolve-Path $ProjectPath -ErrorAction SilentlyContinue).Path
if (-not $ProjectPath) {
    Write-Error "Project path does not exist."
    exit 1
}

# ============================================================
# Validation
# ============================================================

$assetsPath = Join-Path $ProjectPath "Assets"
if (-not (Test-Path $assetsPath)) {
    Write-Error "Cannot find Assets/ directory: $assetsPath"
    exit 1
}

# Determine output directory - prefer project root's .agents/context/
$agentsContextInProject = Join-Path $ProjectPath ".agents\context"
if (Test-Path $agentsContextInProject) {
    $OutputDir = $agentsContextInProject
} elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path (Get-Location) $OutputDir
}

$snapshotPath = Join-Path $OutputDir "architecture-snapshot.md"
$overviewPath = Join-Path $OutputDir "project-overview.md"

# Build exclude pattern from defaults + user-specified dirs
# Default excludes: common third-party / generated / cache directories
$defaultExcludes = @(
    'Plugins', 'ThirdParty', 'Third Party', 'PackageCache',
    'Vendor', 'External', 'Addons', 'TextMesh Pro',
    'DOTween', 'Demigiant'
)
$allExcludes = $defaultExcludes + $ExcludeDirs
# Build regex: match any of these as directory names in the path
$excludePattern = ($allExcludes | ForEach-Object { [regex]::Escape($_) }) -join '|'
$excludeRegex = "\\($excludePattern)\\"

# ============================================================
# Helper Functions
# ============================================================

function Get-DirectoryTree {
    param(
        [string]$Path,
        [int]$CurrentDepth = 0,
        [int]$MaxDepth = 3,
        [string]$Prefix = ""
    )
    if ($CurrentDepth -ge $MaxDepth) { return @() }

    $dirs = Get-ChildItem -Path $Path -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^\.' } |
        Sort-Object Name

    $lines = @()
    for ($i = 0; $i -lt $dirs.Count; $i++) {
        $dir = $dirs[$i]
        $isLast = ($i -eq $dirs.Count - 1)
        $connector = if ($isLast) { "+-- " } else { "|-- " }
        $childPrefix = if ($isLast) { "    " } else { "|   " }

        $childDirs = (Get-ChildItem -Path $dir.FullName -Directory -ErrorAction SilentlyContinue).Count
        $childFiles = (Get-ChildItem -Path $dir.FullName -File -ErrorAction SilentlyContinue).Count
        $csFiles = (Get-ChildItem -Path $dir.FullName -Filter "*.cs" -ErrorAction SilentlyContinue).Count

        $comment = ""
        if ($CurrentDepth -lt $MaxDepth - 1 -and $childDirs -eq 0) {
            $parts = @()
            if ($childFiles -gt 0) { $parts += "$childFiles files" }
            if ($csFiles -gt 0) { $parts += "$csFiles scripts" }
            if ($parts.Count -gt 0) { $comment = " # $($parts -join ', ')" }
        }

        $lines += "${Prefix}${connector}$($dir.Name)/${comment}"
        $childLines = Get-DirectoryTree -Path $dir.FullName -CurrentDepth ($CurrentDepth + 1) -MaxDepth $MaxDepth -Prefix "${Prefix}${childPrefix}"
        $lines += $childLines
    }
    return $lines
}

function Get-UnityVersion {
    param([string]$ProjectPath)
    $versionFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectVersion.txt"
    if (Test-Path $versionFile) {
        $content = Get-Content $versionFile -Raw
        if ($content -match 'm_EditorVersion:\s*(.+)') {
            return $Matches[1].Trim()
        }
    }
    return "Unknown"
}

function Get-RenderPipeline {
    param([string]$ProjectPath)
    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    if (Test-Path $manifestFile) {
        $content = Get-Content $manifestFile -Raw
        $hasURP = $content -match 'com\.unity\.render-pipelines\.universal'
        $hasHDRP = $content -match 'com\.unity\.render-pipelines\.high-definition'
        if ($hasURP -and $hasHDRP) { return "URP + HDRP (unusual)" }
        if ($hasURP) { return "URP" }
        if ($hasHDRP) { return "HDRP" }
    }
    return "Built-in"
}

function Get-InputSystem {
    param([string]$ProjectPath)
    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    $hasNewInput = $false
    $hasLegacy = $true  # Legacy is always available unless explicitly disabled

    if (Test-Path $manifestFile) {
        $content = Get-Content $manifestFile -Raw
        if ($content -match 'com\.unity\.inputsystem') { $hasNewInput = $true }
    }

    # Check ProjectSettings for activeInputHandler
    # 0 = Legacy, 1 = New, 2 = Both
    $settingsFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectSettings.asset"
    if (Test-Path $settingsFile) {
        $content = Get-Content $settingsFile -Raw -ErrorAction SilentlyContinue
        if ($content -match 'activeInputHandler:\s*(\d)') {
            switch ($Matches[1]) {
                "0" { return "Legacy Input Manager" }
                "1" { return "New Input System" }
                "2" { return "Both (Legacy + New Input System)" }
            }
        }
    }

    if ($hasNewInput) { return "New Input System" }
    return "Legacy Input Manager"
}

function Get-UIFramework {
    param([string]$ProjectPath, [array]$ScriptContents)
    $hasUIToolkit = $false
    $hasUGUI = $false

    # Check packages
    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    if (Test-Path $manifestFile) {
        $content = Get-Content $manifestFile -Raw
        if ($content -match 'com\.unity\.ugui') { $hasUGUI = $true }
        # com.unity.ui.builder or com.unity.ui are UI Toolkit indicators
        if ($content -match '"com\.unity\.ui"' -or $content -match 'com\.unity\.ui\.builder') { $hasUIToolkit = $true }
    }

    # Check for UXML/USS files (UI Toolkit usage)
    $assetsPath = Join-Path $ProjectPath "Assets"
    $uxmlFiles = Get-ChildItem -Path $assetsPath -Filter "*.uxml" -Recurse -ErrorAction SilentlyContinue
    if ($uxmlFiles) { $hasUIToolkit = $true }

    # Check script contents (reuse already-loaded content)
    foreach ($sc in $ScriptContents) {
        if ($sc.Content -match 'using\s+UnityEngine\.UI\b') { $hasUGUI = $true }
        if ($sc.Content -match 'using\s+UnityEngine\.UIElements\b') { $hasUIToolkit = $true }
        if ($hasUGUI -and $hasUIToolkit) { break }
    }

    if ($hasUIToolkit -and $hasUGUI) { return "uGUI + UI Toolkit" }
    if ($hasUIToolkit) { return "UI Toolkit" }
    if ($hasUGUI) { return "uGUI" }
    return "uGUI"  # Default assumption for most Unity projects
}

function Get-ScriptingBackend {
    param([string]$ProjectPath)
    $settingsFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectSettings.asset"
    if (Test-Path $settingsFile) {
        $content = Get-Content $settingsFile -Raw -ErrorAction SilentlyContinue
        # Unity serializes scriptingBackend as a map: { platform_id: backend_value }
        # backend_value: 0 = Mono, 1 = IL2CPP
        # Check for any platform set to IL2CPP
        if ($content -match 'scriptingBackend:') {
            # Look for ": 1" entries within the scriptingBackend block
            $blockMatch = [regex]::Match($content, 'scriptingBackend:\s*\{([^}]*)\}')
            if ($blockMatch.Success) {
                $block = $blockMatch.Groups[1].Value
                if ($block -match ':\s*1\b') { return "IL2CPP" }
                return "Mono"
            }
            # Single-line format (older Unity)
            if ($content -match 'scriptingBackend:\s*1\b') { return "IL2CPP" }
        }
    }
    return "Mono"
}

function Get-ApiCompatibilityLevel {
    param([string]$ProjectPath)
    $settingsFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectSettings.asset"
    if (Test-Path $settingsFile) {
        $content = Get-Content $settingsFile -Raw -ErrorAction SilentlyContinue
        # apiCompatibilityLevelPerPlatform or apiCompatibilityLevel
        # Values: 2 = .NET Standard 2.0, 3 = .NET Standard 2.1, 6 = .NET Framework, 1 = .NET 2.0 (legacy)
        $blockMatch = [regex]::Match($content, 'apiCompatibilityLevelPerPlatform:\s*\{([^}]*)\}')
        if ($blockMatch.Success) {
            $block = $blockMatch.Groups[1].Value
            if ($block -match ':\s*6\b') { return ".NET Framework" }
            if ($block -match ':\s*3\b') { return ".NET Standard 2.1" }
            if ($block -match ':\s*2\b') { return ".NET Standard 2.0" }
        }
        # Fallback: single value
        if ($content -match 'apiCompatibilityLevel:\s*(\d)') {
            switch ($Matches[1]) {
                "6" { return ".NET Framework" }
                "3" { return ".NET Standard 2.1" }
                "2" { return ".NET Standard 2.0" }
            }
        }
    }
    return ".NET Standard 2.1"  # Default for modern Unity
}

function Get-TargetPlatforms {
    param([string]$ProjectPath)
    $settingsFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectSettings.asset"
    $platforms = @()
    if (Test-Path $settingsFile) {
        $content = Get-Content $settingsFile -Raw -ErrorAction SilentlyContinue
        if ($content -match 'activeBuildTarget:\s*(\w+)') {
            $target = $Matches[1]
            $mapped = switch ($target) {
                "StandaloneWindows64" { "Windows" }
                "StandaloneWindows"   { "Windows" }
                "StandaloneOSX"       { "macOS" }
                "StandaloneLinux64"   { "Linux" }
                "Android"             { "Android" }
                "iPhone"              { "iOS" }  # Unity internally uses "iPhone"
                "iOS"                 { "iOS" }
                "WebGL"               { "WebGL" }
                "Switch"              { "Nintendo Switch" }
                "PS4"                 { "PlayStation 4" }
                "PS5"                 { "PlayStation 5" }
                "XboxOne"             { "Xbox One" }
                "GameCoreScarlett"    { "Xbox Series X|S" }
                "tvOS"                { "tvOS" }
                "VisionOS"            { "visionOS" }
                "Lumin"               { "Magic Leap" }
                "Stadia"              { "Stadia" }
                "LinuxHeadlessSimulation" { "Linux Server" }
                "WindowsServer"       { "Windows Server" }
                default               { $target }
            }
            $platforms += $mapped
        }
    }
    if ($platforms.Count -eq 0) { $platforms += "Unknown" }
    return ($platforms | Select-Object -Unique) -join ", "
}

function Get-ProjectName {
    param([string]$ProjectPath)
    $settingsFile = Join-Path (Join-Path $ProjectPath "ProjectSettings") "ProjectSettings.asset"
    if (Test-Path $settingsFile) {
        $content = Get-Content $settingsFile -Raw -ErrorAction SilentlyContinue
        if ($content -match 'productName:\s*(.+)') {
            $name = $Matches[1].Trim()
            if ($name -and $name -ne "" -and $name -ne "Product Name") { return $name }
        }
    }
    return (Split-Path $ProjectPath -Leaf)
}

function Get-InstalledPackages {
    param([string]$ProjectPath)
    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    $packages = @()
    if (Test-Path $manifestFile) {
        try {
            $manifest = Get-Content $manifestFile -Raw | ConvertFrom-Json
            $deps = $manifest.dependencies
            if ($deps) {
                $deps.PSObject.Properties | ForEach-Object {
                    $n = $_.Name
                    $v = $_.Value
                    # Exclude Unity built-in modules and local file references
                    if ($n -notmatch '^com\.unity\.modules\.' -and $v -notmatch '^file:') {
                        $packages += [PSCustomObject]@{ Name = $n; Version = $v }
                    }
                }
            }
        } catch { }
    }
    return $packages
}

function Analyze-CSharpFile {
    param([string]$Content, [string]$FilePath)

    $result = @{
        ClassName          = ""
        BaseClass          = ""
        IsSingleton        = $false
        IsEditor           = $false
        IsAbstract         = $false
        IsStatic           = $false
        IsScriptableObject = $false
        HasUpdate          = $false
        MethodCount        = 0
        Role               = ""
        Namespace          = ""
    }

    try {
        # Namespace
        if ($Content -match 'namespace\s+([\w.]+)') {
            $result.Namespace = $Matches[1]
        }

        # Class definition - handles various modifiers and formatting
        if ($Content -match '(?:public|internal)\s+(abstract\s+|static\s+|sealed\s+|partial\s+)*class\s+(\w+)(?:<[^>]+>)?\s*(?::\s*([^\{\r\n]+))?') {
            $modifiers = if ($Matches[1]) { $Matches[1] } else { "" }
            $result.ClassName = $Matches[2]
            $inheritance = if ($Matches[3]) { $Matches[3].Trim() } else { "" }

            if ($modifiers -match 'abstract') { $result.IsAbstract = $true }
            if ($modifiers -match 'static') { $result.IsStatic = $true }

            if ($inheritance) {
                # Split by comma, handle generic types (e.g., MonoBehaviour, ISerializationCallbackReceiver)
                $parts = @()
                $depth = 0
                $current = ""
                foreach ($char in $inheritance.ToCharArray()) {
                    if ($char -eq '<') { $depth++ }
                    elseif ($char -eq '>') { $depth-- }
                    elseif ($char -eq ',' -and $depth -eq 0) {
                        $parts += $current.Trim()
                        $current = ""
                        continue
                    }
                    $current += $char
                }
                if ($current.Trim()) { $parts += $current.Trim() }

                if ($parts.Count -gt 0) {
                    # Strip generic parameters from base class for display
                    $base = $parts[0]
                    if ($base -match '^(\w+)<') { $base = $Matches[1] }
                    $result.BaseClass = $base
                }
            }
        }

        # ScriptableObject detection
        if ($result.BaseClass -eq 'ScriptableObject' -or
            $Content -match ':\s*ScriptableObject\b' -or
            $Content -match 'CreateAssetMenu') {
            $result.IsScriptableObject = $true
        }

        # Singleton detection
        if ($Content -match 'static\s+\w+\s+(Instance|instance|_instance)\s*[{;=]' -or
            $Content -match 'Singleton' -or
            $Content -match 'DontDestroyOnLoad') {
            $result.IsSingleton = $true
        }

        # Editor script detection
        if ($Content -match 'using\s+UnityEditor' -or
            $Content -match ':\s*Editor\b' -or
            $Content -match ':\s*EditorWindow\b' -or
            $Content -match '\[CustomEditor' -or
            $Content -match '\[CustomPropertyDrawer' -or
            $Content -match '\[MenuItem\(') {
            $result.IsEditor = $true
        }

        # Update detection (Update, LateUpdate, FixedUpdate)
        if ($Content -match '\bvoid\s+(Update|LateUpdate|FixedUpdate)\s*\(') {
            $result.HasUpdate = $true
        }

        # Method count
        $result.MethodCount = ([regex]::Matches($Content, '(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|abstract\s+|async\s+|new\s+)*(?:void|bool|int|float|string|double|long|IEnumerator|IEnumerable|Task|UniTask|Coroutine|\w+(?:<[^>]+>)?)\s+\w+\s*[\(<]')).Count

        # Infer role
        $result.Role = Infer-ScriptRole -ClassName $result.ClassName -BaseClass $result.BaseClass `
            -IsSingleton $result.IsSingleton -IsAbstract $result.IsAbstract `
            -IsStatic $result.IsStatic -IsEditor $result.IsEditor -HasUpdate $result.HasUpdate

    } catch { }

    return $result
}

function Infer-ScriptRole {
    param(
        [string]$ClassName,
        [string]$BaseClass,
        [bool]$IsSingleton,
        [bool]$IsAbstract,
        [bool]$IsStatic,
        [bool]$IsEditor,
        [bool]$HasUpdate
    )

    # By base class
    switch ($BaseClass) {
        'Editor'                 { return "Custom Inspector" }
        'EditorWindow'           { return "Editor Window" }
        'PropertyDrawer'         { return "Property Drawer" }
        'DecoratorDrawer'        { return "Decorator Drawer" }
        'StateMachineBehaviour'  { return "State Machine Behaviour" }
        'NetworkBehaviour'       { return "Network Behaviour" }
        'NetworkManager'         { return "Network Manager" }
    }

    # By class name pattern
    if ($ClassName -match 'Manager$|Controller$|System$|Service$') {
        if ($IsSingleton) { return "Manager (Singleton)" }
        return "Manager / Controller"
    }
    if ($ClassName -match 'UI|Panel|Screen|Dialog|Popup|View$|HUD|Widget|Menu$') { return "UI Component" }
    if ($ClassName -match 'Data$|Config$|Settings$|SO$|Definition$') { return "Data Definition" }
    if ($ClassName -match 'Editor$|Inspector$|Window$|Drawer$|Wizard$') { return "Editor Tool" }
    if ($ClassName -match 'Test$|Tests$|Spec$') { return "Test" }
    if ($ClassName -match 'Util|Helper|Extension|Extensions$') { return "Utility" }
    if ($ClassName -match 'Event$|Channel$|Signal$|EventBus') { return "Event / Signal" }
    if ($ClassName -match 'Factory$|Builder$|Pool$|ObjectPool') { return "Creational Pattern" }
    if ($ClassName -match 'State$') { return "State Definition" }
    if ($ClassName -match 'Item$|Weapon$|Skill$|Buff$|Effect$|Ability$') { return "Game Entity" }
    if ($ClassName -match 'AI|Enemy|NPC|Bot|Agent$') { return "AI / NPC Logic" }
    if ($ClassName -match 'Player') { return "Player Logic" }
    if ($ClassName -match 'Camera') { return "Camera Control" }
    if ($ClassName -match 'Audio|Sound|Music') { return "Audio" }
    if ($ClassName -match 'Save|Load|Persist|Serializ') { return "Save System" }
    if ($ClassName -match 'Network|Net$|Server|Client|Lobby|Room') { return "Network" }
    if ($ClassName -match 'Anim') { return "Animation" }
    if ($ClassName -match 'Input') { return "Input" }
    if ($ClassName -match 'Spawn|Generator|Procedural|WorldGen') { return "Spawner / Procedural" }
    if ($ClassName -match 'Inventory|Shop|Currency|Economy') { return "Economy / Inventory" }
    if ($ClassName -match 'Quest|Mission|Objective|Task') { return "Quest System" }
    if ($ClassName -match 'Dialogue|Conversation|Chat') { return "Dialogue System" }
    if ($ClassName -match 'Level|Stage|Wave|Round') { return "Level System" }
    if ($ClassName -match 'Particle|VFX|Visual') { return "VFX" }
    if ($ClassName -match 'Shader|Material|Render') { return "Rendering" }

    # By features
    if ($IsAbstract) { return "Abstract Base" }
    if ($IsStatic) { return "Static Utility" }
    if ($IsSingleton) { return "Singleton" }
    if ($HasUpdate -and $BaseClass -eq 'MonoBehaviour') { return "Runtime Behaviour (Update)" }
    if ($BaseClass -eq 'ScriptableObject') { return "ScriptableObject" }
    if ($BaseClass -eq 'MonoBehaviour') { return "MonoBehaviour" }

    return ""
}

function Get-PackagePurpose {
    param([string]$Name)
    $map = @{
        'render-pipelines\.universal'     = "URP Rendering"
        'render-pipelines\.high-definition' = "HDRP Rendering"
        'render-pipelines\.core'          = "SRP Core"
        'inputsystem'                     = "New Input System"
        'textmeshpro'                     = "Text Rendering"
        'cinemachine'                     = "Camera System"
        'addressables'                    = "Asset Management"
        'localization'                    = "Localization"
        'probuilder'                      = "3D Modeling"
        'burst'                           = "Burst Compiler"
        'collections'                     = "Native Collections"
        'entities'                        = "ECS"
        'mathematics'                     = "Math Library"
        'netcode'                         = "Netcode"
        'shader-graph'                    = "Shader Graph"
        'visual-effect'                   = "VFX Graph"
        'test-framework'                  = "Test Framework"
        'recorder'                        = "Recorder"
        'postprocessing'                  = "Post Processing"
        'ai\.navigation'                  = "AI Navigation"
        'unity-mcp'                       = "MCP AI Bridge"
        'animation\.rigging'              = "Animation Rigging"
        'ugui'                            = "uGUI"
        'collab-proxy'                    = "Version Control"
        'timeline'                        = "Timeline"
        'visualscripting'                 = "Visual Scripting"
        'splines'                         = "Splines"
        'terrain-tools'                   = "Terrain Tools"
        'multiplayer'                     = "Multiplayer"
        'ads'                             = "Ads"
        'analytics'                       = "Analytics"
        'purchasing'                      = "In-App Purchasing"
        'services\.cloud'                 = "Cloud Services"
        'adaptiveperformance'             = "Adaptive Performance"
        'xr\.'                            = "XR / VR"
        'arfoundation'                    = "AR Foundation"
        'openxr'                          = "OpenXR"
        'polybrush'                       = "Polybrush"
        'searcher'                        = "Searcher"
        'sequences'                       = "Sequences"
        'shadergraph'                     = "Shader Graph"
    }
    foreach ($pattern in $map.Keys) {
        if ($Name -match $pattern) { return $map[$pattern] }
    }
    # Check for well-known third-party packages
    if ($Name -match 'dotween|demigiant') { return "Animation Tweening" }
    if ($Name -match 'unitask') { return "Async/Await" }
    if ($Name -match 'zenject|extenject') { return "Dependency Injection" }
    if ($Name -match 'vcontainer') { return "Dependency Injection" }
    if ($Name -match 'naughtyattributes') { return "Inspector Enhancement" }
    if ($Name -match 'odin') { return "Inspector Enhancement" }
    if ($Name -match 'photon|pun') { return "Multiplayer (Photon)" }
    if ($Name -match 'mirror') { return "Multiplayer (Mirror)" }
    if ($Name -match 'fishnet') { return "Multiplayer (FishNet)" }
    if ($Name -match 'newtonsoft|json') { return "JSON Serialization" }
    if ($Name -match 'messagepack') { return "Binary Serialization" }
    if ($Name -match 'r3|reactiveproperty') { return "Reactive Extensions" }
    if ($Name -match 'unirx') { return "Reactive Extensions" }
    return ""
}

# ============================================================
# Convention Detection Functions (reuse loaded script contents)
# ============================================================

function Detect-NamingConvention {
    param([array]$ScriptContents)
    $underscoreCount = 0
    $camelCount = 0
    $mPrefixCount = 0
    foreach ($sc in $ScriptContents) {
        if ($sc.Content -match '\bprivate\s+\w+\s+_[a-z]') { $underscoreCount++ }
        if ($sc.Content -match '\bprivate\s+\w+\s+[a-z]') { $camelCount++ }
        if ($sc.Content -match '\bprivate\s+\w+\s+m_[A-Z]') { $mPrefixCount++ }
    }
    if ($mPrefixCount -gt $underscoreCount -and $mPrefixCount -gt $camelCount) {
        return "PascalCase class / m_PascalCase private field (Unity internal style)"
    }
    if ($underscoreCount -gt $camelCount) {
        return "PascalCase class / _camelCase private field"
    }
    return "PascalCase class / camelCase private field"
}

function Detect-EventSystem {
    param([array]$ScriptContents)
    $hasUnityEvent = $false
    $hasCSharpEvent = $false
    $hasSOChannel = $false
    foreach ($sc in $ScriptContents) {
        if ($sc.Content -match 'UnityEvent\b') { $hasUnityEvent = $true }
        if ($sc.Content -match '\bevent\s+\w+') { $hasCSharpEvent = $true }
        if ($sc.Content -match 'ScriptableObject.*Event|EventChannel|GameEvent') { $hasSOChannel = $true }
        if ($hasUnityEvent -and $hasCSharpEvent -and $hasSOChannel) { break }
    }
    $parts = @()
    if ($hasCSharpEvent) { $parts += "C# event" }
    if ($hasUnityEvent) { $parts += "UnityEvent" }
    if ($hasSOChannel) { $parts += "SO Event Channel" }
    if ($parts.Count -eq 0) { return "C# event" }
    return $parts -join " + "
}

function Detect-DI {
    param([array]$ScriptContents, [string]$ProjectPath)
    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    if (Test-Path $manifestFile) {
        $content = Get-Content $manifestFile -Raw
        if ($content -match 'vcontainer') { return "VContainer" }
        if ($content -match 'zenject|extenject') { return "Zenject" }
    }
    foreach ($sc in $ScriptContents) {
        if ($sc.Content -match '\[Inject\]') { return "Zenject / VContainer" }
        if ($sc.Content -match 'ServiceLocator') { return "Service Locator" }
    }
    return "None"
}

function Detect-TestFramework {
    param([string]$AssetsPath)
    $hasTests = Get-ChildItem -Path $AssetsPath -Filter "*.asmdef" -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $c = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
            $c -match 'TestAssemblies|nunit|UnityEngine\.TestRunner'
        }
    if ($hasTests) { return "Unity Test Framework" }
    # Also check for test scripts directly
    $testScripts = Get-ChildItem -Path $AssetsPath -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 |
        Where-Object {
            $c = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
            $c -match '\[Test\]|\[UnityTest\]|\[TestFixture\]'
        }
    if ($testScripts) { return "Unity Test Framework" }
    return "None"
}

function Detect-SerializationStyle {
    param([array]$ScriptContents)
    $serializeFieldCount = 0
    $publicFieldCount = 0
    foreach ($sc in $ScriptContents) {
        if ($sc.IsEditor) { continue }  # Skip editor scripts
        $serializeFieldCount += ([regex]::Matches($sc.Content, '\[SerializeField\]')).Count
        $publicFieldCount += ([regex]::Matches($sc.Content, 'public\s+(?:int|float|string|bool|double|long|GameObject|Transform|Vector[234]|Quaternion|Color|Sprite|Texture|AudioClip|Material|Mesh|AnimationCurve|List<|Array)\s+\w+')).Count
    }
    if ($serializeFieldCount -gt $publicFieldCount) { return "[SerializeField] private preferred" }
    if ($publicFieldCount -gt 0) { return "public fields" }
    return "[SerializeField] private preferred"
}

function Detect-AsyncPattern {
    param([array]$ScriptContents, [string]$ProjectPath)
    $hasUniTask = $false
    $hasCoroutine = $false
    $hasAsyncAwait = $false

    $manifestFile = Join-Path (Join-Path $ProjectPath "Packages") "manifest.json"
    if (Test-Path $manifestFile) {
        $content = Get-Content $manifestFile -Raw
        if ($content -match 'unitask|cysharp') { $hasUniTask = $true }
    }

    foreach ($sc in $ScriptContents) {
        if ($sc.Content -match 'UniTask\b') { $hasUniTask = $true }
        if ($sc.Content -match 'IEnumerator\b.*yield\s+return|StartCoroutine') { $hasCoroutine = $true }
        if ($sc.Content -match 'async\s+Task\b|await\b') { $hasAsyncAwait = $true }
        if ($hasUniTask -and $hasCoroutine -and $hasAsyncAwait) { break }
    }

    $parts = @()
    if ($hasUniTask) { $parts += "UniTask" }
    if ($hasAsyncAwait) { $parts += "async/await" }
    if ($hasCoroutine) { $parts += "Coroutines" }
    if ($parts.Count -eq 0) { return "Coroutines" }
    return $parts -join " + "
}

# ============================================================
# Main
# ============================================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Unity Architecture Snapshot Generator" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Project: $ProjectPath"
Write-Host ""

# --- Gather data ---

$unityVersion = Get-UnityVersion -ProjectPath $ProjectPath
Write-Host "  [1/9] Unity Version: $unityVersion" -ForegroundColor Green

$renderPipeline = Get-RenderPipeline -ProjectPath $ProjectPath
Write-Host "  [2/9] Render Pipeline: $renderPipeline" -ForegroundColor Green

$inputSystem = Get-InputSystem -ProjectPath $ProjectPath
Write-Host "  [3/9] Input System: $inputSystem" -ForegroundColor Green

$packages = Get-InstalledPackages -ProjectPath $ProjectPath
Write-Host "  [4/9] Packages: $($packages.Count)" -ForegroundColor Green

$projectName = Get-ProjectName -ProjectPath $ProjectPath
$scriptingBackend = Get-ScriptingBackend -ProjectPath $ProjectPath
$dotNetVersion = Get-ApiCompatibilityLevel -ProjectPath $ProjectPath
$targetPlatforms = Get-TargetPlatforms -ProjectPath $ProjectPath
Write-Host "  [5/9] Project info: $projectName | $scriptingBackend | $targetPlatforms" -ForegroundColor Green

Write-Host "  [6/9] Counting files..." -ForegroundColor Yellow
# Collect all C# scripts, excluding third-party directories
$allScripts = Get-ChildItem -Path $assetsPath -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch $excludeRegex }

# Optionally include scripts from embedded/local packages
if ($IncludePackageScripts) {
    $packagesPath = Join-Path $ProjectPath "Packages"
    if (Test-Path $packagesPath) {
        $pkgScripts = Get-ChildItem -Path $packagesPath -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\PackageCache\\' }
        if ($pkgScripts) { $allScripts = @($allScripts) + @($pkgScripts) }
    }
}

$scriptCount = ($allScripts | Measure-Object).Count
$scenes = Get-ChildItem -Path $assetsPath -Filter "*.unity" -Recurse -ErrorAction SilentlyContinue
$sceneCount = ($scenes | Measure-Object).Count
$asmdefs = Get-ChildItem -Path $assetsPath -Filter "*.asmdef" -Recurse -ErrorAction SilentlyContinue
$asmdefCount = ($asmdefs | Measure-Object).Count
$prefabs = Get-ChildItem -Path $assetsPath -Filter "*.prefab" -Recurse -ErrorAction SilentlyContinue
$prefabCount = ($prefabs | Measure-Object).Count
$totalFiles = (Get-ChildItem -Path $assetsPath -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host "        Scripts: $scriptCount | Scenes: $sceneCount | Prefabs: $prefabCount | Total: $totalFiles"

Write-Host "  [7/9] Building directory tree..." -ForegroundColor Yellow
$treeLines = Get-DirectoryTree -Path $assetsPath -MaxDepth $MaxDepth
$treeText = "Assets/`n" + ($treeLines -join "`n")

Write-Host "  [8/9] Analyzing scripts ($scriptCount)..." -ForegroundColor Yellow
# Pre-load all script contents once (avoids re-reading files in convention detection)
$scriptContents = @()
$scriptAnalyses = @()
$progress = 0
foreach ($script in $allScripts) {
    $progress++
    if ($progress % 100 -eq 0) { Write-Host "        Analyzed $progress / $scriptCount ..." -ForegroundColor DarkGray }
    $content = Get-Content $script.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    $analysis = Analyze-CSharpFile -Content $content -FilePath $script.FullName
    $scriptAnalyses += [PSCustomObject]@{ File = $script; Analysis = $analysis }
    $scriptContents += [PSCustomObject]@{
        File = $script
        Content = $content
        IsEditor = $analysis.IsEditor
    }
}

Write-Host "  [9/9] Detecting coding conventions..." -ForegroundColor Yellow
$namingConvention = Detect-NamingConvention -ScriptContents $scriptContents
$eventSystem = Detect-EventSystem -ScriptContents $scriptContents
$diFramework = Detect-DI -ScriptContents $scriptContents -ProjectPath $ProjectPath
$testFramework = Detect-TestFramework -AssetsPath $assetsPath
$serializationStyle = Detect-SerializationStyle -ScriptContents $scriptContents
$asyncPattern = Detect-AsyncPattern -ScriptContents $scriptContents -ProjectPath $ProjectPath
$uiFramework = Get-UIFramework -ProjectPath $ProjectPath -ScriptContents $scriptContents

# Sort: singletons/managers first, then by file size
$sortedScripts = $scriptAnalyses |
    Sort-Object @{Expression = {
        $a = $_.Analysis
        $score = $_.File.Length
        if ($a.IsSingleton) { $score += 100000 }
        if ($a.ClassName -match 'Manager|Controller|System|Service') { $score += 50000 }
        if ($a.IsAbstract) { $score += 30000 }
        if ($a.IsScriptableObject) { $score += 20000 }
        if ($a.IsEditor) { $score += 10000 }
        $score
    }; Descending = $true } |
    Select-Object -First $TopScripts

# Build script table
$keyScriptLines = $sortedScripts | ForEach-Object {
    $relativePath = $_.File.FullName.Replace($ProjectPath, "").TrimStart('\', '/')
    $sizeKB = [math]::Round($_.File.Length / 1024, 1)
    $a = $_.Analysis
    $baseInfo = if ($a.BaseClass) { " : $($a.BaseClass)" } else { "" }
    $singletonMark = if ($a.IsSingleton) { " [Singleton]" } else { "" }
    $role = $a.Role
    "| ``$relativePath`` | ``$($a.ClassName)${baseInfo}`` | $sizeKB KB | $role${singletonMark} |"
}

# Scene table
$sceneLines = $scenes | ForEach-Object {
    $relativePath = $_.FullName.Replace($ProjectPath, "").TrimStart('\', '/')
    $sizeKB = [math]::Round($_.Length / 1024, 1)
    "| ``$relativePath`` | $sizeKB KB | <!-- TODO --> |"
}

# Asmdef table
$asmdefLines = if ($asmdefCount -gt 0) {
    $asmdefs | ForEach-Object {
        $name = $_.BaseName
        $relativePath = $_.FullName.Replace($ProjectPath, "").TrimStart('\', '/')
        $refs = ""
        try {
            $asmdefContent = Get-Content $_.FullName -Raw | ConvertFrom-Json
            if ($asmdefContent.references) {
                $refs = ($asmdefContent.references | Select-Object -First 5) -join ", "
                if ($asmdefContent.references.Count -gt 5) { $refs += " ..." }
            }
        } catch { }
        "| ``$name`` | ``$relativePath`` | $refs |"
    }
} else {
    @("| (No asmdef) | All scripts in default assembly | - |")
}

# Package table
$packageLines = $packages | ForEach-Object {
    $purpose = Get-PackagePurpose -Name $_.Name
    "| ``$($_.Name)`` | $($_.Version) | $purpose |"
}

# Stats
$singletonCount = ($scriptAnalyses | Where-Object { $_.Analysis.IsSingleton }).Count
$editorScriptCount = ($scriptAnalyses | Where-Object { $_.Analysis.IsEditor }).Count
$soCount = ($scriptAnalyses | Where-Object { $_.Analysis.IsScriptableObject }).Count
$updateCount = ($scriptAnalyses | Where-Object { $_.Analysis.HasUpdate }).Count
$abstractCount = ($scriptAnalyses | Where-Object { $_.Analysis.IsAbstract }).Count

$elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 1)
$date = Get-Date -Format "yyyy-MM-dd"

# Detect known constraints
$knownConstraints = @()
if ($testFramework -eq "None") { $knownConstraints += "No automated tests; all verification is manual" }
if ($asmdefCount -eq 0 -and $scriptCount -gt 20) { $knownConstraints += "No assembly definitions; all scripts in default assembly (may slow compilation)" }
if ($inputSystem -eq "Legacy Input Manager") { $knownConstraints += "Using legacy Input Manager (not new Input System)" }
if ($scriptCount -eq 0) { $knownConstraints += "No C# scripts found in Assets/ (new or asset-only project)" }

# Identify notable third-party packages
$notablePackages = @()
foreach ($pkg in $packages) {
    $purpose = Get-PackagePurpose -Name $pkg.Name
    if ($purpose -and $pkg.Name -notmatch '^com\.unity\.(modules|feature)\.' -and
        $pkg.Name -notmatch 'collab-proxy|visualscripting') {
        $notablePackages += [PSCustomObject]@{ Name = $pkg.Name; Version = $pkg.Version; Purpose = $purpose }
    }
}

# ============================================================
# Generate project-overview.md
# ============================================================

$poSb = New-Object System.Text.StringBuilder

[void]$poSb.AppendLine("# Project Overview")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("> **Auto-generated** by ``generate-snapshot.ps1`` on $date.")
[void]$poSb.AppendLine("> Declares the project's tech stack, versions, platforms, and key dependencies.")
[void]$poSb.AppendLine("> AI assistants use this file as conditional context for Skill execution.")
[void]$poSb.AppendLine(">")
[void]$poSb.AppendLine("> **Maintenance**: Re-run the script or manually update when the tech stack changes.")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("---")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("## Basic Info")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("| Item | Value |")
[void]$poSb.AppendLine("|------|-------|")
[void]$poSb.AppendLine("| Project Name | $projectName |")
[void]$poSb.AppendLine("| Unity Version | $unityVersion |")
[void]$poSb.AppendLine("| Render Pipeline | $renderPipeline |")
[void]$poSb.AppendLine("| Target Platforms | $targetPlatforms |")
[void]$poSb.AppendLine("| Input System | $inputSystem |")
[void]$poSb.AppendLine("| UI Framework | $uiFramework |")
[void]$poSb.AppendLine("| Scripting Backend | $scriptingBackend |")
[void]$poSb.AppendLine("| .NET API Level | $dotNetVersion |")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("---")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("## Key Dependencies")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("> Notable packages beyond Unity built-in modules.")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("| Package | Version | Purpose |")
[void]$poSb.AppendLine("|---------|---------|---------|")
foreach ($pkg in $notablePackages) {
    [void]$poSb.AppendLine("| ``$($pkg.Name)`` | $($pkg.Version) | $($pkg.Purpose) |")
}
if ($notablePackages.Count -eq 0) {
    [void]$poSb.AppendLine("| (No notable third-party packages) | - | - |")
}
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("---")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("## Project Structure")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("``````text")
[void]$poSb.AppendLine($treeText)
[void]$poSb.AppendLine("``````")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("---")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("## Coding Conventions (Auto-detected)")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("| Convention | Detected Value |")
[void]$poSb.AppendLine("|-----------|---------------|")
[void]$poSb.AppendLine("| Naming Style | $namingConvention |")
[void]$poSb.AppendLine("| Field Serialization | $serializationStyle |")
[void]$poSb.AppendLine("| Event System | $eventSystem |")
[void]$poSb.AppendLine("| Async Pattern | $asyncPattern |")
[void]$poSb.AppendLine("| Dependency Injection | $diFramework |")
[void]$poSb.AppendLine("| Test Framework | $testFramework |")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("> Auto-detected values are best-effort. Verify and adjust if inaccurate.")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("---")
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("## Known Constraints")
[void]$poSb.AppendLine("")
if ($knownConstraints.Count -gt 0) {
    foreach ($c in $knownConstraints) {
        [void]$poSb.AppendLine("- $c")
    }
} else {
    [void]$poSb.AppendLine("- (No constraints auto-detected)")
}
[void]$poSb.AppendLine("")
[void]$poSb.AppendLine("<!-- Add project-specific constraints below: -->")
[void]$poSb.AppendLine("<!-- - e.g., UI uses legacy uGUI, no migration to UI Toolkit planned -->")
[void]$poSb.AppendLine("<!-- - e.g., Some code uses Resources.Load, migration to Addressables planned -->")

# ============================================================
# Generate architecture-snapshot.md
# ============================================================

$sb = New-Object System.Text.StringBuilder

[void]$sb.AppendLine("# Project Architecture Snapshot")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> Auto-generated by ``generate-snapshot.ps1`` on $date.")
[void]$sb.AppendLine("> This is a **pre-generated static snapshot**, NOT a dynamic scan per session.")
[void]$sb.AppendLine("> Re-run the script when project structure changes significantly.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Project Overview")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Item | Value |")
[void]$sb.AppendLine("|------|-------|")
[void]$sb.AppendLine("| Unity Version | $unityVersion |")
[void]$sb.AppendLine("| Render Pipeline | $renderPipeline |")
[void]$sb.AppendLine("| Input System | $inputSystem |")
[void]$sb.AppendLine("| Total Files (Assets/) | $totalFiles |")
[void]$sb.AppendLine("| C# Scripts | $scriptCount |")
[void]$sb.AppendLine("| Scenes | $sceneCount |")
[void]$sb.AppendLine("| Prefabs | $prefabCount |")
[void]$sb.AppendLine("| Assembly Definitions | $asmdefCount |")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("### Script Statistics")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Category | Count | Note |")
[void]$sb.AppendLine("|----------|-------|------|")
[void]$sb.AppendLine("| Singletons | $singletonCount | Instance field or DontDestroyOnLoad |")
[void]$sb.AppendLine("| Editor Scripts | $editorScriptCount | Uses UnityEditor namespace |")
[void]$sb.AppendLine("| ScriptableObjects | $soCount | Data asset definitions |")
[void]$sb.AppendLine("| Has Update() | $updateCount | Per-frame execution, perf-sensitive |")
[void]$sb.AppendLine("| Abstract Classes | $abstractCount | Framework / template pattern |")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Directory Structure")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("``````text")
[void]$sb.AppendLine($treeText)
[void]$sb.AppendLine("``````")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Key Scripts")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> Sorted by importance (singletons/managers first, then by file size). [Singleton] = singleton pattern detected.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Script Path | Class | Size | Inferred Role |")
[void]$sb.AppendLine("|-------------|-------|------|---------------|")
foreach ($line in $keyScriptLines) { [void]$sb.AppendLine($line) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> Inferred roles are auto-detected and may not be fully accurate. Please verify.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Scenes")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Scene Path | Size | Purpose |")
[void]$sb.AppendLine("|------------|------|---------|")
foreach ($line in $sceneLines) { [void]$sb.AppendLine($line) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Assembly Definitions (asmdef)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Name | Path | References |")
[void]$sb.AppendLine("|------|------|------------|")
foreach ($line in $asmdefLines) { [void]$sb.AppendLine($line) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Installed Packages")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Package | Version | Purpose |")
[void]$sb.AppendLine("|---------|---------|---------|")
foreach ($line in $packageLines) { [void]$sb.AppendLine($line) }
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Core Systems Overview")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> The following sections require **manual input** - the script cannot infer architectural intent.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("### Architecture Pattern")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("<!-- TODO: Describe your architecture pattern, e.g.:")
[void]$sb.AppendLine("- ScriptableObject Event Channels for decoupling")
[void]$sb.AppendLine("- GameManager singleton for game state")
[void]$sb.AppendLine("- MVC pattern for UI")
[void]$sb.AppendLine("-->")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("### Data Flow")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("<!-- TODO: e.g. Input -> PlayerController -> GameManager -> EventBus -> UI/Audio/VFX -->")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("### Initialization Order")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("<!-- TODO: e.g.")
[void]$sb.AppendLine("1. Bootstrap scene -> GameManager.Awake()")
[void]$sb.AppendLine("2. Initialize subsystems")
[void]$sb.AppendLine("3. Load main menu")
[void]$sb.AppendLine("-->")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Known Hotspots & Notes")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("<!-- TODO: List areas that need special attention when modifying code -->")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Snapshot Metadata")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Item | Value |")
[void]$sb.AppendLine("|------|-------|")
[void]$sb.AppendLine("| Generated | $date |")
[void]$sb.AppendLine("| Generator | ``generate-snapshot.ps1`` |")
[void]$sb.AppendLine("| Scan Depth | $MaxDepth levels |")
[void]$sb.AppendLine("| Top Scripts | $TopScripts (max) |")
[void]$sb.AppendLine("| Scripts Analyzed | $scriptCount |")
[void]$sb.AppendLine("| Excluded Dirs | $($allExcludes -join ', ') |")
[void]$sb.AppendLine("| Elapsed | $elapsed seconds |")

# ============================================================
# Write Output Files
# ============================================================

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$utf8Bom = New-Object System.Text.UTF8Encoding $true

# Write architecture-snapshot.md
try {
    $resolvedSnapshot = if (Test-Path $snapshotPath) {
        (Resolve-Path $snapshotPath).Path
    } else { $snapshotPath }
    [System.IO.File]::WriteAllText($resolvedSnapshot, $sb.ToString(), $utf8Bom)
} catch {
    $sb.ToString() | Out-File -FilePath $snapshotPath -Encoding utf8 -Force
}

# Write project-overview.md
try {
    $resolvedOverview = if (Test-Path $overviewPath) {
        (Resolve-Path $overviewPath).Path
    } else { $overviewPath }
    [System.IO.File]::WriteAllText($resolvedOverview, $poSb.ToString(), $utf8Bom)
} catch {
    $poSb.ToString() | Out-File -FilePath $overviewPath -Encoding utf8 -Force
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Done! Snapshot generated." -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Output directory: $OutputDir"
Write-Host "  Elapsed: $elapsed seconds"
Write-Host ""
Write-Host "  Generated files:" -ForegroundColor Cyan
Write-Host "    [OK] project-overview.md    (tech stack, conventions, constraints)"
Write-Host "    [OK] architecture-snapshot.md (scripts, scenes, packages, structure)"
Write-Host ""
Write-Host "  Auto-detected:" -ForegroundColor Cyan
Write-Host "    [OK] Project info ($projectName, $unityVersion, $renderPipeline)"
Write-Host "    [OK] Coding conventions (naming, serialization, events, async, DI, tests)"
Write-Host "    [OK] Script statistics ($scriptCount scripts analyzed)"
Write-Host "    [OK] Directory tree ($MaxDepth levels)"
Write-Host "    [OK] Key scripts with class names and inferred roles"
Write-Host "    [OK] Scene list ($sceneCount scenes)"
Write-Host "    [OK] Assembly definitions ($asmdefCount)"
Write-Host "    [OK] Installed packages ($($packages.Count))"
Write-Host "    [OK] Known constraints ($($knownConstraints.Count) detected)"
Write-Host ""
Write-Host "  Manual sections (marked <!-- TODO --> in architecture-snapshot.md):" -ForegroundColor Yellow
Write-Host "    [ ] Scene purposes"
Write-Host "    [ ] Architecture pattern"
Write-Host "    [ ] Data flow"
Write-Host "    [ ] Initialization order"
Write-Host "    [ ] Known hotspots"
Write-Host ""
if ($allExcludes.Count -gt 0) {
    Write-Host "  Excluded directories:" -ForegroundColor DarkGray
    Write-Host "    $($allExcludes -join ', ')" -ForegroundColor DarkGray
    Write-Host "    (Use -ExcludeDirs to add more, or -IncludePackageScripts to scan Packages/)" -ForegroundColor DarkGray
    Write-Host ""
}
