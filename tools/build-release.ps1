# NexView sürüm paketi: tek dosya exe + taşınabilir zip + (Inno Setup varsa) kurulum
# Kullanım: powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-Edition AFOT]
param([string]$Edition = '')
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Dist = Join-Path $Root 'dist'
$Out = if ($Edition) { Join-Path $Dist "win-x64-$Edition" } else { Join-Path $Dist 'win-x64' }

Write-Host "1/3 Yayınlanıyor..." -ForegroundColor Cyan
dotnet publish (Join-Path $Root 'src\NexView.App\NexView.App.csproj') -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:NexViewEdition=$Edition" -o $Out -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız" }

[xml]$props = Get-Content (Join-Path $Root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version

Write-Host "2/3 Taşınabilir paket..." -ForegroundColor Cyan
$suffix = if ($Edition) { "-$Edition" } else { "" }
$portable = Join-Path $Dist "NexView-$version$suffix-portable"
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory $portable | Out-Null
Copy-Item (Join-Path $Out 'NexView.exe') $portable
Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $portable 'LICENSE.txt')
Copy-Item (Join-Path $Root 'THIRD_PARTY_NOTICES.md') $portable
Set-Content (Join-Path $portable 'portable.txt') 'Bu dosya varsa NexView ayarlarını ve önbelleğini Data klasöründe tutar.'
$zip = Join-Path $Dist "NexView-$version$suffix-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$portable\*" -DestinationPath $zip

if ($Edition) { Write-Host "Bitti (yalnızca taşınabilir): $zip" -ForegroundColor Green; return }
# Yerleşik kurulum sihirbazı: exe adında "Setup" geçtiğinde NexView kurulum modunda açılır
Copy-Item (Join-Path $Out 'NexView.exe') (Join-Path $Dist "NexView-$version-Setup.exe") -Force
Write-Host "3/3 Kurulum (Inno Setup, isteğe bağlı)..." -ForegroundColor Cyan
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
  & $iscc "/DAppVersion=$version" (Join-Path $Root 'installer\NexView.iss') | Out-Host
} else {
  Write-Host "Inno Setup bulunamadı; kurulum atlandı (https://jrsoftware.org/isdl.php)." -ForegroundColor Yellow
}
Write-Host "Bitti: $Dist" -ForegroundColor Green
