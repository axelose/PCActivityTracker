# Contributing

不具合修正と小さく検証可能な改善を歓迎します。変更前に `README.md`、`PRIVACY.md`、`SECURITY.md` の安全境界を確認してください。

## 開発時の必須条件

- 通常ユーザー権限とWindowsの文書化されたUser-mode APIだけを使用する。
- 他プロセスへの注入、Hook、メモリ読み取り、プロセスハンドル取得、ドライバー、検出回避を追加しない。
- Activity、URL、タイトルを外部サービスへ送信しない。ローカルAIはloopback限定かつ集計値だけを扱う。
- 新しく収集・保存する項目には、明示的な設定、保持・削除経路、プライバシーポリシー更新、回帰テストを同じ変更へ含める。
- ログ、テストデータ、画面写真、Issueへ実在のActivityデータや連携トークンを入れない。

## 検証

```powershell
.\scripts\verify-public-source.cmd
.\scripts\self-test.cmd
```

依存関係を変更した場合は `dotnet restore PCActivityTracker.sln --force-evaluate` で `packages.lock.json` を更新し、更新理由と脆弱性監査結果をPull Requestへ記載してください。GitHub Actionsはタグではなく、レビュー済みリリースの完全な40文字commit SHAへ固定します。

公開候補はさらに `docs/RELEASE_CHECKLIST.md` を満たし、署名済み配布物で検証します。脆弱性は公開Issueへ書かず、`SECURITY.md` の非公開手順を使ってください。
