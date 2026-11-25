param(
    [string]$RepoName = "VirtualFootTracking",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Tag = "v0.3.0-depth",
    [string]$Title = "Virtual Foot Tracking v0.3.0 — Real-Time Depth API Kick & Knee Detection (Quest 3S)"
)

$ErrorActionPreference = "Stop"

$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ProjectDir "..")).Path
$ApkPath = Join-Path $ProjectDir "Builds\VirtualFootTracking_v0.3_Depth.apk"

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
Meta Quest 3S Phase 3 — Real-Time Depth API Leg & Kick Detection (`v0.3.0`):
- **Detecção Física em Tempo Real via Depth API (`EnvironmentDepthLegDetector`):**
  - Usa o `EnvironmentRaycastManager` (Meta XR MRUK Depth API) com grade frontal (`11x8` raios) para detectar fisicamente pernas erguidas, chutes frontais/diagonais e elevação de joelhos dentro do campo de visão do headset.
  - Filtragem em 5 camadas: remoção de piso MRUK, remoção de tronco superior, envelope anatômico das pernas, exclusão de braços/mãos/controles via `OVRBody` + anchors, e subtração de ambiente estático (volumes MRUK + **Captura de Fundo Depth** por voxels `0,11 m`).
- **Fusão no Motor Próprio (`ProprietaryFootTrackingSolver`):**
  - Quando a câmera de profundidade detecta chute ou elevação de joelho, destrava o pé do piso imediatamente (`IsFootLocked = false`), direciona o solver analítico IK e marca a proveniência como **`DEPTH_3D`** (**Amarelo**), diferenciando visualmente de `SDK_GEN` (Ciano), `SDK_OBS` (Verde), `ENG_IK` (Laranja) e `ENG_PRED` (Magenta).
- **Nuvem de Pontos Depth 3D + Controles no VR UI:**
  - Esferas amarelas mostram em tempo real exatamente onde o sensor Depth está enxergando suas pernas no Passthrough e no Espelho.
  - Novos botões no Live Tracking, Calibração e Configurações: `CAPTURAR FUNDO (1.5s)` (ou clique no **Analógico Esquerdo**), `DEPTH LEG: ON/OFF`, `NUVEM 3D: ON/OFF` e `LIMPAR FUNDO DEPTH`.
- **Limitações Físicas Esperadas:**
  - O Depth API cobre o cone frontal das câmeras (`~±36°` horizontal, `~-46°..+6°` vertical na nossa grade, alcance útil `0,20–2,3 m`). Chutes atrás das costas ou movimentos fora da visão da câmera retornam suavemente para `Generative Legs + IK`.
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
