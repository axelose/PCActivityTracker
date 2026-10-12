# GitHub公開準備の確認結果

状態整理: 2026-10-12 / 既存Source Preview・検証済み基準版: 3.3.0 / 今回の開発版: 3.4.0 / 対応拡張: 3.2.4

「ソース公開」「自動テスト成功」「一般利用者向け実行ファイルの配布完了」は別の状態です。旧資料の「リポジトリ未公開」「Git index未検証」「GitHub CI未確認」は解消済みで、現在の未完了項目として扱いません。

## 公開済みベースライン（3.2.5）

- 公開リポジトリ: https://github.com/axelose/PCActivityTracker
- Source Preview: https://github.com/axelose/PCActivityTracker/releases/tag/v3.2.5-source-preview
- 対象コミット: `9f68bf94bfac0226b9f0e1ca9dd3bf8da289064d`
- Windows CI成功: https://github.com/axelose/PCActivityTracker/actions/runs/38040400953
- C#／JavaScript CodeQL成功: https://github.com/axelose/PCActivityTracker/actions/runs/38040400921
- Git index／作業ファイルの公開内容検査、19ケースの検査回帰テストは成功済み。
- x64／Arm64のSelf-contained ZIP構築、SBOM、ハッシュ・混入検査は成功済み。Arm64実機起動の証拠とは異なる。
- SQLCipher 4.19.0／SQLite 3.53.4／OpenSSL 3.5.8を固定ソースから構築。旧SQLCipherとのDB／WAL互換性、誤鍵・破損拒否の人工データ検査は成功済み。
- Private Vulnerability Reporting、Dependabot、Secret scanning、Push protectionを設定済み。アラート数は継続的に変化するため固定の安全保証にしない。
- 公開Releaseはソース用プレリリース。未署名の開発ZIPは一般配布していない。

## 今回の変更（3.4.0）

- 混在時間枠のカテゴリ内訳と元の活動区間。別チャンネル・注釈を分離し、選択区間だけの補正では長い既存注釈を変更しない。
- 暗号化DBへの検索条件の保存・適用・削除、カテゴリ除外、全検索結果のカテゴリ集計。直前の同じ長さの期間を同じ条件・分類ルールで比較。
- 選択した未分類のみのOllama候補提案。名称ラベルのみ・最大20件・手動実行・構造化出力検査・承認前プレビュー。常駐AI・自動適用・外部問い合わせなし。
- AI分類は共有GPU排他、空きランタイム、2分の制限・終了後解放、Foregroundの既知ゲーム／画面非表示／期間変更時の中止を使用。
- 新しいExplorer回帰スイート、三テーマの混在・保存条件画面、保存条件とAI承認コマンドを追加。実Ollamaモデルの分類精度、実ブラウザ、ゲームFPS、Arm64実機、72時間常駐は別途検証が必要。
- 公開は引き続きソース用プレリリース。未署名の開発ZIPを一般公開しない。

## 前回の変更（3.3.0）

