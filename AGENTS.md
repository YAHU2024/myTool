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

<!-- aoci:begin -->
## AOCI Repository Cognition

AOCI maintains the versioned repository cognition layer in `aoci.txt`, `aoci.meta.txt`, and `aoci.code.txt`. Entries describe managed objects with evidence-bound tags and F/R/A/S semantics; the index covers architecture, responsibilities, relationships, contracts, and non-obvious constraints. AOCI assets may be updated when managed code changes unless the user explicitly forbids those writes.

### Authority and Authoring

- The current AOCI MCP tool schema, machine contract, Validator, current Plan, and Guide are authoritative in that order. This file, prompts, README, descriptions, and static docs only provide entry points and cannot override them.
- Semantic Root, Meta, Header, Curation, and Entry content is written by the Host model from the current bound Plan, Evidence, source, and complete Candidate. Paths, names, extensions, AST/symbol lists, dependency scans, regexes, templates, and rules may locate or validate evidence but may not generate, prefill, concatenate, or rewrite F/R/A/S or semantic decisions. Do not ask AOCI to invent `origin=host_model`, receipts, or framework meaning.
- Fresh bootstrap follows the current Guide. Do not reconstruct its state machine. Internal batches are not user decisions; stop only for an approval/external-action boundary or a real safety, drift, CAS, Recovery, permission, or third-party conflict.

### Tools and a New Run

Use these MCP tools as defined by their current schemas: `aoci_rules` (session contract), `aoci_overview` (complete cognition), `aoci_maintain` (stable-change maintenance), `aoci_update_entry` (complete bound batch), and `aoci_report` (evidence-insufficient todo when supported). Do not use `aoci_maintain` to replace initial index creation.

At each new Agent Run, determine whether a complete index and a reliable matching cognition receipt already exist. If not reliable, call `aoci_rules` once, then request a normal full `aoci_overview`; an explicit normal Overview is never suppressed by an existing receipt or threshold. Reuse reliable cognition without mechanical rereads. If host context compaction or loss of the system picture is known, treat prior cognition as unreliable: call normal full Overview with a new `refresh_event_id` and `refresh_reasons=["context_compaction"]`, then follow every `next_cursor` until `completed=true` before continuing. Do not use `check_only`, probes, source, Memory, history, `aoci.txt`, search, or entries to repair missing Overview chunks.

Overview delivery rules: a normal Overview must return a consistent requested scope; when `continuation_required=true`, submit the cursor unchanged until complete without asking the user or starting work. Stop on truncation, missing/duplicate/out-of-order chunks, cursor failure, index/config changes, or changed chunk size; if the host truncates, ask for a smaller legal `overview_delivery.chunk_tokens` value and do not change it automatically. After complete delivery, confirm exact body bytes/hash/end marker and submit exactly one existing attestation contract bound to the returned `index_sha256`, `entry_sequence_sha256`, and `entry_count`; stale identities are invalid. A failed or partial attestation does not prove that cognition is absent, but it disables an unqualified full-system claim. `delivery_verified`, `model_cognition_usable`, strict attestation, governance alignment, and current-system reliability are independent states; only the last permits an unqualified reliability claim. Memory cannot replace a matching current receipt.

Use `check_only=true` only for compact checkpoint facts. `semantic_threshold`, `context_compaction`, and real `phase_transition` are checkpoint reasons; a function, test, or tool call is not a phase transition. Ordinary read-only analysis, audits, and version checks do not require maintenance. Challenge answers must use exact 1-based Entry ordinals and current object/tag/core-F values; at least 80% correct with at most one identity miss is required for strict verification. Never semantically retry or bypass a failed attestation. If the user explicitly forbids Ledger, `.aoci`, metadata, or any other write, report the conflict instead of silently substituting Memory.

### Maintenance and Writes

After managed changes reach the final stable state, call `aoci_maintain` once. Before authoring any affected volume, read the authoritative `authoring_meta` from `aoci_header`; runtime instructions supplement it but do not replace its bytes. Read every current candidate and necessary related evidence, then submit one complete `aoci_update_entry` batch with every candidate unchanged in `candidate_id`, `source_sha256`, path/object identity, and domain batch identity. Never submit a prefix or reduce scope for transport limits.

For Code, `code_batch_id` must equal `code_plan.batch_id` and every Code candidate's `batch_id`; never use `authoring_batch.batch_identity` there. Use the compact tag format and current Meta dictionary; R values must be exact object identities, and S may contain only evidence-backed, non-inferable error-preventing constraints. Preserve exact machine fields and complete F/R/A/S entries. If `remaining` is nonzero, immediately call Maintain again from the new preimage until it reaches zero. Only after the final batch succeeds and `remaining=0` run the returned `verify`, aggregate `check`, and `guide` commands; do not Maintain or write again after that.

Handle structured outcomes exactly: `repair_required` means change only the named fields/findings and resubmit the same complete batch; `stopped` means inspect failed step, formal-write evidence, and Recovery, then auto-replan a proven zero-write closure, resume a provable postimage, or perform an exact policy-approved rollback. End the task only for evidence insufficiency, approval/external action, unresolved third-party bytes, or another real hard boundary. Do not bypass conflicts, permissions, human decisions, CAS, or safety signals. If managed files change after maintenance, the result is stale and maintenance must be redone at the next stable point.

### Special Workflows

Initialization, full index creation, Header, Entries, Database, Curation, manual review, orphan removal, migration, and Recovery follow only the current Guide, tool schemas, prompts, and returned commands. Do not preload assumptions or invent command sequences.
<!-- aoci:end -->
