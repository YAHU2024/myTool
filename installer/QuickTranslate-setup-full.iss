; QuickTranslate Installer — 完整版（自包含，~150MB）
; 已内置 .NET 8 运行时，普通用户双击即用
; 编译：ISCC QuickTranslate-setup-full.iss

#define MyAppName "QuickTranslate"
#define MyAppVersion "1.6.0"
#define MyAppPublisher "YaHu"
#define MyAppURL "https://github.com/YAHU2024/myTool"
#define MyAppExeName "QuickTranslate.exe"
#define MyAppIcoName "QuickTranslate.ico"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\publish\releases\v1.6.0
OutputBaseFilename=QuickTranslate-Setup-{#MyAppVersion}-win-x64-full
SetupIconFile=..\QuickTranslate\Assets\{#MyAppIcoName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
DisableWelcomePage=no
VersionInfoDescription=完整自包含版（已内置 .NET 8 运行时，双击即用）

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "快捷方式:"; Flags: checkedonce
Name: "autostart"; Description: "开机自动运行(&A)"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "..\publish\source\v1.6.0-full\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\QuickTranslate\Assets\{#MyAppIcoName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcoName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppIcoName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppIcoName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 QuickTranslate(&R)"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#MyAppExeName}"; Flags: runhidden

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('autostart') then
      RegWriteStringValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        '{#MyAppName}', ExpandConstant('{app}\{#MyAppExeName}'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
end;
