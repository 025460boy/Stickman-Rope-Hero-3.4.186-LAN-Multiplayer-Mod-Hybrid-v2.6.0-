# Hybrid build: no BepInEx runtime, no DEX patch, no native shim.
#   game APK  +  assets/bin/Data/Managed/SRHCoop.dll  (+ optional IL bootstrap)
param(
  [string]$Apk = "F:\QQdownland\Stickman Rope Hero_3.4.186.apk",
  [string]$Out = "dist\StickmanRopeHero_3.4.186_Coop.apk",
  [string]$Java = "C:\Program Files\Java\jdk-23",
  [string]$BuildTools = "build-tools\android-14",
  [switch]$IlPatch
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$dotnet = ".\dotnet\dotnet.exe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
New-Item -ItemType Directory -Force build, dist | Out-Null

Write-Host "[1/4] build mod" -ForegroundColor Cyan
& $dotnet build mod\SRHCoop\SRHCoop.csproj -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "mod build failed" }

$patched = $null
if ($IlPatch) {
  Write-Host "[2/4] IL bootstrap patch" -ForegroundColor Cyan
  & $dotnet run --project tools\IlPatch -c Release -- `
      --game build\Assembly-CSharp.dll `
      --mod mod\SRHCoop\bin\Release\SRHCoop.dll `
      --out build\Assembly-CSharp.patched.dll
  if ($LASTEXITCODE -ne 0) { throw "IL patch failed" }
  $patched = "build\Assembly-CSharp.patched.dll"
} else {
  Write-Host "[2/4] IL bootstrap patch (skipped)" -ForegroundColor Cyan
}

Write-Host "[3/4] repack" -ForegroundColor Cyan
python -c "import zipfile;open(r'build\Assembly-CSharp.dll','wb').write(zipfile.ZipFile(r'$Apk').read('assets/bin/Data/Managed/Assembly-CSharp.dll'))"
if ($patched) {
  python tools\pack_hybrid.py "$Apk" "$Out" --dll mod\SRHCoop\bin\Release\SRHCoop.dll --patched-csharp $patched
} else {
  python tools\pack_hybrid.py "$Apk" "$Out" --dll mod\SRHCoop\bin\Release\SRHCoop.dll
}
if ($LASTEXITCODE -ne 0) { throw "repack failed" }

Write-Host "[4/4] align + sign" -ForegroundColor Cyan
$env:JAVA_HOME = $Java
$env:PATH = "$Java\bin;$env:PATH"
& "$BuildTools\zipalign.exe" -f -p 4 "$Out" "$Out.aligned"
Move-Item -Force "$Out.aligned" "$Out"
& "$BuildTools\apksigner.bat" sign --ks debug.keystore --ks-key-alias androiddebugkey `
    --ks-pass pass:android --key-pass pass:android --min-sdk-version 24 --out "$Out.signed" "$Out"
Move-Item -Force "$Out.signed" "$Out"
& "$BuildTools\apksigner.bat" verify --min-sdk-version 24 "$Out" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signature verify failed" }
Remove-Item "$Out.idsig" -ErrorAction SilentlyContinue

Write-Host "[done] $Out" -ForegroundColor Green

