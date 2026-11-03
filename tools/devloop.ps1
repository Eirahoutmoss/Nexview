# NexView geliştirme döngüsü
# .devloop\request dosyasına yazılan sabit eylemleri çalıştırır. Keyfi komut çalıştırmaz.
# Kullanım: powershell -ExecutionPolicy Bypass -File tools\devloop.ps1
$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent $PSScriptRoot
$Dir = Join-Path $Root '.devloop'
New-Item -ItemType Directory -Force -Path $Dir | Out-Null
$Req = Join-Path $Dir 'request'
$Status = Join-Path $Dir 'status'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$Sln = Join-Path $Root 'NexView.sln'
$Exe = Join-Path $Root 'src\NexView.App\bin\Debug\net10.0\NexView.exe'
$Android = Join-Path $Root 'src\NexView.Android\NexView.Android.csproj'

# Konsolda tıklanınca donmayı önle (QuickEdit kapalı)
try {
  Add-Type -Namespace NV -Name Con -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern System.IntPtr GetStdHandle(int h);
[DllImport("kernel32.dll")] public static extern bool GetConsoleMode(System.IntPtr h, out uint m);
[DllImport("kernel32.dll")] public static extern bool SetConsoleMode(System.IntPtr h, uint m);
'@
  $h = [NV.Con]::GetStdHandle(-10); $m = 0
  if ([NV.Con]::GetConsoleMode($h, [ref]$m)) { [void][NV.Con]::SetConsoleMode($h, ($m -band (-bnot 0x40)) -bor 0x80) }
} catch { }

function Stop-App { Get-Process NexView -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 400 }
function Invoke-Action([string]$name, [scriptblock]$body) {
  $log = Join-Path $Dir "$name.log"
  "running $name $(Get-Date -Format o)" | Set-Content $Status
  Write-Host "[$(Get-Date -Format HH:mm:ss)] $name ..." -NoNewline
  $code = 0
  try { & $body *> $log; $code = $LASTEXITCODE; if ($null -eq $code) { $code = 0 } } catch { $_ | Out-File $log -Append; $code = 1 }
  "done $name $code $(Get-Date -Format o)" | Set-Content $Status
  if ($code -eq 0) { Write-Host " OK" -ForegroundColor Green } else { Write-Host " HATA ($code)" -ForegroundColor Red }
}

"idle $(Get-Date -Format o)" | Set-Content $Status
Write-Host "NexView devloop v2 hazir: $Root  (kapatmak icin Ctrl+C)" -ForegroundColor Cyan
while ($true) {
  if (Test-Path $Req) {
    Start-Sleep -Milliseconds 300
    $lines = @(Get-Content $Req)
    Remove-Item $Req -Force -ErrorAction SilentlyContinue
    $action = ($lines | Select-Object -First 1).Trim()
    $arg = if ($lines.Count -gt 1) { $lines[1].Trim() } else { '' }
    switch ($action) {
      'info'    { Invoke-Action 'info' { dotnet --info; dotnet workload list } }
      'restore' { Invoke-Action 'restore' { dotnet restore $Sln -v q } }
      'build'   { Stop-App; Invoke-Action 'build' { dotnet build $Sln -c Debug -v q -clp:NoSummary } }
      'test'    { Invoke-Action 'test' { dotnet test $Sln -c Debug -v q --nologo } }
      'shot'    {
        Stop-App
        Invoke-Action 'shot' {
          $env:NEXVIEW_DATA_DIR = Join-Path $Dir 'appdata'
          $shot = Join-Path $Dir 'shot.png'
          Remove-Item $shot -ErrorAction SilentlyContinue
          $a = @('--devshot', "`"$shot`"")
          if ($arg) { $a += "`"$(Join-Path $Root $arg)`"" }
          $p = Start-Process -FilePath $Exe -ArgumentList $a -PassThru
          if (-not $p.WaitForExit(90000)) { $p.Kill(); "timeout" ; $global:LASTEXITCODE = 2 } else { "exit $($p.ExitCode)"; $global:LASTEXITCODE = $p.ExitCode }
          Remove-Item Env:\NEXVIEW_DATA_DIR
        }
      }
      'run'     { Stop-App; Start-Process -FilePath $Exe; "done run 0 $(Get-Date -Format o)" | Set-Content $Status }
      'publish' { Stop-App; Invoke-Action 'publish' { dotnet publish (Join-Path $Root 'src\NexView.App\NexView.App.csproj') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $Root 'dist\win-x64') -v q } }
      'android' { Invoke-Action 'android' { dotnet publish $Android -c Release -f net10.0-android -p:AndroidPackageFormat=apk -o (Join-Path $Root 'dist\android') -v q -clp:NoSummary } }
      'android-build' { Invoke-Action 'android-build' { dotnet build $Android -c Debug -f net10.0-android -v q -clp:NoSummary } }
      default   { "unknown $action $(Get-Date -Format o)" | Set-Content $Status }
    }
  }
  Start-Sleep -Milliseconds 700
}
