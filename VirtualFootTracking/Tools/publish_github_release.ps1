param(
    [string]$RepoName = "VirtualFootTracking",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Tag = "v0.3.2-depth",
    [string]$Title = "Virtual Foot Tracking v0.3.2 — Fix MRUK GLOBAL_MESH Mask, Shoe Reach & Left/Right Depth"
)

$ErrorActionPreference = "Stop"

$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ProjectDir "..")).Path
$ApkPath = Join-Path $ProjectDir "Builds\VirtualFootTracking_v0.3.2_DepthFix2.apk"

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
Meta Quest 3S Phase 3 Hotfix (`v0.3.2`) — Corrige a regressão da `v0.3.1` em que nenhum ponto de perna era aceito:
- **Fix Crítico — MRUK `GLOBAL_MESH` apagava 100% da nuvem Depth:**
  - `MRUKRoom.TryGetClosestSurfacePosition` incluía `GLOBAL_MESH` (cuja bounding box envolve o cômodo inteiro e retorna distância negativa `<= 0,14 m`), descartando todo ponto dentro da sala.
  - Substituído por verificação explícita apenas de planos de parede (`room.WallAnchors`, `ComponentType.Plane <= 0,12 m`) e volumes nominais de móveis (`TABLE | COUCH | BED | STORAGE | SCREEN | LAMP | PLANT`).
- **Fix 2 — Alcance Real de Calçado & Sem Auto-Banimento da Perna:**
  - `maxLegReach` ampliado para `coxa + canela + 0,22 m` e aprendizado automático de fundo (`_autoStaticVoxels`) só grava pontos além de `maxLegReach + 0,30 m` (nunca gravando o próprio pé).
  - Rejeição de paredes verticais atua apenas no frame atual.
- **Fix 3 — Diagnóstico Transparente na UI:**
  - HUD Live e aba `DIAG` agora exibem os contadores exatos de rejeição (`Rej[Fl=.. Rch=.. Arm=.. Bg=.. Mrk=..]`), além de manter as correções de Esquerda/Direita e Joelho Esticado vs. Dobrado.
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
