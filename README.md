# QuickTranslate

一款基于 .NET 8 WPF 的轻量级翻译工具，支持接入任意 OpenAI 兼容接口（OpenAI、智谱 GLM、硅基流动等），提供 SSE 流式实时翻译。

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![WPF](https://img.shields.io/badge/WPF-Desktop-0A52A1)
![License](https://img.shields.io/badge/license-MIT-green)

## 功能特性

- **多模型支持** — 兼容所有 OpenAI Chat Completions 接口，开箱支持 OpenAI、智谱 GLM-4.7-Flash、硅基流动 Qwen3 等
- **SSE 流式翻译** — 逐字实时显示翻译结果，首字响应快
- **划词翻译** — 拖拽/双击/三击选词，红点引导交互，悬浮窗即时展示
- **14 种语言** — 简繁中文、英语、日语、韩语、法语、德语、西班牙语、俄语、葡萄牙语、意大利语、阿拉伯语、越南语、泰语
- **语言自动检测** — 智能识别源语言方向，中文→英文，其他→目标语言
- **翻译历史** — SQLite 本地持久化，支持搜索、分页、Anki 导出
- **快捷键自定义** — 支持 Ctrl/Alt/Shift 组合键，全局热键触发
- **自定义 System Prompt** — 支持 `{targetLang}` 占位符，灵活定制翻译风格
- **深色主题** — 精心设计的深色 UI，长时间使用不刺眼
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
│   ├── GlobalKeyboardHook.cs         # 全局键盘钩子（热键触发翻译）
│   ├── SelectionDetector.cs          # 鼠标钩子检测拖拽/双击/三击选词
│   ├── SelectionLocator.cs           # UI Automation 选区像素级定位
│   └── ClipboardHelper.cs            # 剪贴板操作（模拟 Ctrl+C + 恢复）
├── Database/
│   ├── TranslationRecord.cs          # 翻译历史记录模型
│   └── TranslationDbContext.cs       # EF Core SQLite 数据库上下文
├── Services/
│   ├── ITranslationService.cs        # 翻译服务接口
│   └── OpenAITranslationService.cs   # OpenAI 兼容接口实现（SSE 流式）
├── Models/
│   └── AppSettings.cs                # 配置模型（含已保存配置、快捷键等）
├── Helpers/
│   ├── ConfigManager.cs              # 配置持久化（JSON 读写）
│   ├── Win32Api.cs                   # Win32 P/Invoke 声明
│   └── DpiHelper.cs                  # DPI 缩放坐标转换
├── UI/
│   ├── FloatingWindow.xaml/.cs       # 翻译结果悬浮窗（流式输出）
│   ├── RedDotWindow.xaml/.cs         # 红点交互窗口
│   ├── TrayIconManager.cs            # 系统托盘图标与右键菜单
│   ├── SettingsWindow.xaml/.cs       # 设置窗口
│   └── HistoryWindow.xaml/.cs        # 翻译历史查看窗口
├── MainWindow.xaml / .cs             # 主窗口
└── App.xaml / .cs                    # 应用入口
```

## 开发路线

| 期数 | 内容 | 状态 |
|------|------|------|
| 第一期 | 基础骨架 + 手动触发翻译 + 流式输出 | ✅ 已完成 |
| 第二期 | 划词触发 + 红点交互 + 悬浮窗 + UIA 定位 + DPI 适配 | ✅ 已完成 |
| 第三期 | 系统托盘 + 设置持久化 + 开机自启 | ✅ 已完成 |
| 第四期 | 翻译历史 + 快捷键自定义 + 语言自动检测 + System Prompt 自定义 | ✅ 已完成 |

## 许可证

MIT License
