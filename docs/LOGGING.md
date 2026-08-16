# QuickTranslate 日志功能使用文档

本文说明 QuickTranslate 日志功能的日常使用、配置、文件格式、性能指标、隐私边界和开发接入要求。当前日志系统面向本地故障排查，不包含远程上传或自动上报。

## 1. 快速开始

### 1.1 打开日志查看器

1. 在 Windows 系统托盘找到 QuickTranslate 图标。
2. 右键托盘图标。
3. 点击“日志查看”。

日志查看器采用单窗口模式。窗口已经打开时再次点击“日志查看”，应用会激活现有窗口，不会创建第二个实例。关闭日志查看器不会停止后台日志写入。

### 1.2 查看最近日志

日志查看器打开后默认选择最后写入时间最新的日志文件。界面包含以下区域：

| 控件 | 用途 |
| --- | --- |
| 日志文件 | 在当前日志、历史轮转日志、退出追踪和看门狗状态文件之间切换 |
| 级别 | 显示全部日志，或仅显示 Debug、Info、Warn、Error、Fatal 中的一种 |
| 搜索框 | 按模块名、事件名和结构化诊断字段进行不区分大小写的筛选 |
| 刷新 | 重新扫描日志文件并读取所选文件的最新内容 |
| 打开目录 | 使用 Windows 文件资源管理器打开日志目录 |
| 清理 | 立即执行当前保留天数和最大总大小策略 |

查看器一次最多读取所选文件末尾 5000 行，并最多列出最近 31 个日志文件，避免大文件阻塞界面。文件读取在后台进行，不占用日志写入线程。

### 1.3 常用排查方法

启动或退出异常：

1. 选择最新的 `quicktranslate-*.log`。
2. 将级别切换为 `Error` 或 `Fatal`。
3. 搜索 `app.`、`unhandled` 或相关模块名。
4. 托盘退出排查：搜索 `tray.exit.requested`、`app.onexit.`、`tts.dispose.`（默认 Info 可见）。
5. 如果应用异常退出，再查看 `shutdown-trace.log` 和 `watchdog.trace`。

翻译没有结果：

1. 搜索 `translation.`。
2. 查看是否存在 `translation.started`。
3. 检查后续是 `translation.completed`、`translation.failed`、`translation.cancelled` 还是 `translation.cache_hit`。
4. `translation.completed` 表示服务响应完成；`translation.presented` 表示最新有效请求已写入当前界面和历史。
5. 流式卡顿时先看 `average_chunk_gap_ms`、`max_chunk_gap_ms` 和 `stalled_chunk_count`，再看 `max_frame_latency_ms`：前三者高通常表示模型、服务商或网络缓冲，后者高表示本地 UI 呈现延迟。
6. 如果出现原文回显，搜索 `translation.echo_`。翻译内容保持正常流式显示；`echo_confirmed` 表示完整响应结束后检测到与原文高度一致，正文仍保留供用户判断，但不会缓存或写入历史。应用不会因此自动重试或切换模型。
7. 用户从悬浮窗切换模型时搜索 `translation.model_switch_requested`。事件只记录切换前后的模型、供应商和切换时请求是否仍在运行。

快速查词没有结果：

1. 搜索模块 `WordLookupService` 或事件 `lookup.`。
2. 本地词典是否启用由 `App` 模块的 `word_lookup.local_dictionary_enabled/disabled` 记录。
3. `lookup.started` 只记录查询的 Unicode 字符数量和 Provider 标识（`ecdict-oewn-local` 或 `openai-compatible`）。
4. `lookup.completed` 只记录释义、例句和搭配数量以及耗时。
5. 用户主动点击“AI 补全中文”时会记录 `enrichment.started/completed`；事件只包含输入长度、缺失项数量、耗时和 Provider 标识 `openai-compatible-enrichment`，不包含义项或例句文本。
6. 查词日志不会记录查询词、Prompt、释义、例句、API Key 或 Provider 响应正文。

划词、剪贴板或 UIA 异常：

