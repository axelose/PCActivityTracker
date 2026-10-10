# GitHub公開準備の確認結果

確認日: 2026-09-22 / 対象: デスクトップ3.2.5、ブラウザ拡張3.2.3

ローカルで実装・検証できる対策を追加しました。GitHubへのpush、公開Release作成、ストア申請は行っていません。配布バイナリの一般公開には、以下の残課題があります。

## 実装・修正した項目

- Activity DB、鍵、ログ、署名素材、生成物をGit除外。ソース検査はJavaScript／HTMLも含め、認証情報・ユーザーパス・未固定Actions参照を検出する。
- SQLite補助ファイルとクラッシュダンプの除外不足を補完。Git indexのblobと作業ファイルを個別に検査し、部分ステージによる秘密情報の見落としを防ぐ。pubxmlや拡張子なしのテキストも検査対象。
- Gitが読めないときのファイル検査とGit検査を区別。公開用の `-RequireGit` はGitを検証できなければ失敗する。所有者確認を自動的に迂回しない。
- NuGet lock fileとx64／Arm64の固定復元グラフを追加。公開スクリプトはlocked restore後に `--no-restore` でpublishする。
- .NET SDKを10.0.400へ更新。グローバルSDKがなくてもローカルSDKを選択し、global.jsonを起点に互換性を確認する。
- SDK導入スクリプトを固定commit・正しいraw-byte SHA-256で検証。PowerShell 5.1とプロジェクト外からの実行を確認。
- 例外メッセージ／スタックを診断ログと復元ログへ保存しない。機密文字列が生ログへ残らない回帰テストを追加。
- Actionsをcommit SHAへ固定し、NuGet Dependabot、CodeQL、依存変更レビュー、JavaScript構文検査を追加。
- サポート、プライバシー、脆弱性報告、脅威モデル、PR／Issueテンプレート、リリース手順を整備。
- Apache 2.0と使用中SQLCipherのライセンス全文を同梱。拡張manifestとアプリ側の推奨拡張版を検査し、デスクトップだけの更新で拡張の再読み込みを要求しない。
- 固定版Microsoft SBOM ToolによるSPDX 2.2生成・全ファイルハッシュ検証と、SDK／Runtime／CPUを記録したBUILD-INFO.jsonを追加。
- 配布ZIPとフォルダーの全ファイル一致、必須同梱物、Self-contained、バージョン、DB／鍵／ログ混入を検査。公開時は自作EXE・DLLの署名とタイムスタンプも必須。
- 不正ZIP（改変・欠落・重複・パストラバーサル）と未署名公開の拒否を回帰テスト化。CIに両CPUの配布検査を追加。
- SQLCipher 4.19.0／SQLite 3.53.4／OpenSSL 3.5.8へ更新。固定ソースからx64／Arm64 DLLを構築し、旧SQLitePCLRawバンドルを配布依存から除去。ネイティブ依存のSBOMとライセンスを追加。
- 配布先を先に再帰削除する処理を廃止。アプリ／Native Host起動中とファイル使用中は置換を拒否し、置換失敗時はロールバック。成功時も旧成果物を `.previous-<ID>` として保存する。

## 実施した検証

