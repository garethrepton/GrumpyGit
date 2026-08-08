; ─────────────────────────────────────────────────────────────────────────────
; Grumpy AI installer — per-user, no administrator rights required.
;
; THIS FILE IS BRANCH-OWNED. It builds Grumpy AI, and only Grumpy AI. `master`
; carries its own copy of this script that builds Grumpy. They will conflict on
; every merge across the two branches; resolve by keeping the branch's own copy.
; Do not reintroduce an edition switch — one branch, one product, one script.
;
; Grumpy and Grumpy AI are separate products: different AppId, install directory,
; Start-menu name, setup filename and icon. Installing one never upgrades or
; removes the other, and both can sit side by side — which is exactly why the
; icons differ, since the two shortcuts end up next to each other. Only the
; per-user data directory is shared — see [Code].
;
; Everything installs under the user's profile so the installer never triggers a
; UAC prompt and works on locked-down machines. That choice drives most of the
; settings below:
;
;   PrivilegesRequired=lowest   -> run as the invoking user, never elevate
;   DefaultDirName={localappdata}\Programs\Grumpy AI
;   Uninstall entry is written to HKCU (Inno does this automatically when not
;   elevated), so it appears in "Apps & features" for this user only.
;
; Version and source directory are supplied by the build:
;   iscc /DAppVersion=1.2.3 /DPublishDir=..\publish Grumpy.iss
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

; "(Experimental)" is carried in the display name on purpose: it is what the
; Start menu, Apps & features and the uninstaller show, so the warning is
; wherever the user meets the product rather than only in the README.
#define AppName        "Grumpy AI (Experimental)"

; The directory keeps the short name — the display name is where the warning
; belongs, and a path with brackets in it only makes trouble for scripts.
#define InstallDirName "Grumpy AI"

; Its own GUID, distinct from Grumpy's. Sharing it would make this installer
; treat an existing Grumpy as a previous version of itself and replace it.
#define AppGuid        "E4A97C21-5B38-4D6F-9C10-2A7F63B8D45E"

#define OutputName     "Grumpy-AI-" + AppVersion + "-win-x64-setup"
#define IconFile       "..\src\GrumpyGit.App\Assets\sheep-ai.ico"

; A page the user has to click past before anything is written. Nobody reads a
; README before running a setup.exe; they do read the wizard in front of them.
#define NoticeFile     "experimental-notice.txt"

#define AppPublisher   "Gareth Repton"
#define AppExeName     "Grumpy.exe"
#define AppUrl         "https://github.com/garethrepton/GrumpyGit"

[Setup]
; A stable GUID is what lets an upgrade replace the previous install and what
; the uninstaller keys off. It must never change between releases.
AppId={{{#AppGuid}}
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
DefaultDirName={localappdata}\Programs\{#InstallDirName}
DefaultGroupName={#AppName}
UsePreviousAppDir=yes

; ── Output ──────────────────────────────────────────────────────────────────
OutputDir=..\dist
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#IconFile}

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
InfoBeforeFile={#NoticeFile}

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
// Both editions share this one directory, on purpose: your repositories, review
// notes and settings should follow you when you move between Grumpy and Grumpy
// AI. That makes deleting it from either uninstaller destructive to the other,
// so the prompt says so.
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

      if MsgBox('Also delete saved settings and review notes?' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                'This folder is shared by Grumpy and Grumpy AI. If the other ' +
                'edition is still installed, deleting it will wipe its notes too.' + #13#10 + #13#10 +
                'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
