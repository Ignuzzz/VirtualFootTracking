param(
    [string]$RepoName = "VirtualFootTracking",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Tag = "v0.2.0-engine",
    [string]$Title = "Virtual Foot Tracking v0.2.0 — Foot Tracking Engine + Calibration + VR UI (Quest 3S)"
)

$ErrorActionPreference = "Stop"

$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ProjectDir "..")).Path
$ApkPath = Join-Path $ProjectDir "Builds\VirtualFootTracking_v0.2_Engine.apk"

if (-not (Test-Path $ApkPath)) {
    Write-Error "APK not found at $ApkPath. Run .\Tools\build_apk.ps1 first."
    exit 1
}

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) {
    $fallbackGh = Join-Path $env:LOCALAPPDATA "GitHubCLI\bin\gh.exe"
    if (Test-Path $fallbackGh) { $gh = $fallbackGh }
}
if (-not $gh -or -not (Test-Path $gh)) {
    Write-Error "GitHub CLI (gh) not found. Reinstall or add %LOCALAPPDATA%\GitHubCLI\bin to PATH."
    exit 1
}

Write-Host "Checking GitHub authentication..."
$prevPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $gh auth status 2>&1 | Out-Null
$authExit = $LASTEXITCODE
$ErrorActionPreference = $prevPref

if ($authExit -ne 0) {
    Write-Host ""
    Write-Host "You are not signed in to GitHub CLI yet." -ForegroundColor Yellow
    Write-Host "Run this command first and complete login in your browser:" -ForegroundColor Cyan
    Write-Host "  `"$gh`" auth login --web -h github.com -p https"
    Write-Host "Then re-run:" -ForegroundColor Cyan
    Write-Host "  `"$PSCommandPath`""
    exit 1
}

$ErrorActionPreference = "Continue"
$existingRemote = git -C $RepoRoot remote get-url origin 2>$null
$hasOrigin = ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingRemote))
$ErrorActionPreference = "Stop"

if (-not $hasOrigin) {
    Write-Host "Creating GitHub repository '$RepoName' ($Visibility) and pushing branch main..."
    & $gh repo create $RepoName "--$Visibility" --source="$RepoRoot" --remote=origin --push
} else {
    Write-Host "Pushing branch main to existing remote: $existingRemote"
    git -C $RepoRoot push -u origin main
}

$ErrorActionPreference = "Continue"
& $gh release view $Tag --repo ( & $gh repo view --json nameWithOwner -q .nameWithOwner ) 2>$null | Out-Null
$releaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = "Stop"

$notes = @"
Meta Quest 3S Phase 2 — Proprietary Foot Tracking Engine (`v0.2.0`):
- **Foot Tracking Engine Próprio (`ProprietaryFootTrackingSolver` + `AnalyticLegIkSolver`):**
  - Cinemática inversa analítica 2-bone (`Quadril -> Joelho -> Tornozelo`) com limites anatômicos (`2°–148°`) e prevenção de hiperextensão/inversão do joelho em agachamentos, sentar e chutes.
  - Detecção de contato com o piso MRUK (`Stance` vs `Swing`), **Foot Locking** elástico anti-deslizamento e bloqueio de penetração no chão.
  - Fallback biomecânico (`EngineBiomechanicalFallback`) quando dados inferiores do SDK estiverem ausentes.
- **Filtro de Kalman Adaptativo (`AdaptiveKalmanMotionPredictor`):**
  - Baixo jitter quando apoiado, alta responsividade em chutes/movimentos rápidos de combate e predição amortecida (até `0,35s`) em perda temporária (`EngineMotionPredicted`).
- **Calibração Guiada & Manual (`GuidedCalibrationProvider`):**
  - Captura automática de postura em pé (`2,5s`) + ajustes manuais de altura, coxa, canela, quadril, distância dos pés, largura da base, yaw inicial e offsets individuais.
- **Interface VR Interativa (5 painéis) + Laser Pointer nos controles Touch Plus:**
  - Menu Principal, Calibração, Live Tracking (com alternância A/B ao vivo entre Motor Próprio e SDK Bruto), Configurações/Offsets e Diagnóstico Completo.
"@

if ($releaseExists) {
    Write-Host "Release '$Tag' already exists; uploading/replacing APK asset..."
    & $gh release upload $Tag $ApkPath --clobber
} else {
    Write-Host "Creating GitHub Release '$Tag' and uploading APK..."
    & $gh release create $Tag $ApkPath --title $Title --notes $notes
}

$nwo = (& $gh repo view --json nameWithOwner -q .nameWithOwner).Trim()
$apkFileName = Split-Path $ApkPath -Leaf
$downloadUrl = "https://github.com/$nwo/releases/download/$Tag/$apkFileName"
$releasePage = "https://github.com/$nwo/releases/tag/$Tag"

Write-Host ""
Write-Host "SUCCESS! Release published:" -ForegroundColor Green
Write-Host "  Release Page: $releasePage"
Write-Host "  Direct APK Download Link: $downloadUrl"
