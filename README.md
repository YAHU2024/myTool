# QuickTranslate

一款基于 .NET 8 WPF 的轻量级翻译工具，支持接入任意 OpenAI 兼容接口（OpenAI、智谱 GLM、硅基流动等），提供 SSE 流式实时翻译，内置智能内容识别与深度解析。

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![WPF](https://img.shields.io/badge/WPF-Desktop-0A52A1)
![License](https://img.shields.io/badge/license-MIT-green)

## 功能特性

- **多模型支持** — 兼容所有 OpenAI Chat Completions 接口，开箱支持 OpenAI、智谱 GLM-4.7-Flash、硅基流动 Qwen3 等
- **SSE 流式翻译** — 逐字实时显示翻译结果，首字响应快
- **划词翻译** — 拖拽/双击/三击选词，红点引导交互，悬浮窗即时展示
- **智能内容识别** — 自动区分普通文本、代码/命令、专有术语，路由到对应 Prompt 策略
- **深度解析** — 兜底翻译场景显示可点击 `[解析]` 标签，一键触发目标语言深度解析（支持通用/语言学习/文学赏析/商务场景四种预设）
- **14 种语言** — 简繁中文、英语、日语、韩语、法语、德语、西班牙语、俄语、葡萄牙语、意大利语、阿拉伯语、越南语、泰语
- **语言自动检测** — 智能识别源语言方向，中文→英文，其他→目标语言
- **浏览器内翻译** — 仅在浏览器窗口内触发，避免桌面环境误触发
- **翻译历史** — SQLite 本地持久化，支持搜索、分页、Anki 导出，译文/解析分类型记录
- **快捷键自定义** — 支持 Ctrl/Alt/Shift 组合键，全局热键触发
- **多模型管理** — 已保存配置按域名分组，支持一键切换和删除
- **自定义 System Prompt** — 支持 `{targetLang}` 占位符，同时作用于翻译和解析
- **深色主题** — 精心设计的深色 UI，长时间使用不刺眼
- **单实例保护** — Mutex 防止多开，避免全局钩子冲突
- **本地配置** — API Key 等设置保存于 `%APPDATA%\QuickTranslate\settings.json`，不上传任何数据

## 快速开始

### 环境要求

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 运行

```powershell
git clone https://github.com/<your-username>/QuickTranslate.git
cd QuickTranslate\QuickTranslate
dotnet run
```

### 配置 API

启动后最小化到系统托盘，右键托盘图标 → 「设置」打开配置窗口：

| 字段 | 说明 | 示例 |
|------|------|------|
| **Base URL** | API 接口地址 | `https://open.bigmodel.cn/api/paas/v4` |
| **API Key** | 你的密钥 | `sk-xxxxxxxxxxxxxxxx` |
| **Model** | 模型名称（下拉选择已保存模型或手动输入） | `glm-4.7-flash` |

点击「保存」后即可使用。模型下拉框按域名分组显示已保存的配置，选中自动填充 URL 和 Key。

### 常用 API 配置参考

<details>
<summary>智谱 GLM-4.7-Flash（推荐，免费）</summary>

| 字段 | 值 |
|------|------|
| Base URL | `https://open.bigmodel.cn/api/paas/v4` |
| Model | `glm-4.7-flash` |

</details>

<details>
<summary>硅基流动 SiliconFlow</summary>

| 字段 | 值 |
|------|------|
| Base URL | `https://api.siliconflow.cn/v1` |
| Model | `Qwen/Qwen3-8B` |

</details>

<details>
<summary>OpenAI</summary>

| 字段 | 值 |
|------|------|
| Base URL | `https://api.openai.com/v1` |
| Model | `gpt-4o-mini` |

</details>

## 项目结构

```
QuickTranslate/
├── Core/
│   ├── GlobalKeyboardHook.cs         # 全局键盘钩子（独立消息循环线程，热键触发翻译）
│   ├── SelectionDetector.cs          # 鼠标钩子检测拖拽/双击/三击选词
│   ├── SelectionLocator.cs           # UI Automation 选区像素级定位
│   ├── ClipboardHelper.cs            # 剪贴板操作（序列号检测 Ctrl+C + 零污染获取 + 恢复）
│   ├── ContentTypeDetector.cs        # 智能内容类型识别（Translation/Code/Term/Analysis）
│   └── BrowserDetector.cs            # 浏览器窗口检测（仅在浏览器内触发翻译）
├── Database/
│   ├── TranslationRecord.cs          # 翻译历史记录模型
│   └── TranslationDbContext.cs       # EF Core SQLite 数据库上下文（自动兼容新列）
├── Services/
│   ├── ITranslationService.cs        # 翻译服务接口（含流式翻译 + 流式解析）
│   └── OpenAITranslationService.cs   # OpenAI 兼容接口实现（SSE 流式 + 兜底检测 + 解析 Prompt）
├── Models/
│   └── AppSettings.cs                # 配置模型（已保存配置、快捷键、解析预设等）
├── Helpers/
│   ├── ConfigManager.cs              # 配置持久化（JSON 读写）
│   ├── Logger.cs                     # 轻量级异步文件日志器（按天轮转 + 自动清理）
│   ├── Win32Api.cs                   # Win32 P/Invoke 声明
│   └── DpiHelper.cs                  # DPI 缩放坐标转换
├── UI/
│   ├── FloatingWindow.xaml/.cs       # 翻译结果悬浮窗（流式输出 + 可点击解析标签）
│   ├── RedDotWindow.xaml/.cs         # 红点交互窗口
│   ├── TrayIconManager.cs            # 系统托盘图标与右键菜单
│   ├── SettingsWindow.xaml/.cs       # 设置窗口（多模型管理 + 解析预设选择）
│   └── HistoryWindow.xaml/.cs        # 翻译历史查看窗口（译文/解析分列显示）
├── MainWindow.xaml / .cs             # 隐藏主窗口（稳定 WPF Dispatcher 生命周期）
└── App.xaml / .cs                    # 应用入口（单实例 Mutex + 事件调度 + 退出监控）
```

## 开发路线

| 期数 | 内容 | 状态 |
|------|------|------|
| 第一期 | 基础骨架 + 手动触发翻译 + 流式输出 | ✅ 已完成 |
| 第二期 | 划词触发 + 红点交互 + 悬浮窗 + UIA 定位 + DPI 适配 | ✅ 已完成 |
| 第三期 | 系统托盘 + 设置持久化 + 开机自启 | ✅ 已完成 |
| 第四期 | 翻译历史 + 快捷键自定义 + 语言自动检测 + System Prompt 自定义 | ✅ 已完成 |
| 第五期 | 稳定性加固：单实例保护 + 控制台信号防护 + 日志系统 + 剪贴板零污染改造 | ✅ 已完成 |
| 第六期 | 智能内容识别 + 浏览器检测 + 多模型管理 + 兜底语言优化 | ✅ 已完成 |
| 第七期 | 深度解析标签 + 解析预设系统 + 历史类型分列 + 性能优化（异步钩子） | ✅ 已完成 |

## 许可证

MIT License
