; ClipGuard Media Player Setup <version>.exe
; Installs for the current Windows user (no administrator rights) under
; %LOCALAPPDATA%\Programs\ClipGuard Media Player. Settings and logs are in
; %LOCALAPPDATA%\ClipGuard and are never touched by setup or uninstall.
; Built by build\build.ps1:  ISCC /DAppVer=<version> /DSrc=<package folder> /O<out> MediaPlayer.iss

#ifndef AppVer
  #define AppVer "0.0.0"
#endif
#ifndef Src
  #define Src "..\_bin\ClipGuard Media Player"
#endif
#define AppExe "ClipGuard Media Player.exe"

[Setup]
AppId={{3B7E9D52-6A41-4C8F-B2E0-5F19C7A4D836}
AppName=ClipGuard Media Player
AppVersion={#AppVer}
AppVerName=ClipGuard Media Player {#AppVer}
AppPublisher=ClipGuard Media Player
AppPublisherURL=https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player
AppSupportURL=https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/issues
AppUpdatesURL=https://github.com/peterbiancardi-fenceman/ClipGuard-Media-Player/releases
DefaultDirName={localappdata}\Programs\ClipGuard Media Player
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
OutputBaseFilename=ClipGuard Media Player Setup {#AppVer}
SetupIconFile=..\src\clipguard.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=ClipGuard Media Player
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The Blackmagic DeckLink SDK licence, which two of the player's files are built against,
; must be accepted to install (the capture filter and the signal monitor).
LicenseFile=..\licenses\decklink_sdk_license.txt
; The copy protection notice and the no-warranty statement.
InfoBeforeFile=notice.txt

[Messages]
LicenseLabel3=Two of ClipGuard Media Player's files (the capture card filter and its signal monitor) are built against the Blackmagic Design DeckLink SDK. Please read its licence. You must accept it to install.

[Tasks]
Name: "desktopicon"; Description: "Desktop shortcut"

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\ClipGuard Media Player"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\ClipGuard Media Player"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Start ClipGuard Media Player now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.old"
