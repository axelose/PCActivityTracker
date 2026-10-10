# Database design

Database: unpackaged `%LOCALAPPDATA%\PCActivityTracker\activity.db`; MSIX `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\PCActivityTracker\activity.db`. The Settings page displays and opens the actual directory.

SQLite runs through SQLCipher in WAL mode with `synchronous=NORMAL`, a five-second busy timeout, and local indexes. The full database and backups are encrypted with a random 256-bit key. `database.key` contains only a Windows DPAPI ciphertext bound to the current Windows user. Date-times are UTC Unix milliseconds; the UI converts them to local time.

Verified encrypted online backups are stored separately under `%LOCALAPPDATA%\PCActivityTracker\Backups`. They use the same DPAPI-protected key as the main DB and are complete SQLite database files rather than copies of only the WAL-era main file. Restore is staged and applied only after the writer process exits. A DB copied without the matching `database.key`, or to a different Windows user profile, cannot be restored.

## `activity_sessions`

| Column | Purpose |
|---|---|
| `id` | Integer primary key |
| `start_utc_ms`, `end_utc_ms`, `duration_seconds` | Session interval |
| `activity_type` | `Application`, `Website`, or `Idle` |
| `app_name`, `process_name` | Friendly app and process basename |
| `window_title`, `executable_name` | Window title and executable basename when available |
| `browser_name`, `url`, `domain`, `page_title` | Browser-only metadata, controlled by settings |
| `creator_name` | YouTube公開チャンネル名またはTwitch配信者ID。設定で無効化可能 |
| `is_idle`, `category` | Aggregation dimensions |
| `created_utc_ms`, `updated_utc_ms` | Record maintenance timestamps |

Indexes cover time range, end time retention, app/time, domain/time, and creator/time queries. Existing databases are migrated in place by adding the nullable `creator_name` column; activity rows are not deleted. Retention is applied at startup and every 12 hours while the application remains resident.

## `settings`

Key/value settings. The browser bridge token is a random 256-bit local secret stored as Windows DPAPI ciphertext for the current Windows user. Paired browser-extension origins contain only extension IDs, not browsing data.

## `exclusions`

Application and domain exclusion rules with a case-insensitive uniqueness constraint.

## `ai_reports`

| Column | Purpose |
|---|---|
| `id` | Integer primary key |
| `report_type` | `Daily` or `Weekly` |
| `period_start_utc_ms`, `period_end_utc_ms` | Source aggregation period |
| `generated_utc_ms` | Local report generation timestamp |
| `model_name` | Local Ollama model used |
| `content` | Generated Markdown report |
| `prompt_version` | Prompt/schema compatibility version |

`report_type` and `period_start_utc_ms` are unique, so regeneration replaces that period's prior report. AI reports are included in normal SQLite backups and are removed by retention cleanup or “delete all history.” Prompts are not persisted.

## `category_rules`

アプリ、ドメイン、配信者／チャンネルに対するユーザー定義カテゴリを保存します。Activity本体は書き換えず、集計時に適用するため、ルールを削除すると過去の集計も標準分類へ戻ります。

| Column | Purpose |
|---|---|
| `target_type` | `Application` / `Domain` / `Creator` |
| `pattern` | ワイルドカードまたはタイムアウト付き正規表現 |
| `category` | `仕事 > 開発` のような階層カテゴリ名 |
| `match_mode` | `Wildcard` / `Regex` |
| `color` | UI表示用の `#RRGGBB` |
| `priority` | 0〜1000。高いルールを先に適用 |
| `created_utc_ms` | 同種ルールの優先順を決める作成時刻 |

## `activity_annotations`

自動取得した `activity_sessions` を書き換えず、ユーザーによる補正を時間範囲のオーバーレイとして保存します。削除すると元の自動記録表示へ戻ります。

| Column | Purpose |
|---|---|
| `start_utc_ms`, `end_utc_ms` | 補正対象の時間範囲 |
| `project_name`, `task_name` | 任意のプロジェクト・タスク |
| `note` | ローカルの振り返りメモ |
| `category_override` | 該当時間に優先適用する階層カテゴリ |
| `created_utc_ms`, `updated_utc_ms` | 同一区間で新しい補正を優先するための時刻 |

注釈はSQLCipher、バックアップ、保存期間、完全削除の対象です。Activityとの外部キーを持たせないため、複数セッションにまたがる補正や任意粒度の補正を安全に表現できます。
