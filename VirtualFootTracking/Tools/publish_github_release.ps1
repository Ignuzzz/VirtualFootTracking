param(
    [string]$RepoName = "VirtualFootTracking",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Tag = "v0.3.3-depth",
    [string]$Title = "Virtual Foot Tracking v0.3.3 — Anti-Flicker One-Euro, 2-Pass Leg Probe & Fast Kick Extension"
)

$ErrorActionPreference = "Stop"

$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ProjectDir "..")).Path
$ApkPath = Join-Path $ProjectDir "Builds\VirtualFootTracking_v0.3.3_DepthSmooth.apk"

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

$notes = @'
Meta Quest 3S Phase 3 Refinement (`v0.3.3`) — Anti-flicker, sonda 2-pass para chutes rápidos e extensão contínua:
- **Anti-Flicker / Zero Tremor (`OneEuroFilter3D` + Hold de `140 ms`):**
  - Novo `OneEuroFilter3D` com deadzone estacionária (`1,2 cm`) elimina micro-tremor parado e responde instantaneamente durante chutes rápidos (`beta = 14`).
  - Histerese de entrada (`>= 2` frames seguidos ou `>= 5` pontos) + *dropout hold* de `140 ms` com amortecimento exponencial evitam piscadas entre `DEPTH_3D` e `SDK_GEN` quando a câmera perde 1–2 frames durante chutes rápidos.
  - `AdaptiveKalmanMotionPredictor` adapta o ruído de medição `R` para amostras `DEPTH_3D` entre repouso (`0,0018`) e movimento rápido.
- **Sonda Fina 2-Pass (`EnvironmentDepthLegDetector`):**
  - Passo 1 (`9×7 = 63` raios) varre o frustum inteiro; Passo 2 (`3×9 = 27` raios por perna ativa/recente) concentra alta densidade angular (`~4,2°`) ao longo do eixo coxa -> joelho -> canela -> pé.
- **Extensão Contínua & Chutes Rápidos (sem degrau binário):**
  - Substituído o salto abrupto entre joelho dobrado e estendido por transição contínua (`extRatio` entre `1,05×` e `1,32×` da coxa) + **Collinear Straight-Shin Extension** ao longo do vetor `joelho -> canela` quando a perna estica rápido.
'@

$tmpNotes = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($tmpNotes, $notes, (New-Object System.Text.UTF8Encoding($false)))
    if ($releaseExists) {
        Write-Host "Release '$Tag' already exists; uploading/replacing APK asset..."
        & $gh release upload $Tag $ApkPath --clobber
    } else {
        Write-Host "Creating GitHub Release '$Tag' and uploading APK..."
        & $gh release create $Tag $ApkPath --title $Title --notes-file $tmpNotes
    }
    if ($LASTEXITCODE -ne 0) {
        throw "gh release failed with exit code $LASTEXITCODE"
    }
} finally {
    if (Test-Path $tmpNotes) {
        Remove-Item $tmpNotes -Force -ErrorAction SilentlyContinue
    }
}

$nwo = (& $gh repo view --json nameWithOwner -q .nameWithOwner).Trim()
$apkFileName = Split-Path $ApkPath -Leaf
$downloadUrl = "https://github.com/$nwo/releases/download/$Tag/$apkFileName"
$releasePage = "https://github.com/$nwo/releases/tag/$Tag"

Write-Host ""
Write-Host "SUCCESS! Release published:" -ForegroundColor Green
Write-Host "  Release Page: $releasePage"
Write-Host "  Direct APK Download Link: $downloadUrl"
