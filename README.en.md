<div align="center">

# QuickTranslate

**Smart select-to-translate tool · Streaming output · Multi-mode deep analysis**

A lightweight .NET 8 WPF desktop translator that connects to OpenAI-compatible APIs, featuring SSE streaming translation, Edge TTS read-aloud, smart content detection, and automatic update delivery.

<br>

[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![WPF](https://img.shields.io/badge/WPF-Desktop-0A52A1?style=flat-square&logo=windows&logoColor=white)](https://github.com/dotnet/wpf)
[![C#](https://img.shields.io/badge/C%23-12-239120?style=flat-square&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp)
[![Version](https://img.shields.io/github/v/release/YAHU2024/myTool?style=flat-square&label=version)](https://github.com/YAHU2024/myTool/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/YAHU2024/myTool/total?style=flat-square)](https://github.com/YAHU2024/myTool/releases)
[![Stars](https://img.shields.io/github/stars/YAHU2024/myTool?style=flat-square&label=stars)](https://github.com/YAHU2024/myTool/stargazers)
[![Build](https://img.shields.io/github/actions/workflow/status/YAHU2024/myTool/build.yml?branch=main&style=flat-square&label=build)](https://github.com/YAHU2024/myTool/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-22C55E?style=flat-square)](LICENSE)
[![中文](https://img.shields.io/badge/中文-文档-512BD4?style=flat-square)](README.md)

<br>

</div>

---

## Features

| Category | Features |
|:-----|:-----|
| Core Translation | SSE streaming token-by-token output · drag / double-click / triple-click selection · red-dot guidance interaction · floating window instant preview · 14 languages · automatic language detection |
| Smart Detection | Auto-classifies Translation / Code / Term and routes to specialized prompts · confidence diagnostics · browser / terminal scene awareness |
| Multi-mode Sessions | Switch between Translate / Command-parse / Term-explain / Deep-analysis on the same text · instant restore of finished results |
| Markdown | Safe parse & render · standalone copy of fenced code blocks · tables / lists / quotes · only http/https links allowed |
| Text-to-Speech | Edge TTS online synthesis · read selected text · one-click read of translation result · automatic language matching |
| Translation History | SQLite local persistence · search & filter by time / language · paginated browsing · double-click to copy · Anki-format export |
| System Integration | Global hotkeys (customizable) · system tray resident · launch on startup · in-browser trigger · single-instance guard |
| Deep Analysis | 4 built-in presets (general / language-learning / literary / business) · custom profile create / duplicate / edit / delete · multi-turn profile management |
| Performance | LRU + TTL semantic cache · latest-request-wins conflict protection · request snapshot isolation · live setting changes don't affect in-flight requests |
| Auto Update | GitHub Release delivery · silent check on startup · system-proxy compatible · Inno Setup dual installer · SHA256 verification |
| Privacy & Security | Zero-pollution clipboard access · desensitized logs (no original text / API key / prompt body) · local config never uploaded |
| Ops & Diagnostics | Structured JSON Lines logs · dedicated viewer · multi-file switching · level / keyword filtering · P50/P95/P99 latency · auto cleanup |

---

## Screenshots

### Select-to-Translate · Red-dot Guidance

<p align="center">
  <img src="docs/images/红点翻译功能展示.gif" alt="Select-to-translate demo" width="85%">
</p>

Red-dot guidance on text selection, **streaming token-by-token output**, floating window instant preview — triggered by drag / double-click / triple-click.

---

### Settings · Multi-model & Hotkey Management

<p align="center">
  <img src="docs/images/设置页展示.gif" alt="Settings page" width="85%">
</p>

Multi-model switching, customizable global hotkeys, analysis profile management — **changes apply instantly without restart**.

---

### Translation History · Search & Anki Export

<p align="center">
  <img src="docs/images/翻译历史页面.png" alt="Translation history" width="85%">
</p>

SQLite local persistence, search & filter by time / language, paginated browsing, **one-click Anki-format export**.

---

### Log Viewer · JSON Lines & Latency Metrics

<p align="center">
  <img src="docs/images/日志查看器.png" alt="Log viewer" width="85%">
</p>

Structured JSON Lines logs, multi-file switching, level / keyword filtering, **P50/P95/P99 latency statistics**, auto cleanup of expired logs.

---

## Download & Install

Don't want to set up a dev environment? Just download the installer — **double-click to run, no .NET 8 SDK required**.

| Edition | Size | Notes |
|:-----|:-----|:-----|
| **Full (recommended)** | ~150 MB | Self-contained runtime, works out of the box → [Download latest full edition](https://github.com/YAHU2024/myTool/releases/latest) |
| **Standard** | ~15 MB | Requires the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) first → [All releases](https://github.com/YAHU2024/myTool/releases) |

All past versions, changelogs, and SHA256 checksums are on the [Releases page](https://github.com/YAHU2024/myTool/releases). After install, the app minimizes to the system tray; right-click the tray icon to configure.

---

## Quick Start (from source)

### Requirements

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Run

```powershell
git clone https://github.com/YAHU2024/myTool.git
cd myTool\QuickTranslate

dotnet run
```

The app minimizes to the system tray on launch; right-click the tray icon to open settings.

---

## Configure API

Right-click the tray icon and open the settings window:

| Field | Description | Example |
|:-----|:-----|:-------|
| Base URL | API endpoint | `https://api.siliconflow.cn/v1` |
| API Key | Your key | `sk-xxxxxxxxxxxxxxxx` |
| Model | Model name | `Qwen/Qwen3-8B` |

The model dropdown groups saved configurations by domain and auto-fills URL and Key on selection.

<details>
<summary>One-click provider reference (expand)</summary>

<br>

| Provider | Base URL | Model |
|:-------|:---------|:------|
| SiliconFlow (free recommended) | `https://api.siliconflow.cn/v1` | `Qwen/Qwen3-8B` |
| Zhipu GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.7-flash` |
| OpenAI | `https://api.openai.com/v1` | `gpt-4o-mini` |

</details>

<br>

> Logging guide, privacy boundaries, and developer integration: [Logging docs](docs/LOGGING.md).

---

## Project Structure

```text
QuickTranslate/
├── Core/                              # Core engine
│   ├── GlobalKeyboardHook.cs          # Global keyboard hook (independent message loop)
│   ├── SelectionDetector.cs           # Mouse-hook selection detection (drag/double/triple-click)
│   ├── SelectionLocator.cs            # UIA pixel-level selection locator
│   ├── ClipboardHelper.cs             # Zero-pollution clipboard (serial detection + restore)
│   ├── ContentTypeDetector.cs         # Smart content detection (Translation/Code/Term)
│   ├── BrowserDetector.cs             # Browser window awareness
│   ├── TerminalDetector.cs            # Terminal window awareness
│   ├── AutoScrollController.cs        # Streaming auto-scroll (pause/resume on user action)
│   ├── CopyShortcut.cs                # Copy shortcut helper
│   ├── LatestRequestCoordinator.cs    # latest-request-wins coordinator
│   ├── LatestPresentationCoordinator.cs  # Presentation identity coordinator
│   └── FloatingResultSessionCoordinator.cs  # Multi-mode session manager
│
├── Database/                          # Persistence
│   ├── TranslationRecord.cs           # History model
│   └── TranslationDbContext.cs        # EF Core SQLite context
│
├── Services/                          # Business services
│   ├── ITranslationService.cs         # Translation service interface
│   ├── OpenAITranslationService.cs    # OpenAI-compatible SSE streaming
│   ├── TranslationCacheService.cs      # Semantic cache (LRU + 30min TTL)
│   ├── TranslationMetrics.cs          # Metrics (P50/P95/P99)
│   ├── HistoryExporter.cs             # History export (Anki/CSV)
│   ├── AnalysisPromptCatalog.cs       # Built-in / custom analysis profiles
│   ├── UpdateService.cs               # Auto update (GitHub Release + AutoUpdater.NET)
│   ├── ITtsService.cs                 # TTS service interface
│   ├── EdgeTtsService.cs              # Edge TTS read-aloud
│   ├── EdgeTtsClient.cs               # Edge TTS WebSocket client
│   ├── TtsTextSelector.cs             # TTS text selector
│   └── TtsSpeakException.cs           # TTS exception class
│
├── Models/                            # Data models
│   ├── AppSettings.cs                 # Settings (multi-model / hotkeys / profiles / update)
│   ├── TranslationRequest.cs          # Immutable request snapshot
│   ├── FloatingResultSession.cs       # Multi-mode session state
│   ├── AnalysisPromptProfile.cs       # Custom analysis profile
│   └── TranslationTriggerMode.cs      # Translation trigger mode enum
│
├── Helpers/                           # Utilities
│   ├── ConfigManager.cs               # JSON config read/write + migration
│   ├── Logger.cs                      # Async logger (JSON Lines / rotation / cleanup)
│   ├── LogEvent.cs                    # Structured log event model
│   ├── MarkdownRenderer.cs            # Safe Markdown renderer
│   ├── Win32Api.cs                    # Win32 P/Invoke declarations
│   ├── DpiHelper.cs                   # DPI scaling coordinate conversion
│   ├── ApiEndpointValidator.cs        # API endpoint format validation
│   └── AuthenticodeVerifier.cs        # Installer digital signature verification
│
├── UI/                                # User interface
│   ├── FloatingWindow.xaml/.cs        # Floating window (multi-mode/Markdown/TTS/pin)
│   ├── RedDotWindow.xaml/.cs          # Red-dot guidance window
│   ├── TrayIconManager.cs            # System tray (context menu / toast)
│   ├── SettingsWindow.xaml/.cs       # Settings (models / profiles / update)
│   ├── DownloadUpdateWindow.xaml/.cs  # Update download window
│   ├── HistoryWindow.xaml/.cs        # Translation history
│   ├── LogViewerWindow.xaml/.cs      # Log viewer
│   ├── LogEntryReader.cs             # Log read & filter
│   ├── FloatingWindowAnchor.cs        # Window anchor positioning
│   ├── FloatingWindowPlacement.cs     # Window placement management
│   └── FloatingStatusMessage.cs       # Status messages
│
├── Assets/                            # App icon resources
├── app.manifest                       # Windows application manifest
├── QuickTranslate.csproj              # .NET 8 project file
├── MainWindow.xaml/.cs                # Hidden main window (stable WPF lifecycle)
└── App.xaml/.cs                       # App entry (single-instance / update / dispatch)
```

### Top-level layout

```text
myTool/
├── .github/                           # GitHub Actions workflows & issue templates
├── QuickTranslate/                    # Main source project
├── QuickTranslate.Tests/              # xUnit unit tests
├── installer/                         # Inno Setup scripts + version.xml
├── scripts/                           # Helper scripts
├── docs/                              # Documentation
│   ├── images/                        # Screenshots
│   ├── LOGGING.md                     # Logging guide
│   └── RELEASE.md                     # Release process
├── .gitignore                         # Git ignore rules
├── CONTRIBUTING.md                    # Contribution guide
├── LICENSE
├── README.en.md                       # English README
└── README.md
```

---

## Release & Update

### Dual installer

Inno Setup produces two installers:

| Edition | Size | Dependency |
|:-----|:-----|:-----|
| Standard | ~15 MB | Requires .NET 8 runtime |
| Full | ~150 MB | Self-contained, no runtime needed |

### Auto update

On startup the app silently checks the latest GitHub Release. When a new version is found, a tray toast notifies the user; clicking it opens the update dialog. Download and install are handled by AutoUpdater.NET, with SHA256 integrity verification.

See [docs/RELEASE.md](docs/RELEASE.md) for details.

---

## Roadmap

| Phase | Core work | Status |
|:----:|:---------|:----:|
| 1–12 | Skeleton, select-to-translate, tray, history, smart detection, semantic cache, multi-mode sessions, structured logs, TTS, auto update | done |
| 13 | Follow-up analysis | planned |
| 14 | Performance optimization | planned |
| 15 | UI unification & internationalization | planned |

---

## License

<div align="center">

MIT License — free to use, modify, and distribute.

</div>
