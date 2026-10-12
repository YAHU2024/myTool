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

Remove isolated output after validation. After source changes, update README structure with `python scripts\update-readme-tree.py --write`; CI checks it with `--check`. Maintain site images only in `docs/images/`, then run `python scripts\sync-site-assets.py --sync`.

## Style, Tests, and Review

- Use four spaces in C#, existing XAML formatting, `PascalCase` for public symbols, `camelCase` for locals/parameters, and `_camelCase` for private fields. Nullable reference types and implicit usings are enabled. Keep UI event handling in code-behind, reusable behavior in `Core/` or `Services/`, and platform declarations in `Helpers/Win32Api.cs`.
- `QuickTranslate.Tests` is the xUnit project. Every code change must pass build and test; name tests `<ClassName>Tests.cs` and methods by behavior. Automated tests do not prove provider behavior, WPF rendering, DWM composition, mixed-DPI placement, tray integration, file locking, or interactive clipboard behavior; manually verify affected Windows workflows and report automated and manual validation separately.
- CI and `.git/hooks/pre-commit` enforce `dotnet format --verify-no-changes`. The hook is an untracked plain bash script; set it up once with `./scripts/setup-git-hooks.ps1`, inspect it with `cat .git/hooks/pre-commit`, and if it blocks a commit, format, stage the fixes, and retry.
- Use focused imperative Conventional Commit subjects such as `feat(setting): ...` or `fix(red_dot): ...`. PRs must describe user-visible behavior, implementation impact, related issues, and manual verification; include screenshots for XAML/layout changes. Keep generated files, settings, secrets, databases, and logs out of commits.
- After each coherent repair or feature stage, inspect recent commit style and provide a complete ready-to-use commit message with validation. Do not commit unless explicitly requested.

## Security and Privacy

Never commit `%APPDATA%\QuickTranslate` settings, API keys, credentials, databases, or logs. Never log selected text, translation/analysis output, prompts, API keys, Authorization headers, complete provider responses, or exception messages. Logs may contain only diagnostic metadata such as lengths, content type, duration, status, request identity, and exception type. Review old logs before sharing them. See `docs/LOGGING.md` for the detailed logging contract.