- 搜索模块 `ClipboardHelper`、`SelectionDetector` 或 `SelectionLocator`。
- 终端取词决策可搜索 `TerminalDetector` 或事件 `terminal.copy_decision`。该事件只记录进程名、窗口类、焦点控件的 AutomationId/类名/控件类型、焦点进程是否匹配、模式、宿主风险、复制动作风险、决策原因和快捷键类型。
- UIA 焦点识别与选区定位使用独立熔断器。`uia.sta_failed` 和 `uia.circuit_open` 只记录能力类型、连续失败次数和异常类型；控件不支持 TextPattern、无选区或无边界矩形属于正常能力缺失，不计入熔断。
- `Warn` 一般表示本次操作失败但应用仍可继续运行。
- 日志只记录稳定事件名和异常类型，不记录窗口标题、命令行、剪贴板文本或选中文本。

## 2. 日志配置

打开托盘菜单中的“设置”，在“系统设置”区域配置日志。

### 2.1 日志级别

可选值：

| 级别 | 建议用途 | 会记录的内容 |
| --- | --- | --- |
| Debug | 短期深入排查 | Debug 及以上全部事件，数量最多 |
| Info | 日常使用，默认值 | 正常生命周期、警告和错误 |
| Warn | 只关注异常征兆 | Warn、Error、Fatal |
| Error | 只关注失败 | Error、Fatal |
| Fatal | 只关注严重故障 | Fatal |

日志级别是最低记录级别。例如选择 `Warn` 后，Debug 和 Info 不再写入新日志。修改设置并保存后立即对当前进程生效，无需重启。

排查结束后建议恢复为 `Info`，避免 Debug 日志增长过快。

### 2.2 保留天数

- 默认值：7 天。
- 有效范围：1 至 3650 天。
- 非法或超出范围的配置会回退或限制到安全范围。

清理依据文件最后写入时间执行，而不是 Windows 文件创建时间。复制或恢复过日志文件后，清理结果以其最后写入时间为准。

### 2.3 最大总大小

- 默认值：50 MB。
- 有效范围：1 MB 至 1024 MB。
- 设置界面使用 MB，配置文件内部保存字节数。

保存设置后会立即应用新上限并执行一次清理。

## 3. 日志文件与轮转

### 3.1 存储位置

日志位于：

```text
%APPDATA%\QuickTranslate\logs\
```

通常展开为：

```text
C:\Users\<用户名>\AppData\Roaming\QuickTranslate\logs\
```

推荐通过日志查看器的“打开目录”进入，不需要手动展开环境变量。

### 3.2 文件命名

主要日志文件按日期命名：

```text
quicktranslate-2026-07-23.log
```

同一天的单个文件达到 5 MB 后继续轮转：

```text
quicktranslate-2026-07-23-1.log
quicktranslate-2026-07-23-2.log
```

诊断辅助文件：

| 文件 | 用途 |
| --- | --- |
| `shutdown-trace.log` | 同步短行：托盘退出面包屑、Dispatcher/ProcessExit、控制台信号与非托管异常兜底 |
| `watchdog.trace` | 每两秒更新一次进程存活状态，用于判断异常终止时间 |

辅助文件同样会显示在日志查看器中，并纳入保留和总大小策略。应用运行期间会保护当前主日志和正在使用的辅助文件，避免手动清理影响当前进程。

### 3.3 自动清理顺序

应用启动、保存新的日志配置或点击“清理”时，按以下顺序处理：

1. 删除超过保留天数的日志。
2. 重新计算所有受管理日志的总大小。
3. 如果仍超过上限，按最后写入时间从旧到新删除。
4. 跳过当前正在写入或运行中受保护的文件。
5. 单个文件删除失败时继续处理其他文件，不中断应用。

“清理”不等于无条件清空目录。如果文件仍在保留期内且总大小未超限，它会继续保留。

## 4. 日志记录格式

### 4.1 JSON Lines

新的主日志采用 JSON Lines 格式：每行都是一个独立 JSON 对象，文件扩展名仍为 `.log`。示例：

