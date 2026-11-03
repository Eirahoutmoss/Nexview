; NexView — isteğe bağlı Inno Setup betiği (https://jrsoftware.org/isinfo.php)
; Derleme: önce "dotnet publish" ile dist\win-x64\NexView.exe üretin, sonra bu dosyayı Inno Setup ile derleyin.
; Not: NexView kendi içinde de kullanıcı bazlı kurulum yapabilir (Ayarlar → Windows entegrasyonu → Bilgisayara kur).

#define AppName "NexView"
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#define AppPublisher "Hasan Güler"
#define AppExe "NexView.exe"

[Setup]
AppId={{6B1F3A52-6E0B-4C8A-9F1C-2B1D7E4A9C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Eirahoutmoss
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=NexView-{#AppVersion}-Kurulum
SetupIconFile=..\src\NexView.App\Assets\nexview.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "assoc"; Description: "Desteklenen görüntü dosyalarını NexView ile ilişkilendir"; GroupDescription: "Windows entegrasyonu:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\win-x64\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--register"; Flags: runhidden; Tasks: assoc
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; Flags: runhidden; RunOnceId: "NexViewUnregister"
