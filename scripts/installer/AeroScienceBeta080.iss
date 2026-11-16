; Version-specific Beta 080 candidate. Compile only through Build-080BetaCandidate.ps1.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef VcRuntime
  #error VcRuntime is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif

[Setup]
AppId={{CA565DF9-FB90-4F3F-A013-351265D05DF0}
AppName=AeroScience 0.8.0 Beta 1
AppVersion=0.8.0-beta.1
AppVerName=AeroScience 0.8.0 Beta 1
AppPublisher=AeroScience contributors
AppSupportURL=https://github.com/JustSeanC/AeroScience/issues
DefaultDirName={localappdata}\Programs\AeroScience-Beta-v0.8.0
DefaultGroupName=AeroScience 0.8.0 Beta 1
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\AeroScience.Desktop
UninstallAppMutex=Local\AeroScience.Desktop
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no
WizardStyle=modern
SetupIconFile=..\..\src\AeroScience.Engine\wwwroot\branding\aeroscience.ico
SetupLogging=yes
LicenseFile={#PayloadDir}\application\LICENSE
InfoBeforeFile={#PayloadDir}\INSTALL-NOTES.txt
OutputDir={#OutputDirectory}
OutputBaseFilename=AeroScience-Setup-v0.8.0-beta.1
VersionInfoVersion=0.8.0.1
UninstallDisplayName=AeroScience 0.8.0 Beta 1
UninstallDisplayIcon={app}\application\AeroScience.exe
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: desktopicon; Description: "Create a Beta 1 desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VcRuntime}"; DestName: "VC_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\AeroScience 0.8.0 Beta 1\AeroScience 0.8.0 Beta 1"; Filename: "{app}\application\AeroScience.exe"; WorkingDir: "{app}\application"
Name: "{userdesktop}\AeroScience 0.8.0 Beta 1"; Filename: "{app}\application\AeroScience.exe"; WorkingDir: "{app}\application"; Tasks: desktopicon
Name: "{userprograms}\AeroScience 0.8.0 Beta 1\Tester guide"; Filename: "{app}\START-HERE.html"

[Registry]
Root: HKCU; Subkey: "Software\AeroScience\Beta\v0.8.0"; ValueType: string; ValueName: "InstallDirectory"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\application\AeroScience.exe"; Description: "Launch AeroScience 0.8.0 Beta 1"; Flags: postinstall nowait skipifsilent unchecked

[Code]
function WithinPath(Child, Parent: String): Boolean;
begin
  Child := Lowercase(AddBackslash(ExpandFileName(Child)));
  Parent := Lowercase(AddBackslash(ExpandFileName(Parent)));
  Result := Pos(Parent, Child) = 1;
end;

function Overlaps(A, B: String): Boolean;
begin
  Result := (B <> '') and (WithinPath(A, B) or WithinPath(B, A));
end;

function FolderHasContents(Folder: String): Boolean;
var Item: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Folder) + '*', Item) then
  begin
    try
      repeat
        if (Item.Name <> '.') and (Item.Name <> '..') then begin Result := True; Break; end;
      until not FindNext(Item);
    finally FindClose(Item); end;
  end;
end;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function NoReparseAncestors(Folder: String): Boolean;
var Attr: Cardinal; Parent: String;
begin
  Result := True;
  repeat
    Attr := GetFileAttributesW(Folder);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then begin Result := False; Exit; end;
    Parent := ExtractFileDir(RemoveBackslashUnlessRoot(Folder));
    if Parent = Folder then Exit;
    Folder := Parent;
  until Folder = '';
end;

function RegistryConflicts(Key, Folder: String; Depth: Integer): Boolean;
var Names: TArrayOfString; Saved: String; I: Integer;
begin
  Result := False;
  if Depth > 6 then begin Result := True; Exit; end;
  if RegQueryStringValue(HKCU, Key, 'InstallDirectory', Saved) and Overlaps(Folder, Saved) then Result := True;
  if RegQueryStringValue(HKCU, Key, 'MissionDirectory', Saved) and Overlaps(Folder, Saved) then Result := True;
  if RegQueryStringValue(HKCU, Key, 'ScienceLibraryDirectory', Saved) and Overlaps(Folder, Saved) then Result := True;
  if RegQueryStringValue(HKCU, Key, 'CommunityDirectory', Saved) and Overlaps(Folder, Saved) then Result := True;
  if RegGetSubkeyNames(HKCU, Key, Names) then
    for I := 0 to GetArrayLength(Names)-1 do
      if RegistryConflicts(Key + '\' + Names[I], Folder, Depth+1) then Result := True;
end;

function SimulatorClosed: Boolean;
var Locator, Services, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT Name FROM Win32_Process WHERE Name="FlightSimulator2024.exe" OR Name="FlightSimulator.exe" OR Name="fspackagetool.exe"');
    Result := Processes.Count = 0;
  except
    Log('Could not verify simulator/package-builder process state.');
  end;
end;

function InstallationAllowed(Folder: String): Boolean;
begin
  Result := NoReparseAncestors(Folder) and not FolderHasContents(Folder) and
    not Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience')) and
    not Overlaps(Folder, ExpandConstant('{localappdata}\Programs\AeroScience')) and
    not Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Beta-v0.8.0')) and
    not Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Validation-v0.8.0')) and
    not RegistryConflicts('Software\AeroScience', Folder, 0);
end;

function InitializeSetup: Boolean;
var Existing: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\AeroScience\Beta\v0.8.0', 'InstallDirectory', Existing) and
     FileExists(AddBackslash(Existing) + 'application\AeroScience.exe') then
  begin
    MsgBox('An AeroScience 0.8 beta is already installed. This Beta 1 installer will not replace it. Close it and explicitly uninstall the beta app first; its data is retained.', mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := SimulatorClosed;
  if not Result then MsgBox('Close MSFS and its package builder before removing the Beta 1 application. No process was stopped and beta data will be retained.', mbError, MB_OK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Result := InstallationAllowed(WizardDirValue);
    if not Result then MsgBox('Choose an empty Beta 1 application folder, separate from stable installations, validation data and scientific libraries.', mbError, MB_OK);
  end;
end;

function VcRuntimeNeeded: Boolean;
var Installed: Cardinal; Current, Bundled: String; CurrentVersion, BundledVersion: Int64;
begin
  Result := True;
  if RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1) and
     RegQueryStringValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Version', Current) then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
    if GetVersionNumbersString(ExpandConstant('{tmp}\VC_redist.x64.exe'), Bundled) then
    begin
      if (Length(Current) > 0) and ((Current[1] = 'v') or (Current[1] = 'V')) then Delete(Current, 1, 1);
      if StrToVersion(Current, CurrentVersion) and StrToVersion(Bundled, BundledVersion) then Result := ComparePackedVersion(CurrentVersion, BundledVersion) < 0;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  if not InstallationAllowed(ExpandConstant('{app}')) then
  begin
    Result := 'Choose a separate Beta 1 application folder. Existing applications and data were not replaced.'; Exit;
  end;
  if not SimulatorClosed then begin Result := 'Close MSFS and any package builder yourself before installing this candidate. No process was stopped.'; Exit; end;
  if VcRuntimeNeeded then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
    if not ShellExec('runas', ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      Result := 'Microsoft Visual C++ could not start. Approve its administrator prompt and retry.'
    else if (Code = 3010) or (Code = 1641) then NeedsRestart := True
    else if (Code <> 0) and (Code <> 1638) then Result := 'Microsoft Visual C++ installation failed (code ' + IntToStr(Code) + '). No application files were replaced.';
  end;
end;

// No UninstallDelete or automatic toolbar removal: beta data, other installations and Community packages stay intact. Remove only the beta toolbar through beta Settings before uninstalling if desired.