```json
{"Timestamp":"2026-07-23T10:15:30.123-07:00","Level":"Info","Source":"TranslationService","EventName":"translation.completed","Context":{"operation":"translation","content_type":"Translation","requested_target_language":"简体中文","effective_target_language":"English","direction_relation":"Same","direction_confidence":"High","direction_reason":"SourceMatchesRequestedTarget","source_language_family":"Han","text_len":42,"result_len":18,"duration_ms":527.4}}
```

标准字段：

| 字段 | 含义 |
| --- | --- |
| `Timestamp` | 带本地时区偏移的事件时间 |
| `Level` | Debug、Info、Warn、Error 或 Fatal |
| `Source` | 产生日志的模块，例如 App、TranslationService、ClipboardHelper |
| `EventName` | 稳定、可搜索的事件名称 |
| `Context` | 不包含用户内容和凭据的结构化诊断字段 |

查看器也兼容旧版纯文本日志：

```text
2026-07-22 12:34:56.789 [WRN] [App] legacy warning
```

无法解析为结构化或旧版格式的行会作为 `Raw` 记录显示，单行最多展示前 1000 个字符。

### 4.2 常用上下文字段

| 字段 | 含义 |
| --- | --- |
| `operation` | translation 或 analysis 等请求类型 |
| `content_type` | Translation、Code、Term、Analysis 等内容模式 |
| `requested_target_language` | 用户为当前会话选择的目标语言 |
| `effective_target_language` | 方向判断后本次请求实际使用的目标语言；只有高置信度同语言结果才可能使用备选语言 |
| `direction_relation` | 源文本与请求目标的关系：Different、Same 或 Unknown |
| `direction_confidence` | 本地方向判断置信度：None、Low 或 High |
| `direction_reason` | 方向决策原因，例如 AutoDetectionDisabled、SourceMatchesRequestedTarget 或 SourceLanguageUnknown |
| `source_language_family` | 仅基于本地文字系统统计得到的语言族，例如 Han、Latin 或 Unknown |
| `text_len` | 输入字符数，不包含输入内容 |
| `result_len` | 结果字符数，不包含结果内容 |
| `duration_ms` | 操作耗时，单位毫秒 |
| `request_id` | 进程内请求身份，用于判断取消和过期请求 |
| `stream_chunk_count` | 服务接收或呈现泵发布的有效流式 chunk 数量 |
| `first_chunk_ms` | 从开始请求到收到首个有效 chunk 的耗时 |
| `average_chunk_gap_ms` | 相邻有效 chunk 到达间隔的平均值；只在至少收到两个有效 chunk 时计算 |
| `max_chunk_gap_ms` | 相邻有效 chunk 的最大到达间隔 |
| `stalled_chunk_count` | 到达间隔不小于 250ms 的 chunk 次数，用于识别服务端或网络停顿 |
| `ui_frame_count` | 合并后真正应用到 UI 的帧数 |
| `coalesced_chunk_count` | 被合并进已有 UI 帧的 chunk 数量 |
| `first_frame_latency_ms` | 首批 chunk 从发布到 UI 应用完成的管线耗时 |
| `max_frame_latency_ms` | 任一批 chunk 从发布到 UI 应用完成的最大管线耗时 |
| `average_ui_apply_ms` / `max_ui_apply_ms` | 呈现泵等待并执行 UI 帧的平均/最大耗时 |
| `average_dispatcher_queue_ms` / `max_dispatcher_queue_ms` | UI 委托进入 Dispatcher 后的平均/最大排队时间 |
| `average_ui_execution_ms` / `max_ui_execution_ms` | UI 委托实际执行的平均/最大耗时，不包含排队 |
| `average_markdown_render_ms` / `max_markdown_render_ms` | 增量 Markdown 更新的平均/最大耗时 |
| `markdown_allocated_bytes` / `markdown_parsed_characters` | 流式 Markdown 路径的线程分配量和累计解析字符数 |
| `gc_gen0_collections` / `gc_gen1_collections` / `gc_gen2_collections` | 请求期间发生的各代 GC 次数（进程口径） |
| `gc_pause_ms` | 请求期间新增的 GC 总暂停时间（进程口径） |
| `runtime_allocated_bytes` | 请求期间进程累计分配量的增量近似值 |
| `composition_requested_frame_count` | 内容更新后登记的合成帧请求数 |
| `composition_presented_frame_count` | 请求完成前观测到的真实 WPF 合成帧数 |
| `composition_coalesced_request_count` | 在同一合成帧前被合并的重复内容更新数 |
| `average_composition_wait_ms` / `max_composition_wait_ms` | 内容更新到下一次 `CompositionTarget.Rendering` 的平均/最大等待时间 |
| `error_type` / `exception_type` | 异常类型名称，不包含异常消息 |
| `query_scalars` | 查词输入的 Unicode 字符数量，不包含查询内容 |
| `senses` / `examples` / `collocations` | 结构化查词结果的项目数量，不包含项目正文 |

