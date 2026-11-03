# NexView Android derleme ortamı kurulumu
# Kullanım (Yönetici PowerShell): powershell -ExecutionPolicy Bypass -File F:\NexView\tools\setup-android.ps1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Write-Host "1/3 .NET Android iş yükü kuruluyor (birkaç dakika sürebilir)..." -ForegroundColor Cyan
dotnet workload install android
if ($LASTEXITCODE -ne 0) { throw "Android iş yükü kurulamadı. PowerShell'i yönetici olarak açtığınızdan emin olun." }

# Mevcut Android Studio / SDK kurulumunu kullan, yoksa kullanıcı klasörüne kur
$sdk = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, "$env:LOCALAPPDATA\Android\Sdk", "C:\Android\Sdk") |
  Where-Object { $_ -and (Test-Path (Join-Path $_ 'platform-tools')) } | Select-Object -First 1
if (-not $sdk) { $sdk = "$env:LOCALAPPDATA\Android\Sdk" }
$jdk = @($env:JAVA_HOME, "C:\Program Files\Android\Android Studio\jbr", "$env:LOCALAPPDATA\Android\jdk") |
  Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\java.exe')) } | Select-Object -First 1
if (-not $jdk) { $jdk = "$env:LOCALAPPDATA\Android\jdk" }
$sdk = $sdk.TrimEnd('\'); $jdk = $jdk.TrimEnd('\')
Write-Host "   SDK: $sdk"
Write-Host "   JDK: $jdk"

Write-Host "2/3 Android SDK ve JDK bileşenleri tamamlanıyor..." -ForegroundColor Cyan
$probe = Join-Path $env:TEMP 'nexview-android-probe'
if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
dotnet new android -o $probe --force | Out-Null
dotnet build (Join-Path $probe 'nexview-android-probe.csproj') -t:InstallAndroidDependencies -f net10.0-android `
  "-p:AndroidSdkDirectory=$sdk" "-p:JavaSdkDirectory=$jdk" -p:AcceptAndroidSDKLicenses=True -v q
if ($LASTEXITCODE -ne 0) { throw "Android bağımlılıkları kurulamadı." }

Write-Host "3/3 Yollar kaydediliyor..." -ForegroundColor Cyan
[Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdk, 'User')
[Environment]::SetEnvironmentVariable('JAVA_HOME', $jdk, 'User')
@"
<Project>
  <PropertyGroup>
    <AndroidSdkDirectory>$sdk</AndroidSdkDirectory>
    <JavaSdkDirectory>$jdk</JavaSdkDirectory>
  </PropertyGroup>
</Project>
"@ | Set-Content (Join-Path $Root 'android-paths.props') -Encoding UTF8
Remove-Item $probe -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Hazır. Şimdi devloop penceresini kapatıp yeniden başlatın." -ForegroundColor Green
