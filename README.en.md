<div align="center">

# QuickTranslate

**More than translation. Select text and start understanding with AI.**

QuickTranslate is a Windows AI tool that works inside your reading flow. Select text for AI translation, local-dictionary lookup enhanced by a cloud model, code and term analysis, and follow-up questions grounded in the result. No repeated copy-paste or window switching: move from reading the words to understanding the idea in one interaction.

<br>

[![.NET 8](https://img.shields.io/badge/.NET-8-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![WPF](https://img.shields.io/badge/WPF-Desktop-0A52A1?style=flat-square&logo=windows&logoColor=white)](https://github.com/dotnet/wpf)
[![C#](https://img.shields.io/badge/C%23-12-239120?style=flat-square&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp)
[![Version](https://img.shields.io/github/v/release/YAHU2024/myTool?style=flat-square&label=version)](https://github.com/YAHU2024/myTool/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/YAHU2024/myTool/total?style=flat-square)](https://github.com/YAHU2024/myTool/releases)
[![Stars](https://img.shields.io/github/stars/YAHU2024/myTool?style=flat-square&label=stars)](https://github.com/YAHU2024/myTool/stargazers)
[![Build](https://img.shields.io/github/actions/workflow/status/YAHU2024/myTool/build.yml?branch=main&style=flat-square&label=build)](https://github.com/YAHU2024/myTool/actions/workflows/build.yml)
[![License: MPL-2.0](https://img.shields.io/badge/License-MPL--2.0-22C55E?style=flat-square)](LICENSE)
[![中文](https://img.shields.io/badge/中文-文档-512BD4?style=flat-square)](README.md)

<br>

[Download](#download--install) · [Run from source](#quick-start-from-source) · [See the demos](#screenshots) · [Open an issue](https://github.com/YAHU2024/myTool/issues)

</div>

---

## Three AI Experiences, One Open Entry Point

| Core experience | What QuickTranslate does |
|:----------------|:-------------------------|
| **Smart AI translation** | Detects whether the selected text is translation, code, or a term, routes it to a purpose-built prompt, and streams the model result |
| **AI-assisted lookup** | Starts with ECDICT + OEWN locally, uses AI to complete missing Chinese definitions, and can add phonetics, structured definitions, examples, and collocations on a local miss |
| **Focused AI follow-ups** | Ask questions about a deep-analysis result with up to 10 turns of context, turning one unclear point into a progressively clearer explanation |
| **Open model access** | Bring any OpenAI-compatible Base URL and Model instead of depending on one provider |

Local dictionary hits stay offline by default. Settings and history remain on your device, and privacy-safe logs omit selected text, prompt bodies, and API keys.

> Finding it useful? Give the project a [**Star**](https://github.com/YAHU2024/myTool) so others can discover it.

## Features

| Category | Features |
|:-----|:-----|
| Core Translation | SSE streaming token-by-token output · drag / double-click / triple-click selection · red-dot guidance interaction · floating window instant preview · 14 languages · automatic language detection |
| Smart Detection | Auto-classifies Translation / Code / Term and routes to specialized prompts · confidence diagnostics · browser / terminal scene awareness |
| Multi-mode Sessions | Switch between Translate / Command-parse / Term-explain / Deep-analysis on the same text · instant restore of finished results |
| Analysis Follow-ups | Up to 10 contextual questions after deep analysis · streaming answers · history-node navigation · retry for the latest failed turn |
| Quick Lookup | Local dictionary priority (ECDICT/OEWN) · automatic AI fallback on miss · one-click AI Chinese completion · unified POS labels · structured definitions / phonetics / examples / collocations · five recent items · speech and copy · centered popup / toggle visibility |
| Markdown | Incremental rendering while streaming · syntax highlighting after a fence closes · standalone code copy · tables / lists / quotes · only http/https links allowed |
| Text-to-Speech | Edge TTS online synthesis · read selected text · one-click read of translation result · automatic language matching |
| Translation History | SQLite local persistence · search & filter by time / language · paginated browsing · double-click to copy · Anki-format export |
| System Integration | Two independent global hotkey sets (select-to-translate / quick lookup) · lookup hotkey has on/off toggle disabled by default · single-click tray lookup · restore latest translation from the context menu · launch on startup · in-browser trigger · single-instance guard |
| Deep Analysis | 4 built-in presets (general / language-learning / literary / business) · custom profile create / duplicate / edit / delete · multi-turn profile management |
| Model Access | Custom OpenAI-compatible Base URL and Model · saved configurations grouped by domain · thinking mode disabled by default · explicit thinking control for Zhipu / DeepSeek / SiliconFlow |
| Performance | LRU + TTL semantic cache · latest-request-wins conflict protection · request snapshot isolation · live setting changes don't affect in-flight requests |
| Auto Update | GitHub Release delivery · silent check on startup · system-proxy compatible · Inno Setup dual installer · SHA256 verification |
| Privacy & Security | Zero-pollution clipboard access · desensitized logs (no original text / API key / prompt body) · local config never uploaded |
| Ops & Diagnostics | Structured JSON Lines logs · dedicated viewer · multi-file switching · level / keyword filtering · P50/P95/P99 latency · auto cleanup |

---

## Screenshots

### AI Select-to-Translate · Red-dot Guidance

<p align="center">
  <img src="docs/images/红点翻译功能展示.gif" alt="Select-to-translate demo" width="85%">
</p>

Select text to open red-dot guidance and route it to translation, code, or term mode with a **streaming AI result**. Trigger it by drag, double-click, or triple-click.

---

### Analysis Follow-ups · Keep Exploring the Result

<p align="center">
  <img src="docs/images/解析追问功能展示.gif" alt="Analysis follow-up demo" width="85%">
</p>

After deep analysis, keep asking in the same floating window with up to 10 contextual turns. Answers stream in place, while history nodes let you revisit, locate, or retry the latest turn.

---

### Settings · Multi-model & Hotkey Management

<p align="center">
  <img src="docs/images/设置页展示.gif" alt="Settings page" width="85%">
</p>

Multi-model switching, customizable global hotkeys, analysis profile management — **changes apply instantly without restart**.

---

### AI Lookup · Local Dictionary Foundation, Cloud Model Completion

<p align="center">
  <img src="docs/images/快速查词窗口.png" alt="Quick Lookup window" width="85%">
</p>

Single-click the tray or press `Alt+W` to open a compact lookup panel. **ECDICT + OEWN stay first**, while AI can complete missing Chinese definitions; a local miss falls back to the cloud model for structured definitions and optional phonetics, examples, and collocations.

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
| **Full (recommended)** | ~85 MB | Bundles the runtime and local dictionary → [Download latest full edition](https://github.com/YAHU2024/myTool/releases/latest) |
| **Standard** | ~47 MB | Bundles the local dictionary; requires the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) → [All releases](https://github.com/YAHU2024/myTool/releases) |

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

Single-click the tray icon or press `Alt+W` (enable "Quick Lookup hotkey" in Settings first) to show or hide Quick Lookup, then enter a word or phrase and press `Enter`. Lookup prefers the local dictionary at `Data\word-dictionary.db` (ECDICT + OEWN). Part-of-speech labels are consistently localized, ECDICT Chinese definitions and OEWN English definitions stay in separate fields, and untranslated OEWN examples are labeled as English examples. Local hits send nothing by default. Only clicking "AI 补全中文" sends missing English definitions and examples to the configured OpenAI-compatible provider; enriched results are labeled "本地词典 + AI 补全 · model." Missing local entries still fall back to AI lookup. Release packages bundle the local dictionary by default; source builds must run `scripts\prepare-word-dictionary.ps1` from the repository root first to generate it. The five recent items are process-local and cleared on exit. Use "Restore latest translation" in the tray context menu for the previous select-to-translate result.

---

## Configure API

Right-click the tray icon and open the settings window:

| Field | Description | Example |
|:-----|:-----|:-------|
| Base URL | API endpoint | `https://api.siliconflow.cn/v1` |
| API Key | Your key | `sk-xxxxxxxxxxxxxxxx` |
| Model | Model name | `Qwen/Qwen3-8B` |

The model dropdown groups saved configurations by domain and auto-fills URL and Key on selection. Thinking mode is disabled by default. When enabled, Zhipu and DeepSeek use `thinking.type`, while SiliconFlow uses `enable_thinking`; unrecognized providers do not receive an assumed thinking parameter.

Quick Lookup shares the translation Base URL, API Key, and Model settings. Local dictionary hits do not require an API key or network; only local misses send the query to the configured provider. AI-generated definitions are learning aids rather than authoritative dictionary data, and uncertain optional fields such as phonetics may be omitted.

<details>
<summary>One-click provider reference (expand)</summary>

<br>

| Provider | Base URL | Model |
|:-------|:---------|:------|
| SiliconFlow (free recommended) | `https://api.siliconflow.cn/v1` | `Qwen/Qwen3-8B` |
| Zhipu GLM | `https://open.bigmodel.cn/api/paas/v4` | `glm-4.7-flash` |
| DeepSeek | `https://api.deepseek.com/v1` | `deepseek-chat` |
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
│   ├── CopyShortcut.cs                # Copy shortcut helper
│   ├── AutoScrollController.cs        # Streaming auto-scroll (pause/resume on user action)
│   ├── LatestRequestCoordinator.cs    # latest-request-wins coordinator
│   ├── LatestPresentationCoordinator.cs  # Presentation identity coordinator
│   ├── FloatingResultSessionCoordinator.cs  # Multi-mode session manager
│   ├── TrayClickCoordinator.cs        # Tray click coordinator (left/right/scroll actions)
│   ├── WordLookupSessionCoordinator.cs # Lookup session race-condition guard
│   ├── WordLookupTextFormatter.cs     # Lookup result text formatter
│   ├── RecentLookupBuffer.cs          # Recent lookup buffer
│   └── TtsPlaybackCoordinator.cs      # TTS playback coordinator (multi-owner, busy avoidance)
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
│   ├── TtsSpeakException.cs           # TTS exception class
│   ├── IWordLookupService.cs          # Word lookup service interface
│   ├── OpenAIWordLookupService.cs     # OpenAI-compatible word lookup
│   ├── LocalDictionaryWordLookupService.cs # ECDICT + OEWN local lookup
│   ├── CompositeWordLookupService.cs   # Local dictionary first, AI fallback
│   └── WordLookupPromptBuilder.cs     # Word lookup prompt builder
│
├── Models/                            # Data models
│   ├── AppSettings.cs                 # Settings (multi-model / hotkeys / profiles / update)
│   ├── TranslationRequest.cs          # Immutable request snapshot
│   ├── FloatingResultSession.cs       # Multi-mode session state
│   ├── AnalysisPromptProfile.cs       # Custom analysis profile
│   ├── TranslationTriggerMode.cs      # Translation trigger mode enum
│   └── WordLookupModels.cs            # Word lookup result models (definition/phonetic/example/collocation)
│
├── Helpers/                           # Utilities
│   ├── ConfigManager.cs               # JSON config read/write + migration
│   ├── Logger.cs                      # Async logger (JSON Lines / rotation / cleanup)
│   ├── LogEvent.cs                    # Structured log event model
│   ├── MarkdownRenderer.cs            # Safe Markdown renderer
│   ├── CodeSyntaxHighlighter.cs       # Local fenced-code syntax highlighting
│   ├── Win32Api.cs                    # Win32 P/Invoke declarations
│   ├── DpiHelper.cs                   # DPI scaling coordinate conversion
│   ├── ApiEndpointValidator.cs        # API endpoint format validation
│   └── AuthenticodeVerifier.cs        # Installer digital signature verification
│
├── UI/                                # User interface
│   ├── FloatingWindow.xaml/.cs        # Floating window (multi-mode/Markdown/TTS/pin)
│   ├── RedDotWindow.xaml/.cs          # Red-dot guidance window
│   ├── QuickLookupWindow.xaml/.cs     # Quick lookup window (structured definition/speech)
│   ├── TrayIconManager.cs            # System tray (context menu / toast)
│   ├── SettingsWindow.xaml/.cs       # Settings (models / hotkeys / profiles / update)
│   ├── DownloadUpdateWindow.xaml/.cs  # Update download window
│   ├── HistoryWindow.xaml/.cs        # Translation history
│   ├── LogViewerWindow.xaml/.cs      # Log viewer
│   ├── LogEntryReader.cs             # Log read & filter
│   ├── FloatingWindowAnchor.cs        # Window anchor positioning
│   ├── FloatingWindowPlacement.cs     # Window placement management
│   ├── TrayPanelPlacement.cs          # Tray panel placement (multi-monitor DPI)
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
│   ├── RELEASE.md                     # Release process
│   ├── RELEASE_NOTES_NEXT.md          # Next-release notes draft
│   └── THIRD_PARTY_NOTICES.md         # Third-party notices
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
| Standard | ~47 MB | Local dictionary included; requires .NET 8 runtime |
| Full | ~85 MB | Local dictionary included; self-contained |

### Auto update

On startup the app silently checks the latest GitHub Release. When a new version is found, a tray toast notifies the user; clicking it opens the update dialog. Download and install are handled by AutoUpdater.NET, with SHA256 integrity verification.

See [docs/RELEASE.md](docs/RELEASE.md) for the release process and the [next-release notes draft](docs/RELEASE_NOTES_NEXT.md) for pending user-visible changes.

---

## Roadmap

| Phase | Core work | Status |
|:----:|:---------|:----:|
| 1–12 | Skeleton, select-to-translate, tray, history, smart detection, semantic cache, multi-mode sessions, real-time Markdown, syntax highlighting, structured logs, TTS, auto update | done |
| 13 | Quick lookup panel + independent global hotkey + tray click integration + local dictionary (ECDICT/OEWN) + AI Chinese completion + POS normalization | done |
| 14 | Contextual analysis follow-ups + streaming answers + history-node navigation | done |
| 15 | Performance optimization | planned |
| 16 | UI unification & internationalization | planned |

---

## Open Source Acknowledgements

QuickTranslate uses open-source components including [AutoUpdater.NET](https://github.com/ravibpatel/AutoUpdater.NET), [Markdig](https://github.com/xoofx/markdig), [ColorCode](https://github.com/CommunityToolkit/ColorCode-Universal), [Entity Framework Core](https://github.com/dotnet/efcore), and [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw). Its local dictionary is built from [ECDICT](https://github.com/skywind3000/ECDICT) and [Open English WordNet](https://en-word.net/). We thank their maintainers and contributors.

See [Third-Party Notices](docs/THIRD_PARTY_NOTICES.md) for complete sources, versions, attributions, and license terms.

---

## License

<div align="center">

New versions released on or after 2026-08-06 are licensed under the
[Mozilla Public License 2.0 (MPL-2.0)](LICENSE).
MPL-2.0 permits personal, commercial, and redistribution use. When distributing
MPL-covered files with modifications, the modified source for those files must
also be made available. New independent files may use other licenses. The
project name, logo, and icons are not licensed as trademarks.

Versions released before this date remain available under the original MIT
License included with those releases; this change is not retroactive.

</div>
