# Threat model

## Assets

- Activity DBとバックアップに含まれる利用日時、アプリ、ドメイン、任意のURL・タイトル・配信者名
- SQLCipher鍵を保護するDPAPI暗号文
- ブラウザ拡張ID、連携トークン、AIレポート、ローカル注釈

## Trust boundaries

```text
Chromium extension
  -> Chromium Native Messaging framing
  -> registered native host
  -> current-user-only Windows named pipe
  -> desktop process
  -> SQLCipher DB + DPAPI-protected key

Desktop process -> loopback-only Ollama (explicit user action, aggregate values only)
```

Windows、Chromium、.NET Runtime、SQLCipher、利用者が導入したOllamaは信頼する外部コンポーネントです。インターネット上のWebページ、ウィンドウタイトル、拡張機能から届く値は信頼せず、長さ・形式を検証して保存前にURLを縮約します。

## In-scope threats and controls

| Threat | Main control |
|---|---|
| 別ユーザー／未登録拡張からのIPC | HKCU登録、許可した拡張origin、CurrentUserOnly pipe、ランダムトークン、HMACチャレンジ |
| URL内の認証情報・検索語の保存 | userinfo、query、fragmentの削除、長さ上限、記録設定と除外 |
| DBファイルの持ち出し | SQLCipher全面暗号化、Windowsユーザーに束縛したDPAPI鍵 |
| 診断・Issue経由の漏えい | 匿名化診断ZIP、公開Issueテンプレート、サイズ制限ログ、明示的な手動作成 |
| 悪意ある／巨大IPC入力 | 64KBフレーム上限、同時接続上限、タイムアウト、JSON・scheme・origin検証 |
| 依存関係・CIの改ざん | NuGet source mappingとlock file、脆弱性監査、Actions SHA固定、CodeQL、署名済み公開物 |
| 意図しないクラウド送信 | Activity送信コードなし、ブラウザはNative Messagingのみ、AI接続はloopback限定 |

## Out of scope and residual risk

- 同じWindowsユーザー権限で動くマルウェア、管理者、デバッガからの完全防御
- 画面やキーボードを直接観測できる第三者からの防御
- Windowsアカウント／DPAPI鍵を失った後のDB復旧
- 利用者が手動コピーしたDB・診断ファイル・画面写真の管理
- WebサイトUI変更による配信者名の誤取得

ゲームやアンチチートへ注入、Hook、メモリ読み取り、ハンドル取得、ドライバー導入、検出回避は設計境界外であり、追加しません。

## Review triggers

新しい収集項目、外部接続、IPC、権限、アップデーター、エクスポート、同期、Google Calendar連携を追加する変更では、この文書、`PRIVACY.md`、削除経路、既定値、回帰テストを同時に更新します。
