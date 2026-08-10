const
  WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2BootstrapperURL = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2BootstrapperName = 'MicrosoftEdgeWebview2Setup.exe';

function HasWebView2Version(RootKey: Integer): Boolean;
var
  Version: string;
begin
  Result := RegQueryStringValue(RootKey, WebView2ClientKey, 'pv', Version) and
            (CompareText(Trim(Version), '0.0.0.0') <> 0) and
            (Trim(Version) <> '');
end;

function IsWebView2RuntimeInstalled: Boolean;
begin
  Result := HasWebView2Version(HKLM32) or
            HasWebView2Version(HKLM64) or
            HasWebView2Version(HKCU);
end;

function TryInstallWebView2Runtime: string;
var
  BootstrapperPath: string;
  ExitCode: Integer;
begin
  Result := '';
  BootstrapperPath := ExpandConstant('{tmp}\') + WebView2BootstrapperName;

  try
    Log('WebView2 Runtime not found; downloading the Microsoft Evergreen Bootstrapper.');
    DownloadTemporaryFile(
      WebView2BootstrapperURL,
      WebView2BootstrapperName,
      '',
      nil);

    if not Exec(
      BootstrapperPath,
      '/silent /install',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ExitCode) then
    begin
      Result := '无法启动 Microsoft Edge WebView2 Runtime 安装程序。';
      exit;
    end;

    if (ExitCode <> 0) or not IsWebView2RuntimeInstalled then
      Result := Format('Microsoft Edge WebView2 Runtime 安装未完成（退出代码：%d）。', [ExitCode]);
  except
    Result := 'Microsoft Edge WebView2 Runtime 下载或安装失败：' + GetExceptionMessage;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
var
  RuntimeError: string;
begin
  Result := '';
  if IsWebView2RuntimeInstalled then
  begin
    Log('WebView2 Runtime detected.');
    exit;
  end;

  RuntimeError := TryInstallWebView2Runtime;
  if RuntimeError = '' then
  begin
    Log('WebView2 Runtime installation completed.');
    exit;
  end;

  Log('WebView2 Runtime prerequisite warning: ' + RuntimeError);
  if WizardSilent then
  begin
    { Updating must remain possible because the app can open release notes in the system browser. }
    Log('Silent install will continue; QuickTranslate will use its browser fallback.');
    exit;
  end;

  if MsgBox(
    RuntimeError + #13#10 + #13#10 +
    'QuickTranslate 仍可安装，但应用内更新说明将改用系统浏览器打开。是否继续安装？',
    mbConfirmation,
    MB_YESNO) <> IDYES then
    Result := RuntimeError;
end;
