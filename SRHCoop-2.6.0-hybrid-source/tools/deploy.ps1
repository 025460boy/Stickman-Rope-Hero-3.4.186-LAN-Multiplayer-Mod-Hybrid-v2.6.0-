param(
  [string]$Apk = "build\SRHLanCoop-v1.apk",
  [int]$Boot = 42,
  [string]$Serial = "127.0.0.1:7555",
  [switch]$Fresh
)
$ErrorActionPreference = 'Continue'
$adb = 'C:\platform-tools\adb.exe'
$pkg = 'com.mgc.stickman.rope.hero'
$d = '/storage/emulated/0/Android/data/com.mgc.stickman.rope.hero/files'
Set-Location 'E:\deepseek\work'

if ($Fresh) { & $adb -s $Serial uninstall $pkg 2>&1 | Out-Null }
$out = & $adb -s $Serial install -r $Apk 2>&1
$out | Select-Object -Last 1

& $adb -s $Serial shell "rm -rf $d/BepInEx/plugins/* $d/BepInEx/LogOutput.log $d/BepInEx/core/bootstrap.log"
& $adb -s $Serial shell am force-stop $pkg
& $adb -s $Serial logcat -c
& $adb -s $Serial shell am start -n "$pkg/com.unity3d.player.UnityPlayerNativeActivity" 2>&1 | Out-Null

$waited = 0
while ($waited -lt $Boot) {
  Start-Sleep -Seconds 5
  $waited += 5
  $log = & $adb -s $Serial shell "grep -c 'Local player bound' $d/BepInEx/LogOutput.log 2>/dev/null" 2>&1
  if ($log -match '^[1-9]') { break }
}
Write-Output ("boot wait: " + $waited + "s")

# dismiss the daily-bonus popup if present, then the map overlay
& $adb -s $Serial shell input tap 855 935
Start-Sleep -Seconds 3
& $adb -s $Serial shell input tap 860 830
Start-Sleep -Seconds 4
& $adb -s $Serial shell "tail -6 $d/BepInEx/LogOutput.log" 2>&1
& $adb -s $Serial shell screencap -p /sdcard/state.png
& $adb -s $Serial pull /sdcard/state.png .\state.png 2>&1 | Out-Null