## 5. 性能指标说明

日志查看器底部显示当前进程内的统计快照：

```text
显示 120/250 条 | 今日完成 18 | 平均 430ms | P95 920ms | 缓存命中率 22% | 回显疑似/确认 3/2 | 模型切换 1/2
```

统计口径：

- “今日完成”包含 API 完成结果和缓存命中结果。
- 平均耗时、P50、P95、P99 仅使用最近 100 次真正完成的 API 请求。
- 缓存命中不计入 API 延迟分布，避免把近乎零耗时的缓存结果拉低延迟。
- 取消请求、失败请求和过期请求不会计入成功延迟。
- 缓存命中率直接来自 `TranslationCacheService` 的命中和未命中计数，不从日志文本推算。
- “回显疑似/确认”是本进程内检测到高相似结果的两级计数；正文保留供用户判断，确认回显不会缓存或写入历史。
- “模型切换”显示“成功次数/用户请求次数”。传输失败或切换后仍确认回显计为失败；被下一次选择取代的旧请求只按取消处理。
- 所有指标仅保存在当前进程内，应用重启后重新统计；跨午夜会重置“今日”计数。

当前界面显示平均耗时和 P95。P50、P99 已由指标服务计算，可用于后续诊断或开发扩展。

流式卡顿判读：

- `max_dispatcher_queue_ms` 高，而 `max_ui_execution_ms`、`max_markdown_render_ms` 和 `gc_pause_ms` 都低：优先检查 WPF 布局、窗口尺寸变化或其他 Dispatcher 工作。
- `gc_pause_ms` 与排队峰值同量级，且存在 Gen1/Gen2 回收：优先检查请求期间的累计分配和长寿命 WPF 对象。
- `max_ui_execution_ms` 与 `max_markdown_render_ms` 接近：停顿发生在当前 Markdown 更新帧内。
- `max_composition_wait_ms` 高或合成请求大量合并：UI 更新已经提交，但实际呈现帧受布局或合成限制。
- 合成观测不阻塞流式泵；请求结束时最后一帧可能仍待呈现，因此 requested 数可略大于 presented 数。

## 6. 隐私与安全边界

当前版本禁止向应用日志写入：

- 用户选中的原文；
- 快速查词的查询词、释义、音标、例句和搭配；
- 翻译、解析或模型响应正文；
- 系统提示词和自定义提示词；
- API Key；
- Authorization 请求头；
- 供应商完整错误响应体；
- 可能包含敏感内容的异常消息。

允许记录的是长度、类型、耗时、状态码、事件状态、请求 ID 和异常类型等诊断元数据。

注意：从旧版本保留下来的历史日志可能包含旧版曾记录的输入或结果摘要。新版本不会自动改写历史文件。如果需要彻底移除这些历史内容，请在确认不再需要排查后手动删除旧日志，或临时缩短保留天数并执行“清理”。分享日志前仍应人工检查文件内容。

日志功能不会自动上传、同步或发送文件。所有日志默认只存储在当前 Windows 用户的 `%APPDATA%` 目录。

## 7. 故障排查

### 7.1 日志查看器为空

依次检查：

1. 点击“刷新”。
2. 将级别切换为“全部”。
3. 清空搜索框。
4. 点击“打开目录”，确认日志文件是否存在。
5. 检查设置中的日志级别是否为 `Fatal` 或 `Error`，导致普通操作没有日志。

