; QuickTranslate Installer — 轻量版（框架依赖，含本地词典，~47MB）
; 用户需预先安装 .NET 8 运行时
; 编译：ISCC QuickTranslate-setup.iss
;
; ─── 代码签名（Authenticode） ──────────────────────────────────────
; 安装程序编译后必须进行 Authenticode 签名，以建立独立信任链。
; 签名私钥必须离线保管，不得进入仓库、CI 日志或构建产物。
;
; 签名命令（在 ISCC 编译完成后执行）：
;   signtool sign /fd SHA256 ^
;     /tr http://timestamp.digicert.com /td SHA256 ^
;     /f <certificate.pfx> /p <password> ^
;     /d "QuickTranslate Setup" ^
;     <OutputDir>\QuickTranslate-Setup-{version}-win-x64[-full].exe
;
; 签名后验证：
;   signtool verify /pa /v <path-to-installer>.exe
;
; 发布前检查清单：
;   □ 两个安装包均已签名（轻量版 + 完整版）
;   □ 签名时间戳有效（signtool verify 通过）
;   □ 证书 Subject 包含 "YaHu"（与 UpdateService.ExpectedPublisher 一致）
;   □ version.xml 的 <signer><subject> 匹配证书 Subject
;   □ 未签名的安装包不得上传到 GitHub Release
;
; 证书到期前 30 天开始续期流程，详见 docs/RELEASE.md

#define MyAppName "QuickTranslate"
#ifndef MyAppVersion
#define MyAppVersion "1.8.7"
#endif
#define MyAppPublisher "YaHu"
#define MyAppURL "https://github.com/YAHU2024/myTool"
#define MyAppExeName "QuickTranslate.exe"
#define MyAppIcoName "QuickTranslate.ico"
#define DotnetURL "https://dotnet.microsoft.com/download/dotnet/8.0"

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
OutputDir=..\publish\releases\v{#MyAppVersion}
OutputBaseFilename=QuickTranslate-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\QuickTranslate\Assets\{#MyAppIcoName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
CloseApplications=force
DisableWelcomePage=no
VersionInfoDescription=标准版（框架依赖，需已安装 .NET 8 运行时，体积小）

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "快捷方式:"; Flags: checkedonce
Name: "autostart"; Description: "开机自动运行(&A)"; GroupDescription: "启动选项:"; Flags: unchecked

[Files]
Source: "..\publish\source\v{#MyAppVersion}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\QuickTranslate\Assets\{#MyAppIcoName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppIcoName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppIcoName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#MyAppIcoName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 QuickTranslate(&R)"; Flags: postinstall nowait shellexec

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#MyAppExeName}"; Flags: runhidden

[Code]
const
  Net8Key = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App\8.0.0';
  Net8KeyWOW = 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App\8.0.0';

function IsDotNet8Installed: Boolean;
var
  Value: string;
begin
  Result := RegQueryStringValue(HKLM, Net8Key, 'Version', Value) or
            RegQueryStringValue(HKLM, Net8KeyWOW, 'Version', Value);
  if not Result then
    Result := RegQueryStringValue(HKCU, Net8Key, 'Version', Value) or
              RegQueryStringValue(HKCU, Net8KeyWOW, 'Version', Value);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  if not IsDotNet8Installed then
  begin
    if MsgBox('此程序需要 .NET 8 运行时。是否前往微软官网下载并安装？' + #13#13 +
              '安装完成后请重新运行本安装程序。' + #13#13 +
              '提示：也可下载「完整版」安装包（自带运行时，无需额外安装）。',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', '{#DotnetURL}', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end
  else
    Result := True;
end;

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
