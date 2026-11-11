; Private tester packaging only. Compile through Build-BetaInstaller.ps1.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef VcRuntime
  #error VcRuntime is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef BetaVersion
  #define BetaVersion "0.7.0-beta.1"
#endif
#if BetaVersion == "0.7.1-beta.2"
  #define BetaName "AeroScience 0.7.1-beta.2"
  #define BetaAppId "{{54CD1188-2D18-4E77-B8F5-CAD530C332F6}"
  #define BetaNumericVersion "0.7.1.2"
  #define OwnRegistryIndex 3
#elif BetaVersion == "0.7.0-beta.1"
  #define BetaName "AeroScience 0.7.0 Beta 1"
  #define BetaAppId "{{6F9DE035-3672-4865-8EE2-E8E38573D14A}"
  #define BetaNumericVersion "0.7.0.1"
  #define OwnRegistryIndex 2
#else
  #error Unsupported private beta identity
#endif

[Setup]
AppId={#BetaAppId}
AppName={#BetaName}
AppVersion={#BetaVersion}
AppVerName={#BetaName}
AppPublisher=AeroScience contributors
AppSupportURL=https://github.com/JustSeanC/AeroScience/issues
DefaultDirName={localappdata}\Programs\AeroScience-Beta-v{#BetaVersion}
DefaultGroupName={#BetaName}
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
WizardStyle=modern
SetupIconFile=..\..\src\AeroScience.Engine\wwwroot\branding\aeroscience.ico
SetupLogging=yes
LicenseFile={#PayloadDir}\application\LICENSE
InfoBeforeFile={#PayloadDir}\START-HERE.md
OutputDir={#OutputDirectory}
OutputBaseFilename=AeroScience-Setup-v{#BetaVersion}
VersionInfoVersion={#BetaNumericVersion}
VersionInfoProductVersion={#BetaNumericVersion}
VersionInfoProductTextVersion={#BetaVersion}
VersionInfoTextVersion={#BetaVersion}
UninstallDisplayName={#BetaName}
UninstallDisplayIcon={app}\application\AeroScience.exe
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: desktopicon; Description: "Create an {#BetaName} desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VcRuntime}"; DestName: "VC_redist.x64.exe"; Flags: dontcopy

[Icons]
Name: "{userprograms}\{#BetaName}\{#BetaName}"; Filename: "{app}\application\AeroScience.exe"; WorkingDir: "{app}\application"
Name: "{userdesktop}\{#BetaName}"; Filename: "{app}\application\AeroScience.exe"; WorkingDir: "{app}\application"; Tasks: desktopicon
Name: "{userprograms}\{#BetaName}\Tester guide"; Filename: "{app}\START-HERE.md"

[Registry]
Root: HKCU; Subkey: "Software\AeroScience\Beta\v{#BetaVersion}"; ValueType: string; ValueName: "InstallDirectory"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\application\AeroScience.exe"; Description: "Launch {#BetaName}"; Flags: postinstall nowait skipifsilent

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

function InstallationAllowed(Folder: String): Boolean;
var Saved: String; I, J: Integer; Metadata: AnsiString; ExistingBeta: Boolean;
    Keys, Values: TArrayOfString;
begin
  Result := True;
  Result := not (Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience')) or
    Overlaps(Folder, ExpandConstant('{localappdata}\Programs\AeroScience')) or
    Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Validation-v0.5.0')) or
    Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Validation-v0.6.0')) or
    Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Validation-v0.7.0')) or
    Overlaps(Folder, ExpandConstant('{localappdata}\AeroScience-Beta-v0.7.1-beta.2')) or
#if BetaVersion == "0.7.1-beta.2"
    Overlaps(Folder, ExpandConstant('{localappdata}\Programs\AeroScience-Beta-v0.7.0-beta.1')) or
#endif
    FileExists(AddBackslash(Folder) + 'missions.db') or FileExists(AddBackslash(Folder) + 'library.json'));
  SetArrayLength(Keys, 4); SetArrayLength(Values, 3);
  Keys[0] := 'Software\AeroScience'; Keys[1] := 'Software\AeroScience\Validation\v0.7.0'; Keys[2] := 'Software\AeroScience\Beta\v0.7.0-beta.1';
  Keys[3] := 'Software\AeroScience\Beta\v0.7.1-beta.2';
  Values[0] := 'InstallDirectory'; Values[1] := 'MissionDirectory'; Values[2] := 'ScienceLibraryDirectory';
  for I := 0 to 3 do
    for J := 0 to 2 do
      if not ((I = {#OwnRegistryIndex}) and (J = 0)) then
        if RegQueryStringValue(HKCU, Keys[I], Values[J], Saved) and Overlaps(Folder, Saved) then Result := False;
  if not Result then
  begin
    Exit;
  end;
  ExistingBeta := FileExists(AddBackslash(Folder) + 'application\AeroScience.exe') and FileExists(AddBackslash(Folder) + 'application\AeroScience.runtimeconfig.json');
  if ExistingBeta then ExistingBeta := LoadStringFromFile(AddBackslash(Folder) + 'BETA-BUILD.json', Metadata);
  if ExistingBeta then ExistingBeta := Pos('"version": "{#BetaVersion}"', Metadata) > 0;
  if FolderHasContents(Folder) and not ExistingBeta then
  begin
    Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Result := InstallationAllowed(WizardDirValue);
    if not Result then MsgBox('Choose an empty {#BetaName} application folder, separate from existing installations, validation data and scientific libraries.', mbError, MB_OK);
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
    Result := 'Choose a separate {#BetaName} application folder. Existing applications and data were not replaced.'; Exit;
  end;
  if VcRuntimeNeeded then
  begin
    ExtractTemporaryFile('VC_redist.x64.exe');
    if not ShellExec('runas', ExpandConstant('{tmp}\VC_redist.x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
      Result := 'Microsoft Visual C++ could not start. Approve its administrator prompt and retry.'
    else if (Code = 3010) or (Code = 1641) then NeedsRestart := True
    else if (Code <> 0) and (Code <> 1638) then Result := 'Microsoft Visual C++ installation failed (code ' + IntToStr(Code) + '). No application files were replaced.';
  end;
end;

// No UninstallDelete or automatic toolbar removal: validation data and shared toolbar stay intact.
