# P1-2 手工验证计划：管理员权限自动更新独立信任链

> 状态：待执行  
> 关联：`BUGFIX_PLAN.md` → P1-2  
> 自动化基础：单元测试 221/221 通过（含 16 项 AuthenticodeVerifier 相关测试）

---

## 两种运行模式

自动更新的 Authenticode 验证由 `AppSettings.RequireAuthenticodeSignature` 控制：

| 配置值 | 模式 | 签名无效时 | 适用场景 |
|:-------|:-----|:-----------|:---------|
| `false`（**默认**） | 咨询模式 | 记录警告日志，**继续安装** | 暂未购买证书期间的日常使用 |
| `true` | 严格模式 | **中止安装**并显示明确错误 | 持有代码签名证书后启用 |

> 本验证计划的阶段 A/B 可在咨询模式下准确验证 AuthenticodeVerifier 的判断逻辑。
> 阶段 C 需要先购买证书并设置 `RequireAuthenticodeSignature = true`。

---

## 验证总览

| 阶段 | 目标 | 前置条件 | 预计耗时 |
|:-----|:-----|:---------|:---------|
| **A — 自签名证书测试** | 端到端验证 AuthenticodeVerifier 判断正确性 | Windows SDK (signtool)、管理员权限 | 10 分钟 |
| **B — 微信/HTTP 模拟更新** | 验证下载→校验→签名验证→拒绝 完整流程 | 阶段 A 环境、本地 HTTP 服务 | 20 分钟 |
| **C — 真实证书端到端测试** | 真实 Authenticode 签名的完整自动更新 | 代码签名证书（购买后） | 30 分钟 |

---

## 阶段 A：自签名证书测试（可立即执行）

### 前提条件

- [x] 代码已编译通过（`dotnet build`，0 错误）
- [x] 自动化测试全通过（`dotnet test`，219/219）
- [ ] Windows SDK 已安装（需 `signtool.exe`）
- [ ] 以**管理员身份**运行 PowerShell（安装证书到 Trusted Root 需要）

### 执行步骤

#### A1：构建项目

```powershell
cd d:\YaHu\Documents\myTool
dotnet build QuickTranslate\QuickTranslate.csproj -c Debug
```

#### A2：运行自动化验证脚本

```powershell
# 以管理员身份运行 PowerShell，然后：
powershell -ExecutionPolicy Bypass -File scripts\test-authenticode.ps1
```

脚本自动完成：
1. 生成自签名代码签名证书（Subject: `CN=YaHu, O=YaHu`）
2. 用证书签名测试 EXE
3. 临时安装证书到 Trusted Root
4. 运行 3 项 xUnit 测试：
   - **Test A**：签名文件 → 预期 `Valid`（验证正向路径）
   - **Test B**：无签名文件 → 预期 `NotSigned`（验证拒绝路径）
   - **Test C**：签名文件 + 错误发布者 → 预期失败（验证发布者匹配）
5. 自动清理证书和临时文件

#### A3：验证结果判读

| 测试 | 预期结果 | 验证内容 |
|:-----|:---------|:---------|
| Test A | ✅ 通过 | Authenticode 签名被正确识别为 `Valid`，证书链验证通过 |
| Test B | ✅ 通过 | 无签名文件返回 `NotSigned`，不降级为 `Valid` |
| Test C | ❌ 失败 | ManualVerify_SignedFile 断言 Valid，但实际应返回 PublisherMismatch — 证明发布者不匹配被正确检测 |

> Test C 的 "失败" 是预期的——说明发布者不匹配时系统拒绝继续。
> 严格验证需等阶段 C（真实证书）或手动构造 `ManualVerify` 专用断言。

### 阶段 A 验收标准

- [ ] Test A（签名文件）返回 `Valid`
- [ ] Test B（无签名文件）返回 `NotSigned`
- [ ] 从 Cert:\CurrentUser\Root 确认测试证书已正确安装和清理
- [ ] 218 项非 skip 测试全通过（`dotnet test`，不含 ManualVerify_*）

---

## 阶段 B：本地 HTTP 模拟完整更新流程

验证从"发现新版本"到"签名验证后启动/拒绝"的完整用户可见路径。

