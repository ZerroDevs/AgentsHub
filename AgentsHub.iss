; Script generated for Inno Setup 6
; Agents Hub Installer Configuration

#define MyAppName "Agents Hub"
; The version comes from the build. build-installer.cmd reads <Version> out of AgentsHub.csproj
; and passes it as /DMyAppVersion, so the project file stays the single source of truth. The
; fallback keeps a bare ISCC run working instead of failing on an undefined symbol.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyAppSourceDir
  #define MyAppSourceDir "bin\Release\net10.0-windows\win-x64\publish"
#endif
#define MyAppPublisher "ZeroNuxs"
#define MyAppURL "https://github.com/ZerroDevs/AgentsHub"
#define MyAppExeName "AgentsHub.exe"

[Setup]
; Unique AppId identifier (do not change across versions to allow seamless upgrades)
AppId={{E5D4A27A-94B8-4FE2-9E83-6A728D84B501}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=dist
OutputBaseFilename=AgentsHub-Setup-v{#MyAppVersion}
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

; Platform target: 64-bit Windows
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; User permission mode: allows per-user or administrative install
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

; Detect running instances via mutex and close cleanly
AppMutex=Global\AgentsHub_SingleInstance_Mutex,AgentsHub_SingleInstance_Mutex
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Launch Agents Hub minimized to system tray on Windows startup"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; الملف التنفيذي وكافة مخرجات الـ publish (DLLs وأي ملفات runtime مصاحبة)
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Excludes: "Profiles\*"; Flags: ignoreversion recursesubdirs createallsubdirs

; ملفات الواجهة الأمامية لمجلد UI بالكامل (HTML / CSS / JS / xterm)
Source: "ui\*"; DestDir: "{app}\ui"; Flags: ignoreversion recursesubdirs createallsubdirs

; مجلد الأصول الثابتة
Source: "assets\*"; DestDir: "{app}\assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; سكريبتات وأدوات التشغيل
Source: "scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs

; ملفات الرخص والتوثيق
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--tray"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete] wipe of {app}.
;
; Per-profile credentials now live under %LOCALAPPDATA%\AgentsHub, outside the install folder,
; so an uninstall cannot reach them. The install folder is still not wiped because a custom
; theme or a user-added script there may be the only copy. Inno already removes every file it
; installed; anything left behind is user data, which is meant to survive an uninstall.