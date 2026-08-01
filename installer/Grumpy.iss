; ─────────────────────────────────────────────────────────────────────────────
; GrumpyGit installer — per-user, no administrator rights required.
;
; Everything installs under the user's profile so the installer never triggers a
; UAC prompt and works on locked-down machines. That choice drives most of the
; settings below:
;
;   PrivilegesRequired=lowest   -> run as the invoking user, never elevate
;   DefaultDirName={localappdata}\Programs\Grumpy
;   Uninstall entry is written to HKCU (Inno does this automatically when not
;   elevated), so it appears in "Apps & features" for this user only.
;
; Version and source directory are supplied by the build:
;   iscc /DAppVersion=1.2.3 /DPublishDir=..\publish GrumpyGit.iss
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName        "Grumpy"
#define AppPublisher   "Gareth Repton"
#define AppExeName     "Grumpy.exe"
#define AppUrl         "https://github.com/garethrepton/GrumpyGit"

[Setup]
; A stable GUID is what lets an upgrade replace the previous install and what
; the uninstaller keys off. It must never change between releases.
AppId={{7B3F2C64-9A41-4E58-B0D2-6C1E5F8A93D7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; ── Per-user install: no admin, no UAC ──────────────────────────────────────
PrivilegesRequired=lowest
; Do not offer an "install for all users" option — that path needs admin and
; would silently change the install location and uninstall scope.
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
UsePreviousAppDir=yes

; ── Output ──────────────────────────────────────────────────────────────────
OutputDir=..\dist
OutputBaseFilename=Grumpy-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\GrumpyGit.App\Assets\grumpy.ico

; ── Uninstall ───────────────────────────────────────────────────────────────
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; Ask the Restart Manager to close a running GrumpyGit rather than failing on a
; locked file. Without this, installing over a running app leaves stale files
; and uninstalling silently leaves the exe behind.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

SetupLogging=yes
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Recurse the whole publish output. The app is published self-contained, so the
; user does not need a .NET runtime installed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; {autoprograms}/{autodesktop} resolve to the per-user locations because this
; installer never elevates.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Anything the app writes *inside* its own install directory (logs, caches) that
; was not part of the file list would otherwise be orphaned and leave {app}
; behind as a non-empty folder.
Type: filesandordirs; Name: "{app}"

[Code]
// ───────────────────────────────────────────────────────────────────────────
// User data lives outside the install directory, under %LOCALAPPDATA%\Grumpy:
//   settings.json, review-state\, review-notes\
// Removing it is NOT automatic — review notes are user-authored content that
// cannot be regenerated, and a reinstall should find them intact. The user is
// asked explicitly, and only on a full uninstall (not an upgrade).
//
// Note: use // comments in [Code], not { }. A brace comment containing an Inno
// constant such as {app} is terminated early by that constant's closing brace.
// ───────────────────────────────────────────────────────────────────────────
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Must match AppPaths.Root in the application (%LOCALAPPDATA%\Grumpy).
    DataDir := ExpandConstant('{localappdata}\Grumpy');

    if DirExists(DataDir) then
    begin
      // Never ask during a silent uninstall. There is nobody to answer, and an
      // unanswered prompt hangs the process indefinitely — which is exactly how the
      // v0.1.3 release build stalled: CI installs and uninstalls with /VERYSILENT, and
      // the data directory had started existing on the runner because the test suite
      // creates it. Silence means keep the data, matching the dialog's own default and
      // erring toward not destroying user-authored notes.
      if UninstallSilent then
        Exit;

      if MsgBox('Also delete GrumpyGit''s saved settings and review notes?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
