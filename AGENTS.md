# Repository Guidelines

## Project

- `QuickTranslate/` is the .NET 8 Windows desktop app; `Core/` owns interaction and coordination, `Services/` owns provider and integration contracts, `Database/` owns EF Core SQLite history, `Models/` owns shared data, `Helpers/` owns configuration/logging/DPI/Win32, and `UI/` owns WPF windows, tray, and log viewing. Keep XAML beside its `.xaml.cs`; `App.xaml(.cs)` owns startup/lifecycle.
- `docs/` contains user and maintainer documentation, including `docs/internal/plans/`. Generated output belongs in `bin/`, `obj/`, `publish/`, or isolated repository-local output such as `.build-output/` and `.phase9-verify/`; never commit it.
- Logging ownership: `Helpers/Logger.cs` and `LogEvent.cs` provide privacy-safe JSONL logging, rotation, and cleanup; `Services/TranslationMetrics.cs` provides process-local metrics; `UI/LogEntryReader.cs` and `LogViewerWindow.xaml(.cs)` parse and display logs.

## Commands

Run from the repository root:

```powershell
dotnet restore .\QuickTranslate\QuickTranslate.csproj
dotnet build .\QuickTranslate\QuickTranslate.csproj
dotnet test .\QuickTranslate.Tests\QuickTranslate.Tests.csproj
dotnet format .\QuickTranslate\QuickTranslate.csproj --verify-no-changes
dotnet run --project .\QuickTranslate\QuickTranslate.csproj
dotnet publish .\QuickTranslate\QuickTranslate.csproj -c Release
```

The project requires Windows 10/11 and the .NET 8 SDK. If a running app locks normal outputs, keep it running and use isolated paths:

```powershell
dotnet build .\QuickTranslate\QuickTranslate.csproj --no-restore -p:BaseOutputPath=.build-output\
dotnet test .\QuickTranslate.Tests\QuickTranslate.Tests.csproj --no-restore -p:BaseOutputPath=.build-output\
```

Run build, test, and format commands serially; they share generated `obj/` files even when output paths are isolated. Clean validation output with `dotnet clean` for the relevant project and the same `BaseOutputPath`; do not broadly delete output directories that another run may use.

After adding, removing, or moving source files, update README structure with `python scripts\update-readme-tree.py --write`; CI checks it with `--check`. Maintain site images only in `docs/images/`, then run `python scripts\sync-site-assets.py --sync`.

## Design and Reuse

- Use four spaces in C#, existing XAML formatting, `PascalCase` for public symbols, `camelCase` for locals/parameters, and `_camelCase` for private fields. Nullable reference types and implicit usings are enabled.
- Keep `App` and window code focused on lifecycle, event wiring, and presentation. Put new business decisions, request coordination, and result mapping in focused `Core/` policies or coordinators; keep provider and external integration behavior in `Services/`, and platform declarations in `Helpers/Win32Api.cs`.
- Express core rules using ordinary data models with explicit inputs and results. Avoid dependencies on windows, controls, or `Dispatcher` in those rules so they can be tested and reused independently.
- Check existing policies, coordinators, and service contracts before adding another implementation. Shared behavior should have one source of truth; preserve differences between workflows rather than merging merely similar code.
- Introduce interfaces or abstractions for a concrete external dependency, responsibility boundary, testing need, or actual reuse. Simple pure functions can be reused directly. Do not require an interface per class, a blanket MVVM migration, fixed layer counts, or class-size quotas.
- Keep changes within the requested behavior and its necessary dependencies. Document non-obvious contracts and tradeoffs near their implementation; reserve a separate design note for changes with substantial lifecycle or compatibility impact.

## Async and Resource Ownership

- Make ownership of tasks, cancellation sources, processes, streams, event subscriptions, and windows explicit. The owner must define cancellation, completion, and cleanup, including window close and application exit where relevant.
- Propagate cancellation through async operations and distinguish user cancellation, timeout, failure, and superseded work. Use existing request/session coordinators to prevent late results from updating the current UI, history, cache, or success metrics.
- Keep UI updates on the owning dispatcher and avoid blocking I/O or synchronous waits on the UI thread. Background tasks must have observed exceptions and a defined lifetime; avoid unowned fire-and-forget work.
- Put timeout, retry, and fallback decisions at an explicit boundary. Keep attempts bounded and avoid overlapping retry policies; classify failures before retrying so cancellation and permanent failures do not silently trigger another request.
- Do not silently swallow business failures. Best-effort diagnostics or cleanup may ignore failures only when the reason is explicit and the application can safely continue; recovery must not replace a meaningful failure with apparent success.