### 7.2 最新日志没有立即出现

普通日志由后台线程批量写入，通常最多延迟约 500 毫秒。查看器当前不进行无限实时 tail，需要点击“刷新”重新读取文件。Fatal 日志会尝试立即刷盘。

### 7.3 点击清理后文件仍存在

这是正常行为，可能原因包括：

- 文件仍在保留期内；
- 日志总大小未超过上限；
- 文件是当前正在写入的主日志；
- 文件是运行中的 `shutdown-trace.log` 或 `watchdog.trace`；
- Windows 或其他程序正在占用文件。

退出 QuickTranslate 后可在文件资源管理器中处理不再需要的文件。

### 7.4 搜索不到异常消息

日志出于隐私原因只记录异常类型，不记录 `Exception.Message`。请使用稳定事件名、模块、`error_type` 或 `exception_type` 搜索。例如：

```text
translation.failed
HttpRequestException
uia.selection_failed
clipboard.operation_failed
```

### 7.5 日志写入失败会怎样

日志系统采用尽力写入策略。目录创建、写入、轮转或删除失败不会使翻译主流程崩溃。无法写入时日志可能丢失，因此排查磁盘权限、磁盘空间或安全软件拦截时，应同时检查 `%APPDATA%\QuickTranslate\logs` 的访问权限。

## 8. 开发者接入指南

### 8.1 普通兼容调用

现有简单调用继续有效：

```csharp
Logger.Info("App", "应用启动");
Logger.Warn("ClipboardHelper", "clipboard unavailable");
Logger.Error("TranslationService", "translation failed", exception);
```

异常重载只记录异常类型，不写入异常消息。

### 8.2 结构化事件调用

新代码优先使用稳定事件名和结构化上下文：

```csharp
Logger.Info("TranslationService", "translation.completed", new
{
    operation = "translation",
    content_type = request.ContentType.ToString(),
    requested_target_language = request.RequestedTargetLanguage,
    effective_target_language = request.EffectiveTargetLanguage,
    direction_relation = request.Direction.Relation.ToString(),
    direction_confidence = request.Direction.Confidence.ToString(),
    direction_reason = request.Direction.Reason.ToString(),
    source_language_family = request.Direction.SourceLanguageFamily.ToString(),
    text_len = request.Text.Length,
    result_len = result.Length,
    duration_ms = elapsed.TotalMilliseconds
});
```

错误事件：

```csharp
Logger.Error("App", "translation.failed", new
{
    request_id = requestScope.RequestId,
    error_type = exception.GetType().Name
}, exception);
```

事件命名建议使用小写点分格式：

```text
<domain>.<action>
translation.started
translation.completed
translation.cancelled
clipboard.read_failed
uia.selection_failed
```

### 8.3 禁止的日志写法

不要记录正文、凭据或异常消息：

```csharp
// 禁止
Logger.Info("TranslationService", request.Text);
Logger.Info("TranslationService", result);
Logger.Warn("Api", responseBody);
Logger.Error("Api", exception.Message);
Logger.Debug("Config", settings.ApiKey);
```

应改为长度、类型和稳定错误类别：

```csharp
Logger.Warn("Api", "provider.request_failed", new
{
    status_code = (int)response.StatusCode,
    error_type = exception.GetType().Name,
    text_len = request.Text.Length
});
```

### 8.4 指标接入约束

- Logger 只负责记录事件，不维护业务统计。
- 翻译统计统一通过 `TranslationMetrics`。
- 缓存计数统一使用 `TranslationCacheService.Hits`、`Misses` 和 `HitRate`。
- 只有最新有效请求完成并进入展示/历史链路后才记录成功。
- 请求取消、失败和展示身份过期必须使用各自独立计数。
- 不得把缓存耗时加入 API 延迟窗口。

### 8.5 相关代码

