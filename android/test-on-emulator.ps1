# Starts the "NotchPhone" Android emulator, waits until it has booted, and installs Notch Island on it.
# Needs Windows Hypervisor Platform switched on (Windows features), once.
#   powershell -ExecutionPolicy Bypass -File android\test-on-emulator.ps1 [path\to.apk]
param([string]$Apk = "$PSScriptRoot\app\build\outputs\apk\release\app-release.apk")

$sdk = "$env:LOCALAPPDATA\Android\Sdk"
$adb = "$sdk\platform-tools\adb.exe"
$emu = "$sdk\emulator\emulator.exe"

if (-not (& $adb devices | Select-String "emulator-\d+\s+device")) {
    Write-Host "Starting the NotchPhone emulator..."
    Start-Process $emu -ArgumentList "-avd NotchPhone -no-snapshot-save -no-boot-anim -camera-front none -camera-back none"
    & $adb wait-for-device
    Write-Host "Waiting for Android to finish booting (a minute or two the first time)..."
    do { Start-Sleep 3; $booted = (& $adb shell getprop sys.boot_completed 2>$null) -match "1" } until ($booted)
}

Write-Host "Installing $Apk ..."
& $adb install -r -g $Apk
# the island sits around a camera hole: give the emulated phone one (Settings > Developer options > Display cutout)
& $adb shell cmd overlay enable com.android.internal.display.cutout.emulation.hole 2>$null | Out-Null
& $adb shell monkey -p com.windowsnotch.island -c android.intent.category.LAUNCHER 1 | Out-Null
Write-Host "Done: Notch Island is open on the emulator."