## Logging and Diagnosis

- Follow `docs/LOGGING.md` for event contracts, field definitions, and troubleshooting. Use the existing logger and metrics owners instead of introducing another diagnostics path.
- New events use stable lowercase dotted names and explicit structured metadata. Carry the same request/session identity across stages; distinguish attempts when retries exist. Important operation paths should expose start and terminal outcomes, failure stage/category, and relevant durations or counts.
- The boundary responsible for handling an operation's outcome owns its terminal event and metrics. Avoid logging the same exception at each layer; record a recovery or fallback decision when it changes the outcome. Keep service completion distinct from successful UI presentation.
- Treat expected cancellation and superseded results as normal control flow. Use levels according to operational impact; do not emit an Info event for every stream fragment, pointer movement, or polling iteration.
- Construct context from explicitly selected diagnostic fields. `Logger` serializes supplied context and does not automatically remove secrets: never pass an entire request, settings, provider response, or exception object as context. Exception overloads record type only; do not log `Exception.Message` or `Exception.ToString()`.
- Update the relevant event/field documentation when changing a diagnostic contract. Diagnostics must remain best effort and must not determine business success or cause an operation to fail.

## Tests and Validation

- `QuickTranslate.Tests` is the xUnit project. Name test classes `<ClassName>Tests` and methods by behavior. Production code changes must pass build, tests, and format verification. Documentation-only changes need the relevant documentation and diff checks rather than an application rebuild.
- For a defect fix, add a regression test at a layer that can reproduce the trigger reliably and assert the observable behavior. Choose cases by risk: cancellation, timeout, duplicate triggers, out-of-order results, malformed input, mapping failures, resource cleanup, and fallback where affected. Avoid tests that merely mirror private implementation details or increase counts without protecting behavior.
- Unit tests use fake providers/OCR, synthetic content, and temporary directories by default. Do not require live network access, credentials, or writes to real user settings/data. Keep external integration tests explicit and report unavailable prerequisites as skipped, not passed.
- Coordinate concurrent tests with controllable signals or fake dependencies instead of fixed delays. Tests touching shared/static or platform state must isolate and restore it, and use appropriate serialization when isolation is not possible.
- Privacy regression tests feed distinctive synthetic sensitive values through the actual context-construction or logging path, then assert those values are absent from emitted data. An absence assertion on a record that never received sensitive input is not sufficient privacy coverage.
- Automated tests do not prove real provider behavior, WPF rendering, DWM composition, mixed-DPI placement, tray integration, file locking, or interactive clipboard behavior. Manually verify affected Windows workflows; report automated results, skipped tests, manual results, and unverified areas separately. Do not claim GUI acceptance from STA/XAML tests alone.

## Review and Delivery

- Run `dotnet format --verify-no-changes` before completing a code change. The current CI format step is advisory (`continue-on-error: true`), not a merge gate. The optional local pre-commit hook is installed with `./scripts/setup-git-hooks.ps1`; verify its presence instead of assuming it exists. If an installed hook blocks a commit, format, review and stage the relevant fixes, then retry without bypassing it.
- Use focused imperative Conventional Commit subjects such as `feat(setting): ...` or `fix(red_dot): ...`. Use a unified repository-style Chinese commit title and message. PRs must describe user-visible behavior, implementation impact, related issues, and manual verification; include screenshots for XAML/layout changes. Keep generated files, settings, secrets, databases, and logs out of commits.
- After each coherent repair or feature stage, inspect recent commit style and provide a complete ready-to-use Chinese commit message with validation. Do not commit unless explicitly requested; commit only the authorized scope and preserve unrelated working changes.

## Security and Privacy

Never commit `%APPDATA%\QuickTranslate` settings, API keys, credentials, databases, or logs. Never log selected text, translation/analysis output, prompts, API keys, Authorization headers, complete provider responses, or exception messages. Logs may contain only diagnostic metadata such as lengths, content type, duration, status, request identity, and exception type. Review old logs before sharing them. See `docs/LOGGING.md` for the detailed logging contract.