| 検証 | 結果 |
|---|---|
| ソースファイル検査 | 成功。模擬トークン混入の拒否は3.2.3時点で検証済み |
| Gitを必須にした検査 | 所有者不一致によるGit拒否を検出し、想定どおり失敗。Git indexは未検証 |
| Releaseビルド | 3.2.5で警告0、エラー0。GitHub CI本実行は未確認 |
| bin/objを持たないソースコピーからのビルド・自己診断 | 3.2.3時点で成功。SDK／NuGetキャッシュは既存環境を利用 |
| 3.2.5 x64の自己診断 | 成功。SQLite 3.53.4／SQLCipher 4.19.0を実行確認。DB暗号化・移行・削除、Native Host往復通信、分析、ローカルAI境界、ログ保護など |
| ネイティブDLL | x64／Arm64ともビルド成功。Arm64端末での実行は未実施 |
| 旧版DB互換性 | x64で4.5.2との相互読込・更新、未チェックポイントWAL、誤鍵・破損拒否、平文非露出が成功 |
| publish後のlocked restore | 成功。通常ビルドと配布ビルドのグラフ不一致を解消 |
| 拡張JavaScriptの構文 | 3.2.3時点で成功。今回は拡張コードの変更なし。実ブラウザのペアリング再確認は未実施 |
| NuGetの直接・推移依存関係の既知脆弱性監査 | 2026-09-22の再監査で0件。ネイティブ部分の安全性を保証する結果ではない |
| SBOMと配布ZIP | 3.2.5 x64／Arm64のSelf-contained作成と検査成功。全ファイルハッシュ、必須同梱物、ネイティブ版情報・CPU、ZIP一致を確認 |
| 公開検査の拒否テスト | 3.2.5で改変／欠落／重複ZIP、不正パス、未署名公開の5ケースすべて成功 |
| 配布先の安全な置換 | ファイルロック拒否、途中失敗のロールバック、旧版保存、起動中アプリの拒否の4ケース成功 |
| ステージ済み内容の検査 | 隔離Gitリポジトリで19ケース成功（Windows PowerShell 5.1／PowerShell 7）。実リポジトリは未コミット・index 0件。自動で追加／コミットしていない |
| コード署名 | 未署名のローカル検証版。公開禁止マーカーあり |
| GitHub Actions／CodeQL本実行 | リポジトリ未公開のため未実施 |

## 公開前の残課題

1. **ネイティブDB更新後の実機検証を完了する。** 無償Community版の独自ビルドへ更新済み。人工データの互換性検査は成功したが、Arm64実機と代表的な実運用での確認は残る。手順・制限は `NATIVE_DATABASE_UPGRADE.md` を参照。実ユーザーの履歴DBはこの検査で変更していない。
2. **リポジトリの所有権と公開設定を確定する。** 現在Gitは所有者の違いを検出している。所有者・取得元の確認後にアクセス設定を解決し、`scripts\verify-public-source.cmd -RequireGit` を成功させる。その後、公開先、Private Vulnerability Reporting、Ruleset、Secret scanning、Dependabotを設定してCIを実行する。
3. **正式な署名・配布経路を用意する。** 証明書未取得で現在のZIPは未署名。信頼された署名方式、正式Publisher、配布URLを確定し、署名・タイムスタンプを実証する。準備事項は `CODE_SIGNING.md`。SBOM生成は実装済みだが、BUILD-INFOは署名付きprovenanceではない。GitHub側のattestation発行は未実装。
4. **配布対象で実機確認する。** .NET未導入Windows 11、Arm64、Chrome／Edge／Braveで導入・更新・連携・削除を確認。MSIXはNative Messaging登録未対応のため、サイト連携を含む完全版としては公開しない。
5. **ストア公開情報を確定する。** 公開プライバシーURL、サポートURL、拡張ID、架空データの画面画像を用意する。

## 検証成果物

- `artifacts/release/PCActivityTracker-3.2.5-win-x64.zip`
- `artifacts/release/PCActivityTracker-3.2.5-win-arm64.zip`
- `artifacts/release/PCActivityTracker-Browser-Extension-3.2.3.zip`
- `artifacts/release/SHA256SUMS.txt`

再配布ビルド時のファイル使用中エラーで欠けた67ファイルは、残存ZIPから復元して全ファイルハッシュを検証しました。利用者がアプリを終了した後に、安全な置換処理で両CPUを再ビルドしました。最終ログは `artifacts/verification/publish-safe-win-x64-325.log`、`publish-safe-win-arm64-325.log`、`self-test-release-325.log`、`release-rejection-325.log` にあります。この表の最終結果は配布ZIP作成後の追記です。3.2.5をローカルで試す場合は通知領域の旧版を「終了」してから新版EXEを起動してください。対応拡張は3.2.3のままです。自己診断は架空データを使い、ユーザーの既存履歴DBは変更していません。一般向け公開準備の完了を意味する成果物ではありません。
