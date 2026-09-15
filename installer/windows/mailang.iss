#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\installer\publish"
#endif

[Setup]
AppId={{91C77874-7DE1-4E75-A769-796ED8385FE6}
AppName=MAILang
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\MAILang
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=mailang-{#AppVersion}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
UninstallDisplayIcon={app}\mailang.exe
LicenseFile=..\..\LICENSE

[Files]
Source: "{#PublishDir}\Mail.Cli.exe"; DestDir: "{app}"; DestName: "mailang.exe"; Flags: ignoreversion

[Code]
const
  OwnershipKey = 'Software\MAILang\Installer';

function NormalizePath(Value: String): String;
begin
  Value := Trim(Value);
  if (Length(Value) >= 2) and (Value[1] = '"') and
     (Value[Length(Value)] = '"') then
    Value := Copy(Value, 2, Length(Value) - 2);
  Result := RemoveBackslashUnlessRoot(Value);
end;

function IsInstallPath(Value: String): Boolean;
begin
  Result := CompareText(NormalizePath(Value), NormalizePath(ExpandConstant('{app}'))) = 0;
end;

function HasInstallPath(Value: String): Boolean;
var
  Separator: Integer;
  Part: String;
begin
  Result := False;
  repeat
    Separator := Pos(';', Value);
    if Separator = 0 then Part := Value
    else Part := Copy(Value, 1, Separator - 1);
    if IsInstallPath(Part) then begin
      Result := True;
      Exit;
    end;
    if Separator = 0 then Exit;
    Delete(Value, 1, Separator);
  until False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  UserPath: String;
begin
  if CurStep <> ssPostInstall then Exit;
  RegQueryStringValue(HKCU, 'Environment', 'Path', UserPath);
  if HasInstallPath(UserPath) then Exit;
  if (UserPath <> '') and (UserPath[Length(UserPath)] <> ';') then
    UserPath := UserPath + ';';
  if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path',
    UserPath + ExpandConstant('{app}')) then
    RaiseException('Could not add MAILang to the user PATH.');
  RegWriteDWordValue(HKCU, OwnershipKey, 'AddedToPath', 1);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserPath, Part, NewPath: String;
  Added, Separator: Cardinal;
  First: Boolean;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if not RegQueryDWordValue(HKCU, OwnershipKey, 'AddedToPath', Added) then Exit;
  if Added <> 1 then Exit;
  if RegQueryStringValue(HKCU, 'Environment', 'Path', UserPath) then begin
    NewPath := '';
    First := True;
    repeat
      Separator := Pos(';', UserPath);
      if Separator = 0 then Part := UserPath
      else Part := Copy(UserPath, 1, Separator - 1);
      if not IsInstallPath(Part) then begin
        if not First then NewPath := NewPath + ';';
        NewPath := NewPath + Part;
        First := False;
      end;
      if Separator = 0 then Break;
      Delete(UserPath, 1, Separator);
    until False;
    if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath) then
      RaiseException('Could not remove MAILang from the user PATH.');
  end;
  RegDeleteValue(HKCU, OwnershipKey, 'AddedToPath');
  RegDeleteKeyIfEmpty(HKCU, OwnershipKey);
end;
