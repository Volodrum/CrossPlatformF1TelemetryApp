; Windows install wizard (Inno Setup 6): asks where to install, then runs the Velopack setup into that folder, so the
; installed app still updates itself. Velopack's own Setup.exe is one-click and asks nothing; this wraps it.
;
; Built by .github/workflows/release.yml after vpk pack:
;   ISCC.exe /DAppVersion=0.5.2 /DVelopackSetup=releases\F1TelemetryApp-win-x64-Setup.exe /Odist installer\F1Telemetry.iss
;
; It installs nothing of its own and registers no uninstaller: the app, its shortcuts and its entry in Settings → Apps
; are Velopack's, and uninstalling there removes the whole install folder. That is why the folder must be new or empty.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef VelopackSetup
  #define VelopackSetup "..\releases\F1TelemetryApp-win-x64-Setup.exe"
#endif

; Velopack's pack id (release.yml PACK_ID): its install folder name and its uninstall registry key.
#define PackId "F1TelemetryApp"
#define AppTitle "F1 Telemetry"

[Setup]
AppId={#PackId}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppVerName={#AppTitle} {#AppVersion}
AppPublisher=Volodrum
AppPublisherURL=https://github.com/Volodrum/CrossPlatformF1TelemetryApp
DefaultDirName={localappdata}\{#PackId}
; A folder picked with Browse gets "F1TelemetryApp" added, so D:\Games becomes D:\Games\F1TelemetryApp.
AppendDefaultDirName=yes
DirExistsWarning=no
DisableProgramGroupPage=yes
; Per user, like Velopack: no admin rights, and updates never need them.
PrivilegesRequired=lowest
Uninstallable=no
CreateUninstallRegKey=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\F1Telemetry.App\Assets\Brand\app.ico
WizardStyle=modern
; The Velopack setup inside is already compressed.
Compression=zip/1
OutputBaseFilename={#PackId}-win-x64-Installer
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"

[CustomMessages]
en.DesktopShortcut=Create a desktop shortcut
en.Installing=Installing F1 Telemetry…
en.Updates=Installed copies update themselves from GitHub releases. Your recordings and settings stay in %LOCALAPPDATA%\F1Telemetry, whatever folder you pick.
en.NotEmpty=The folder%n%n%1%n%nis not empty. Uninstalling F1 Telemetry deletes its whole install folder, so pick a new or empty one.
en.NotWritable=Can't write to%n%n%1%n%nPick a folder of your own (not Program Files): F1 Telemetry installs without admin rights and updates itself there.
en.AlreadyInstalled=F1 Telemetry is already installed in%n%n%1%n%nIt is reinstalled there. To move it, uninstall it first (Settings → Apps).
en.Failed=The installation failed (code %1). Details are in%n%n%2

uk.DesktopShortcut=Створити ярлик на робочому столі
uk.Installing=Встановлення F1 Telemetry…
uk.Updates=Встановлена програма оновлюється сама з релізів на GitHub. Записи й налаштування лишаються в %LOCALAPPDATA%\F1Telemetry, незалежно від того, яку теку ви виберете.
uk.NotEmpty=Тека%n%n%1%n%nне порожня. Видалення F1 Telemetry видаляє всю теку встановлення, тож виберіть нову або порожню.
uk.NotWritable=Неможливо записати в%n%n%1%n%nВиберіть власну теку (не Program Files): F1 Telemetry встановлюється без прав адміністратора й там же оновлюється.
uk.AlreadyInstalled=F1 Telemetry вже встановлено в%n%n%1%n%nЇї буде перевстановлено туди ж. Щоб перенести, спершу видаліть її (Параметри → Програми).
uk.Failed=Встановлення не вдалося (код %1). Подробиці в%n%n%2

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"

[Files]
Source: "{#VelopackSetup}"; DestDir: "{tmp}"; DestName: "VelopackSetup.exe"; Flags: deleteafterinstall

[Code]
var
  InstalledDir: String;

{ Where Velopack installed the app before, from its uninstall entry; empty when it isn't installed. }
function ExistingInstall(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#PackId}', 'InstallLocation', Result) then
    Result := '';
  if (Result <> '') and not FileExists(AddBackslash(Result) + 'Update.exe') then
    Result := '';
end;

procedure InitializeWizard();
begin
  InstalledDir := ExistingInstall();
  if InstalledDir <> '' then
  begin
    WizardForm.DirEdit.Text := InstalledDir;
    if not WizardSilent() then
      MsgBox(FmtMessage(ExpandConstant('{cm:AlreadyInstalled}'), [InstalledDir]), mbInformation, MB_OK);
  end;
  WizardForm.SelectDirLabel.Caption := WizardForm.SelectDirLabel.Caption + #13#10#13#10 + ExpandConstant('{cm:Updates}');
end;

{ An update or reinstall goes where the app already is. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectDir) and (InstalledDir <> '');
end;

function IsEmptyDir(const Dir: String): Boolean;
var
  Find: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Dir) + '*', Find) then
  try
    repeat
      if (Find.Name <> '.') and (Find.Name <> '..') then
      begin
        Result := False;
        Exit;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
end;

function CanWrite(const Dir: String): Boolean;
var
  Probe: String;
begin
  Probe := AddBackslash(Dir) + '.f1telemetry-write-test';
  Result := ForceDirectories(Dir) and SaveStringToFile(Probe, 'ok', False);
  if Result then
    DeleteFile(Probe);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Dir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Dir := RemoveBackslashUnlessRoot(WizardDirValue());
    if DirExists(Dir) and not IsEmptyDir(Dir) and not FileExists(AddBackslash(Dir) + 'Update.exe') then
    begin
      MsgBox(FmtMessage(ExpandConstant('{cm:NotEmpty}'), [Dir]), mbError, MB_OK);
      Result := False;
    end
    else if not CanWrite(Dir) then
    begin
      MsgBox(FmtMessage(ExpandConstant('{cm:NotWritable}'), [Dir]), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

{ After Inno has unpacked the Velopack setup: run it silently into the chosen folder, then drop the desktop shortcut
  Velopack makes when it wasn't asked for. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Log: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  WizardForm.StatusLabel.Caption := ExpandConstant('{cm:Installing}');
  Log := ExpandConstant('{tmp}\..\F1TelemetryApp-install.log');
  if not Exec(ExpandConstant('{tmp}\VelopackSetup.exe'), '--silent --installto "' + RemoveBackslashUnlessRoot(WizardDirValue()) + '" --log "' + Log + '"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
  begin
    MsgBox(FmtMessage(ExpandConstant('{cm:Failed}'), [IntToStr(Code), Log]), mbCriticalError, MB_OK);
    Exit;
  end;

  if not WizardIsTaskSelected('desktopicon') then
    DeleteFile(ExpandConstant('{userdesktop}\{#AppTitle}.lnk'));
end;
