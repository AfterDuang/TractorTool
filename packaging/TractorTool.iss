; Compile only through Build-Installer.ps1, using a verified ZIP snapshot.
#if VER != EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 is required
#endif

[Setup]
AppId={{EC75DE88-DAD6-461A-A554-10F96F04A865}
AppName=TractorTool
AppVersion={#AppVersion}
VersionInfoVersion={#AppNumericVersion}
DefaultDirName={localappdata}\Programs\TractorTool
DefaultGroupName=TractorTool
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
OutputBaseFilename=TractorTool-{#AppVersion}-win-x64-setup
SetupIconFile=..\TractorTool\Assets\tractortool.ico
UninstallDisplayIcon={app}\TractorTool.exe
WizardStyle=modern
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
AppMutex=Local\TractorTool.Application.Active
SetupMutex=Local\TractorTool.Installer.Active
InfoBeforeFile=InstallerNotes.txt

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "installation.ini"; DestDir: "{app}"; Flags: ignoreversion
#include PayloadInclude

[Icons]
Name: "{autoprograms}\TractorTool"; Filename: "{app}\TractorTool.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\TractorTool"; Filename: "{app}\TractorTool.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Messages]
SetupAppRunningError=TractorTool 正在运行。请从托盘菜单或“设置 → 关于软件”退出后再继续。关闭窗口只会隐藏，后台任务可能仍在运行。
UninstallAppRunningError=TractorTool 正在运行。请从托盘菜单或“设置 → 关于软件”退出后再卸载。
ConfirmUninstall=是否卸载 %1？%n%n插件、配置、下载的依赖和自定义数据目录将保留。

[Code]
const
  InstallationId = 'EC75DE88-DAD6-461A-A554-10F96F04A865';
var
  InstallBundledPlugins: Boolean;
  MaintenanceGate: THandle;
  SeedFinalizationFailed: Boolean;

function GetFileAttributesW(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateMutexW(Attributes: Integer; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

function HasLinks(Dir: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(Dir) + '*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then begin
          Result := (Found.Attributes and $400) <> 0;
          if not Result and ((Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) then
            Result := HasLinks(AddBackslash(Dir) + Found.Name);
          if Result then exit;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

procedure EndMaintenance;
begin
  if MaintenanceGate <> 0 then begin
    ReleaseMutex(MaintenanceGate);
    CloseHandle(MaintenanceGate);
    MaintenanceGate := 0;
  end;
end;

function BeginMaintenance: Boolean;
var
  WaitResult: Cardinal;
begin
  Result := True;
  if MaintenanceGate <> 0 then exit;
  MaintenanceGate := CreateMutexW(0, False, 'Local\TractorTool.Maintenance.Gate');
  Result := MaintenanceGate <> 0;
  if not Result then exit;
  WaitResult := WaitForSingleObject(MaintenanceGate, 0);
  if (WaitResult <> 0) and (WaitResult <> $80) then begin
    CloseHandle(MaintenanceGate);
    MaintenanceGate := 0;
    Result := False;
    exit;
  end;
  Result := not CheckForMutexes('Local\TractorTool.Application.Active');
  if not Result then EndMaintenance;
end;

function InitialSeed(Root: String): AnsiString;
begin
  if DirExists(Root + '\Plugins') then Result := 'keep:{#SeedHash}' else Result := '{#SeedHash}';
end;

function ReadSeed(Name: String; var Seed: AnsiString): Boolean;
var
  Size: Integer;
begin
  Result := FileSize(Name, Size) and ((Size = Length('{#SeedHash}')) or (Size = Length('keep:{#SeedHash}'))) and
    LoadStringFromFile(Name, Seed) and ((Seed = '{#SeedHash}') or (Seed = 'keep:{#SeedHash}'));
end;

function CheckDestination: String;
var
  Root, Parent, DefaultData, Marker: String;
  Seed: AnsiString;
  Attributes: Cardinal;
  Found: TFindRec;
begin
  Result := '';
  Root := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  DefaultData := AddBackslash(ExpandConstant('{localappdata}\TractorTool'));
  if (Length(Root) <= 3) or (CompareText(Copy(AddBackslash(Root), 1, Length(DefaultData)), DefaultData) = 0) then begin
    Result := '请选择独立的软件目录，不能安装到磁盘根目录或应用数据目录。';
    exit;
  end;
  Parent := Root;
  while Length(Parent) > 3 do begin
    Attributes := GetFileAttributesW(Parent);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin
      Result := '安装目录不能经过目录链接，请选择实际目录。';
      exit;
    end;
    Parent := ExtractFileDir(Parent);
  end;
  if HasLinks(Root) then begin
    Result := '安装目录中有文件或目录链接，请选择不包含链接的安装位置。';
    exit;
  end;
  Marker := Root + '\.plugin-seed';
  if DirExists(Marker) or DirExists(Marker + '.new') then begin
    Result := '安装事务标记被目录占用，请检查安装位置。';
    exit;
  end;
  if FileExists(Marker + '.new') then begin
    if FileExists(Marker) or not ReadSeed(Marker + '.new', Seed) or (Seed <> InitialSeed(Root)) then begin
      Result := '首次安装临时记录与当前安装包或目录不符，已保留原文件。';
      exit;
    end;
  end;
  { A matching transaction was recorded only after checking the original destination. }
  if FileExists(Marker) then begin
    if not ReadSeed(Marker, Seed) or ((Seed = '{#SeedHash}') and DirExists(Root + '\Plugins\.management')) then
      Result := '首次安装记录与此安装包不符，或插件已由软件接管。请使用原安装包或选择新的空目录。';
    exit;
  end;
  if GetIniString('TractorTool', 'Id', '', Root + '\installation.ini') = InstallationId then exit;
  if FindFirst(Root + '\*', Found) then begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') and (Found.Name <> '.plugin-seed.new') and
          not ((CompareText(Found.Name, 'Plugins') = 0) and ((Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0)) then begin
          Result := '此目录不是安装器管理的 TractorTool 安装。请选择新空目录，保留原文件。';
          exit;
        end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion, NewVersion: Int64;
  Seed: AnsiString;
  Root, Marker: String;
begin
  if not BeginMaintenance then begin
    Result := '请从托盘菜单或设置显式退出 TractorTool 后重试，不能只关闭窗口。';
    exit;
  end;
  Result := CheckDestination;
  if Result <> '' then begin
    EndMaintenance;
    exit;
  end;
  Root := ExpandConstant('{app}');
  Marker := Root + '\.plugin-seed';
  if GetPackedVersion(ExpandConstant('{app}\TractorTool.dll'), InstalledVersion) and
    StrToVersion('{#AppNumericVersion}', NewVersion) and (ComparePackedVersion(InstalledVersion, NewVersion) > 0) then begin
    Result := '已安装更新版本，请使用相同或更新版本的安装器。';
    exit;
  end;
  InstallBundledPlugins := not DirExists(ExpandConstant('{app}\Plugins'));
  if FileExists(Marker) then begin
    if not ReadSeed(Marker, Seed) then begin
      Result := '首次安装记录已变化，已停止。';
      exit;
    end;
    InstallBundledPlugins := Seed = '{#SeedHash}';
  end else if FileExists(Marker + '.new') or InstallBundledPlugins or
    (GetIniString('TractorTool', 'Id', '', Root + '\installation.ini') <> InstallationId) then begin
    if not ForceDirectories(Root) or
      (not FileExists(Marker + '.new') and not SaveStringToFile(Marker + '.new', InitialSeed(Root), False)) or
      not ReadSeed(Marker + '.new', Seed) or (Seed <> InitialSeed(Root)) or not RenameFile(Marker + '.new', Marker) then
      Result := '无法记录首次安装，请检查安装目录的写入权限后重试。';
  end;
end;

function SeedPlugins: Boolean;
begin
  Result := InstallBundledPlugins;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if FileExists(ExpandConstant('{app}\.plugin-seed')) and not DeleteFile(ExpandConstant('{app}\.plugin-seed')) then
      SeedFinalizationFailed := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and SeedFinalizationFailed then
    WizardForm.FinishedLabel.Caption := '文件已复制，但首次安装收尾失败。请使用同一安装包重试，完成前软件不会启动。';
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if SeedFinalizationFailed then Result := 1;
end;

procedure DeinitializeSetup;
begin
  EndMaintenance;
end;

function InitializeUninstall: Boolean;
var
  Reason: String;
begin
  Reason := CheckDestination;
  Result := Reason = '';
  if not Result then begin
    MsgBox(Reason, mbError, MB_OK);
    exit;
  end;
  Result := BeginMaintenance;
  if not Result then
    MsgBox('请退出 TractorTool 或其他安装进程后再卸载。', mbError, MB_OK);
end;

procedure DeinitializeUninstall;
begin
  EndMaintenance;
end;
