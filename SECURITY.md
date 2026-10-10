# Security Policy

## Supported versions

セキュリティ修正は最新の安定版だけに提供します。古いバージョンを利用している場合は、履歴をバックアップしたうえで最新版へ更新してください。

## Reporting a vulnerability

リポジトリ上部の **Security** → **Advisories** → **Report a vulnerability** から、GitHub Private Vulnerability Reportingを使って非公開で報告してください。機能が表示されない場合、公開Issueへ詳細を投稿せず、リポジトリ所有者がPrivate Vulnerability Reportingを有効にするまで報告内容を保留してください。

報告には、影響するバージョン、期待した動作、再現に必要な最小手順、影響範囲を含めてください。実在するURL、ウィンドウタイトル、Activity DB、`database.key`、ブラウザ連携トークン、個人を特定できる画面写真は添付しないでください。必要な場合は架空データで再現してください。

個人開発のため回答・修正までの期間は保証していません。修正公開前の開示時期はメンテナーと調整してください。

## Scope

Native Messaging／Named Pipe IPC、SQLCipher鍵管理とDB移行・削除、診断情報の匿名化、更新・署名検証、権限昇格、意図しない外部通信に関する問題を優先します。ゲームやアンチチートを回避する機能は対象外であり、実装しません。

## Release security requirements

公開配布物は信頼できる証明書またはMicrosoft Storeで署名し、SHA-256を公開します。未署名成果物に含まれる `UNSIGNED-DEVELOPMENT-BUILD.txt` を削除して公開版として扱うことは禁止します。詳細は `docs/RELEASE_CHECKLIST.md` を参照してください。
