param(
  [string]$Apk = "build\SRHCoop-test2.apk",
  [int]$Wait = 40,
  [string]$Serial = "127.0.0.1:7555",
  [switch]$SkipInstall
)
$ErrorActionPreference = 'Continue'
$adb = 'C:\platform-tools\adb.exe'
$pkg = 'com.mgc.stickman.rope.hero'
Set-Location 'E:\deepseek\work'

if (-not $SkipInstall) {
  & $adb -s $Serial uninstall $pkg 2>&1 | Out-Null
  $out = & $adb -s $Serial install -r $Apk 2>&1
  $out | Select-Object -Last 1
}
& $adb -s $Serial logcat -c
& $adb -s $Serial shell am start -n "$pkg/com.unity3d.player.UnityPlayerNativeActivity" 2>&1 | Out-Null
Start-Sleep -Seconds $Wait

$pid_ = & $adb -s $Serial shell pidof $pkg
Write-Output ("pid=" + $pid_)

Write-Output "=== be5 ==="
& $adb -s $Serial logcat -d -s be5:* 2>&1 | Select-Object -Last 25
Write-Output "=== Unity errors/exceptions ==="
& $adb -s $Serial logcat -d -s Unity:* 2>&1 | Select-String 'Exception|Error|error|BepInEx|Chainloader|plugin' | Select-Object -Last 25