### B1：启动本地 HTTP 文件服务器

```powershell
# 使用 PowerShell 内置 HTTP 监听器（不需要额外安装）
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://localhost:9999/")
$listener.Start()
Write-Host "Serving files from publish\releases\v1.8.1"

# 使用 Python（如果已安装）更简单：
# python -m http.server 9999 -d publish\releases\v1.8.1
```

### B2：准备测试文件

```powershell
$ver = "1.8.1"
$testDir = "publish\releases\v$ver"

# 复制已签名的安装包（或构造测试包）
Copy-Item $env:TEMP\QuickTranslate-AuthTest-*\test-update-installer.exe $testDir\ -Force
Rename-Item "$testDir\test-update-installer.exe" "QuickTranslate-Setup-$ver-win-x64-full.exe"

# 更新 version.xml 指向本地服务器
@"
<?xml version="1.0" encoding="UTF-8"?>
<item>
  <version>99.99.99</version>
  <url>http://localhost:9999/QuickTranslate-Setup-$ver-win-x64-full.exe</url>
  <changelog>http://localhost:9999/CHANGELOG.md</changelog>
  <args>/SILENT /SUPPRESSMSGBOXES /NORESTART</args>
  <checksum algorithm="SHA256">$( (Get-FileHash "$testDir\QuickTranslate-Setup-$ver-win-x64-full.exe" -Algorithm SHA256).Hash )</checksum>
  <mandatory>false</mandatory>
  <signer>
    <subject>YaHu</subject>
  </signer>
</item>
"@ | Out-File -FilePath "$testDir\version.xml" -Encoding utf8
```

### B3：修改应用指向本地更新源

临时修改 `UpdateService.cs` 中的 `UpdateXmlUrl`（仅用于测试）：

```csharp
private const string UpdateXmlUrl = "http://localhost:9999/version.xml";
```

编译并运行应用，触发"检查更新"：

### B4：测试 4 种场景

> **注意**：场景 2-3 的"阻止安装"行为仅在 `RequireAuthenticodeSignature = true`（严格模式）时发生。
> 默认的咨询模式下，无签名/发布者不匹配仅记录日志并继续安装。
> 测试时需先修改 `settings.json` 或代码中的默认值来启用严格模式。

| 场景 | 操作 | `RequireAuthenticodeSignature = true` 预期行为 | `= false`（默认） |
|:-----|:-----|:---------|:---------|
| **正常签名安装包** | 签名文件 + 正确发布者 | 通过 → 安装 | 通过 → 安装 |
| **无签名安装包** | 未签名文件 | 失败 → **阻止**安装 | 日志警告 → 继续安装 |
| **错误发布者签名** | 用不同证书签名 | PublisherMismatch → **阻止** | 日志警告 → 继续安装 |
| **SHA256 不匹配** | 下载后文件损坏 | "校验失败" → **总是阻止**（不受开关影响） | 同左 |

> 注：场景 2-4 需要手动构造对应的安装包文件。

### 阶段 B 验收标准

- [ ] 合法签名包可完成下载 → 校验 → 签名验证 → UAC 弹窗
- [ ] 下载进度窗口显示正确的百分比和状态文字
- [ ] 签名验证失败时显示明确的中文错误信息（非堆栈跟踪）
- [ ] 取消按钮在下载阶段可正常工作
- [ ] 签名验证失败后**不启动任何安装程序**（任务管理器确认无 installer 进程）

---

## 阶段 C：真实证书端到端测试（需代码签名证书）

### 前置条件

- [ ] 已获取正式代码签名证书（DigiCert / Sectigo / GlobalSign）
- [ ] 证书 Subject 包含 "YaHu"（或同步更新 `UpdateService.ExpectedPublisher`）
- [ ] 签名工作站已配置 signtool + 时间戳服务器

### C1：签名发布安装包

按 `docs/RELEASE.md` 第三步B 流程签名两个安装包：

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
  /f <certificate.pfx> /d "QuickTranslate Setup vX.Y.Z" `
  publish\releases\vX.Y.Z\QuickTranslate-Setup-X.Y.Z-win-x64-full.exe

signtool verify /pa /v publish\releases\vX.Y.Z\QuickTranslate-Setup-X.Y.Z-win-x64-full.exe
```

