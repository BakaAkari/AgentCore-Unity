# ============================================
# RagMem - LLM Model Discovery & Selection
# ============================================
# Queries LiteLLM /v1/models API and lets user pick a model.
#
# Mode 1 (EnvFile): Read URL/Key from .env, interactive select, update .env
#   powershell -File select-llm-model.ps1 -EnvFile path\.env
#
# Mode 2 (Standalone): Direct URL+Key, output model list for AI Agent
#   powershell -File select-llm-model.ps1 -BaseUrl "http://..." -ApiKey "sk-..."
#   powershell -File select-llm-model.ps1 -BaseUrl "http://..." -ApiKey "sk-..." -ListOnly
#
# -ListOnly: Output one model name per line (machine-readable), no interactive prompt.
#            Exit code 0 = success (models found), 1 = API error or no models.
#
# Exit codes: 0 = success or skipped, 1 = error (only in -ListOnly mode)
# ============================================

param(
    [string]$EnvFile,
    [string]$BaseUrl,
    [string]$ApiKey,
    [string]$CurrentModel,
    [switch]$ListOnly
)

# ---- Resolve parameters from EnvFile or direct args ----

if ($EnvFile) {
    # Mode 1: Read from .env file
    if (-not (Test-Path $EnvFile)) {
        Write-Host "  [SKIP] .env file not found: $EnvFile"
        exit 0
    }

    $envContent = Get-Content $EnvFile -Raw

    if ($envContent -match '(?m)^LITELLM_BASE_URL=(.+)$') {
        $BaseUrl = $Matches[1].Trim()
    } else {
        Write-Host "  [SKIP] LITELLM_BASE_URL not found in .env"
        exit 0
    }

    if ($envContent -match '(?m)^LITELLM_API_KEY=(.+)$') {
        $ApiKey = $Matches[1].Trim()
    } else {
        $ApiKey = "sk-placeholder"
    }

    if (-not $CurrentModel) {
        if ($envContent -match '(?m)^LLM_MODEL=(.+)$') {
            $CurrentModel = $Matches[1].Trim()
        } else {
            $CurrentModel = "gpt-4o-mini"
        }
    }
} elseif ($BaseUrl) {
    # Mode 2: Standalone with direct parameters
    if (-not $ApiKey) { $ApiKey = "sk-placeholder" }
    if (-not $CurrentModel) { $CurrentModel = "" }
} else {
    Write-Host "  [ERROR] Must provide either -EnvFile or -BaseUrl parameter."
    Write-Host "  Usage:"
    Write-Host "    -EnvFile path\.env                    (read from .env, interactive)"
    Write-Host "    -BaseUrl http://... [-ApiKey sk-...]   (standalone)"
    Write-Host "    -BaseUrl http://... -ListOnly          (machine-readable output)"
    exit 1
}

# ---- Query /v1/models API ----

$url = "$BaseUrl/v1/models"

if (-not $ListOnly) {
    Write-Host "  Querying $url ..."
}

try {
    $headers = @{ "Authorization" = "Bearer $ApiKey" }
    $response = Invoke-RestMethod -Uri $url -Headers $headers -TimeoutSec 10 -ErrorAction Stop
    $models = @($response.data | Where-Object { $_.id } | ForEach-Object { $_.id } | Sort-Object)

    if ($models.Count -eq 0) {
        if ($ListOnly) {
            exit 1
        }
        Write-Host "  [SKIP] No models returned from API."
        exit 0
    }

    # ---- ListOnly mode: output model names and exit ----
    if ($ListOnly) {
        foreach ($m in $models) {
            Write-Output $m
        }
        exit 0
    }

    # ---- Interactive mode: display list and let user pick ----
    Write-Host ""
    Write-Host "  Available LLM models ($($models.Count) found):"
    Write-Host "  ----------------------------------------"
    for ($i = 0; $i -lt $models.Count; $i++) {
        $marker = if ($CurrentModel -and $models[$i] -eq $CurrentModel) { " <-- current" } else { "" }
        Write-Host ("  {0,3}. {1}{2}" -f ($i+1), $models[$i], $marker)
    }
    Write-Host "  ----------------------------------------"
    if ($CurrentModel) {
        Write-Host "  Current: $CurrentModel"
    }
    Write-Host ""
    Write-Host "  Enter model number to select, or press Enter to keep current:"
    $userInput = Read-Host "  Selection"

    if ($userInput -and $userInput -match '^\d+$') {
        $idx = [int]$userInput - 1
        if ($idx -ge 0 -and $idx -lt $models.Count) {
            $newModel = $models[$idx]
            Write-Host "  Selected: $newModel"

            # Update .env file if in EnvFile mode
            if ($EnvFile -and $envContent) {
                $newContent = $envContent -replace '(?m)^LLM_MODEL=.+$', "LLM_MODEL=$newModel"
                Set-Content -Path $EnvFile -Value $newContent -NoNewline
                Write-Host "  [OK] .env updated with LLM_MODEL=$newModel"
            }
        } else {
            Write-Host "  Invalid selection. Keeping current model: $CurrentModel"
        }
    } else {
        Write-Host "  Keeping current model: $CurrentModel"
    }
} catch {
    if ($ListOnly) {
        exit 1
    }
    Write-Host "  [SKIP] Cannot reach LiteLLM API at $url"
    Write-Host "         Error: $($_.Exception.Message)"
    if ($EnvFile) {
        Write-Host "         LLM_MODEL will use the value from .env as-is."
    }
    exit 0
}
