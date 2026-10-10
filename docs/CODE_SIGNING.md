# コード署名の準備

2026-09-22時点では証明書未取得のため、実際のコード署名は未完了です。無償Community版SQLCipherの利用と、Windowsが信頼する配布者署名は別の事項です。自己署名証明書の自動作成や信頼ストアへの追加は行いません。

## 現在できること

- `publish.cmd -AllowUnsigned` でローカル検証版を作成できます。一般公開禁止マーカーが付き、公開検査は拒否します。
- 署名対象はデスクトップとNative HostのEXE・DLL、LocalAi.Runtime.dll、対象CPUのpcactivity_sqlcipher.dllです。
- 署名処理後にSBOM・ZIP・ハッシュ一覧を生成します。署名後にDLLを書き換えないでください。

## 証明書取得後の手順

1. 公開者名と署名方式を決め、Windowsが信頼するコード署名証明書と秘密鍵の利用環境を用意します。現在のスクリプトはCurrentUserの証明書ストアからThumbprintで選択するSignTool方式です。別方式のクラウド署名には追加対応が必要です。
2. Windows SDKのSignToolを準備し、証明書に対応する鍵プロバイダーが利用できることを確認します。秘密鍵、PFX、PINやパスワードをソースやチャットへ貼り付けないでください。
3. リポジトリ所有者を確認したうえで `scripts\verify-public-source.cmd -RequireGit`、自己診断、旧版DB互換性検証を成功させます。
4. 次を両CPUについて実行します。署名処理はSHA-256とRFC 3161タイムスタンプを指定し、SignToolで検証します。

```powershell
.\scripts\publish.cmd -Runtime win-x64 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
.\scripts\publish.cmd -Runtime win-arm64 -CertificateThumbprint YOUR_CERTIFICATE_THUMBPRINT
```

5. `verify-release.cmd` を `-AllowUnsigned` なしで実行し、クリーンなWindows 11で署名者・起動・ブラウザ連携を確認してから公開します。

証明書を用いた署名・タイムスタンプの成功は、この環境では未検証です。証明書取得や公開は利用者の判断が必要で、自動購入・申請・公開は行いません。署名だけでSmartScreenの警告が必ずなくなる、あるいは安全性が保証されるとは扱いません。
