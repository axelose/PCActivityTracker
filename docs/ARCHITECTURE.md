# Architecture

PC Activity Tracker is a local-only WPF application organized by feature rather than by an elaborate layer framework.

```text
Windows documented user-mode APIs
  ├─ Foreground window + title
  ├─ Last input time
  └─ Tool Help process snapshot (executable basename only)
                 │
                 ▼
          Activity Tracker ◄──── Browser State Store ◄──── current-user Named Pipe ◄──── Native Messaging host ◄──── Chrome/Edge extension
                 │
                 ▼
         Session coalescing
                 │
                 ▼
         Activity Repository ──── SQLCipher SQLite ──── DPAPI-protected 256-bit key
                 │
                 ├─ Aggregation Service ── WPF/MVVM dashboard
                 │       ├─ focus blocks + previous-week comparison
                 │       ├─ interruption/recovery + time-of-day focus evidence
                 │       └─ hourly overlap split ── 7 x 24 heatmap
                 ├─ Activity Explorer ── cross-search + variable time buckets
                 │       └─ non-destructive project/task/category annotation overlays
                 ├─ AI Report Service ── evidence/freshness/quality gate ── LocalAi.Runtime ── local Ollama
                 │       ├─ Game Safety ── existing foreground process observation
                 │       └─ AiReport Repository ── SQLite
                 ├─ Online Backup Service ── local verified backups
                 └─ IActivityExportSink ── future opt-in integrations
```

## Responsibilities

| Module | Responsibility |
|---|---|
| `Tracking` | Foreground sampling, idle detection, process-name lookup, session coalescing |
| `Browser` | DPAPI-secret authenticated Native Messaging receiver over a current-user-only Named Pipe; no network listener |
| `Services/AggregationService` | Range clipping, rollups, focus blocks, interruption/return-time evidence, time-of-day focus ratios, context switches, focus-quality score and period delta analysis |
| `Services/ActivityExplorerService` | Cross-search over app/site/creator/category/project fields, 1-60 minute zoom buckets, drill-down evidence and category-rule explanations |
| `Data/ActivityAnnotationRepository` | Non-destructive project/task/note/category overlays; original captured sessions are never rewritten |
| `Services/AiReportService` | Daily/weekly aggregate prompt construction, evidence snapshot, freshness detection and pre-save quality validation; never sends a raw timeline |
| `Services/GameSafetyService` | Prevents/interrupts AI generation from the process name already observed by normal foreground tracking; does not enumerate or open game processes |
| `Services/ThemeService` | Applies System/Light/Dark palettes through dynamic WPF resources and the documented Windows title-bar attribute |
| `LocalAi.Runtime` | Reusable localhost-only Ollama client, loaded-model preflight, cancellation/timeout and in-process/cross-process GPU execution serialization |
| `Data` | SQLCipher encrypted SQLite schema, plaintext migration and repositories |
| `ViewModels` / `Views` | Dashboard, Today, Day, Week, Month, AI daily/weekly reports, settings |
| `Services/SettingsService` | Privacy switches, retention, exclusions, local category rules, startup and backup preferences |
| `Services/BackupService` | SQLite online backup, quick-check verification, local rotation and staged offline restore |
| `Services/DiagnosticExportService` | Metadata-only support ZIP; excludes DB, raw logs, activity values, tokens and user paths |
| `PCActivityTracker.NativeHost` | Minimal persistent stdio-to-Named-Pipe proxy launched and origin-restricted by Chromium |
| `Services/AutoStartService` | Per-user HKCU sign-in startup through a silent launcher; no elevation |
| `Integrations` | Interface-only boundary for future explicit exports |

## Safety boundary

Foreground process metadata comes from `GetForegroundWindow`, `GetWindowThreadProcessId`, and a system Tool Help snapshot. The app does not open a handle to the foreground process. It does not inject code, hook APIs or graphics, read process memory, install a driver, request elevation, hide itself, or inspect game internals. The only handle created by the process lookup is a handle to the system snapshot itself, which is closed immediately.

Browser page metadata is trusted only when Chromium launches the registered native host for an explicitly paired extension origin and the message has the correct per-user secret. The small host forwards framed JSON to a `PipeOptions.CurrentUserOnly` Named Pipe. Before reading and sending the active tab, the extension verifies an HMAC challenge proving that the desktop process possesses that secret. There is no browser-data TCP listener. The extension declares `incognito: not_allowed`; if no extension-confirmed normal-tab state exists, browser window titles are suppressed even when general window-title recording is enabled.

The complete SQLite database, WAL and online backups use SQLCipher. A random 256-bit database key is stored separately only as Windows DPAPI ciphertext for the current user. A legacy plaintext database is checkpointed, exported to a new encrypted file, quick-checked, atomically swapped, and its temporary plaintext copy is overwritten and removed on a best-effort basis. Losing `database.key` or moving it to another Windows user makes the encrypted history unrecoverable.

The optional AI path accepts only an HTTP loopback endpoint (`localhost`, `127.0.0.1`, or `::1`). Its HTTP transport disables proxies and redirects and rejects any resolved address that is not loopback. It is invoked manually and receives aggregate usage totals, focus totals, daily totals, and hourly totals. Raw URLs, URL paths, page/window titles, and individual timeline rows are excluded before prompt construction. `LocalAi.Runtime` serializes GPU jobs with an in-process semaphore plus `%LOCALAPPDATA%\LocalAiRuntime\ollama-gpu.lock`; the default generation request uses `keep_alive: 0` to release the model afterward.

## Session policy

The foreground state is sampled every ten seconds. Equal fingerprints are extended in place. A heartbeat updates the open database row every sixty seconds; a state change closes it immediately. This provides crash tolerance without creating one row per minute. The tracker process runs at BelowNormal priority with Windows EcoQoS, and dashboard refresh timers stop while the window is hidden. Closing the window releases its visual tree and trims the process working set. The resident tray icon uses the documented `Shell_NotifyIcon` API directly, so Windows Forms and System.Drawing are not loaded only for notification-area support.

Focus time is local analysis only: idle intervals or unobserved gaps of five minutes or more close a block, and blocks with at least 25 minutes of recorded non-idle work count as focus. Hourly analysis clips every session at hour boundaries in SQLite, so an activity spanning multiple hours contributes accurately to each cell without materializing full Activity rows in the UI.

Category rules are evaluated locally in explicit priority order and can use exact, wildcard, or timeout-bounded regular-expression matching. Hierarchical labels and colors are display metadata only. The Activity Explorer exposes the winning rule and conflict count before saving a rule. Manual corrections are stored as time-range annotations; reporting splits a captured session at annotation boundaries so only the corrected interval changes category totals. This preserves the original evidence and makes correction deletion an undo operation.
