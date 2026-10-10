# GitHub public release guide

## Repository settings required before the first release

1. Default branchを `main` にし、Pull Request、必須CI、会話解決を要求するRulesetを設定する。
2. GitHub Private Vulnerability Reporting、Dependency graph、Dependabot alerts、Dependabot security updatesを有効にする。
3. Secret scanningとPush protectionを有効にする。利用プランで使用できない場合は、ローカルの秘密情報検査を追加してから公開する。
4. ActionsがPull Requestから書き込み権限やsecretsを取得しない設定にする。
5. 公開版のタグとReleaseを変更不可にする運用を採用し、署名済み成果物、ブラウザ拡張ZIP、`SHA256SUMS.txt`、SBOM、provenanceを同じReleaseへ添付する。

## Release boundary

`scripts\publish.ps1` は証明書Thumbprintなしでは公開用成果物を作りません。`-AllowUnsigned` はローカル検証専用で、成果物に公開禁止マーカーを追加します。マーカーの手動削除は署名の代替になりません。

初回公開前に、次のどちらかを選択してください。

- Microsoft StoreへMSIXを提出し、Store署名と更新経路を利用する。
- 信頼されたWindowsコード署名証明書でZIP版のデスクトップ／Native HostのEXE・DLL、LocalAi.Runtime.dll、独自ビルドのpcactivity_sqlcipher.dllを署名し、タイムスタンプを付ける。準備事項は `CODE_SIGNING.md` を参照する。

現状のMSIXはChromium Native Messaging登録をインストーラーから構成しないため、サイト連携を含む完全版として配布できません。GitHubで先行公開する場合は、署名済みSelf-contained ZIPを正式版とし、MSIXはブラウザ連携対応後に公開します。

## Maintainer release flow

```powershell
.\scripts\verify-public-source.cmd -RequireGit
.\scripts\self-test.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-native-compatibility.ps1
.\scripts\publish.cmd -Runtime win-x64 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

Arm64も同じバージョンと証明書で作成します。`docs\RELEASE_CHECKLIST.md`を完了し、クリーンなWindows 11環境で署名、ハッシュ、起動、更新、削除、ブラウザ連携を確認してからタグを作成します。

リポジトリURL、Publisher identity、HTTPSダウンロード先、Chrome Web Store／Edge Add-ons IDはローカルコードから決められないため、所有者が確定してテンプレートへ設定する必要があります。

## Local verification limits

`verify-public-source.cmd` の通常実行は、Gitが利用できないソースZIPでもファイル検査を行います。`Git index checked: False` のとき、Gitへ追加済みの内容は未検証です。公開時は必ず `-RequireGit` を付けてください。Gitが所有者の不一致（dubious ownership）を検出した場合は、所有者と取得元を確認し、利用者自身でアクセス設定を解決してから再実行します。スクリプトは `safe.directory` を自動変更しません。

Git利用時は `ls-files --stage -z` でindexを列挙し、各ファイルの保存済みblobを直接検査します。作業フォルダーの修正だけではステージ済みの秘密情報を隠せません。追跡済みファイルと未追跡候補の現在内容も別に検査します。DB補助ファイルやクラッシュダンプを強制追加した場合も拒否し、未解決競合・symlink・submoduleは手動レビューが必要なため失敗扱いです。

検査結果にはindex件数と公開候補件数を表示します。indexが0件なら、コミット予定の内容を検査したことにはなりません。初回の追加後とコミット直前に再実行してください。`scripts/test-public-source.ps1` は隔離した一時Gitリポジトリで、部分ステージ、作業ファイル削除、強制追加、日本語パスなどを検証します。実プロジェクトをステージ／コミットせず、テスト用データは終了時に削除します。

これは既知形式の秘密情報・危険ファイルの検査であり、画像の中身や過去コミットの全履歴を自動監査するものではありません。履歴を含めて公開するときは別途履歴検査が必要です。

NuGetの既知脆弱性検査が0件でも、同梱SQLCipher／SQLiteのネイティブコードまで安全と判定したことにはなりません。自己診断が表示するエンジンバージョンも確認し、更新時は既存暗号化DBとバックアップの互換性を検証してください。更新の判定手順は `NATIVE_DATABASE_UPGRADE.md` を参照してください。

## Automated package checks

`publish.cmd` は署名後に固定版Microsoft SBOM ToolでSPDX 2.2を生成し、全配布ファイルのハッシュを検証してからZIPを作ります。続いて `verify-release.cmd` がZIPとフォルダーの一致、パストラバーサル・重複エントリ、DB／鍵／ログ混入、Self-contained構成、製品バージョン、署名とタイムスタンプを検査します。失敗した場合、既存の完成済み成果物には置き換えません。

ビルド開始前と置換直前に、置換対象フォルダーのPC Activity Tracker／Native Hostの終了を確認します。別バージョンのフォルダーで動くアプリには干渉しません。旧成果物は再帰削除せず、`.previous-<ID>` を付けた名前に保存してから新版へ切り替えます。途中失敗時は戻す処理を行います。ロールバックも失敗した場合は警告に出る保存先を保管し、アプリ終了・ファイルロック解消後に復元してください。旧成果物はディスク容量を使うため、新版の動作確認後に必要に応じて手動で整理してください。

SBOMは `_manifest/spdx_2.2/manifest.spdx.json`、ビルド情報は `BUILD-INFO.json` にあります。管理依存の検出は一時コピーしたNuGet復元情報を対象にし、個人の履歴DBは読みません。ライセンス情報を取得する外部APIは無効化しています。依存一覧にはx64／Arm64双方の復元グラフが含まれます。3.2.5のSQLCipher／OpenSSL／LLVM-MinGWランタイムは別の `NATIVE-SBOM.spdx.json` に入力バージョン・取得先・チェックサムを記録し、主SBOMの外部文書参照として連結します。入力と配布ファイルの追跡用であり、ネイティブ内部の全ソースファイルの部品表ではありません。

CIでは両CPUのパッケージを作り、未署名版の公開拒否、改変・欠落・重複ZIP、不正な相対パスの拒否も回帰テストします。署名証明書と実機なしで署名済み配布やArm64起動を保証することはできません。

`BUILD-INFO.json` は署名付きprovenanceではありません。GitHub側のビルド来歴の署名・attestation発行とReleaseへの添付は、公開先と署名方式の確定後に追加する未完了項目です。ZIP内SBOMのハッシュが一致しても、配布者の真正性は別途署名・信頼できる配布元で確認してください。
