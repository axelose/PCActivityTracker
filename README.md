# PC Activity Tracker

Windows 11上で、利用アプリ、Foreground Window、Idle、Chrome/Edgeのアクティブサイトを自動記録し、Today / Day / Week / Monthで振り返るローカル専用WPFアプリです。

## 公開先と配布状況

- [ソースコード](https://github.com/axelose/PCActivityTracker)
- [不具合・機能要望](https://github.com/axelose/PCActivityTracker/issues)（履歴DBや個人のURL・タイトルは添付しないでください）
- [プライバシーポリシー](https://github.com/axelose/PCActivityTracker/blob/main/PRIVACY.md)
- [脆弱性の非公開報告](https://github.com/axelose/PCActivityTracker/security)

ソースコードの公開・取得にコード署名証明書は不要です。証明書未取得の間は、下記手順でソースからビルドして利用してください。正式な実行ファイルの配布は、署名・実機検証を完了したうえで[Releases](https://github.com/axelose/PCActivityTracker/releases)から案内します。`-AllowUnsigned`で作成するZIPはローカル検証用であり、公開配布用ではありません。

## 採用技術

- C# / WPF / MVVM
- .NET 10 LTS (`net10.0-windows10.0.22000.0`)
- 共通 `LocalAi.Runtime`（Ollamaクライアント、アプリ間GPU排他制御）
- 固定ソースからビルドしたSQLCipherによる暗号化SQLiteアクセス層
- Chrome / Edge Manifest V3 Extension
- Chromium Native Messaging＋現在のWindowsユーザー専用Named Pipe

SQLCipher 4.19.0（SQLite 3.53.4）とOpenSSL 3.5.8をビルド時に固定ソースから構築してSelf-contained版へ同梱します。通常の記録・集計は外部サービスなしで動作し、AI日報・週報だけは同じPCで動くOllamaを任意で使用します。

## 実装済み機能

- Foregroundアプリ、プロセス名、ウィンドウタイトル、実行ファイル名の記録
- 10秒サンプリングと同一Activityのセッション結合（1分集計に十分な精度を保ちつつ負荷を抑制）
- 指定時間操作がない場合のIdle記録
- Windows Lock／Unlock通知を検知し、ロック中を通常利用へ加算しない
- SQLCipherによるDB全体の暗号化、WAL、期間インデックス、保存期間クリーンアップ
- 長期常駐中も12時間ごとに保存期間を適用し、期限切れデータを削除
- Today / Dayタイムライン
- Weekのアプリ・サイト・日別・合計・Idle集計
- Monthのアプリ・サイト・日別・カテゴリ別集計
- 今日の主要指標とランキングを表示するダッシュボード
- 今日の集中時間（Idleまたは5分以上の空白で区切った25分以上の実作業ブロック）
- 直近7日の24時間帯ヒートマップと、今週／前週同期間の利用時間比較
- 行動分析ページ（コンテキスト切替回数・切替頻度・平均セッション・5分未満セッション）
- 根拠を併記する0〜100の集中品質スコア
- KPI・ランキングから根拠セッションへ移動できる分析エクスプローラー
- アプリ・ドメイン・配信者／チャンネル・カテゴリ・プロジェクト・タイトル等の横断検索
- 1／5／10／15／30／60分の可変粒度タイムライン
- 元の自動記録を変更しない、時間範囲へのプロジェクト・タスク・メモ・カテゴリ補正
- 主な割り込み元、30分以内の作業復帰時間、時間帯別集中率、連続集中時間分布
- アプリ・ドメイン・配信者／チャンネル別の階層カテゴリ分類ルール（色・優先度・正規表現・影響プレビュー・競合表示）
- アプリ・サイト・カテゴリ別に前週からの主要な増減要因を表示
- Chrome / Edge / Braveのアクティブな通常タブだけを受け取るローカル連携
- ブラウザ連携の最終受信、ブラウザ・サイト・配信者、拡張バージョン、正常／拒否件数を表示する接続診断
- YouTubeの公開チャンネル名とTwitchの配信者IDを記録し、配信者・チャンネル別に集計
- 初回プライバシー同意、記録ON/OFF、URL/ページ/ウィンドウタイトルON/OFF、アプリ/ドメイン除外、完全削除、保存期間
- Windowsサインイン時の自動起動と、起動時に画面を開かない通知領域常駐
- SQLiteオンラインバックアップ（自動1日1回／手動）と、整合性確認・復元前安全コピー付きの復元
- ウィンドウを閉じても通知領域で記録を継続（画面UIをメモリから解放し、軽量なWindowsネイティブ通知領域メニューから終了）
- Tracker自身をBelowNormal優先度・Windows EcoQoSで動作させ、非表示時はUI集計を停止。通知領域には`Shell_NotifyIcon`を直接使用し、常駐のためだけにWindows Forms / System.Drawingを読み込まない
- スリープ等で取得間隔が空いた時間を直前アプリへ加算しないギャップ保護
- Tracker自身を記録対象から自動除外
- Week／Monthの合計・ランキング・日別時間をSQLite側で集計し、メモリ使用量を抑制
- 記録エラー状態の画面表示と、最大512KBのローカル循環診断ログ
- DBや履歴内容を含めず、環境・件数・エラー種別だけを出力する匿名化診断ZIP
- ローカルOllamaによるAI日報・週報（手動生成、再生成、キャンセル、SQLite保存）
- AIへ渡す情報をアプリ・ドメイン・カテゴリ・配信者／チャンネル・時間帯・集中時間の集計値に限定し、URL・ページタイトル・ウィンドウタイトルを除外
- 同一プロセス内と複数対応アプリ間でOllamaのGPUジョブを直列化し、生成後は既定でモデルをGPUメモリから解放
- Ollamaのロード済みモデルを生成前に確認し、競合時は新しい生成を開始しない保護設定
- 指定ゲームがForeground中はAI生成を開始せず、生成中にゲームへ戻った場合も自動キャンセルするゲームセーフモード
- AIレポートの根拠集計、生成後のActivity更新による鮮度警告、保存前の構成・URL・評価表現検査
- Windows設定連動／ライト／ダークのテーマ切替（即時反映・設定保存）

## ディレクトリ構成

```text
PCActivityTracker/
├─ PCActivityTracker.sln
├─ src/LocalAi.Runtime/      # 共通OllamaクライアントとGPU実行制御
├─ src/PCActivityTracker/
│  ├─ Browser/          # Native Messaging受信
│  ├─ Data/             # SQLiteとRepository
│  ├─ Domain/           # Activity、設定モデル
│  ├─ Integrations/     # 将来連携の境界
│  ├─ Services/         # 集計、設定、トレイ
│  ├─ Tracking/         # Foreground、Idle、セッション化
│  ├─ ViewModels/       # MVVM
│  └─ Views/            # WPF画面
├─ src/PCActivityTracker.NativeHost/ # Chromium stdioとユーザー専用Pipeの中継
├─ browser-extension/   # Chrome / Edge Manifest V3拡張
├─ packaging/           # MSIX / App Installerテンプレート
├─ docs/                # アーキテクチャ、DB設計、公開チェックリスト
└─ scripts/             # build / run / self-test / publish / package-msix
```

## セットアップ

1. Windows 11に [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) または対応するVisual Studioの「.NETデスクトップ開発」を導入します。管理者権限なしのローカル導入も選べます。
2. PowerShellでリポジトリ直下へ移動します。
3. `dotnet --list-sdks` に `10.x` が表示されない場合、次を実行します。SDKはプロジェクト内の `.dotnet` にだけ保存されます。導入スクリプトとSDKは固定バージョンを使い、実行前に導入スクリプトのSHA-256を検証します。

```powershell
.\scripts\bootstrap-dotnet.cmd
```

4. 次を実行します。

```powershell
.\scripts\build.cmd
.\scripts\self-test.cmd
```

SQLCipherの開発ビルドには、後述のGit for Windowsとネイティブビルド用ツール取得が必要です。配布版の実行は通常ユーザー権限で、管理者として起動する必要はありません。

依存関係は各プロジェクトの `packages.lock.json` で固定しています。Pull Requestや公開候補では、次の公開ソース検査も実行してください。

```powershell
.\scripts\verify-public-source.cmd
dotnet restore PCActivityTracker.sln --locked-mode
```

AI日報・週報も使う場合は、別途[Ollama for Windows](https://ollama.com/download/windows)をインストールし、少なくとも1つモデルをローカルへ取得してください。例:

```powershell
ollama pull qwen3.5:9b
```

アプリの「設定・プライバシー」→「ローカルAI（Ollama）」で「接続確認」を押し、モデルを選んで設定を保存します。既定の接続先は `http://127.0.0.1:11434` です。AI機能を使わなければOllamaは不要です。

`.cmd` はPowerShellの実行ポリシーに影響されません。`.ps1` を使う場合は、システム設定を恒久変更せず、現在のプロセスだけ許可して実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

## 起動

```powershell
.\scripts\run.cmd
```

ローカルSDKを導入した場合、`run.cmd` はプロジェクト内の `.dotnet` を実行時ランタイムとして自動指定します。Windows全体へ.NET Runtimeを別途インストールする必要はありません。
`run.cmd` はCPUに合う最新のSelf-contained成果物を優先するため、古い開発ビルドや不足しているシステム側.NET Runtimeを誤って起動しません。成果物がない場合だけソースを再ビルドします。画面を閉じるか最小化するとタスクトレイへ移動し、ダッシュボードのUIと表示データをメモリから解放してworking setを縮小します。タスクトレイのアイコンをダブルクリックすると画面を再生成します。タスクマネージャー上のメモリは画面表示中よりトレイ格納後の方が大きく下がります。

初回起動では記録項目と保存期間を確認するプライバシー画面を表示し、同意が完了するまで記録を開始しません。完全URL、ページタイトル、ウィンドウタイトル、配信者名は既定OFFです。

「設定・プライバシー」から「Windowsへのサインイン時に自動起動する」を有効にできます。管理者権限は不要です。開発版は `wscript.exe` と `scripts/run-silent.vbs`、Self-contained ZIP版は現在のWindowsユーザーの `HKCU` とEXEの `--background` 起動を使うため、PowerShell／コマンドプロンプトは残りません。MSIX版はWindows公式のStartup Taskを使います。Windows設定やタスクマネージャーで明示的に無効化した後は、Windowsの「設定 > アプリ > スタートアップ」から再度有効にしてください。MSIX版でも画面を出さずに起動する場合は、併せて「起動時は画面を開かず通知領域に常駐する」を有効にします。

## Self-contained配布物の作成

3.2.5以降の開発ビルドはSQLCipher／OpenSSLを固定ソースからビルドします。先にGit for Windows（Perlを含む）を導入してください。Portable版はドキュメント配下の `PCActivityBuildTools/PortableGit` に展開するか、環境変数 `PCAT_BUILD_GIT_ROOT` で展開先を指定します。初回はLLVM-MinGW等を `artifacts/native` に取得するため、ネット接続・数GBの空き容量・空白を含まないプロジェクトパスが必要です。アプリの利用者にこれらのツールは不要です。

```powershell
.\scripts\build-native-sqlcipher.cmd -Runtime win-x64
.\scripts\self-test.cmd
```

通常ビルドのスクリプトはx64 DLLを、配布スクリプトは指定CPUのDLLを準備します。Arm64開発と互換性テストは `docs/NATIVE_DATABASE_UPGRADE.md` を参照してください。

公開用スクリプトは、.NET Runtimeを同梱したデスクトップZIP、ブラウザ拡張ZIP、SHA-256一覧を `artifacts/release` に作成します。正式公開ではコード署名証明書のThumbprintが必須です。

```powershell
.\scripts\publish.cmd -Runtime win-x64 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

ローカル検証に限り、未署名ビルドを明示的に作成できます。出力には公開禁止の警告ファイルが入ります。

```powershell
.\scripts\publish.cmd -Runtime win-x64 -AllowUnsigned
```

Windows 11 SDKのMSIX Packaging Toolsと本番証明書がある環境では、署名済みMSIXと自動更新用App Installerを生成できます。

```powershell
.\scripts\package-msix.cmd -Publisher "CN=YOUR PUBLISHER" -PublisherDisplayName "YOUR NAME" -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT -BaseUri "https://example.com/downloads"
```

公開判定は `docs/RELEASE_CHECKLIST.md` と `docs/GITHUB_RELEASE.md` に従います。Microsoft Storeへ提出する場合は、ストア側の署名工程に合わせて未署名パッケージを別管理してください。GitHubのSource code自動ZIPをWindows実行版として案内せず、署名済み成果物と `SHA256SUMS.txt` をReleaseへ添付します。

公開スクリプトは本アプリのライセンスに加え、同梱する.NET RuntimeのライセンスとThird Party Noticesも配布フォルダーへコピーします。ブラウザストアの申請文・権限理由・審査手順の原稿は `browser-extension/STORE_LISTING.md` にあります。

3.2.4以降はSPDX 2.2形式の依存関係・ファイルハッシュ一覧 `_manifest/spdx_2.2/manifest.spdx.json` と、SDK／Runtime／対象CPUを記載した `BUILD-INFO.json` も同梱します。SBOMは固定バージョンのMicrosoft SBOM Toolで生成・検証します。3.2.5ではネイティブ依存の版・ソース取得先・チェックサムを `NATIVE-SBOM.spdx.json` に記録し、主SBOMの外部文書参照として連結します。初回のビルドにはツールの取得が必要ですが、アプリ実行時の外部通信は増えません。依存一覧は両CPU向け復元グラフを含むため、ZIPに実際に入っている依存だけの一覧ではなく、安全性や署名付き来歴を保証するものでもありません。

ZIPと展開フォルダーの全ファイル一致、SBOM、署名、ローカルデータ混入の検査を再実行できます（未署名のローカル検証版に限り `-AllowUnsigned` を追加）。

```powershell
.\scripts\verify-release.cmd -PublishDirectory .\artifacts\release\PCActivityTracker-3.2.5-win-x64 -ArchivePath .\artifacts\release\PCActivityTracker-3.2.5-win-x64.zip
```

またはVisual Studioで `PCActivityTracker.sln` を開き、`PCActivityTracker` をスタートアッププロジェクトとして実行します。ウィンドウの×ボタンまたは最小化で画面UIを破棄してタスクトレイへ移動します。完全に終了するには通知領域アイコンを右クリックし「終了」を選びます。

## AI日報・週報の使い方

1. 左メニューの「AI日報・週報」を開きます。
2. 「日報」または「週報」を選び、上部の日付で対象期間を指定します。
3. 「生成 / 再生成」を押します。生成中は「中止」でキャンセルできます。
4. 完成したMarkdown形式のレポートはSQLiteへローカル保存され、同じ期間を開くと再表示されます。「コピー」で任意のメモへ貼り付けられます。

生成は自動実行されず、ボタンを押した場合だけローカルOllamaへ要求します。複数の `LocalAi.Runtime` 対応アプリが同時に生成しようとした場合は `%LOCALAPPDATA%\LocalAiRuntime\ollama-gpu.lock` を使って1件ずつ実行します。「Ollamaで別のモデルが実行中なら生成しない」がONの場合はOllamaの `/api/ps` を確認し、GPUメモリにモデルが残っていれば開始しません。「生成完了後にモデルをGPUメモリから解放する」をONにするとOllamaへ `keep_alive: 0` を指定します。

ゲームセーフモードはActivity Trackerが通常のForeground取得で観測したプロセス名だけを使います。対象プロセス名はカンマ区切りで編集でき、`.exe` は省略できます。ゲームプロセスへの注入、Hook、メモリ読み取り、アンチチート回避は行いません。AI画面には生成に使う上位アプリ／サイト／カテゴリと時間集計を表示します。保存済みレポートより新しいActivityがあれば再生成を案内し、生成結果が必須見出しを欠く、完全URLを含む、または禁止した評価的表現を含む場合はSQLiteへ保存しません。

テーマは「設定・プライバシー」→「外観テーマ」で `Windowsの設定に合わせる`、`ライト`、`ダーク` から選べます。選択はすぐ反映され、「設定を保存」で次回起動時にも引き継がれます。

## Chrome / Edge / Brave Extensionの導入

1. Chromeで `chrome://extensions` を開き、「デベロッパーモード」をONにします。
2. 「パッケージ化されていない拡張機能を読み込む」で `browser-extension` フォルダーを選びます。
3. 拡張機能の設定画面に表示された32文字の拡張IDをコピーします。
4. Self-contained ZIP版のデスクトップアプリを起動し、「設定・プライバシー」→「Chrome / Edge / Brave 連携」へ拡張IDを貼り付けて「拡張IDを登録」を押します。
5. ブラウザを完全に終了して再起動します。
6. デスクトップアプリの連携トークンを拡張機能の設定画面へ貼り付け、「保存して接続確認」を押します。

Edgeでは `edge://extensions` を開き、「開発者モード」→「展開して読み込み」で同じフォルダーを選びます。シークレット / InPrivateでの動作はmanifestレベルで無効です。拡張機能から通常タブだと確認できないブラウザについては、ウィンドウタイトルの記録設定がONでもタイトルを保存しません。

Braveでは `brave://extensions` を開き、「デベロッパーモード」→「パッケージ化されていない拡張機能を読み込む」で同じフォルダーを選びます。アプリで拡張IDを登録すると、Brave専用のユーザー別Native Messagingレジストリも自動設定されます。更新時はカード上の丸い矢印を押して再読み込みします。

拡張機能のファイルを更新した場合は、`chrome://extensions` / `edge://extensions` / `brave://extensions` にある拡張機能カードの丸い矢印（再読み込み）を押してください。現在の拡張機能バージョンは `3.2.3` です。再読み込み後、すでに開いているYouTubeタブも1回再読み込みしてください。拡張アイコンから送信の一時停止と接続確認ができます。

YouTubeでは動画、Shorts、チャンネルページに表示される公開チャンネル名を取得します。Twitchでは通常のチャンネルURL（例: `twitch.tv/example_streamer`）から配信者IDを取得します。取得結果はToday/Dayの詳細と、Dashboard/Week/Monthの「配信者・チャンネル別」に表示されます。

## データ保存場所

ZIP版・開発版は `%LOCALAPPDATA%\PCActivityTracker\activity.db`、MSIX版は `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalState\PCActivityTracker\activity.db` です。設定画面に現在使用中の絶対パスを表示し、「保存フォルダーを開く」から実体を開けます。

`activity.db`、WAL、バックアップはSQLCipherでDB全体を暗号化します。ランダムな256-bit鍵は `database.key` にDPAPI暗号文として保存され、同じWindowsユーザーだけが復号できます。既存の平文DBは初回起動時に整合性確認後、自動的に暗号化形式へ移行します。`database.key` を削除・置換するとDBとバックアップを復号できません。また、Windowsを再インストールした別ユーザー環境では復元できないため、端末移行用のパスワード付きエクスポートは今後の課題です。

SQLiteのWAL運用中は同じ場所に `activity.db-wal` と `activity.db-shm` が存在する場合があります。設定画面から保存フォルダーを開けます。
診断エラーが発生した場合だけ、同じ場所へ `diagnostics.log` を作成します。3.2.3以降は日時・コンポーネント・例外の型・HResultだけを保存し、例外メッセージやスタックトレースは保存しません。診断ログは外部送信されず、512KBを超えると `diagnostics.previous.log` へ1世代だけローテーションします。旧版で生成したログにはパス等が含まれ得るため、公開Issueへ添付せず匿名化診断ZIPを使用してください。

バックアップは次のフォルダーへ最大20件保存します。

```text
%LOCALAPPDATA%\PCActivityTracker\Backups
```

自動バックアップは有効時に1日1回、手動バックアップは設定画面の「今すぐバックアップ」で作成します。保存期間はバックアップ内のActivityとAIレポートにも適用され、期限より古いバックアップは削除されます。DB更新前には `activity-pre-migration-...db` を自動作成します。

ZIP版を更新する場合は、通知領域から旧版を完全終了し、新版ZIPを新しいフォルダーへ展開して起動します。履歴は `%LOCALAPPDATA%` にあるため引き継がれます。ブラウザ連携は新版アプリで同じ拡張IDを再登録し、ブラウザを完全再起動してください。旧フォルダーは新版の履歴表示と連携を確認してから削除します。

ZIP版の削除だけでは履歴を自動削除しません。アンインストール前に自動起動をOFFにし、ブラウザ連携をリセットしてから、端末から完全に消す場合は設定画面の「履歴を完全削除」を実行してください。MSIX版はWindowsのクリーンアンインストール対象となり、パッケージ専用LocalState内の履歴も削除されます。MSIX版をアンインストール後も履歴を保持したい場合は、先にアプリを終了し、「保存フォルダーを開く」で表示したフォルダー全体を任意の同期対象外フォルダーへコピーしてください。OneDrive等へコピーした場合、そのコピーは本アプリのローカル専用管理外です。

## プライバシーと安全性

- Activityデータを外部サーバーへ送信するコードはありません。
- AIの接続先はHTTPの `localhost` / `127.0.0.1` / `::1` だけを許可し、実際の接続IPもloopbackへ限定します。プロキシ、自動リダイレクト、リモートホスト、HTTPS URLは拒否します。
- AIプロンプトに含めるのは期間別の集計値だけです。完全URL、URLパス、クエリ、ページタイトル、ウィンドウタイトル、個別タイムラインは含めません。
- AIレポート本文とモデル名はActivity DB内へ保存され、バックアップ・保存期間・「履歴を完全削除」の対象になります。
- 手動で入力したプロジェクト、タスク、メモ、カテゴリ補正も暗号化Activity DB内だけへ保存され、バックアップ・保存期間・「履歴を完全削除」の対象になります。
- AI生成前のゲーム判定には既存のForegroundプロセス名だけを再利用し、ゲームを列挙したり対象プロセスを開いたりしません。
- ブラウザ拡張はChromium Native Messagingで専用ホストだけを起動し、サイト情報は現在のWindowsユーザーだけが接続できるNamed Pipeへ渡します。外部ネットワークやlocalhostポートは使用しません。
- IPCはWindows DPAPIで保護した256-bitランダムトークン、明示登録した拡張ID、HMACチャレンジで認証します。設定画面からトークン、登録ID、Chrome/Edgeのユーザー別Native Messaging登録を失効できます。
- 拡張が読むのはアクティブな通常タブのURL、タイトル、およびYouTube画面上の公開チャンネル名だけです。TwitchはURLから配信者IDを判定します。閲覧履歴一覧、コメント、説明本文は読みません。
- 保存URLからユーザー情報（`user:password@`）、クエリ文字列、フラグメントを自動削除し、認証情報・検索語・アクセストークン等をDBへ残しません。
- URLは受信直後にサニタイズし、極端に長いパスはドメイン部分だけへ縮約します。ページタイトルは500文字、配信者・チャンネル名は200文字を上限とします。
- 「履歴を完全削除」ではSQLiteのsecure delete、WAL切り詰め、VACUUMに加え、全バックアップと診断ログも削除します。
- Native Messaging中継は64KB上限・同時4接続・要求タイムアウトを設け、停止時には処理中のユーザー専用Pipeを安全に終了します。
- 匿名化診断ZIPはDB、生ログ、URL、タイトル、ドメイン、配信者、除外値、トークン、ユーザーパスを含めません。
- ブラウザ診断に表示する通信カウンターや最終受信情報もメモリ内／ローカル画面だけで扱い、外部へ送信しません。
- URL/タイトル/配信者・チャンネル名の記録をOFFにしても、既存レコードは自動消去されません。必要なら「履歴を完全削除」を実行してください。
- 除外ドメインはそのサブドメインも含めて記録しません。銀行等の機微なドメインは事前登録を推奨します。
- DLL/Process Injection、API/DirectX Hook、プロセスメモリ読み取り、対象プロセスのハンドル取得、ドライバー、管理者権限、プロセス隠蔽、アンチチート回避は使用していません。

設計の安全境界は [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)、脅威と対策は [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md)、テーブル詳細は [docs/DATABASE.md](docs/DATABASE.md) を参照してください。サポートへ出す情報は [SUPPORT.md](SUPPORT.md)、脆弱性は [SECURITY.md](SECURITY.md) に従ってください。

## 現在の制限

- UI表示は現在日本語だけです。MSIX manifestも `ja-jp` のみを宣言しています。
- Windowsの通常APIから安全に取得できる範囲を優先し、実行ファイルのフルパスではなくファイル名だけを保存します。
- 管理者として動く別デスクトップ上のウィンドウや保護された画面ではタイトルが空になる場合があります。
- ブラウザ拡張を導入していない場合、Chrome/Edgeは通常アプリとしてのみ記録されます。
- Native Messaging登録は現在Self-contained ZIP版に対応します。MSIXではブラウザから見えるユーザー別ホスト登録をインストーラー側で行う必要があるため、現状のMSIX版ではサイト連携を利用できません。
- ブラウザは拡張機能が通常タブだと確認した場合だけページ情報を記録します。拡張未接続時は、一般のウィンドウタイトル記録がONでもブラウザタイトルを保存しません。
- ブラウザのアクティブタブ更新はイベント＋30秒heartbeatです。拡張またはアプリ停止中の履歴を後から取り込みません。
- YouTubeの画面構成変更や動画側の表示状態によってチャンネル名を取得できない場合があります。Twitchのカテゴリ・検索・動画一覧等、配信者を一意に決められないURLでは記録しません。
- 自動カテゴリはローカルのルールベースです。「行動分析」画面から階層カテゴリ、色、ワイルドカード／正規表現、0〜1000の優先度を設定できます。同じ優先度ではCreator、Domain、Applicationの順に評価し、適用理由は分析エクスプローラーへ表示します。
- 複数Windowsユーザー間の履歴統合とCalendar連携は未実装です。自動起動・設定・DBはWindowsユーザーごとに分離されます。
- AIレポートの品質と生成時間は選択したOllamaモデルとPC性能に依存します。生成は集計データに基づく補助的な振り返りで、内容の正確性を保証するものではありません。

## Google Calendar連携方針

TrackingとDatabaseは外部連携に依存しません。将来は `Integrations/IActivityExportSink` の実装として、ユーザーが明示的に有効化した場合だけOAuthとCalendar APIを扱います。Activityを時間帯でまとめる処理、Calendarイベント化、予定対実績比較は外部連携層に置き、URLやウィンドウタイトルを既定で送らない設計にします。

## DB設計概要

- `activity_sessions`: 時刻、種類、アプリ、プロセス、タイトル、ブラウザ、URL、ドメイン、配信者・チャンネル名、Idle、カテゴリ
- `activity_annotations`: 元データとは独立した時間範囲、プロジェクト、タスク、メモ、カテゴリ補正
- `settings`: ローカル設定、DPAPIで暗号化した連携トークン、ペアリング済み拡張ID
- `exclusions`: アプリ/ドメイン除外ルール
- `ai_reports`: 日報・週報の対象期間、生成日時、モデル、本文、プロンプト版

Activityは1分ごとの行ではなく、同じfingerprintが続く間は1行を更新します。表示時に任意の日・週・月範囲へクリップして集計します。