| 文件 | 职责 |
| --- | --- |
| `Helpers/Logger.cs` | 日志级别、异步队列、JSONL、轮转和清理 |
| `Helpers/LogEvent.cs` | 结构化日志记录模型 |
| `Services/TranslationMetrics.cs` | 翻译次数和延迟分位数 |
| `UI/LogEntryReader.cs` | JSONL、旧日志和 Raw 行读取 |
| `UI/LogViewerWindow.xaml(.cs)` | 日志查看、筛选、刷新和统计展示 |
| `Models/AppSettings.cs` | 日志级别、保留天数和最大总大小配置 |
| `App.xaml.cs` | 初始化、运行时配置和翻译生命周期接入 |

### 8.6 验证命令

从仓库根目录执行：

```powershell
dotnet build .\QuickTranslate\QuickTranslate.csproj
dotnet test .\QuickTranslate.Tests\QuickTranslate.Tests.csproj
git diff --check
```

如果运行中的 QuickTranslate 锁定默认输出，请使用仓库内隔离输出目录：

```powershell
dotnet build .\QuickTranslate\QuickTranslate.csproj --no-restore -p:BaseOutputPath=.phase9-verify\
dotnet test .\QuickTranslate.Tests\QuickTranslate.Tests.csproj --no-restore -p:BaseOutputPath=.phase9-verify\
```

验证完成后删除生成的隔离输出目录，不要提交 `bin/`、`obj/` 或其他构建产物。

自动化测试不能替代以下 Windows 桌面验证：托盘入口、日志窗口关闭/重开、日志刷新、真实文件轮转、设置即时生效、文件占用以及混合 DPI 下的窗口显示。

## Streaming timing events

| Event | Level | Context keys (no text body) |
|------|-------|-----------------------------|
| translation.completed | Info | operation, content_type, requested_target_language, effective_target_language, direction_relation, direction_confidence, direction_reason, source_language_family, text_len, result_len, duration_ms, stream_chunk_count, first_chunk_ms, average_chunk_gap_ms, max_chunk_gap_ms, stalled_chunk_count |
| translation.presented | Info | operation, content_type, model, provider, result_len, duration_ms, stream/UI/Dispatcher/Markdown/GC/composition timing fields listed above |
| analysis.follow_up.completed | Info | turn, answer_len, duration_ms, request_id, stream_chunk_count, first_chunk_ms, average_chunk_gap_ms, max_chunk_gap_ms, stalled_chunk_count |
| analysis.follow_up.presented | Info | turn, request_id, stream/UI/Dispatcher/Markdown/GC/composition timing fields listed above |

这些字段只包含枚举、语言名称、计数和毫秒值。它们不记录 chunk 正文、累计结果、问题、回答、Prompt、API Key、Authorization 头或供应商响应体。方向判断完全在本地完成；自动判断关闭时实际目标始终等于用户选择的目标。拉丁文字语言之间无法仅凭文字系统可靠区分，因此保持 `Unknown` 并使用请求目标，不自动切换到备选语言。

## Translation quality and model-switch events

| Event | Level | Context keys (no text body) |
|------|-------|-----------------------------|
| translation.echo_suspected | Info | model, provider, source_len, result_len, similarity, length_ratio, reason |
| translation.echo_confirmed | Warn | model, provider, source_len, result_len, similarity, length_ratio, reason |
| translation.model_switch_requested | Info | from_model, from_provider, to_model, to_provider, request_running |

这些日志不记录源文本、模型输出、系统提示词、完整 API 地址、API Key 或响应正文。`provider` 仅为 API 地址的主机名。回显检测只在完整响应结束后决定是否缓存和写入历史，不阻塞流式展示，也不会撤回已经展示的正文。模型切换只在用户明确选择后用于当前会话，不会静默修改默认模型；旧请求的迟到分片由请求身份门禁丢弃。

## Analysis follow-up events (Phase 11)

| Event | Level | Context keys (no text body) |
|------|-------|-----------------------------|
| analysis.follow_up.started | Info | turn, question_len, context_chars, request_id |
| analysis.follow_up.completed | Info | turn, answer_len, duration_ms, request_id, stream_chunk_count, first_chunk_ms, average_chunk_gap_ms, max_chunk_gap_ms, stalled_chunk_count |
| analysis.follow_up.presented | Info | turn, request_id, stream_chunk_count, ui_frame_count, coalesced_chunk_count, first_frame_latency_ms, max_frame_latency_ms |
| analysis.follow_up.cancelled | Debug | turn, request_id |
| analysis.follow_up.failed | Warn | turn, error_type, status_code, request_id |
| analysis.follow_up.limit_reached | Info | turn_count, context_chars, limit_kind, request_id |