### C2：创建测试 Release（Draft）

```powershell
gh release create vX.Y.Z-test `
  --draft `
  --title "vX.Y.Z — Authenticode Trust Chain Test" `
  --notes "签名验证测试" `
  publish\releases\vX.Y.Z\QuickTranslate-Setup-X.Y.Z-win-x64-full.exe `
  installer\version.xml
```

### C3：修改 UpdateXmlUrl 指向测试 Release

```csharp
private const string UpdateXmlUrl =
    "https://github.com/YAHU2024/myTool/releases/download/vX.Y.Z-test/version.xml";
```

### C4：执行完整更新流程

在干净的 Windows 虚拟机或测试机上：
1. 安装当前版本 QuickTranslate
2. 触发"检查更新"
3. 观察完整流程：通知 → 下载 → SHA256 校验 → Authenticode 验证 → UAC 弹窗 → 安装 → 重启

### C5：测试拒绝场景

| 场景 | 构造方式 | 预期 |
|:-----|:---------|:-----|
| **签名损坏** | 用十六进制编辑器修改签名后的 exe 的 1 个字节 | 签名验证失败 → 不执行 |
| **发布者不匹配** | 修改 `UpdateService.ExpectedPublisher` 为错误值 | PublisherMismatch → 不执行 |
| **不满足的 SHA256** | 修改 `version.xml` 的 `<checksum>` | 校验失败 → 不执行 |

### 阶段 C 验收标准

- [ ] 正常签名包：完整更新链路通过（下载 → 校验 → 签名 → UAC → 安装 → 应用重启）
- [ ] 未签名包：被 Authenticode 验证拦截，不启动安装程序
- [ ] 签名损坏包：被验证拦截
- [ ] 发布者不匹配包：被拦截，显示发布者错误
- [ ] SHA256 不匹配：被拦截，显示校验错误
- [ ] 所有错误场景均显示中文友好提示，不泄露堆栈跟踪
- [ ] 安装完成后应用自动重启正常工作
- [ ] 旧版配置文件和数据在新版本中完好无损

---

## 阶段 D：回归清单（所有阶段完成后）

无论哪个阶段，变更后均需执行完整回归：

### 自动化

```powershell
dotnet build QuickTranslate\QuickTranslate.csproj        # 0 错误
dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj  # 全部通过
dotnet list QuickTranslate\QuickTranslate.csproj package --vulnerable --include-transitive  # 无新增漏洞
```

### 手工回归（在测试机上）

- [ ] 托盘图标正常显示
- [ ] 划词翻译 / 快捷键正常工作
- [ ] 设置窗口打开、保存正常
- [ ] 历史记录读写正常
- [ ] TTS 朗读正常
- [ ] 窗口拖动、缩放正常
- [ ] 多显示器 DPI 混排正常
- [ ] 剪贴板恢复正常
- [ ] 流式翻译取消正常

---

## 故障排查

### signtool 找不到

```powershell
# 确认 Windows SDK 已安装
# 典型路径：C:\Program Files (x86)\Windows Kits\10\bin\10.0.xxxxx.0\x64\signtool.exe

# 或在 Visual Studio Developer PowerShell 中运行
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\Launch-VsDevShell.ps1"
```

### 脚本报 "Certificate enrolment denied"

需要以管理员身份运行 PowerShell 安装证书到 Trusted Root。

### ManualVerify_SignedFile 被 Skip

检查环境变量是否正确设置：

```powershell
$env:AUTHENTICODE_TEST_FILE = "C:\Users\...\test-update-installer.exe"
dotnet test --filter "ManualVerify" -v n
```

### 下载超时

检查防火墙/代理设置，确保 `HttpClient` 可访问测试 HTTP 服务器。

---

## 总结

| 阶段 | 可执行时间 | 验证覆盖 |
|:-----|:-----------|:---------|
| A | **现在**（无需证书） | AuthenticodeVerifier 判断逻辑的完整正确性 |
| B | **现在**（需配置本地服务器） | 下载 → 校验 → 签名验证 UI 流程 |
| C | **购买证书后** | 真实 Authenticode + UAC + 完整安装 |
| D | **每次变更后** | 确保无回归 |
