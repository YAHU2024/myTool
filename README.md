<div align="center">

# ⚡ QuickTranslate

**智能快捷翻译工具 · 划词即译 · 多模式深度解析**

一款基于 .NET 8 WPF 的轻量级桌面翻译工具，接入任意 OpenAI 兼容接口，提供 SSE 流式实时翻译，内置智能内容识别与多模式深度解析。

<br>

[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![WPF Desktop](https://img.shields.io/badge/WPF-Desktop-0A52A1?style=flat-square&logo=windows&logoColor=white)](https://github.com/dotnet/wpf)
[![C#](https://img.shields.io/badge/C%23-12-239120?style=flat-square&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-22C55E?style=flat-square)](LICENSE)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-8B5CF6?style=flat-square)]()

<br>

</div>

---

## 📖 目录

- [✨ 功能特性](#-功能特性)
- [🚀 快速开始](#-快速开始)
- [⚙️ 配置 API](#️-配置-api)
- [📁 项目结构](#-项目结构)
- [🗺️ 开发路线](#️-开发路线)
- [📄 许可证](#-许可证)

---

## ✨ 功能特性

<div align="center">

| 类别 | 特性 |
|:-----|:-----|
| **🎯 核心翻译** | SSE 流式逐字输出 · 拖拽/双击/三击划词 · 红点引导交互 · 悬浮窗即时展示 · 14 种语言支持 · 语言自动检测 |
| **🧠 智能识别** | 自动区分 `Translation` / `Code` / `Term`，路由专用 Prompt · 置信度诊断 · 浏览器/终端场景感知 |
| **🔄 多模式会话** | 同文本：**翻译** · **命令解析** · **术语解释** · **深度解析** 四模式严格切换 · 已完成结果瞬时恢复 |
| **📝 安全 Markdown** | 安全解析渲染 · 围栏代码块独立复制 · 表格/列表/引用 · 仅允许 `http/https` 链接 |
| **📚 翻译历史** | SQLite 本地持久化 · 按时间/语言搜索筛选 · 分页浏览 · 双击复制 · Anki 格式导出 |
| **🖥️ 系统集成** | 全局快捷键（自定义组合键） · 系统托盘常驻 · 开机自启 · 浏览器内触发 · 单实例保护 |
| **🎨 深度解析** | 4 种内置预设（通用/语言学习/文学赏析/商务） · 自定义方案新建/复制/编辑/删除 · 多轮方案管理 |
| **⚡ 性能优化** | LRU+TTL 语义缓存 · `latest-request-wins` 请求冲突防护 · 请求快照隔离 · 设置修改不影响运行中请求 |
| **🔒 隐私安全** | 零污染剪贴板获取 · 日志脱敏（不记录原文/API Key/Prompt 正文） · 本地配置不上传 |
| **📊 运维诊断** | 结构化 JSON Lines 日志 · 专用查看器 · 多文件切换 · 级别/关键字筛选 · P50/P95/P99 延迟指标 · 自动清理 |

</div>

---

## 🚀 快速开始

### 环境要求

- ✅ **Windows 10 / 11**
- ✅ **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**

### 运行

```powershell
# 克隆仓库
git clone https://github.com/<your-username>/QuickTranslate.git
cd QuickTranslate\QuickTranslate

# 还原依赖 & 启动
dotnet run
```

启动后自动最小化到 **系统托盘**，右键托盘图标即可开始配置。

---

## ⚙️ 配置 API

右键托盘图标 → 「**设置**」打开配置窗口：

| 字段 | 说明 | 示例值 |
|:-----|:-----|:-------|
| **`Base URL`** | API 接口地址 | `https://api.siliconflow.cn/v1` |
| **`API Key`** | 你的密钥 | `sk-xxxxxxxxxxxxxxxx` |
| **`Model`** | 模型名称 | `Qwen/Qwen3-8B` |

模型下拉框按域名分组展示已保存配置，选中自动填充 URL 和 Key。

<details>
<summary><b>🔧 一键配置参考</b>（点击展开）</summary>

<br>

| 服务商 | Base URL | Model |
|:-------|:---------|:------|
| **硅基流动** 🔥 推荐免费 | `https://api.siliconflow.cn/v1` | `Qwen/Qwen3-8B` |
| **智谱 GLM** | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.7-flash` |
| **OpenAI** | `https://api.openai.com/v1` | `gpt-4o-mini` |

</details>

<br>

> 📖 日志功能使用指南、隐私边界和开发接入见 [日志功能使用文档](docs/LOGGING.md)。

---

## 📁 项目结构

```text
QuickTranslate/
├── 📂 Core/                          # 核心引擎
│   ├── GlobalKeyboardHook.cs         # 全局键盘钩子（独立消息循环，热键触发）
│   ├── SelectionDetector.cs          # 鼠标钩子 → 拖拽/双击/三击选词检测
│   ├── SelectionLocator.cs           # UIA 像素级选区定位
│   ├── ClipboardHelper.cs            # 零污染剪贴板（序列号检测 + 恢复）
│   ├── ContentTypeDetector.cs        # 智能内容识别（Translation/Code/Term + 置信度）
│   ├── BrowserDetector.cs            # 浏览器窗口感知
│   ├── TerminalDetector.cs           # 终端窗口感知
│   ├── CopyShortcut.cs               # 复制快捷键辅助
│   ├── AutoScrollController.cs       # 流式自动滚动（用户操作暂停/恢复）
│   ├── LatestRequestCoordinator.cs   # 请求协调（latest-request-wins）
│   ├── LatestPresentationCoordinator.cs  # 展示身份协调
│   └── FloatingResultSessionCoordinator.cs  # 多模式会话统一管理
│
├── 📂 Database/                      # 持久化层
│   ├── TranslationRecord.cs          # 翻译历史记录模型
│   └── TranslationDbContext.cs       # EF Core SQLite 上下文
│
├── 📂 Services/                      # 业务服务
│   ├── ITranslationService.cs        # 翻译服务接口（流式翻译 + 流式解析）
│   ├── OpenAITranslationService.cs   # OpenAI 兼容接口（SSE + 四类 Prompt 路由）
│   ├── TranslationCacheService.cs    # 语义缓存（LRU + 30min TTL）
│   ├── TranslationMetrics.cs         # 指标统计（P50/P95/P99、缓存命中率）
│   └── AnalysisPromptCatalog.cs      # 内置/自定义解析方案目录
│
├── 📂 Models/                        # 数据模型
│   ├── AppSettings.cs                # 配置模型（多模型、快捷键、解析预设等）
│   ├── TranslationRequest.cs         # 不可变请求快照
│   ├── FloatingResultSession.cs      # 多模式会话状态模型
│   └── AnalysisPromptProfile.cs      # 自定义解析方案
│
├── 📂 Helpers/                       # 工具类
│   ├── ConfigManager.cs              # JSON 配置读写 + 旧配置迁移
│   ├── Logger.cs                     # 异步日志器（JSON Lines、按天轮转、自动清理）
│   ├── LogEvent.cs                   # 结构化日志事件模型
│   ├── MarkdownRenderer.cs           # 安全 Markdown → FlowDocument 渲染
│   ├── Win32Api.cs                   # Win32 P/Invoke 声明
│   └── DpiHelper.cs                  # DPI 缩放坐标转换
│
├── 📂 UI/                            # 用户界面
│   ├── FloatingWindow.xaml/.cs       # 悬浮窗（多模式操作栏/流式/Markdown/图钉）
│   ├── RedDotWindow.xaml/.cs         # 红点引导窗口
│   ├── TrayIconManager.cs            # 系统托盘（深色渲染菜单）
│   ├── SettingsWindow.xaml/.cs       # 设置窗口（模型管理 + 解析方案管理）
│   ├── HistoryWindow.xaml/.cs        # 翻译历史查看
│   ├── LogViewerWindow.xaml/.cs      # 日志查看器
│   └── LogEntryReader.cs             # 日志读取与筛选
│
├── MainWindow.xaml / .cs             # 隐藏主窗口（稳定 WPF 生命周期）
└── App.xaml / .cs                    # 应用入口（单实例 + 事件调度 + 退出监控）
```

---

## 🗺️ 开发路线

| 期数 | 核心内容 | 状态 |
|:----:|:---------|:----:|
| 📐 **第一期** | 基础骨架 + 手动触发翻译 + 流式输出 | ✅ |
| 🖱️ **第二期** | 划词触发 + 红点交互 + 悬浮窗 + UIA 定位 + DPI 适配 | ✅ |
| 🗂️ **第三期** | 系统托盘 + 设置持久化 + 开机自启 | ✅ |
| 📋 **第四期** | 翻译历史 + 快捷键自定义 + 语言自动检测 + Prompt 自定义 | ✅ |
| 🛡️ **第五期** | 单实例保护 + 信号防护 + 日志系统 + 剪贴板零污染 | ✅ |
| 🧠 **第六期** | 智能内容识别 + 分类回归测试 + 浏览器检测 + 多模型管理 | ✅ |
| 🚦 **第七期** | 请求生命周期重构 + 语义缓存 + `latest-request-wins` | ✅ |
| 🎨 **第八期** | 多模式会话 + Markdown 渲染 + 流式视角控制 + 窗口拖拽缩放 | ✅ |
| 📊 **第九期** | 结构化日志 + 日志查看器 + 级别筛选 + P50/P95/P99 指标 | ✅ |
| 📝 **第十期** | 四类 Prompt 行为契约 + 内置/自定义解析方案管理 + 日志隐私 | ✅ |
| 💬 **第十一期** | 解析追问功能（规划中） | 🔲 |
| ⚡ **第十二期** | 性能优化（规划中） | 🔲 |
| 🌐 **第十三期** | UI 统一与国际化（规划中） | 🔲 |

---

## 📄 许可证

<div align="center">

**MIT License** — 自由使用、修改和分发。

</div>
