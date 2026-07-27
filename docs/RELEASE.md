# QuickTranslate 发布流程

> 本文档描述从代码到 GitHub Release 的完整发布步骤，适用于所有版本迭代。

---

## 目录

- [前置准备](#前置准备)
- [第一步：更新版本号](#第一步更新版本号)
- [第二步：编译发布产物](#第二步编译发布产物)
- [第三步：生成安装程序](#第三步生成安装程序)
- [第四步：更新文档与提交](#第四步更新文档与提交)
- [第五步：创建 GitHub Release](#第五步创建-github-release)
- [完整命令速查](#完整命令速查)
- [常见问题](#常见问题)

---

## 前置准备

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6+](https://jrsoftware.org/download.php/is.exe)（用于生成安装程序）
- [GitHub CLI](https://cli.github.com/)（`gh`，需登录 `gh auth login`）

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

---

## 第二步：编译发布产物

项目同时交付两种安装包，因此需要编译两类源文件。

### 2.1 编译轻量版源文件（框架依赖，~15MB）

```powershell
dotnet publish QuickTranslate\QuickTranslate.csproj `
  -c Release `
  -o publish\source\v1.8.0
```

### 2.2 编译完整版源文件（自包含，~150MB）

```powershell
dotnet publish QuickTranslate\QuickTranslate.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -o publish\source\v1.8.0-full
```

### 2.3 创建发布目录 + 打包 zip

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
| `QuickTranslate-setup.iss` | `Setup-{ver}-win-x64.exe` | ~15 MB | 已安装 .NET 8 的专业用户 |
| `QuickTranslate-setup-full.iss` | `Setup-{ver}-win-x64-full.exe` | ~150 MB | 普通用户，双击安装即用 |

### 3.2 编译安装程序

```powershell
# 标准版（轻量，需用户已有 .NET 8）
ISCC installer\QuickTranslate-setup.iss

# 完整版（自包含，普通用户双击即用）
ISCC installer\QuickTranslate-setup-full.iss
```

---

## 第四步：更新文档与提交

### 4.1 编写更新日志

在 GitHub Release 中记录变更内容。建议分类：

- **✨ 新增特性** — 新功能
- **🔧 优化改进** — 性能、体验优化
- **🐛 修复** — Bug 修复
- **📦 依赖** — 依赖变更说明

### 4.2 准备发布提交

先完成下一步的 `version.xml` 和校验和更新，再将所有版本文件放进同一个发布提交。
不要在 `version.xml` 更新前打标签。

---

## 第五步：更新 version.xml 并创建 GitHub Release

### 5.0 更新 version.xml（自动更新依赖）

先算出完整版安装包的 SHA256（供 `<checksum>` 使用）：

```powershell
(Get-FileHash publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe -Algorithm SHA256).Hash
```

再打开 `installer\version.xml`，把版本号、下载链接和校验和更新为新版本。**五个元素缺一不可**：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<item>
  <version>1.8.0</version>
  <url>https://github.com/YAHU2024/myTool/releases/download/v1.8.0/QuickTranslate-Setup-1.8.0-win-x64-full.exe</url>
  <changelog>https://github.com/YAHU2024/myTool/releases/tag/v1.8.0</changelog>
  <args>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART</args>
  <checksum algorithm="SHA256">上一步算出的哈希</checksum>
  <mandatory>false</mandatory>
</item>
```

> - `<version>` 必须与 `QuickTranslate.csproj` 的版本一致，否则 `UpdateServiceTests` 会失败。
> - `<args>` 丢失会导致更新时弹出完整安装向导，而非静默安装。
> - `<checksum>` 填错会让所有用户更新失败（AutoUpdater 报 "Checksum differs"），
>   务必用上一步的输出，且对应的是**完整版**（`-full.exe`）安装包。
> - `<url>` 指向完整版，是为了避免目标机器缺 .NET 8 运行时导致更新后启动失败。

改完跑一次 `dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj` 验证这些约束。

验证通过后提交发布分支并通过 PR 合并：

```powershell
git add QuickTranslate\QuickTranslate.csproj docs\RELEASE.md `
  installer\QuickTranslate-setup.iss installer\QuickTranslate-setup-full.iss `
  installer\version.xml
git commit -m "chore(release): bump version to 1.8.0"
git push -u origin HEAD
```

PR 合并后在最新 `main` 提交上创建并推送标签：

```powershell
git switch main
git pull --ff-only
git tag -a v1.8.0 -m "Release v1.8.0"
git push origin v1.8.0
```

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
  --title "v$ver" `
  --notes "在此填写更新日志（支持 Markdown）" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip `
  installer\version.xml

# 核对五个附件后再正式发布
gh release edit v$ver --draft=false --latest
```

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
6. 保持 Draft，核对五个附件和校验和后再正式发布
7. 建议在 Release 说明中注明两个版本的区别（参考[第四步 4.1](#41-编写更新日志)）

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

# 0. ⚠️ 先手动修改：
#    - QuickTranslate\QuickTranslate.csproj 中的 <Version>、<AssemblyVersion>、<FileVersion>
#    - 两个 installer\QuickTranslate-setup*.iss 中的版本号、源目录和输出目录
#    - installer\version.xml 中的 <version>、<url>、<changelog>
#      （<checksum> 要等安装包编译出来才能算，见步骤 4.5）

# 1. 编译轻量版源
dotnet publish QuickTranslate\QuickTranslate.csproj -c Release -o publish\source\v$ver

# 2. 编译完整版源（自包含，约 150MB）
dotnet publish QuickTranslate\QuickTranslate.csproj -c Release -r win-x64 --self-contained true -o publish\source\v$ver-full

# 3. 打包 zip
New-Item publish\releases\v$ver -ItemType Directory -Force
Compress-Archive -Path publish\source\v$ver\*      -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip
Compress-Archive -Path publish\source\v$ver-full\*  -DestinationPath publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip

# 4. 编译两个安装程序
ISCC installer\QuickTranslate-setup.iss
ISCC installer\QuickTranslate-setup-full.iss

# 4.5 算完整版安装包的 SHA256，填进 installer\version.xml 的 <checksum>
(Get-FileHash publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe -Algorithm SHA256).Hash

# 4.6 验证 version.xml（版本一致性、args、checksum 格式）
dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj

# 5. 提交发布分支并通过 PR 合并
git add QuickTranslate\QuickTranslate.csproj docs\RELEASE.md `
  installer\QuickTranslate-setup.iss installer\QuickTranslate-setup-full.iss `
  installer\version.xml
git commit -m "chore(release): bump version to $ver"
git push -u origin HEAD

# PR 合并后，在最新 main 上打标签
git switch main
git pull --ff-only
git tag -a v$ver -m "Release v$ver"
git push origin v$ver

# 6. 创建 GitHub Release（同时上传 5 个文件）
gh release create v$ver `
  --draft `
  --title "v$ver" `
  --notes "在此填写更新日志" `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64.exe `
  publish\releases\v$ver\QuickTranslate-Setup-$ver-win-x64-full.exe `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64.zip `
  publish\releases\v$ver\QuickTranslate-v$ver-win-x64-full.zip `
  installer\version.xml

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

### 如何验证发布产物正常？

在干净的 Windows 环境（或虚拟机）中测试**两个版本**：
1. 下载标准版安装 → 确认 .NET 检测提示 → 下载完整版代替
2. 完整版安装 → 确认安装目录、快捷方式、开机自启正常
3. 运行程序 → 托盘图标、设置窗口、翻译功能正常
4. 控制面板「程序和功能」中确认可正常卸载
