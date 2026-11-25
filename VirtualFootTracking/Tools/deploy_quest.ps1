param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$ApkPath = "",
    [switch]$TailLogcat
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $ApkPath = Join-Path $ProjectPath "Builds\VirtualFootTracking_v0.1_Diag.apk"
}

$adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
if (-not $adb) {
    $fallbackAdb = Join-Path $env:LOCALAPPDATA "Android\platform-tools\adb.exe"
    if (Test-Path $fallbackAdb) { $adb = $fallbackAdb }
}

if (-not $adb -or -not (Test-Path $adb)) {
    Write-Error "adb.exe not found. Ensure Android Platform-Tools is installed."
    exit 1
}

if (-not (Test-Path $ApkPath)) {
    Write-Error "APK not found at $ApkPath. Run Tools\build_apk.ps1 first."
    exit 1
}

$devices = & $adb devices -l
Write-Host "Connected ADB devices:"
$devices | Write-Host

$onlineDevice = $devices | Select-String -Pattern "\bdevice\b" | Select-Object -First 1
if (-not $onlineDevice) {
    Write-Error "No authorized Quest 3S detected via ADB. Connect USB, enable Developer Mode, and accept RSA debugging prompt inside headset."
    exit 1
}

$pkg = "com.virtualfoottracking.quest"
Write-Host "Installing $ApkPath..."
& $adb install -r -d "`"$ApkPath`""

Write-Host "Granting runtime permissions (if supported by OS prompt bypass)..."
& $adb shell pm grant $pkg com.oculus.permission.BODY_TRACKING 2>$null
& $adb shell pm grant $pkg com.oculus.permission.USE_SCENE 2>$null

Write-Host "Launching VirtualFootTracking on Quest 3S..."
& $adb shell monkey -p $pkg -c com.oculus.intent.category.VR 1 2>$null
if ($LASTEXITCODE -ne 0) {
    & $adb shell am start -n "$pkg/com.unity3d.player.UnityPlayerGameActivity"
}

if ($TailLogcat) {
    Write-Host "Streaming filtered logcat (Ctrl+C to stop)..."
    & $adb logcat -s Unity:V VFT:V OVR:V MRUK:V OpenXR:V
}
