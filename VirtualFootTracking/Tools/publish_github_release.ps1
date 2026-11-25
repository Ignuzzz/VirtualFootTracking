param(
    [string]$RepoName = "VirtualFootTracking",
    [ValidateSet("public", "private")]
    [string]$Visibility = "public",
    [string]$Tag = "v0.3.1-depth",
    [string]$Title = "Virtual Foot Tracking v0.3.1 — Depth Left/Right Fix, Auto Anti-Wall & Straight/Bent Knee"
)

$ErrorActionPreference = "Stop"

$ProjectDir = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$RepoRoot = (Resolve-Path (Join-Path $ProjectDir "..")).Path
$ApkPath = Join-Path $ProjectDir "Builds\VirtualFootTracking_v0.3.1_DepthFix.apk"

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
Meta Quest 3S Phase 3 Fix (`v0.3.1`) — Correções baseadas no teste real em hardware:
- **Fix 1 — Separação Real Perna Esquerda vs. Direita (`EnvironmentDepthLegDetector` + `ProprietaryFootTrackingSolver`):**
  - Corrigido o vetor de orientação da pelve: no skeleton `OVRBody`, `hips.WorldRotation * Vector3.forward` apontava lateralmente e rotulava a perna esquerda como direita. Agora o eixo lateral (`Right - Left`) e frontal (`Cross(Right, Up)`) são calculados diretamente a partir das juntas bilaterais de quadril/braços/ombros, com centro sagital exato entre os quadris.
- **Fix 2 — Proteção Automática Contra Paredes/Móveis ("Movimentos Ghosts" em Salas Pequenas):**
  - Verificação contínua via MRUK (`IsPositionInRoom` + `TryGetClosestSurfacePosition <= 0,14 m` para paredes/móveis), aprendizado automático contínuo de voxels estáticos fora do alcance da perna (`_autoStaticVoxels`, `0,11 m`), rejeição de planos verticais planos (paredes sem MRUK) e corredor anatômico mais justo (`coxa + canela + 0,07 m`).
- **Fix 3 — Joelho Esticado vs. Joelho Dobrado (`Straight vs. Bent Knee`):**
  - Substituído o filtro esférico centrado na coxa (que descartava canela e pé esticados) por um filtro no plano sagital da perna + extremidade distal suportada real sem puxar de volta para o joelho, com grade ampliada para `13×10` (`130` raios) e `MaxBufferPoints = 136`.
  - Chutes esticados até a ponta do pé mantêm o joelho reto (`~2°–10°`), enquanto elevação apenas de joelho dobrado aciona `IsKneeDominant`.
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
