#define AppName "Kobra Time Lapse"
#define AppVersion "1.0.1"
#define AppPublisher "A to PC"
#define AppURL "https://github.com/A-to-PC/Kobra-Time-Lapse"

[Setup]
AppId={{E9C1C1CE-1BA8-47B9-AC2A-B9CFE426D89D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
; Per-user install, no admin/UAC prompt -- this app only ever makes outbound MQTT/RTSP
; connections (never listens for inbound traffic), so unlike Kobra LAN Monitor there's no
; firewall rule to add and no reason to ask for elevation at all. Deliberately consistent
; (always lowest, never switches to admin across versions) so {autopf}/{autodesktop}/{group}
; below resolve the same way every time -- see feedback-inno-setup-auto-constants: the bug
; there was specifically caused by switching privilege modes between builds, not by using
; the auto constants themselves.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Kobra Time Lapse
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=KobraTimeLapse-v{#AppVersion}-Setup
SetupIconFile=Assets\icon.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#AppVersion}.0
VersionInfoCopyright=Copyright (C) 2026 A to PC. All rights reserved.
VersionInfoCompany=A to PC

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"

[Files]
; Self-contained single-file publish output: the exe itself plus a handful of native WPF
; interop DLLs that .NET's single-file bundling can't merge in (a real platform limitation,
; not something left out by oversight), plus ffmpeg.exe (a separate third-party binary).
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\KobraTimeLapse.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\KobraTimeLapse.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\KobraTimeLapse.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings (including your printer/camera details) live under %AppData%\Kobra Time Lapse,
; separate from {app} -- both need explicit removal here. Back up settings.json first if
; you want to keep your printer/camera config across an uninstall.
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{userappdata}\Kobra Time Lapse"
