; Compile only through Build-Installer.ps1 after SDK/runtime/payload validation.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef VcRuntime
  #error VcRuntime is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef PackageVersion
  #error PackageVersion is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif

[Setup]
AppId={{BE9647D4-6501-48F2-BB5E-AEFAEC0F4542}
AppName=AeroScience
AppVersion={#ReleaseVersion}
AppVerName=AeroScience {#ReleaseVersion}
AppPublisher=AeroScience contributors
AppPublisherURL=https://github.com/JustSeanC/AeroScience
AppSupportURL=https://github.com/JustSeanC/AeroScience/issues
DefaultDirName={localappdata}\Programs\AeroScience
DefaultGroupName=AeroScience
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\AeroScience.Desktop
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
WizardStyle=modern
SetupLogging=yes
LicenseFile={#PayloadDir}\LICENSE
InfoBeforeFile=INSTALLER-NOTICE.txt
OutputDir={#OutputDirectory}
OutputBaseFilename=AeroScience-Setup-v{#ReleaseVersion}
VersionInfoVersion={#PackageVersion}.0
UninstallDisplayName=AeroScience
UninstallDisplayIcon={app}\AeroScience.exe
Compression=lzma2
SolidCompression=yes
ChangesAssociations=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VcRuntime}"; DestName: "VC_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\AeroScience\AeroScience"; Filename: "{app}\AeroScience.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\AeroScience"; Filename: "{app}\AeroScience.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\AeroScience"; ValueType: string; ValueName: "InstallDirectory"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\AeroScience.exe"; Description: "Launch AeroScience and finish first-run setup"; Flags: postinstall nowait skipifsilent

[Code]
function WithinPath(Child, Parent: String): Boolean;
begin
  Child := Lowercase(AddBackslash(ExpandFileName(Child)));
  Parent := Lowercase(AddBackslash(ExpandFileName(Parent)));
  Result := Pos(Parent, Child) = 1;
end;

function DataConflicts(InstallDir, DataDir: String): Boolean;
begin
  Result := (DataDir <> '') and (WithinPath(DataDir, InstallDir) or WithinPath(InstallDir, DataDir));
end;

function FolderHasContents(Folder: String): Boolean;
var
  Item: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Folder) + '*', Item) then
  begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Folder, Saved: String;
  ExistingApp: Boolean;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Folder := WizardDirValue;
  if DataConflicts(Folder, ExpandConstant('{localappdata}\AeroScience')) or
     FileExists(AddBackslash(Folder) + 'missions.db') or FileExists(AddBackslash(Folder) + 'library.json') then
    Result := False;
  if RegQueryStringValue(HKCU, 'Software\AeroScience', 'MissionDirectory', Saved) and DataConflicts(Folder, Saved) then Result := False;
  if RegQueryStringValue(HKCU, 'Software\AeroScience', 'ScienceLibraryDirectory', Saved) and DataConflicts(Folder, Saved) then Result := False;
  if not Result then
  begin
    MsgBox('Choose an application installation folder separate from your mission database, scientific library and configuration. Existing data will not be moved by the installer.', mbError, MB_OK);
    Exit;
  end;
  ExistingApp := FileExists(AddBackslash(Folder) + 'AeroScience.exe') and FileExists(AddBackslash(Folder) + 'AeroScience.runtimeconfig.json');
  if RegQueryStringValue(HKCU, 'Software\AeroScience', 'InstallDirectory', Saved) then
    ExistingApp := ExistingApp or (WithinPath(Folder, Saved) and WithinPath(Saved, Folder));
  if FolderHasContents(Folder) and not ExistingApp then
  begin
    MsgBox('This folder already contains files and is not a recognized AeroScience application installation. Choose an empty application folder or the existing AeroScience installation. Configure your separate data folders in the first-run wizard.', mbError, MB_OK);
    Result := False;
  end;
end;

function VcRuntimeNeeded: Boolean;
var
  Installed: Cardinal;
  Current, Bundled: String;
  CurrentVersion, BundledVersion: Int64;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1) and
     RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Current) then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
    if GetVersionNumbersString(ExpandConstant('{tmp}\VC_redist.x64.exe'), Bundled) then
    begin
      if (Length(Current) > 0) and ((Current[1] = 'v') or (Current[1] = 'V')) then Delete(Current, 1, 1);
      if StrToVersion(Current, CurrentVersion) and StrToVersion(Bundled, BundledVersion) then
        Result := ComparePackedVersion(CurrentVersion, BundledVersion) < 0;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if VcRuntimeNeeded then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
    WizardForm.StatusLabel.Caption := 'Installing Microsoft Visual C++ runtime...';
    if not ShellExec('runas', ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      Result := 'The Microsoft Visual C++ prerequisite could not start. Approve its Windows administrator prompt and retry.'
    else if (Code = 3010) or (Code = 1641) then NeedsRestart := True
    else if (Code <> 0) and (Code <> 1638) then Result := 'Microsoft Visual C++ installation failed (code ' + IntToStr(Code) + '). No application files have been replaced. Retry setup after resolving the prerequisite.';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if FileExists(ExpandConstant('{app}\AeroScience.exe')) then
      if not Exec(ExpandConstant('{app}\AeroScience.exe'), '--uninstall-toolbar', ExpandConstant('{app}'), SW_SHOW, ewWaitUntilTerminated, Code) or (Code <> 0) then
        MsgBox('The AeroScience Community toolbar was retained. Close MSFS and remove it later through AeroScience Settings if desired. Your mission database, scientific library and settings are also retained.', mbInformation, MB_OK);
  end;
end;

; No UninstallDelete: user databases, libraries, settings and toolbar backups are retained.
