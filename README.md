# QuickTranslate

一款基于 .NET 8 WPF 的轻量级翻译工具，支持接入任意 OpenAI 兼容接口（OpenAI、智谱 GLM、硅基流动等），提供 SSE 流式实时翻译。

![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![WPF](https://img.shields.io/badge/WPF-Desktop-0A52A1)
![License](https://img.shields.io/badge/license-MIT-green)

## 功能特性

- **多模型支持** — 兼容所有 OpenAI Chat Completions 接口，开箱支持 OpenAI、智谱 GLM-4.7-Flash、硅基流动 Qwen3 等
- **SSE 流式翻译** — 逐字实时显示翻译结果，首字响应快
- **14 种语言** — 简繁中文、英语、日语、韩语、法语、德语、西班牙语、俄语、葡萄牙语、意大利语、阿拉伯语、越南语、泰语
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

启动后在窗口底部「API 设置」区域填写：

| 字段 | 说明 | 示例 |
|------|------|------|
| **Base URL** | API 接口地址 | `https://open.bigmodel.cn/api/paas/v4` |
| **API Key** | 你的密钥 | `sk-xxxxxxxxxxxxxxxx` |
| **Model** | 模型名称 | `glm-4.7-flash` |

点击「保存设置」后即可使用。

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
├── Services/
│   ├── ITranslationService.cs        # 翻译服务接口
│   └── OpenAITranslationService.cs   # OpenAI 兼容接口实现（含 SSE 流式）
├── Models/
│   └── AppSettings.cs                # 配置模型
├── Helpers/
│   └── ConfigManager.cs              # 配置持久化（JSON 读写）
├── MainWindow.xaml / .cs             # 主窗口
└── App.xaml / .cs                    # 应用入口
```

## 开发路线

| 期数 | 内容 | 状态 |
|------|------|------|
| 第一期 | 基础骨架 + 手动触发翻译 + 流式输出 | ✅ 已完成 |
| 第二期 | 划词触发 + 悬浮窗 | 🔲 计划中 |
| 第三期 | 系统托盘 + 开机自启 | 🔲 计划中 |
| 第四期 | 翻译历史 + Anki 导出 + 快捷键 | 🔲 计划中 |

## 许可证

MIT License