- ドメイン境界・プロセス名の標準分類、Gaming、チャンネルID／完全一致ルール。
- 履歴からの区間補正と、確認後に保存する分類ルール。未分類の時間順整理、最大100対象の原子的な一括保存。
- 元履歴を更新せず、手動補正を優先。分類専用回帰テストで大小文字を区別するIDの保存、改名、変更後の再確認、原子性、時間の保存を検証。
- C# 8スイート・ブラウザ20ケース・統合テスト・公開内容検査の19ケースをローカルで確認。WPFは三テーマの人工データ画面と分類コマンドを確認。
- 3.3.0コミット `80db60760411b988bde0a3458e395756133c480c` の[CI](https://github.com/axelose/PCActivityTracker/actions/runs/38201334313)／[CodeQL](https://github.com/axelose/PCActivityTracker/actions/runs/38201334465)成功を確認。3.4.0のGitHub検証状態は新しい対象コミットのActionsで確認すること。
- 公開はソース用プレリリース。証明書未取得の未署名実行版は一般配布しない。実ブラウザ・Arm64実機・ゲームFPS・72時間常駐の検証とは区別する。

## 前回の変更（3.2.6）

以下はローカル検証済みの変更です。GitHub上の検証状態は、対象コミットのActions実行結果で確認してください。既存の3.2.5 Source Previewのタグ・Releaseは更新せず、未署名の開発実行ファイルも一般配布しません。

| 項目 | 実装内容 | 検証方法 |
|---|---|---|
| ブラウザ通信 | 5秒の応答期限、ポート破棄、最新状態だけの送信、ページ応答の1秒期限 | NodeのNative Messaging／Chromium API模擬回帰テスト |
| ページ切替・プライバシー | DOM取得後にアクティブタブ、URL、シークレット、停止状態、トークンを再確認 | 遅延応答・タブ切替・一時停止テスト |
| チャンネル集計 | 表示名と識別子を分離、取得済みIDで集計、名前変更後の検索、未取得状態を表示 | 暗号化した人工DBの集計・検索・設定OFFテスト |
| DB移行 | schema 5→6、creator_id追加、移行前バックアップ、旧履歴は推測統合しない | 旧版人工DBの移行・再初期化・バックアップ検査 |
| メモリ・常駐負荷 | 強制GC／Working Set縮小を廃止、32MiB以上・2分以上の間隔で非ブロッキング最適化GCを要求。画面を開くまで更新タイマーを停止 | 純粋な判定境界テスト、自身のメモリカウンター、非表示起動のタイマー検査 |
| CI | main push＋PRに整理、旧実行キャンセル、固定SHAの取得アーカイブのみキャッシュ、部分DL・期限・再試行 | ローカル構文・公開内容検査。変更後のGitHub実行結果は対象コミットのActionsで確認 |
| 保守性 | MainViewModelを責務別partialファイルへ分割、既存XAMLバインディングAPIを維持 | ReleaseビルドとWPF三テーマの非表示レイアウト検査 |
| 独立テスト | memory／browser／aggregation／settings／ui／migrationを個別実行可能にした | `scripts/test-regression.cmd` |

今回の検証結果は `artifacts/verification/regression-report.json` に生成されます。基点commitに加え、未コミット状態、検証開始・終了時のソースハッシュ、選択したスイート、成否を記録します。未コミット版を既存commitの検証結果と取り違えないためのローカル記録であり、署名付きprovenanceではありません。

## 一般向け実行版の残課題

1. 信頼されたコード署名・タイムスタンプ経路と正式Publisherの確定。証明書は未取得。
2. main／リリースタグの保護、署名付きprovenance、公開Releaseの変更制御。Rulesetは未設定。
3. .NET未導入のWindows 11 x64／Arm64実機で、導入・更新・自動起動・削除・Chrome／Edge／Brave連携を確認。
4. 72時間常駐、ゲーム併用負荷、Defender／SmartScreen、外部送信の実機検証。模擬テストだけで合格扱いにしない。
5. 拡張ストア公開情報（プライバシー・サポートURL、拡張ID、人工データ画像）の整備。
6. MSIXのNative Messaging登録。未対応のままブラウザ連携付き完全版として配布しない。

## 制限と安全境界

- YouTubeの不変チャンネルIDを取得できた場合は改名後も同じキーで集計できる。ハンドル／Twitchログイン名は変更可能で、外部APIなしには改名後の同一人物を保証できない。
- 名前だけの旧履歴と識別子付き新履歴は推測で統合しない。取得不明は視聴ゼロではない。
- UIの非表示テストは画面の見栄えや実ブラウザのDOM互換性を保証しない。
- GC要求の測定値は単発の人工テスト。実アプリの長時間メモリ減少やゲームFPS改善を実証した数値ではない。
- ユーザーの実履歴、起動中のアプリ、GitHub公開設定は今回のテストで変更しない。
