#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""README 项目结构自动更新脚本（注释映射驱动）

用法:
  python scripts/update-readme-tree.py            # 生成并显示差异（不写文件）
  python scripts/update-readme-tree.py --write    # 生成并写入 README.md / README.en.md
  python scripts/update-readme-tree.py --check    # 供 CI 使用：有差异则退出码 1

原理:
  - 结构块内容由本文件内的“路径 -> 注释”映射表驱动（保持现有精选 + 注释风格）
  - 每次运行会校验映射路径在文件系统中仍存在，并报告未映射的新文件
  - 新文件不会自动加入（不猜注释），需人工在映射表中补充条目
"""

import argparse
import difflib
import os
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 顶层目录扫描时的忽略项（与 .gitignore 同源，避免误报）
IGNORE_TOP = {
    ".git", ".workbuddy", ".idea", ".vs", "publish", "bin", "obj",
    ".build-output", "__pycache__", ".agents", ".claude", "AGENTS.md",
    "local-ocr-fixtures", "m3-scene-output",
}
# QuickTranslate/ 下不入库或无需展示的项（Data 为本地词典数据，*.db 被 gitignore）
IGNORE_QUICK = {".build-output", ".m3-run-build", "__pycache__", "bin", "obj", "Data"}


def _mk(*pairs):
    return dict(pairs)


# ---------------------------------------------------------------------------
# 注释映射表（顺序即渲染顺序；目录以 / 结尾）
# ---------------------------------------------------------------------------
ZH_QUICK = _mk(
    # Core
    ("Core/", "核心引擎"),
    ("Core/GlobalKeyboardHook.cs", "全局键盘钩子（独立消息循环）"),
    ("Core/SelectionDetector.cs", "鼠标钩子选词检测（拖拽/双击/三击）"),
    ("Core/SelectionLocator.cs", "UIA 像素级选区定位"),
    ("Core/ClipboardHelper.cs", "零污染剪贴板（序列号检测+恢复）"),
    ("Core/ClipboardRestoreCoordinator.cs", "后台剪贴板恢复队列"),
    ("Core/ContentTypeDetector.cs", "智能内容识别（Translation/Code/Term）"),
    ("Core/BrowserDetector.cs", "浏览器窗口感知"),
    ("Core/TerminalDetector.cs", "终端宿主识别与复制风险判断"),
    ("Core/SelectionCapturePolicy.cs", "选区复制动作安全策略"),
    ("Core/RecentSelectionCopyEvaluator.cs", "选中即复制判定（OSC52/copyOnSelection）"),
    ("Core/UiaCircuitBreaker.cs", "UIA 失败熔断与恢复"),
    ("Core/CopyShortcut.cs", "复制快捷键辅助"),
    ("Core/AnalysisConversationFormatter.cs", "解析追问对话上下文格式化"),
    ("Core/AutoScrollController.cs", "流式自动滚动（用户操作暂停/恢复）"),
    ("Core/LatestRequestCoordinator.cs", "latest-request-wins 请求协调"),
    ("Core/LatestPresentationCoordinator.cs", "展示身份协调"),
    ("Core/FloatingResultSessionCoordinator.cs", "多模式会话统一管理"),
    ("Core/TranslationDirectionResolver.cs", "自动/手动翻译方向决策"),
    ("Core/TranslationRouteResolver.cs", "翻译与解释模式路由"),
    ("Core/ModelProfileCatalog.cs", "当前会话可用模型方案目录"),
    ("Core/ModelSelectionCoordinator.cs", "会话级模型切换协调"),
    ("Core/TrayClickCoordinator.cs", "托盘点击协调（左键/右键/滚轮动作）"),
    ("Core/WordLookupSessionCoordinator.cs", "查词会话防竞态管理"),
    ("Core/WordLookupTextFormatter.cs", "查词结果格式化"),
    ("Core/RecentLookupBuffer.cs", "最近查词缓冲区"),
    ("Core/ReasoningSummaryAccumulator.cs", "思考摘要累积（上限截断）"),
    ("Core/StreamingCompositionMetrics.cs", "流式组合指标"),
    ("Core/StreamingDispatcherMetrics.cs", "流式分发指标"),
    ("Core/StreamingPresentationPump.cs", "流式展示帧泵（帧合并/发布）"),
    ("Core/StreamingRuntimeMetrics.cs", "流式运行指标"),
    ("Core/TtsPlaybackCoordinator.cs", "TTS 播放协调（多所有者、忙避让）"),
    ("Core/OcrBlockValidator.cs", "OCR 文本块与资源边界校验"),
    ("Core/OcrBlockAggregator.cs", "OCR 行块确定性聚合"),
    ("Core/OcrLanguageSelector.cs", "OCR 语言选择与降级"),
    ("Core/OcrTextNormalizer.cs", "OCR 文本规范化"),
    ("Core/ScreenshotTranslationCoordinator.cs", "截图翻译 OCR 到译文协调"),
    ("Core/ScreenshotSelection.cs", "截图框选物理矩形与资源门禁"),
    # Database
    ("Database/", "持久化层"),
    ("Database/TranslationRecord.cs", "翻译历史模型"),
    ("Database/TranslationDbContext.cs", "EF Core SQLite 上下文"),
    # Services
    ("Services/", "业务服务"),
    ("Services/ITranslationService.cs", "翻译服务接口"),
    ("Services/TranslationStreamEvent.cs", "流式事件类型（开始/内容增量/推理增量/完成）"),
    ("Services/OpenAITranslationService.cs", "OpenAI 兼容 SSE 流式翻译"),
    ("Services/ProviderKind.cs", "官方 API Host 与供应商类型解析"),
    ("Services/ProviderModelCapabilities.cs", "公共模型能力描述"),
    ("Services/ProviderRequestPolicy.cs", "供应商请求参数策略"),
    ("Services/ProviderHttpError.cs", "安全的供应商 HTTP 错误提取"),
    ("Services/TranslationPromptBuilder.cs", "翻译任务与输入保护 Prompt"),
    ("Services/TranslationEchoDetector.cs", "原文回显质量检测"),
    ("Services/BigModelModelCapabilities.cs", "智谱模型思考能力"),
    ("Services/DeepSeekModelCapabilities.cs", "DeepSeek 模型思考能力"),
    ("Services/SiliconFlowModelCapabilities.cs", "SiliconFlow 模型思考能力"),
    ("Services/OpenAIModelCapabilities.cs", "OpenAI 模型推理能力"),
    ("Services/PromptInputContract.cs", "模型输入安全与长度契约"),
    ("Services/TranslationCacheService.cs", "语义缓存（LRU + 30min TTL）"),
    ("Services/TranslationMetrics.cs", "指标统计（P50/P95/P99）"),
    ("Services/HistoryExporter.cs", "翻译历史导出（Anki/CSV）"),
    ("Services/AnalysisPromptCatalog.cs", "内置/自定义解析方案目录"),
    ("Services/UpdateService.cs", "自动更新（GitHub Release + AutoUpdater.NET）"),
    ("Services/FeedbackContentBuilder.cs", "公开反馈字段构建与敏感内容检查"),
    ("Services/FeedbackLinkService.cs", "固定 GitHub Issue Form 链接"),
    ("Services/CrashRecoveryTracker.cs", "异常退出状态与恢复提示跟踪"),
    ("Services/ITtsService.cs", "TTS 服务接口"),
    ("Services/EdgeTtsService.cs", "Edge TTS 朗读服务"),
    ("Services/EdgeTtsClient.cs", "Edge TTS WebSocket 客户端"),
    ("Services/TtsTextSelector.cs", "TTS 文本选择器"),
    ("Services/TtsSpeakException.cs", "TTS 异常类"),
    ("Services/IOcrService.cs", "OCR 引擎无关接口"),
    ("Services/ScreenshotTranslationMapping.cs", "截图翻译 UnitId 映射"),
    ("Services/IScreenshotCaptureService.cs", "截图捕获接口"),
    ("Services/GdiScreenshotCaptureService.cs", "GDI 物理像素截图捕获"),
    ("Services/WindowsMediaOcrService.cs", "Windows 内置 OCR 适配器"),
    ("Services/IWordLookupService.cs", "查词服务接口"),
    ("Services/IWordLookupEnrichmentService.cs", "AI 查词增强接口"),
    ("Services/OpenAIWordLookupService.cs", "OpenAI 兼容查词服务"),
    ("Services/LocalDictionaryWordLookupService.cs", "ECDICT + OEWN 本地查词"),
    ("Services/CompositeWordLookupService.cs", "本地词典优先，AI 兜底"),
    ("Services/WordLookupPromptBuilder.cs", "查词 Prompt 构建器"),
    ("Services/WordPartOfSpeechNormalizer.cs", "词性标签标准化"),
    # Models
    ("Models/", "数据模型"),
    ("Models/AppSettings.cs", "配置模型（多模型/快捷键/解析预设/更新设置）"),
    ("Models/ProviderPreset.cs", "无密钥供应商预置目录"),
    ("Models/TranslationRequest.cs", "不可变请求快照"),
    ("Models/TranslationRequestContext.cs", "会话请求语义快照"),
    ("Models/TranslationDirectionDecision.cs", "翻译方向决策结果"),
    ("Models/FloatingResultSession.cs", "多模式会话状态"),
    ("Models/AnalysisPromptProfile.cs", "自定义解析方案"),
    ("Models/AnalysisFollowUpRequest.cs", "解析追问请求与语义快照"),
    ("Models/TranslationTriggerMode.cs", "翻译触发模式枚举"),
    ("Models/ThinkingModePreference.cs", "思考模式偏好"),
    ("Models/FeedbackModels.cs", "反馈草稿、诊断摘要与字段模型"),
    ("Models/WordLookupModels.cs", "查词结果模型（释义/音标/例句/搭配）"),
    ("Models/OcrModels.cs", "OCR 图像、文本块与资源限制模型"),
    # Helpers
    ("Helpers/", "工具类"),
    ("Helpers/ConfigManager.cs", "JSON 配置读写 + 旧配置迁移"),
    ("Helpers/Logger.cs", "异步日志器（JSON Lines/轮转/清理）"),
    ("Helpers/LogEvent.cs", "结构化日志事件模型"),
    ("Helpers/MarkdownRenderer.cs", "安全 Markdown 渲染"),
    ("Helpers/StreamingMarkdownRenderer.cs", "流式 Markdown 渲染器"),
    ("Helpers/CodeSyntaxHighlighter.cs", "围栏代码块本地语法高亮"),
    ("Helpers/Win32Api.cs", "Win32 P/Invoke 声明"),
    ("Helpers/DpiHelper.cs", "DPI 缩放坐标转换"),
    ("Helpers/ApiEndpointValidator.cs", "API 端点格式验证"),
    ("Helpers/AuthenticodeVerifier.cs", "安装包数字签名校验"),
    # UI
    ("UI/", "用户界面"),
    ("UI/FloatingWindow.xaml/.cs", "悬浮窗（多模式/Markdown/TTS/图钉）"),
    ("UI/MarkdownInteraction.cs", "Markdown 交互辅助"),
    ("UI/RedDotWindow.xaml/.cs", "红点引导窗口"),
    ("UI/ScreenshotSelectionWindow.xaml/.cs", "单显示器截图框选遮罩"),
    ("UI/QuickLookupWindow.xaml/.cs", "快速查词窗口（结构化释义/朗读）"),
    ("UI/TrayIconManager.cs", "系统托盘（右键菜单/气泡通知）"),
    ("UI/SettingsWindow.xaml/.cs", "设置窗口（模型/快捷键/解析方案/更新管理）"),
    ("UI/DownloadUpdateWindow.xaml/.cs", "更新下载窗口"),
    ("UI/UpdateAvailableWindow.xaml/.cs", "更新说明与用户确认窗口"),
    ("UI/ModelSelectorControl.xaml/.cs", "当前会话模型选择控件"),
    ("UI/HistoryWindow.xaml/.cs", "翻译历史查看"),
    ("UI/LogViewerWindow.xaml/.cs", "日志查看器"),
    ("UI/ThoughtBlockView.cs", "思考区块视图"),
    ("UI/LogEntryReader.cs", "日志读取与筛选"),
    ("UI/FeedbackWindow.xaml/.cs", "帮助与反馈窗口"),
    ("UI/CrashRecoveryPromptWindow.xaml/.cs", "异常退出后的恢复提示"),
    ("UI/SharedSettingsStyles.xaml", "反馈窗口复用的设置控件样式"),
    ("UI/SharedToolWindowStyles.xaml", "工具窗口共享样式"),
    ("UI/FloatingWindowAnchor.cs", "窗口锚点定位"),
    ("UI/FloatingWindowPlacement.cs", "窗口位置管理"),
    ("UI/TrayPanelPlacement.cs", "托盘面板位置计算（多显示器 DPI）"),
    ("UI/FloatingStatusMessage.cs", "状态消息"),
    ("UI/TransientButtonFeedback.cs", "瞬态按钮反馈"),
    # 根级文件
    ("Assets/", "应用图标资源"),
    ("app.manifest", "Windows 应用清单"),
    ("AssemblyInfo.cs", "程序集信息声明"),
    ("QuickTranslate.csproj", ".NET 8 项目文件"),
    ("MainWindow.xaml/.cs", "隐藏主窗口（稳定 WPF 生命周期）"),
    ("App.xaml/.cs", "应用入口（单实例/更新调度/事件分发）"),
)

ZH_TOP = _mk(
    (".github/", "GitHub Actions 工作流 & Issue 模板"),
    ("QuickTranslate/", "主项目源码"),
    ("QuickTranslate.Tests/", "xUnit 单元测试（仅列代表性文件）"),
    ("QuickTranslate.Tests/FeedbackContentBuilderTests.cs", "反馈字段与敏感内容测试"),
    ("QuickTranslate.Tests/FeedbackLinkServiceTests.cs", "Issue Form 链接测试"),
    ("QuickTranslate.Tests/CrashRecoveryTrackerTests.cs", "异常退出状态测试"),
    ("QuickTranslate.Tests/FeedbackWindowTests.cs", "反馈窗口样式加载测试"),
    ("installer/", "Inno Setup 安装脚本 + version.xml"),
    ("scripts/", "辅助脚本"),
    ("site/", "GitHub Pages 项目站点（静态页 + 素材）"),
    ("docs/", "项目文档"),
    ("docs/images/", "文档配图"),
    ("docs/LOGGING.md", "日志功能文档"),
    ("docs/PR_MERGE_GUIDE.md", "PR 创建、审批与合并流程"),
    ("docs/RELEASE.md", "发布流程文档"),
    ("docs/RELEASE_NOTES_NEXT.md", "下一版本发布说明草稿"),
    ("docs/THIRD_PARTY_NOTICES.md", "第三方依赖声明"),
    (".gitignore", "Git 忽略规则"),
    ("CONTRIBUTING.md", "贡献指南"),
    ("LICENSE", ""),
    ("README.en.md", "英文 README"),
    ("README.md", ""),
)

EN_QUICK = _mk(
    ("Core/", "Core engine"),
    ("Core/GlobalKeyboardHook.cs", "Global keyboard hook (independent message loop)"),
    ("Core/SelectionDetector.cs", "Mouse hook selection detection (drag/double/triple-click)"),
    ("Core/SelectionLocator.cs", "UIA pixel-level selection locator"),
    ("Core/ClipboardHelper.cs", "Zero-pollution clipboard (serial detection + restore)"),
    ("Core/ClipboardRestoreCoordinator.cs", "Background clipboard restore queue"),
    ("Core/ContentTypeDetector.cs", "Smart content detection (Translation / Code / Term)"),
    ("Core/BrowserDetector.cs", "Browser window awareness"),
    ("Core/TerminalDetector.cs", "Terminal host awareness + copy-risk detection"),
    ("Core/SelectionCapturePolicy.cs", "Selection-copy safety policy"),
    ("Core/RecentSelectionCopyEvaluator.cs", "Select-to-copy detection (OSC52 / copyOnSelection)"),
    ("Core/UiaCircuitBreaker.cs", "UIA failure breaker and recovery"),
    ("Core/CopyShortcut.cs", "Copy shortcut helper"),
    ("Core/AnalysisConversationFormatter.cs", "Analysis follow-up conversation formatting"),
    ("Core/AutoScrollController.cs", "Streaming auto-scroll (pause/resume on user action)"),
    ("Core/LatestRequestCoordinator.cs", "latest-request-wins request coordination"),
    ("Core/LatestPresentationCoordinator.cs", "Presentation identity coordination"),
    ("Core/FloatingResultSessionCoordinator.cs", "Multi-mode session coordination"),
    ("Core/TranslationDirectionResolver.cs", "Auto/manual translation direction decisions"),
    ("Core/TranslationRouteResolver.cs", "Translation and explanation mode routing"),
    ("Core/ModelProfileCatalog.cs", "Session-level available model profile catalog"),
    ("Core/ModelSelectionCoordinator.cs", "Session-level model-switch coordination"),
    ("Core/TrayClickCoordinator.cs", "Tray interaction coordination (left/right/scroll)"),
    ("Core/WordLookupSessionCoordinator.cs", "Lookup session race-condition guard"),
    ("Core/WordLookupTextFormatter.cs", "Lookup result text formatter"),
    ("Core/RecentLookupBuffer.cs", "Recent lookup buffer"),
    ("Core/ReasoningSummaryAccumulator.cs", "Reasoning summary accumulation (cap enforcement)"),
    ("Core/StreamingCompositionMetrics.cs", "Streaming composition metrics"),
    ("Core/StreamingDispatcherMetrics.cs", "Streaming dispatch metrics"),
    ("Core/StreamingPresentationPump.cs", "Streaming presentation frame pump (coalescing/publishing)"),
    ("Core/StreamingRuntimeMetrics.cs", "Streaming runtime metrics"),
    ("Core/TtsPlaybackCoordinator.cs", "TTS playback coordination (multi-owner, busy avoidance)"),
    ("Core/OcrBlockValidator.cs", "OCR text-block and resource-boundary validation"),
    ("Core/OcrBlockAggregator.cs", "Deterministic OCR line-block aggregation"),
    ("Core/OcrLanguageSelector.cs", "OCR language selection and fallback"),
    ("Core/OcrTextNormalizer.cs", "OCR text normalization"),
    ("Core/ScreenshotTranslationCoordinator.cs", "Screenshot OCR-to-translation coordination"),
    ("Core/ScreenshotSelection.cs", "Physical screenshot region and resource gate"),
    ("Database/", "Persistence layer"),
    ("Database/TranslationRecord.cs", "Translation history model"),
    ("Database/TranslationDbContext.cs", "EF Core SQLite context"),
    ("Services/", "Business services"),
    ("Services/ITranslationService.cs", "Translation service interface"),
    ("Services/TranslationStreamEvent.cs", "Streaming event kinds (started / content delta / reasoning delta / completed)"),
    ("Services/OpenAITranslationService.cs", "OpenAI-compatible streaming translation"),
    ("Services/ProviderKind.cs", "Official API host and provider parsing"),
    ("Services/ProviderModelCapabilities.cs", "Shared model capability descriptor"),
    ("Services/ProviderRequestPolicy.cs", "Provider request parameter policy"),
    ("Services/ProviderHttpError.cs", "Safe provider HTTP error extraction"),
    ("Services/TranslationPromptBuilder.cs", "Translation task and input-protection prompts"),
    ("Services/TranslationEchoDetector.cs", "Original-text echo quality detection"),
    ("Services/BigModelModelCapabilities.cs", "Zhipu model-thinking capabilities"),
    ("Services/DeepSeekModelCapabilities.cs", "DeepSeek model-thinking capabilities"),
    ("Services/SiliconFlowModelCapabilities.cs", "SiliconFlow model-thinking capabilities"),
    ("Services/OpenAIModelCapabilities.cs", "OpenAI reasoning capabilities"),
    ("Services/PromptInputContract.cs", "Model input safety and length contract"),
    ("Services/TranslationCacheService.cs", "Semantic cache (LRU + 30 min TTL)"),
    ("Services/TranslationMetrics.cs", "Metrics (P50/P95/P99)"),
    ("Services/HistoryExporter.cs", "History export (Anki/CSV)"),
    ("Services/AnalysisPromptCatalog.cs", "Built-in / custom analysis profiles"),
    ("Services/UpdateService.cs", "Auto-updater (GitHub Release + AutoUpdater.NET)"),
    ("Services/FeedbackContentBuilder.cs", "Public feedback fields and sensitivity checks"),
    ("Services/FeedbackLinkService.cs", "Fixed GitHub Issue Form links"),
    ("Services/CrashRecoveryTracker.cs", "Unclean-exit state and recovery-prompt tracking"),
    ("Services/ITtsService.cs", "TTS service interface"),
    ("Services/EdgeTtsService.cs", "Edge TTS read-aloud service"),
    ("Services/EdgeTtsClient.cs", "Edge TTS WebSocket client"),
    ("Services/TtsTextSelector.cs", "TTS text selector"),
    ("Services/TtsSpeakException.cs", "TTS exception class"),
    ("Services/IOcrService.cs", "Engine-agnostic OCR interface"),
    ("Services/ScreenshotTranslationMapping.cs", "Screenshot translation UnitId mapping"),
    ("Services/IScreenshotCaptureService.cs", "Screenshot capture interface"),
    ("Services/GdiScreenshotCaptureService.cs", "GDI physical-pixel screenshot capture"),
    ("Services/WindowsMediaOcrService.cs", "Windows built-in OCR adapter"),
    ("Services/IWordLookupService.cs", "Word lookup service interface"),
    ("Services/IWordLookupEnrichmentService.cs", "AI word lookup enrichment interface"),
    ("Services/OpenAIWordLookupService.cs", "OpenAI-compatible word lookup service"),
    ("Services/LocalDictionaryWordLookupService.cs", "ECDICT + OEWN local lookup"),
    ("Services/CompositeWordLookupService.cs", "Local dictionary first, AI fallback"),
    ("Services/WordLookupPromptBuilder.cs", "Word lookup prompt builder"),
    ("Services/WordPartOfSpeechNormalizer.cs", "POS label normalization"),
    ("Models/", "Data models"),
    ("Models/AppSettings.cs", "Settings (multi-model / hotkeys / profiles / updates)"),
    ("Models/ProviderPreset.cs", "Credential-free provider preset catalog"),
    ("Models/TranslationRequest.cs", "Immutable request snapshot"),
    ("Models/TranslationRequestContext.cs", "Session request semantic snapshot"),
    ("Models/TranslationDirectionDecision.cs", "Translation direction decision result"),
    ("Models/FloatingResultSession.cs", "Multi-mode session state"),
    ("Models/AnalysisPromptProfile.cs", "Custom analysis profile"),
    ("Models/AnalysisFollowUpRequest.cs", "Analysis follow-up request and semantic snapshot"),
    ("Models/TranslationTriggerMode.cs", "Translation trigger mode enum"),
    ("Models/ThinkingModePreference.cs", "Thinking-mode preference"),
    ("Models/FeedbackModels.cs", "Feedback draft, diagnostics, and field models"),
    ("Models/WordLookupModels.cs", "Word lookup result models (definition / phonetic / example / collocation)"),
    ("Models/OcrModels.cs", "OCR image, text-block, and resource-limit models"),
    ("Helpers/", "Utilities"),
    ("Helpers/ConfigManager.cs", "JSON configuration read/write + migration"),
    ("Helpers/Logger.cs", "Async logger (JSON Lines / rotation / cleanup)"),
    ("Helpers/LogEvent.cs", "Structured log event model"),
    ("Helpers/MarkdownRenderer.cs", "Safe Markdown renderer"),
    ("Helpers/StreamingMarkdownRenderer.cs", "Streaming Markdown renderer"),
    ("Helpers/CodeSyntaxHighlighter.cs", "Local code-block syntax highlighting"),
    ("Helpers/Win32Api.cs", "Win32 P/Invoke declarations"),
    ("Helpers/DpiHelper.cs", "DPI coordinate conversion"),
    ("Helpers/ApiEndpointValidator.cs", "API endpoint format validation"),
    ("Helpers/AuthenticodeVerifier.cs", "Installer digital-signature verification"),
    ("UI/", "User interface"),
    ("UI/FloatingWindow.xaml/.cs", "Floating window (multi-mode / Markdown / TTS / pin)"),
    ("UI/MarkdownInteraction.cs", "Markdown interaction helper"),
    ("UI/RedDotWindow.xaml/.cs", "Red-dot guidance window"),
    ("UI/ScreenshotSelectionWindow.xaml/.cs", "Single-monitor screenshot selection overlay"),
    ("UI/QuickLookupWindow.xaml/.cs", "Quick lookup window (structured definitions / speech)"),
    ("UI/TrayIconManager.cs", "System tray (context menu / toast)"),
    ("UI/SettingsWindow.xaml/.cs", "Settings (models / hotkeys / profiles / updates)"),
    ("UI/DownloadUpdateWindow.xaml/.cs", "Update download window"),
    ("UI/UpdateAvailableWindow.xaml/.cs", "Update details and confirmation"),
    ("UI/ModelSelectorControl.xaml/.cs", "Current-session model selector"),
    ("UI/HistoryWindow.xaml/.cs", "Translation history viewer"),
    ("UI/LogViewerWindow.xaml/.cs", "Log viewer"),
    ("UI/ThoughtBlockView.cs", "Thought-block view"),
    ("UI/LogEntryReader.cs", "Log reading and filtering"),
    ("UI/FeedbackWindow.xaml/.cs", "Help and feedback window"),
    ("UI/CrashRecoveryPromptWindow.xaml/.cs", "Unclean-exit recovery prompt"),
    ("UI/SharedSettingsStyles.xaml", "Settings control styles reused by feedback UI"),
    ("UI/SharedToolWindowStyles.xaml", "Shared tool-window styles"),
    ("UI/FloatingWindowAnchor.cs", "Window anchor positioning"),
    ("UI/FloatingWindowPlacement.cs", "Window placement management"),
    ("UI/TrayPanelPlacement.cs", "Tray-panel placement for multi-monitor DPI"),
    ("UI/FloatingStatusMessage.cs", "Status messages"),
    ("UI/TransientButtonFeedback.cs", "Transient button feedback"),
    ("Assets/", "App icon resources"),
    ("app.manifest", "Windows application manifest"),
    ("AssemblyInfo.cs", "Assembly metadata declarations"),
    ("QuickTranslate.csproj", ".NET 8 project file"),
    ("MainWindow.xaml/.cs", "Hidden main window (stable WPF lifecycle)"),
    ("App.xaml/.cs", "App entry (single-instance / update scheduling / dispatch)"),
)

EN_TOP = _mk(
    (".github/", "GitHub Actions workflows & issue templates"),
    ("QuickTranslate/", "Main source project"),
    ("QuickTranslate.Tests/", "xUnit test project (representative subset shown)"),
    ("QuickTranslate.Tests/FeedbackContentBuilderTests.cs", "Feedback fields and sensitivity tests"),
    ("QuickTranslate.Tests/FeedbackLinkServiceTests.cs", "Issue Form link tests"),
    ("QuickTranslate.Tests/CrashRecoveryTrackerTests.cs", "Unclean-exit state tests"),
    ("QuickTranslate.Tests/FeedbackWindowTests.cs", "Feedback window style-loading tests"),
    ("installer/", "Inno Setup scripts + version.xml"),
    ("scripts/", "Helper scripts"),
    ("site/", "GitHub Pages project site (static pages + assets)"),
    ("docs/", "Project documentation"),
    ("docs/images/", "Documentation images"),
    ("docs/LOGGING.md", "Logging guide"),
    ("docs/PR_MERGE_GUIDE.md", "PR creation, review, and merge workflow"),
    ("docs/RELEASE.md", "Release process"),
    ("docs/RELEASE_NOTES_NEXT.md", "Draft for next-release notes"),
    ("docs/THIRD_PARTY_NOTICES.md", "Third-party notices"),
    (".gitignore", "Ignore rules"),
    ("CONTRIBUTING.md", "Contribution guide"),
    ("LICENSE", ""),
    ("README.en.md", "English README"),
    ("README.md", ""),
)


# ---------------------------------------------------------------------------
# 树渲染
# ---------------------------------------------------------------------------
def render_tree(mapping, root_name):
    """按映射顺序渲染目录树；同层名称自动对齐到最大宽度 + 2 空格。

    映射键可为 "Core/"（目录，尾斜杠）或 "Core/xxx.cs"（文件）；
    组合键 "UI/Foo.xaml/.cs" 表示成对文件，作为整体名称处理。
    """
    children = {}
    dirs = set()
    for path, note in mapping.items():
        p = path.rstrip("/")
        if path.endswith("/"):
            dirs.add(p)
        if "/" in p:
            head, tail = p.rsplit("/", 1)
            if tail.startswith("."):
                # 组合键 Foo.xaml/.cs：把 .cs 合并回整体名称
                if "/" in head:
                    parent, name = head.rsplit("/", 1)
                else:
                    parent, name = "", head
                name = name + "/" + tail
            else:
                parent, name = head, tail
        else:
            parent, name = "", p
        children.setdefault(parent, []).append((name, note, p))

    # 第一遍：生成结构化行（层级、名称文本、注释）
    rows = []  # (depth, name_text, note)

    def walk(parent, prefix, depth):
        for i, (name, note, fullpath) in enumerate(children.get(parent, [])):
            last = i == len(children[parent]) - 1
            branch = "└── " if last else "├── "
            display = name + "/" if fullpath in dirs else name
            rows.append((depth, prefix + branch + display, note))
            if fullpath in children:
                walk(fullpath, prefix + ("    " if last else "│   "), depth + 1)

    walk("", "", 0)

    # 同层名称宽度对齐
    maxw = {}
    for depth, name_text, _ in rows:
        maxw[depth] = max(maxw.get(depth, 0), len(name_text))

    lines = [root_name.rstrip("/") + "/"]
    for depth, name_text, note in rows:
        if note:
            lines.append(name_text.ljust(maxw[depth] + 2) + "# " + note)
        else:
            lines.append(name_text)
    return lines


# ---------------------------------------------------------------------------
# 文件系统校验
# ---------------------------------------------------------------------------
def verify_mapping(mapping, root_dir):
    """校验映射路径存在；返回 missing 列表。

    组合键（如 "UI/FloatingWindow.xaml/.cs"）表示成对文件，
    校验其基础文件（FloatingWindow.xaml）存在即视为有效。
    """
    missing = []

    def _exists(path):
        full = os.path.join(root_dir, path)
        if os.path.exists(full):
            return True
        head, tail = os.path.split(path)
        if head and tail.startswith("."):
            # 组合键 Foo.xaml/.cs：校验 Foo.xaml 存在（顶层隐藏文件如 .gitignore 不走此分支）
            return os.path.exists(os.path.join(root_dir, head))
        return False

    for path in mapping:
        if not _exists(path):
            missing.append(path)
    return missing


def _covered_names(mapping):
    """映射覆盖的实际文件名集合（含组合键拆分的 Foo.xaml 与 Foo.xaml.cs）。"""
    covered = set()
    for path in mapping:
        p = path.rstrip("/")
        if "/" in p:
            head, tail = p.rsplit("/", 1)
            if tail.startswith("."):
                # 组合键 Foo.xaml/.cs → 覆盖 Foo.xaml 与 Foo.xaml.cs
                base = head.rsplit("/", 1)[-1]
                covered.add(base)
                covered.add(base + tail)
            else:
                covered.add(tail)
        else:
            covered.add(p)
    return covered


def scan_new(root_dir, mapping, ignore, recursive=False):
    """报告未映射且不在忽略列表的条目。

    recursive=False：只扫顶层条目（用于仓库根目录）。
    recursive=True：递归扫描源码文件（.cs/.xaml/.manifest/.csproj），
    用于 QuickTranslate/ 目录——子目录新增文件也能被发现。
    """
    covered = _covered_names(mapping)
    mapped = {p.split("/", 1)[0] for p in mapping}

    if not recursive:
        actual = set()
        for name in os.listdir(root_dir):
            if name in ignore or name in covered:
                continue
            if name.startswith(".") and name not in {".github", ".gitignore"}:
                continue
            actual.add(name)
        return sorted(actual - mapped)

    skip_dirs = ignore | {"bin", "obj"}
    found = set()
    for dirpath, dirnames, filenames in os.walk(root_dir):
        dirnames[:] = [d for d in dirnames if d not in skip_dirs]
        for fn in filenames:
            if not fn.endswith((".cs", ".xaml", ".manifest", ".csproj")):
                continue
            rel = os.path.relpath(os.path.join(dirpath, fn), root_dir).replace("\\", "/")
            if rel.split("/", 1)[0] in ignore:
                continue
            if fn in covered:  # 组合键拆分覆盖（如 Foo.xaml.cs）
                continue
            found.add(rel)
    return sorted(found)


# ---------------------------------------------------------------------------
# README 区块替换
# ---------------------------------------------------------------------------
def replace_block(content, anchor, new_block):
    """替换 anchor 标题后的第一个 ```text ... ``` 区块内容。"""
    lines = content.splitlines(keepends=True)
    out = []
    i = 0
    replaced = False
    while i < len(lines):
        if lines[i].rstrip("\n") == anchor and not replaced:
            # 找到该标题
            out.append(lines[i])
            i += 1
            # 跳过空行与说明行（如“由脚本维护”提示），直到代码块围栏
            while i < len(lines) and lines[i].strip() == "":
                out.append(lines[i])
                i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                out.append(lines[i])
                i += 1
            if i < len(lines) and lines[i].strip().startswith("```"):
                i += 1  # 跳过 ```text 行
                while i < len(lines) and not lines[i].strip().startswith("```"):
                    i += 1
                if i < len(lines):
                    i += 1  # 跳过闭合 ``` 行
                out.append("```text\n")
                for ln in new_block:
                    out.append(ln + "\n")
                out.append("```\n")
                replaced = True
                continue
        out.append(lines[i])
        i += 1
    if not replaced:
        raise RuntimeError(f"未找到区块锚点: {anchor}")
    return "".join(out)


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
def main():
    parser = argparse.ArgumentParser(description="更新 README 项目结构")
    parser.add_argument("--write", action="store_true", help="写入 README.md / README.en.md")
    parser.add_argument("--check", action="store_true", help="CI 校验：有差异则退出码 1")
    args = parser.parse_args()

    quick_root = os.path.join(REPO_ROOT, "QuickTranslate")
    problems = []
    any_diff = False

    for lang, quick_map, top_map, readme_name in (
        ("中文", ZH_QUICK, ZH_TOP, "README.md"),
        ("英文", EN_QUICK, EN_TOP, "README.en.md"),
    ):
        readme_path = os.path.join(REPO_ROOT, readme_name)
        with open(readme_path, encoding="utf-8") as f:
            content = f.read()

        new_quick = render_tree(quick_map, "QuickTranslate/")
        new_top = render_tree(top_map, "myTool/")

        content = replace_block(content, "## 项目结构" if lang == "中文" else "## Project Structure", new_quick)
        content = replace_block(content, "### 顶层目录" if lang == "中文" else "### Top-level layout", new_top)

        old_lines = open(readme_path, encoding="utf-8").read().splitlines()
        new_lines = content.splitlines()
        diff = list(difflib.unified_diff(old_lines, new_lines, fromfile=readme_name, tofile=readme_name + " (生成)", lineterm=""))
        if diff:
            any_diff = True
            print(f"[{lang}] {readme_name} 有差异，{len(diff)} 行：")
            print("\n".join(diff[:80]))
            if len(diff) > 80:
                print(f"...（共 {len(diff)} 行）")
        else:
            print(f"[{lang}] {readme_name} 一致")

        if args.write and diff:
            with open(readme_path, "w", encoding="utf-8", newline="\n") as f:
                f.write(content)

    # 文件系统校验（QuickTranslate/ 递归扫描源码文件；顶层只扫一层）
    for label, mapping, root, ignore, recursive in (
        ("QuickTranslate/", ZH_QUICK, quick_root, IGNORE_QUICK, True),
        ("顶层目录", ZH_TOP, REPO_ROOT, IGNORE_TOP, False),
    ):
        missing = verify_mapping(mapping, root)
        for p in missing:
            problems.append(f"[{label}] 映射路径不存在: {p}")
        new_files = scan_new(root, mapping, ignore, recursive=recursive)
        for p in new_files:
            problems.append(f"[{label}] 存在未映射的新条目: {p}（请在映射表补充注释，或加入忽略列表）")

    if problems:
        print("\n校验问题：")
        for p in problems:
            print("  ! " + p)

    if args.check:
        sys.exit(1 if (any_diff or problems) else 0)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
