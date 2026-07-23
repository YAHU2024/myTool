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
4. 如果应用异常退出，再查看 `shutdown-trace.log` 和 `watchdog.trace`。

翻译没有结果：

1. 搜索 `translation.`。
2. 查看是否存在 `translation.started`。
3. 检查后续是 `translation.completed`、`translation.failed`、`translation.cancelled` 还是 `translation.cache_hit`。
4. `translation.completed` 表示服务响应完成；`translation.presented` 表示最新有效请求已写入当前界面和历史。

划词、剪贴板或 UIA 异常：

- 搜索模块 `ClipboardHelper`、`SelectionDetector` 或 `SelectionLocator`。
- `Warn` 一般表示本次操作失败但应用仍可继续运行。
- 日志只记录稳定事件名和异常类型，不记录剪贴板文本或选中文本。

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
| `shutdown-trace.log` | 记录进程退出、控制台信号和非托管异常兜底信息 |
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
{"Timestamp":"2026-07-23T10:15:30.123-07:00","Level":"Info","Source":"TranslationService","EventName":"translation.completed","Context":{"operation":"translation","content_type":"Translation","target_language":"简体中文","text_len":42,"result_len":18,"duration_ms":527.4}}
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
| `target_language` | 目标语言名称 |
| `text_len` | 输入字符数，不包含输入内容 |
| `result_len` | 结果字符数，不包含结果内容 |
| `duration_ms` | 操作耗时，单位毫秒 |
| `request_id` | 进程内请求身份，用于判断取消和过期请求 |
| `error_type` / `exception_type` | 异常类型名称，不包含异常消息 |

## 5. 性能指标说明

日志查看器底部显示当前进程内的统计快照：

```text
显示 120/250 条 | 今日完成 18 | 平均 430ms | P95 920ms | 缓存命中率 22%
```

统计口径：

- “今日完成”包含 API 完成结果和缓存命中结果。
- 平均耗时、P50、P95、P99 仅使用最近 100 次真正完成的 API 请求。
- 缓存命中不计入 API 延迟分布，避免把近乎零耗时的缓存结果拉低延迟。
- 取消请求、失败请求和过期请求不会计入成功延迟。
- 缓存命中率直接来自 `TranslationCacheService` 的命中和未命中计数，不从日志文本推算。
- 所有指标仅保存在当前进程内，应用重启后重新统计；跨午夜会重置“今日”计数。

当前界面显示平均耗时和 P95。P50、P99 已由指标服务计算，可用于后续诊断或开发扩展。

## 6. 隐私与安全边界

当前版本禁止向应用日志写入：

- 用户选中的原文；
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
    target_language = request.TargetLanguage,
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
