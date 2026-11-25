param(
    [string]$UnityExe = "",
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$LogFile = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LogFile)) {
    $logsDir = Join-Path $ProjectPath "Logs"
    New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
    $LogFile = Join-Path $logsDir "build_apk.log"
}

function Find-UnityEditor {
    $candidates = @(
        "C:\Users\Bruno\Unity\Editors\6000.0.85f2\Editor\Unity.exe",
        "$env:ProgramFiles\Unity\Hub\Editor\6000.0.85f2\Editor\Unity.exe",
        "D:\Unity\Hub\Editor\6000.0.85f2\Editor\Unity.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }

    $unityCli = Join-Path $env:LOCALAPPDATA "Unity\bin\unity.exe"
    if (Test-Path $unityCli) {
        try {
            $jsonOut = & $unityCli editors --json --quiet 2>$null
            if ($jsonOut) {
                $parsed = $jsonOut | ConvertFrom-Json
                foreach ($ed in $parsed) {
                    if ($ed.location -and (Test-Path $ed.location)) {
                        return $ed.location
                    }
                }
            }
        } catch { }
    }

    $wildcards = @(
        "$env:ProgramFiles\Unity\Hub\Editor\6000.0.*\Editor\Unity.exe",
        "C:\Users\Bruno\Unity\Editors\6000.0.*\Editor\Unity.exe",
        "D:\Unity\Hub\Editor\6000.0.*\Editor\Unity.exe"
    )
    foreach ($w in $wildcards) {
        $match = Get-Item $w -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if ($match) { return $match.FullName }
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($UnityExe)) {
    $UnityExe = Find-UnityEditor
}

if (-not $UnityExe -or -not (Test-Path $UnityExe)) {
    Write-Error "Unity 6000.0 LTS Editor (6000.0.85f2) was not found. Install it via Unity Hub or run elevated: unity install 6000.0.85f2 -m android --cm --accept-eula"
    exit 1
}

Write-Host "Using Unity Editor: $UnityExe"
Write-Host "Project Path: $ProjectPath"
Write-Host "Log File: $LogFile"

$argsList = @(
    "-batchmode",
    "-quit",
    "-projectPath", "`"$ProjectPath`"",
    "-buildTarget", "Android",
    "-executeMethod", "VirtualFootTracking.Editor.QuestBuildPipeline.ConfigureBuildSceneAndApk",
    "-logFile", "`"$LogFile`""
)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $UnityExe
$psi.Arguments = ($argsList -join " ")
$psi.UseShellExecute = $true
$psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden

$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
$exitCode = $proc.ExitCode
Write-Host "Unity exited with code: $exitCode"

if (Test-Path $LogFile) {
    $errors = Select-String -Path $LogFile -Pattern "error CS|BuildFailedException|No valid Unity Editor license|License error|ERROR:" -ErrorAction SilentlyContinue | Select-Object -First 25
    if ($errors) {
        Write-Host "`n--- Key Log Diagnostics ---"
        $errors | ForEach-Object { Write-Host $_.Line }
    }
}

$apkPath = Join-Path $ProjectPath "Builds\VirtualFootTracking_v0.3.1_DepthFix.apk"
if ($exitCode -eq 0 -and (Test-Path $apkPath)) {
    $sizeMb = [math]::Round((Get-Item $apkPath).Length / 1MB, 2)
    Write-Host "`nSUCCESS: APK generated at $apkPath ($sizeMb MB)"
    exit 0
} else {
    Write-Error "Build failed (ExitCode=$exitCode). Inspect $LogFile for full details."
    exit $exitCode
}
