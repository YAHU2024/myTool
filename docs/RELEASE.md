# QuickTranslate 发布流程

> 本文档描述从代码到 GitHub Release 的完整发布步骤，适用于所有版本迭代。

---

## 目录

- [前置准备](#前置准备)
- [第一步：更新版本号](#第一步更新版本号)
- [第二步：编译发布产物](#第二步编译发布产物)
- [第三步：生成安装程序](#第三步生成安装程序)
- [第三步B：对安装程序进行 Authenticode 签名](#第三步b对安装程序进行-authenticode-签名)
- [第四步：更新文档与提交](#第四步更新文档与提交)
- [第五步：更新 version.xml 并创建 GitHub Release](#第五步更新-versionxml-并创建-github-release)
- [第六步：证书与信任链管理](#第六步证书与信任链管理)
- [完整命令速查](#完整命令速查)
- [常见问题](#常见问题)

---

## 前置准备

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Python 3（用于从 ECDICT/OEWN 源文件生成发布词典）
- [Inno Setup 6+](https://jrsoftware.org/download.php/is.exe)（用于生成安装程序）
- [GitHub CLI](https://cli.github.com/)（`gh`，需登录 `gh auth login`）
- [Windows SDK SignTool](https://developer.microsoft.com/windows/downloads/windows-sdk/)（用于 Authenticode 签名，通常随 Visual Studio 安装）
- 代码签名证书（`.pfx` 文件，私钥离线保管）

> 发布前确认文档一致性：`python scripts/update-readme-tree.py --check`（README 项目结构由脚本维护，漂移会导致 CI 失败）。

---

## 第一步：更新版本号

### 1.1 修改 csproj

打开 `QuickTranslate\QuickTranslate.csproj`，将版本号改为新版本（以 v1.8.0 为例）：

```xml
<Version>1.8.0</Version>
<AssemblyVersion>1.8.0.0</AssemblyVersion>
<FileVersion>1.8.0.0</FileVersion>
```

### 1.2 修改安装程序脚本

打开以下两个文件，将 `#define MyAppVersion` 改为新版本：

- `installer\QuickTranslate-setup.iss`（轻量版）
- `installer\QuickTranslate-setup-full.iss`（完整版）

同时确认两个脚本中的 `OutputDir` 路径指向新版本目录。

---

## 第二步：编译发布产物

项目同时交付两种安装包，因此需要编译两类源文件。

### 2.1 准备本地词典

将已校验版本的 `ecdict.csv` 和 `oewn-2025-json.zip` 放在 `.build-output\word-dict-mini\`，然后执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\prepare-word-dictionary.ps1
```

脚本会核对固定 SHA-256，并原子生成 `QuickTranslate\Data\word-dictionary.db`。该数据库被 Git 忽略，但后续两个 `dotnet publish` 都会把它复制到 `Data\`；`THIRD_PARTY_NOTICES.md` 也会随包发布。缺少数据库时 publish 仍可成功，但发布包将只能使用 AI 查词，因此正式发布前必须检查这两个文件。

### 2.2 编译轻量版源文件（框架依赖，含词典）

```powershell
dotnet publish QuickTranslate\QuickTranslate.csproj `
  -c Release `
  -o publish\source\v1.8.0
```

### 2.3 编译完整版源文件（自包含，含词典）

```powershell
dotnet publish QuickTranslate\QuickTranslate.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o publish\source\v1.8.0-full

# 完整版重新分发 .NET Runtime，必须携带当前发布 SDK 的许可证与第三方声明
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
Copy-Item (Join-Path $dotnetRoot "LICENSE.txt") `
  publish\source\v1.8.0-full\DOTNET-LICENSE.txt
Copy-Item (Join-Path $dotnetRoot "ThirdPartyNotices.txt") `
  publish\source\v1.8.0-full\DOTNET-THIRD-PARTY-NOTICES.txt
```

两个发布目录都必须包含项目级 `THIRD_PARTY_NOTICES.md`；完整版还必须包含
`DOTNET-LICENSE.txt` 与 `DOTNET-THIRD-PARTY-NOTICES.txt`。升级 NuGet 依赖或
.NET SDK 后，应重新执行 `dotnet list QuickTranslate\QuickTranslate.csproj package --include-transitive`
并核对声明文件，不得直接沿用旧版本清单。

### 2.4 创建发布目录 + 打包 zip

```powershell
$ver = "1.8.0"
New-Item publish\releases\v$ver -ItemType Directory -Force

# 轻量版 zip
Compress-Archive -Path publish\source\v$ver\* `
  -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip

# 完整版 zip（自包含，免运行时）
Compress-Archive -Path publish\source\v$ver-full\* `
  -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip
```

---

## 第三步：生成安装程序

### 3.1 脚本说明

两个独立的 `.iss` 脚本，各自编译：

| 脚本 | 产物 | 体积 | 适用人群 |
|:-----|:-----|:-----|:---------|
| `QuickTranslate-setup.iss` | `Setup-{ver}-win-x64.exe` | ~47 MB | 已安装 .NET 8 的专业用户 |
| `QuickTranslate-setup-full.iss` | `Setup-{ver}-win-x64-full.exe` | ~85 MB | 普通用户，双击安装即用 |

### 3.2 编译安装程序

两个安装器都会在安装前检查 Microsoft Edge WebView2 Evergreen Runtime。
如果 Runtime 缺失，安装器会从微软官方 HTTPS 地址下载架构自适应的
Evergreen Bootstrapper，并使用 `/silent /install` 安装。Bootstrapper 不进入
仓库或发布源，避免提交和长期分发过期的第三方二进制文件。

交互安装中，如果下载或安装失败，用户可选择继续；应用会保留用系统浏览器
查看更新说明的降级路径。静默自动更新不会因 WebView2 安装失败而中断，失败
详情写入安装日志。发布验收必须分别覆盖 Runtime 已安装、缺失后安装成功、
断网失败三种环境。

```powershell
# 标准版（轻量，需用户已有 .NET 8）
ISCC installer\QuickTranslate-setup.iss

# 完整版（自包含，普通用户双击即用）
ISCC installer\QuickTranslate-setup-full.iss
```

### 3.3 编译后完整性校验（必做）

编译产物若被任何工具后处理（例如清空版本资源、截断尾部数据），Inno Setup
内嵌的 CRC 校验会失效，运行时报 `The setup files are corrupted. Please obtain
a new copy of the program.`，自动更新将全线失败。发布前必须执行以下校验，
**任何一步失败都禁止上传到 GitHub Release**：

```powershell
$ver = "1.9.2"
$files = @(
  "publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe",
  "publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe"
)
foreach ($f in $files) {
  if (-not (Test-Path $f)) { Write-Error "缺失: $f"; exit 1 }
  $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $f))
  if ($vi.ProductVersion -ne $ver) {
    Write-Error "完整性校验失败: $f ProductVersion='$($vi.ProductVersion)' (期望 $ver)。安装包可能被后处理工具修改，禁止发布。"
    exit 1
  }
  if ($vi.CompanyName -ne "YaHu") {
    Write-Error "完整性校验失败: $f CompanyName='$($vi.CompanyName)' (期望 YaHu)。"
    exit 1
  }
  $sizeMB = [math]::Round((Get-Item $f).Length / 1MB, 1)
  Write-Host "  PASS: $f ($sizeMB MB, ProductVersion=$($vi.ProductVersion))"
}
```

通过标准：
- 两个文件均输出 `PASS`
- `ProductVersion` 必须等于 `MyAppVersion`，`CompanyName` 必须等于 `MyAppPublisher`
- 安装包未签名（`Get-AuthenticodeSignature` 显示 `NotSigned`）属正常，不影响本校验

---

## 第三步B：确认 Authenticode 模式并签名

Authenticode 是自动更新的独立信任链，是否强制签名取决于当前发布模式：

- **咨询模式**：尚未购买代码签名证书时允许发布未签名安装包，但必须保留
  SHA256 校验，并在发布 PR 和 Release 核验结果中明确标注“未签名”。
- **严格模式**：购买证书并启用 `RequireAuthenticodeSignature` 后，两个安装包
  必须完成签名和验证；未签名或签名验证失败的安装包不得上传。

不得把咨询模式的未签名产物描述成已签名，也不得为了通过发布而临时关闭
已经启用的严格模式。

### 3B.1 为什么需要签名

自动更新下载的安装包以管理员权限运行。仅靠 SHA256 校验和验证下载完整性
不够安全——校验和与安装包都由同一个 GitHub Release 分发，攻击者一旦取得
Release 发布权限就可以同时替换两者。

Authenticode 签名提供**独立信任链**：
- 签名私钥离线保管，不进入仓库、CI 日志或构建产物
- 即使 GitHub Release 被完全控制，攻击者也无法伪造有效签名
- 应用在执行安装包前依次验证 SHA256 → Authenticode 签名 → 证书链 → 发布者身份
- 严格模式下任一签名验证失败立即中止安装，禁止降级继续

**两阶段推进策略**：

自动更新的 Authenticode 验证由配置项 `RequireAuthenticodeSignature` 控制
（`AppSettings.RequireAuthenticodeSignature`，默认 `false`）：

| 配置值 | 模式 | 行为 |
|:-------|:-----|:-----|
| `false`（默认） | 咨询模式 | SHA256 校验照常；Authenticode 结果仅记录日志，**不阻断**更新 |
| `true` | 严格模式 | 签名无效或发布者不匹配时**中止安装**并报错 |

项目初期可先以咨询模式运行，验证管线无编译/运行时问题。购买代码签名证书后，
将 `RequireAuthenticodeSignature` 改为 `true`，严格验证即刻生效，无需修改代码。
（可在 `settings.json` 中覆盖默认值，或在新版本中将默认值改为 `true`。）

### 3B.2 环境准备

确认 SignTool 可用（通常在 `C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe`）：

```powershell
signtool /?
```

### 3B.3 签名安装包

```powershell
$ver = "1.8.0"
$certPath = "D:\secure\quicktranslate-code-signing.pfx"
$timestampUrl = "http://timestamp.digicert.com"

# 签名完整版（自包含）
signtool sign /fd SHA256 `
  /tr $timestampUrl /td SHA256 `
  /f $certPath `
  /d "QuickTranslate Setup (Full) v$ver" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe

# 签名轻量版（框架依赖）
signtool sign /fd SHA256 `
  /tr $timestampUrl /td SHA256 `
  /f $certPath `
  /d "QuickTranslate Setup v$ver" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe
```

> **签名参数说明**
>
> | 参数 | 含义 |
> |:-----|:-----|
> | `/fd SHA256` | 文件摘要算法使用 SHA256 |
> | `/tr <url>` | RFC 3161 时间戳服务器 URL |
> | `/td SHA256` | 时间戳摘要算法 |
> | `/f <pfx>` | 代码签名证书文件（含私钥） |
> | `/d "..."` | 签名描述信息 |
>
> 密钥管理：
> - `.pfx` 文件必须离线保管，不要提交到仓库
> - 如果证书有密码，加 `/p <password>` 参数
> - 如果使用硬件令牌（如 YubiKey）或 HSM，改用 `/csp` + `/k` 参数

### 3B.4 验证签名

签名后立即验证，确保签名和时间戳均有效：

```powershell
signtool verify /pa /v publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe
signtool verify /pa /v publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe
```

验证通过的特征：
- 输出包含 `Successfully verified`
- 签名证书的 Subject 包含 `YaHu`（与 `UpdateService.ExpectedPublisher` 一致）
- 时间戳时间在证书有效期内

### 3B.5 检查清单

发布前先确认本次采用的模式，再执行对应清单。

咨询模式：

- [ ] `AppSettings.RequireAuthenticodeSignature` 的发布默认值仍为 `false`
- [ ] 两个安装包的 SHA256 已计算并核对，`version.xml` 指向完整版安装包摘要
- [ ] 发布 PR 和 Draft Release 核验结果明确标注“安装包未签名（咨询模式）”
- [ ] 没有把未签名产物描述为已通过 Authenticode 验证

严格模式：

- [ ] 两个安装包均已签名（轻量版 + 完整版）
- [ ] `signtool verify /pa /v` 对两个包均通过
- [ ] 证书 Subject 包含 "YaHu"（与 `UpdateService.cs` 中 `ExpectedPublisher` 常量一致）
- [ ] 时间戳有效（签名时证书在有效期内）
- [ ] `installer/version.xml` 的 `<signer><subject>` 与证书 Subject 匹配
- [ ] 只有验证通过的安装包才能上传到 GitHub Release
- [ ] 若旧版本仍处于咨询模式，已安排一次可验证的严格模式过渡发布

---

## 第四步：更新文档与提交

### 4.1 编写更新日志

先更新并审核 [`RELEASE_NOTES_NEXT.md`](RELEASE_NOTES_NEXT.md)，再将其中已经进入本次发布的内容复制到 GitHub Release。建议分类：

- **新增特性** — 新功能
- **优化改进** — 性能、体验优化
- **修复** — Bug 修复
- **依赖** — 依赖变更说明

草稿中的自动化结果与人工验收边界必须保持分开；没有执行的真实 Provider、安装升级、混合 DPI 或辅助功能验证不得写成已通过。发布完成后，将已发布条目移出草稿，为下一版本重新建立基线。

### 4.2 准备发布提交

先完成下一步的 `version.xml` 和校验和更新，再将所有版本文件放进同一个发布提交。
不要在 `version.xml` 更新前打标签。发布提交应从 `main` 切出 `chore/release` 分支，
通过 Draft PR 运行 CI 并供发布人检查。

**人工确认门 1：合并 release PR**

代理可以准备分支、推送并创建 Draft PR，但不得启用自动合并。CI 通过后必须向发布人
报告版本差异、发布说明、自动化结果、安装包签名模式和人工验证缺口，然后停止操作。
只有发布人明确同意后，才能将 PR 标记为 Ready 并合并。不能把“准备发布”或“CI 通过”
解释为合并授权。

---

## 第五步：更新 version.xml 并创建 GitHub Release

### 5.0 更新 version.xml（自动更新依赖）

先算出完整版安装包的 SHA256（供 `<checksum>` 使用）：

```powershell
(Get-FileHash publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe -Algorithm SHA256).Hash
```

再打开 `installer\version.xml`，把版本号、下载链接和校验和更新为新版本。**六个元素缺一不可**：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<item>
  <version>1.8.0</version>
  <url>https://github.com/YAHU2024/myTool/releases/download/v1.8.0/QuickTranslate-Setup-1.8.0-win-x64-full.exe</url>
  <changelog>https://github.com/YAHU2024/myTool/releases/tag/v1.8.0</changelog>
  <args>/SILENT /SUPPRESSMSGBOXES /NORESTART</args>
  <checksum algorithm="SHA256">上一步算出的哈希</checksum>
  <mandatory>false</mandatory>
  <signer>
    <subject>YaHu</subject>
  </signer>
</item>
```

> - `<version>` 必须与 `QuickTranslate.csproj` 的版本一致，否则 `UpdateServiceTests` 会失败。
> - `<args>` 使用 `/SILENT` 隐藏安装向导但保留进度窗口；不要改回完全无反馈的 `/VERYSILENT`。
> - 两个安装脚本的 `[Run]` 项不能带 `skipifsilent`，否则更新完成后不会重新启动应用。
> - `<checksum>` 填错会让所有用户更新失败，务必用上一步的输出，且对应的是**完整版**（`-full.exe`）安装包。
> - `<url>` 指向完整版，是为了避免目标机器缺 .NET 8 运行时导致更新后启动失败。
> - `<signer><subject>` 填写代码签名证书的 Subject 子串（大小写不敏感），用于独立信任链交叉验证。
>   证书续期时需同步更新此元素和 `UpdateService.ExpectedPublisher` 常量。

改完跑一次 `dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj` 验证这些约束。

验证通过后提交发布分支并创建 Draft PR（常规 PR 流程见
[`docs/PR_MERGE_GUIDE.md`](PR_MERGE_GUIDE.md)）：

```powershell
git switch -c chore/release
git add QuickTranslate\QuickTranslate.csproj docs\RELEASE.md `
  installer\QuickTranslate-setup.iss installer\QuickTranslate-setup-full.iss `
  installer\version.xml QuickTranslate\Services\UpdateService.cs
git commit -m "chore(release): 版本号升级到 1.8.0"
git push -u origin HEAD
gh pr create --draft --base main --title "chore(release): 版本号升级到 1.8.0" `
  --body-file "$env:TEMP\quicktranslate-release-pr.md"
```

Draft PR 创建后等待人工确认。获得明确合并授权后，再将 PR 标记为 Ready 并合并；
随后同步 `main` 并记录准确的合并提交：

```powershell
$pr = <PR编号>
$prHeadSha = gh pr view $pr --json headRefOid --jq .headRefOid
gh pr ready $pr
# 仅在发布人明确授权后执行合并，或由发布人在 GitHub 网页手动合并
gh pr merge $pr --squash --delete-branch

git switch main
git pull --ff-only
$mergeSha = gh pr view $pr --json mergeCommit --jq .mergeCommit.oid

# 即使 main 随后又有新提交，也要固定到这个 release PR 自己的 squash commit
git merge-base --is-ancestor $mergeSha HEAD
if ($LASTEXITCODE -ne 0) { throw "Release PR squash commit is not on the current main branch." }

# squash 会生成新的提交；发布前必须证明它与已核验的 PR head 文件树一致
git diff --exit-code $prHeadSha $mergeSha --
if ($LASTEXITCODE -ne 0) { throw "Squash commit differs from the approved release PR head." }
```

树一致性检查通过后，才能把合并前已经核验且与 `version.xml` 校验值匹配的产物
上传到 Draft Release。不要在合并后随意重建并继续沿用旧校验值；如果重建了安装包，
必须重新计算 SHA256、更新 `version.xml`，并通过新的 release PR 审批循环。

> 此文件作为 Release 附件上传后，已安装用户的应用会通过
> `https://github.com/YAHU2024/myTool/releases/latest/download/version.xml`
> 自动检测到新版本。**该地址只解析被标记为 Latest 的 Release**，所以：
> 预发布（pre-release）不会被识别；若某次 Release 漏传 `version.xml`，
> 在下一次补传之前所有用户的检查都会提示"检查更新失败"。

### 5.1 使用 gh CLI 创建

```powershell
$ver = "1.8.0"

gh release create v$ver `
  --draft `
  --target $mergeSha `
  --title "v$ver" `
  --notes-file "$env:TEMP\quicktranslate-release-notes.md" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip `
  installer\version.xml

# 禁止在此自动继续。核对 Draft 并取得发布人的第二次明确确认后，
# 才能执行下一行；发布人也可以在 GitHub 网页手动发布。
gh release edit v$ver --draft=false --latest
```

`gh release create` 在远端不存在指定 tag 时可能自动创建 tag，因此必须显式传入
已经确认的 `$mergeSha`，不得依赖默认分支的当前状态。

**人工确认门 2：Draft Release 转正式 Release**

Draft 创建后必须核对标题、发布说明、目标提交、五个附件、SHA256、签名模式、
Latest/Pre-release 选项和人工验证缺口，并把 Draft 链接及核验结果交给发布人。
没有明确授权时，禁止执行 `--draft=false`、禁止设置 Latest，也禁止以其他方式公开发布。

### 5.2 在 GitHub 网页创建

1. 打开 https://github.com/YAHU2024/myTool/releases
2. 点击 **Draft a new release**
3. 选择标签 `v1.8.0`
4. 填写标题和更新日志
5. 拖拽上传以下 5 个文件：
   - `QuickTranslate-Setup-{version}-win-x64.exe` — 标准版安装程序
   - `QuickTranslate-Setup-{version}-win-x64-full.exe` — 完整版安装程序
   - `QuickTranslate-v{version}-win-x64.zip` — 标准版压缩包
   - `QuickTranslate-v{version}-win-x64-full.zip` — 完整版压缩包
   - `version.xml` — 自动更新元数据
6. 保持 Draft，核对目标提交、五个附件、校验和、签名模式和发布说明
7. 将 Draft 链接和核验结果交给发布人，等待第二次明确确认
8. 由发布人手动点击发布，或明确授权代理转为正式 Release 并设为 Latest
9. 建议在 Release 说明中注明两个版本的区别（参考[第四步 4.1](#41-编写更新日志)）

---

## 第六步：证书与信任链管理

### 6.1 信任链架构

```
签名私钥 (离线保管)
    │
    ▼
[签名] 安装包 (Authenticode)
    │
    ├── 证书链验证 (Windows 信任存储)
    ├── 发布者身份验证 (Subject 匹配 "YaHu")
    └── 时间戳验证 (签名时证书在有效期内)
    │
    ▼
[验证] 自动更新服务 (UpdateService.VerifyInstaller)
    │
    ├── SHA256 传输完整性 (防止下载损坏)
    ├── Authenticode 签名有效性 (防止篡改)
    └── 发布者身份匹配 (防止证书替换)
    │
    ▼
启动安装程序 (管理员权限)
```

信任锚点是 `UpdateService.ExpectedPublisher` 常量，该常量与代码一起版本化发布。

### 6.2 证书获取与保护

**选择证书颁发机构 (CA)**

推荐 Microsoft Trusted Root Program 成员的标准代码签名证书，例如：
- DigiCert
- Sectigo (Comodo)
- GlobalSign

必须选择支持 Authenticode 代码签名的证书类型（通常标注为 "Code Signing" 或 "Microsoft Authenticode"）。

**私钥保护要求**

1. 私钥（`.pfx` 文件）使用强密码加密存储
2. 不放入仓库、CI 环境变量、日志或构建产物
3. 签名操作在离线或隔离的签名工作站执行
4. CI 签名需要使用 GitHub Actions Secrets（加密存储），并在使用后立即清除

### 6.3 证书续期流程

证书到期前 30 天启动续期，避免自动更新因证书过期而全线阻断。

**续期步骤：**

1. **提前计划**（到期前 60 天）
   - 确认当前证书到期日期
   - 向 CA 提交续期申请
   - 新证书的 Subject 必须包含 "YaHu"（或更新 `ExpectedPublisher`）

2. **获取新证书**（到期前 30 天）
   - 下载新 `.pfx` 文件并安全存储
   - 验证新证书的 Subject 符合要求
   - 测试签名流程（用测试文件）

3. **过渡期发布**（到期前 14 天）
   - 如果 Subject 变化：更新 `UpdateService.ExpectedPublisher` 常量
   - 发布一个用新证书签名的小版本更新
   - 更新 `installer/version.xml` 的 `<signer><subject>`
   - 验证现有用户可通过自动更新下载新签名安装包

4. **旧证书过期后**
   - 安全销毁旧证书私钥
   - 确认没有仍在分发的旧签名安装包

### 6.4 紧急证书撤销

当私钥泄露或怀疑泄露时，需要立即撤销证书并发布紧急更新。

**撤销步骤：**

1. **立即联系 CA** 请求证书撤销
2. **发布安全公告** 通知用户
3. **获取新证书** 并完成续期流程（见 6.3）
4. **发布紧急更新**
   - 用新证书签名新的安装包
   - 更新 `UpdateService.ExpectedPublisher`（如 Subject 变化）
   - 更新 `installer/version.xml`
   - 在 Release 中注明为安全更新
5. **事后审查**
   - 排查泄露原因
   - 更新密钥管理流程
   - 检查是否有恶意签名安装包在分发

### 6.5 签名故障处理

**问题：signtool 签名失败**
- 检查证书是否在有效期内
- 检查时间戳服务器是否可访问（`/tr` 参数）
- 检查是否使用了正确的跨签名证书（EV 证书可能需要）

**问题：自动更新签名验证失败**
- 用 `signtool verify /pa /v` 检查安装包签名状态
- 确认 `version.xml` 的 `<signer><subject>` 正确
- 确认 `UpdateService.ExpectedPublisher` 与证书 Subject 匹配
- 检查用户机器的时间是否偏差过大（影响时间戳验证）

**问题：证书到期后用户无法更新**
- 已安装的旧版本中 `ExpectedPublisher` 仍指向旧证书 Subject
- 证书过期后 Windows 仍认可时间戳签名的旧包
- 但如果 Subject 变化，用户需要手动下载一次新版本
- **预防措施**：续期时尽量保持 Subject 一致，或提前发布过渡版本

---

## 目录结构参考

发布完成后，`publish/` 目录结构如下：

```
publish/
├── releases/                        # 各版本分发产物
│   ├── v1.6.0/
│   │   ├── QuickTranslate-Setup-1.6.0-win-x64.exe       ← 标准版安装包
│   │   └── QuickTranslate-v1.6.0-win-x64.zip             ← 标准版压缩包
│   └── v1.8.0/                      # ← 当前版本（双版本）
│       ├── QuickTranslate-Setup-1.8.0-win-x64.exe        ← 标准版安装包（15MB）
│       ├── QuickTranslate-Setup-1.8.0-win-x64-full.exe   ← 完整版安装包（150MB）
│       ├── QuickTranslate-v1.8.0-win-x64.zip             ← 标准版压缩包
│       └── QuickTranslate-v1.8.0-win-x64-full.zip        ← 完整版压缩包
└── source/                          # 构建源（可选保留，用于重建安装包）
    ├── v1.8.0/                      # ← 轻量版原始文件
    │   ├── QuickTranslate.exe
    │   ├── QuickTranslate.dll
    │   ├── *.dll
    │   └── runtimes/
    └── v1.8.0-full/                 # ← 完整版原始文件（含运行时 ~120MB）
        ├── QuickTranslate.exe
        ├── QuickTranslate.dll
        ├── *.dll
        └── *.NET 运行时文件.../
```

---

## 完整命令速查

将以下命令中的版本号替换为实际值，按顺序执行即可：

```powershell
# ===== v1.8.0 发布流程 =====

$ver = "1.8.0"

# 0. 手动修改：
#    - QuickTranslate\QuickTranslate.csproj 中的 <Version>、<AssemblyVersion>、<FileVersion>
#    - 两个 installer\QuickTranslate-setup*.iss 中的版本号、源目录和输出目录
#    - installer\version.xml 中的 <version>、<url>、<changelog>
#      （<checksum> 要等安装包编译出来才能算，见步骤 4.5）

# 1. 校验源文件并生成两个发布包共用的本地词典
powershell -ExecutionPolicy Bypass -File scripts\prepare-word-dictionary.ps1

# 2. 编译轻量版源
dotnet publish QuickTranslate\QuickTranslate.csproj -c Release -o publish\source\v$ver

# 3. 编译完整版源（自包含）
dotnet publish QuickTranslate\QuickTranslate.csproj -c Release -r win-x64 --self-contained true -o publish\source\v$ver-full

# 4. 打包 zip
New-Item publish\releases\v$ver -ItemType Directory -Force
Compress-Archive -Path publish\source\v$ver\*      -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip
Compress-Archive -Path publish\source\v$ver-full\*  -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip

# 5. 编译两个安装程序
ISCC installer\QuickTranslate-setup.iss
ISCC installer\QuickTranslate-setup-full.iss

# 5.1 编译后完整性校验（必做，任一失败禁止上传）
#     校验 ProductVersion=$ver、CompanyName=YaHu，防止安装包被后处理工具修改
$files = @("publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe",
           "publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe")
foreach ($f in $files) {
  $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $f))
  if ($vi.ProductVersion -ne $ver -or $vi.CompanyName -ne "YaHu") {
    Write-Error "完整性校验失败: $f ProductVersion='$($vi.ProductVersion)' CompanyName='$($vi.CompanyName)'"; exit 1
  }
  Write-Host "  PASS: $f (ProductVersion=$($vi.ProductVersion))"
}

# 4.1 严格模式：签名两个安装包（咨询模式跳过，但必须在核验结果中标注未签名）
$certPath = "D:\secure\quicktranslate-code-signing.pfx"
$tsUrl = "http://timestamp.digicert.com"

signtool sign /fd SHA256 /tr $tsUrl /td SHA256 /f $certPath `
  /d "QuickTranslate Setup v$ver" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe

signtool sign /fd SHA256 /tr $tsUrl /td SHA256 /f $certPath `
  /d "QuickTranslate Setup (Full) v$ver" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe

# 4.2 验证签名
signtool verify /pa /v publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe
signtool verify /pa /v publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe

# 4.5 算完整版安装包的 SHA256，填进 installer\version.xml 的 <checksum>
(Get-FileHash publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe -Algorithm SHA256).Hash

# 4.6 验证 version.xml（版本一致性、args、checksum 格式）
dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj

# 5. 提交发布分支并创建 Draft PR
git switch -c chore/release
git add QuickTranslate\QuickTranslate.csproj docs\RELEASE.md `
  installer\QuickTranslate-setup.iss installer\QuickTranslate-setup-full.iss `
  installer\version.xml QuickTranslate\Services\UpdateService.cs
git commit -m "chore(release): 版本号升级到 $ver"
git push -u origin HEAD
gh pr create --draft --base main --title "chore(release): 版本号升级到 $ver" `
  --body-file "$env:TEMP\quicktranslate-release-pr.md"

# STOP：人工确认门 1。报告差异和验证结果；未取得明确授权时不得执行以下四行
$pr = <PR编号>
$prHeadSha = gh pr view $pr --json headRefOid --jq .headRefOid
gh pr ready $pr
gh pr merge $pr --squash --delete-branch

# 合并后同步 main，验证 squash 文件树，并固定 Draft Release 的目标提交
git switch main
git pull --ff-only
$mergeSha = gh pr view $pr --json mergeCommit --jq .mergeCommit.oid
git merge-base --is-ancestor $mergeSha HEAD
if ($LASTEXITCODE -ne 0) { throw "Release PR squash commit is not on the current main branch." }
git diff --exit-code $prHeadSha $mergeSha --
if ($LASTEXITCODE -ne 0) { throw "Squash commit differs from the approved release PR head." }

# 6. 创建 Draft Release（同时上传 5 个文件）
gh release create v$ver `
  --draft `
  --target $mergeSha `
  --title "v$ver" `
  --notes-file "$env:TEMP\quicktranslate-release-notes.md" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip `
  installer\version.xml

# STOP：人工确认门 2。报告 Draft 核验结果；未取得明确授权时不得执行下一行。
# 发布人也可以在 GitHub 网页手动发布，不需要代理继续操作。
gh release edit v$ver --draft=false --latest
```

---

## 常见问题

### 应该给用户推荐哪个版本？

| 用户类型 | 推荐版本 | 文件名 |
|:---------|:---------|:-------|
| 普通用户 | 完整版 | `QuickTranslate-Setup-{ver}-win-x64-full.exe` |
| 开发者/已装 .NET 8 | 标准版 | `QuickTranslate-Setup-{ver}-win-x64.exe` |

> 建议在 GitHub Release 描述中同时提供两个版本，并用简短文字说明差异。

### 安装程序检测 .NET 8 但用户没有运行时怎么办？

安装程序会弹出提示框，询问是否前往微软官网下载 .NET 8 运行时，并在提示中建议用户也可使用「完整版」安装包。

### 如何只编译一种安装包？

```powershell
# 仅轻量版
ISCC installer\QuickTranslate-setup.iss

# 仅完整版
ISCC installer\QuickTranslate-setup.iss /DFullVersion
```

### ISCC 编译报错需要 .NET 6 运行时怎么办？

如果 Inno Setup 通过 `dotnet-innosetup` 全局工具安装（而非独立安装），它依赖 .NET 6 运行时。当系统只安装了 .NET 8 时，ISCC 会报错：

```
You must install or update .NET to run this application.
Framework: 'Microsoft.NETCore.App', version '6.0.0'
```

**临时方案**：设置 `DOTNET_ROLL_FORWARD` 环境变量，允许 .NET 8 向前兼容运行：

```powershell
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
ISCC installer\QuickTranslate-setup.iss
ISCC installer\QuickTranslate-setup-full.iss
```

**永久方案**：安装独立的 Inno Setup 6（非 dotnet 工具版），下载地址：https://jrsoftware.org/download.php/is.exe

> 检查当前安装方式：`dotnet tool list --global`，若存在 `dotnet-innosetup` 则为全局工具安装。

### 签名失败怎么办？

检查以下项目：
1. 证书文件（`.pfx`）是否存在且密码正确
2. 证书是否在有效期内
3. 是否安装了 Windows SDK（需要 `signtool.exe`）
4. 时间戳服务器是否可达（可尝试替换为其他时间戳 URL）

### 如何验证发布产物正常？

在干净的 Windows 环境（或虚拟机）中测试**两个版本**：
1. 下载标准版安装 → 确认 .NET 检测提示 → 下载完整版代替
2. 完整版安装 → 确认安装目录、快捷方式、开机自启正常
3. 运行程序 → 托盘图标、设置窗口、翻译功能正常
4. 控制面板「程序和功能」中确认可正常卸载
5. 右键安装包 → 属性 → 数字签名 → 确认签名有效且发布者为 YaHu