These events never contain the selected source text, questions, answers, summaries,
message bodies, system prompts, API keys, Authorization headers, endpoint/model
configuration, provider response bodies, or exception messages.

## TTS events (Phase 14 / 14.1)

| Event | Level | Context keys (no text body) |
|------|-------|-----------------------------|
| tts.speak.started | Info | text_len, voice, rate, speak_id, language_hint, selection_mode, voice_source |
| tts.speak.completed | Info | duration_ms, speak_id, audio_bytes, attempt, voice, voice_source, selection_mode |
| tts.speak.cancelled | Info | speak_id, error_kind, selection_mode, voice |
| tts.speak.failed | Error | speak_id, exception_type, error_kind, voice, text_len, attempt, selection_mode |
| tts.speak.retry | Info | attempt, error_kind, voice, selection_mode, text_len |
| tts.speak.voice_fallback | Info | from, to, lang, reason, selection_mode |
| tts.speak.truncated | Warn | text_len, max_chars |

`error_kind` values: `empty_audio`, `websocket`, `timeout`, `cancelled`, `protocol`, `playback`.

`selection_mode`: `auto` | `manual`. `voice_source`: `auto` | `user` | `fallback`.

Never log SSML, spoken text, tokens, cookies, or absolute temp paths.

## Exit breadcrumbs

Thin dual-channel stage marks for tray exit diagnosis. Exit semantics are unchanged; no hang detector, no TTS service-internal exit logs, no new settings.

| Event | Level | Source | When | Context keys (metadata only) |
| --- | --- | --- | --- | --- |
| `tray.exit.requested` | Info | App | `OnExitRequested` entry, before `Shutdown` / `BeginInvoke(Shutdown)` | `dispatcher_access`, `thread_id` |
| `app.onexit.begin` | Info | App | start of `OnExit` | `thread_id`, `has_tts` |
| `tts.dispose.begin` | Info | App | only when `_ttsService is not null`, before `DisposeAsync().GetResult()` | `thread_id`, `dispatcher_access` |
| `tts.dispose.end` | Info | App | TTS dispose returned successfully | `duration_ms`, `thread_id` |
| `tts.dispose.failed` | Warn | App | TTS dispose threw (cleanup continues) | `duration_ms`, `exception_type` |
| `app.onexit.complete` | Info | App | end of cleanup, replaces free-text “应用退出”, before `Logger.Shutdown()` | `duration_ms` |

Each of the stages above also appends one best-effort line to `shutdown-trace.log` via `Logger.WriteShutdownTrace`:

```text
[yyyy-MM-dd HH:mm:ss.fff] tray.exit.requested dispatcher_access=False thread_id=12
```

Existing VEH / ConsoleCtrl / `Dispatcher.ShutdownFinished` / `ProcessExit` writers may still append raw lines to the same file.

### How to read a hang

| Observation | Likely stuck point |
| --- | --- |
| `tray.exit.requested` present, no `app.onexit.begin`, `watchdog.trace` still refreshing | Shutdown not entered (dispatcher queue / earlier hang) |
| `app.onexit.begin` present, no `app.onexit.complete`, watchdog still alive | Stuck inside `OnExit` cleanup |
| `tts.dispose.begin` without `tts.dispose.end` or `tts.dispose.failed` | Stuck in TTS dispose |
| Full chain through `app.onexit.complete` but process remains | Hang after app cleanup (runtime / native teardown) |

Privacy: only `thread_id`, `dispatcher_access`, `duration_ms`, `exception_type`, `has_tts` (and short stage labels). Never log paths, spoken text, SSML, tokens, or `Exception.Message`.

Second-instance early `Shutdown()` is not required to emit the tray exit chain.
