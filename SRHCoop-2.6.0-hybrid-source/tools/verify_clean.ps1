param(
  [string]$Apk = "dist\StickmanRopeHero_3.4.186_LANCoop.apk",
  [string]$Serial = "127.0.0.1:7555"
)
$ErrorActionPreference = 'Continue'
$adb = 'C:\platform-tools\adb.exe'
$pkg = 'com.mgc.stickman.rope.hero'
$d = '/storage/emulated/0/Android/data/com.mgc.stickman.rope.hero/files'
Set-Location 'E:\deepseek\work'

& $adb -s $Serial uninstall $pkg 2>&1 | Out-Null
$install = & $adb -s $Serial install -r $Apk 2>&1
Write-Output ("install: " + ($install | Select-Object -Last 1))

& $adb -s $Serial shell am start -n "$pkg/com.unity3d.player.UnityPlayerNativeActivity" 2>&1 | Out-Null
Start-Sleep -Seconds 38
& $adb -s $Serial shell input tap 855 675      # privacy ACCEPT
Start-Sleep -Seconds 22
& $adb -s $Serial shell input tap 1404 628     # google-play dialog OK if present
Start-Sleep -Seconds 2
& $adb -s $Serial shell input tap 855 800      # menu play
Start-Sleep -Seconds 25
& $adb -s $Serial shell input tap 1404 628     # dialog again if it appeared
Start-Sleep -Seconds 2
& $adb -s $Serial shell input tap 855 800

Start-Sleep -Seconds 20
Write-Output "=== log ==="
& $adb -s $Serial shell "cat $d/BepInEx/LogOutput.log" 2>&1 | Select-Object -Last 12
& $adb -s $Serial shell screencap -p /sdcard/verify.png
& $adb -s $Serial pull /sdcard/verify.png .\verify.png 2>&1 | Out-Null
