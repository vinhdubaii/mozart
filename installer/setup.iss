; Mozart Browser — Inno Setup script
; Builds MozartBrowser-Setup-{#MyAppVersion}.exe from the dotnet publish output.
;
; AppVersion is normally overridden at CI time via:
;   iscc setup.iss /DMyAppVersion=1.2.0
; so it stays in sync with the git tag, without editing this file by hand.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Mozart Browser"
#define MyAppPublisher "Lidora Studio"
#define MyAppURL "https://github.com/vinhdubaii/mozart-browser"
#define MyAppExeName "MozartBrowser.exe"

[Setup]
; This GUID must stay constant across every release so Windows/Inno Setup
; recognizes new installers as *updates* to the same app instead of a
; separate parallel install.
AppId={{8F2C6E1A-4B7D-4E2F-9A31-3C0B8D2F5E77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\Mozart Browser
DefaultGroupName=Mozart Browser
DisableProgramGroupPage=yes
OutputBaseFilename=MozartBrowser-Setup-{#MyAppVersion}
; SetupIconFile is generated automatically by build.yml / release.yml (see
; "Generate app icon from source PNG" step) from assets/source/mozart-logo-2000.png.
; Guarded with #if so this .iss still compiles locally even before that step
; has ever run (e.g. a fresh clone with no CI run yet).
#if FileExists("..\assets\mozart.ico")
SetupIconFile=..\assets\mozart.ico
#endif
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Everything from `dotnet publish` output gets dropped here by CI before
; compiling this script (see .github/workflows/release.yml). This includes
; the bundled Fixed Version WebView2 runtime folder ("WebView2\"), produced
; automatically by the WebView2.Runtime.X64 NuGet package — no separate
; [Files] entry is needed for it.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Mozart Browser"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall Mozart Browser"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Mozart Browser"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Registers Mozart as a Windows "default browser" candidate, using the same
; three-part pattern (ProgId + Capabilities + RegisteredApplications) that
; Chrome/Edge/Firefox all use. Without this, Mozart simply never appears in
; Settings > Default apps for the user to pick — DefaultBrowserService in the
; app only *reads* this state and opens that Settings page; it cannot set the
; default itself (blocked by Windows since Win8). ProgId here
; ("MozartBrowserHTML") must exactly match DefaultBrowserService.ProgId in code.

; ---- ProgId: what http(s)/.htm/.html get opened with once chosen ----
Root: HKCU; Subkey: "Software\Classes\MozartBrowserHTML"; ValueType: string; ValueName: ""; ValueData: "Mozart Browser Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MozartBrowserHTML\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\MozartBrowserHTML\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; ---- Capabilities: what Windows shows in the "Choose default apps" picker ----
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A fast, privacy-focused web browser."
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities\URLAssociations"; ValueType: string; ValueName: "http"; ValueData: "MozartBrowserHTML"
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities\URLAssociations"; ValueType: string; ValueName: "https"; ValueData: "MozartBrowserHTML"
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities\FileAssociations"; ValueType: string; ValueName: ".htm"; ValueData: "MozartBrowserHTML"
Root: HKCU; Subkey: "Software\MozartBrowser\Capabilities\FileAssociations"; ValueType: string; ValueName: ".html"; ValueData: "MozartBrowserHTML"

; ---- Tells Windows the Capabilities key above exists and is a browser candidate ----
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Mozart Browser"; ValueData: "Software\MozartBrowser\Capabilities"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Mozart Browser"; Flags: nowait postinstall skipifsilent
