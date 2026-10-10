# ネイティブDBエンジンのソースビルド

2026-09-21 / デスクトップ3.2.5

SQLCipher 4.5.2（SQLite 3.39.2）を、無償Community版のSQLCipher 4.19.0（SQLite 3.53.4）へ更新しました。OpenSSL 3.5.8 LTSを静的リンクし、LLVM-MinGWのUCRTターゲットでx64／Arm64のDLLを作成します。SQLCipher 5 betaは使用しません。

## ビルド

.NET SDKとGit for Windows（Perl／sh／cygpathを含む）を準備します。通常版はProgram Files配下を検出します。Portable版はドキュメント配下の `PCActivityBuildTools/PortableGit` に展開するか、環境変数 `PCAT_BUILD_GIT_ROOT`／`-GitRoot` で指定してください。空白を含まないプロジェクトパスで実行します。

```powershell
.\scripts\build-native-sqlcipher.cmd -Runtime win-x64
.\scripts\build-native-sqlcipher.cmd -Runtime win-arm64
.\scripts\self-test.cmd
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-native-compatibility.ps1
```

Arm64開発ではDLL準備後に `dotnet build PCActivityTracker.sln -c Release -r win-arm64` を使います。通常の `build.cmd` はx64向け。`publish.cmd -Runtime win-arm64` はArm64 DLLも準備します。

取得先とSHA-256は `.config/native-sources.json` に固定しています。DLLは `artifacts/native/out/<CPU>/pcactivity_sqlcipher.dll`、ソース・ログ・キャッシュは `artifacts/native` 内です。初回はネット接続、数GBの空き容量、数分以上を見込んでください。最大2並列でビルドし、システムPATHを変更しません。アプリ実行時にビルドツールや接続は不要です。

Portable Gitに不足するPure Perlモジュールだけを固定した公式Perlソースからビルド時の検索パスへ追加します。OpenSSLの暗号コードは変更せず、Arm64用のプラットフォーム設定のみ `scripts/native/openssl-arm64.conf` で補います。

## 組み込みと互換性

旧SQLitePCLRawバンドルを製品依存から除去しました。アプリは自身の `runtimes/<CPU>/native/pcactivity_sqlcipher.dll` だけを絶対パスで読み込み、SQLite版と暗号化APIを確認します。PATH上の別のSQLiteにはフォールバックしません。

暗号化キー、DPAPI、ページ形式、スキーマ移行方式は変更していません。`cipher_migrate` や自動再暗号化は追加せず、実ユーザーDBをテストやビルドへ持ち込んでいません。

x64では次のテストが成功しました。

- 旧4.5.2の暗号化DBを新4.19.0で読み書きし、旧版で再読込。
- 新版で作成したDBを旧版で読込。
- 旧版のコミット済み・未チェックポイントWALを新版で復旧。
- 誤った鍵と暗号化ページ破損を拒否。
- SQLite／暗号化整合性検査、平文ヘッダー・架空の機密文字列の非露出。
- アプリ全体の自己診断（暗号化移行、バックアップ復元、削除、分析、ブラウザIPC等）。

互換性プローブは隔離された一時ディレクトリだけを操作します。旧エンジンはチェックサム固定のテスト用NuGetから取得し、配布物には含めません。人工データでの確認であり、全既存DBや将来スキーマのダウングレードを保証するものではありません。実ユーザーの更新前にはバックアップを推奨します。

Arm64はクロスビルド済みですが、Arm64実機の起動・互換性・性能は未確認です。.NET未導入環境、長時間常駐、実ブラウザとの確認も公開チェックリストに従ってください。

## 配布と保守

`native-build.json` には入力版、ソースハッシュ、フラグ、署名前DLLのSHA-256を記録します。署名付きprovenanceでも、ビット単位の再現性の保証でもありません。コード署名後のハッシュはSBOMとZIP検証を使用してください。

ライセンスは `THIRD-PARTY-NOTICES.md` と `licenses` に同梱します。本プロジェクト独自ビルドであり、Zeteticの公式バイナリ／商用サポート付き製品ではありません。

今後はソースピン更新→両CPUビルド→旧版互換性→自己診断→署名・SBOM・ZIP検証→実機確認の順で更新します。OpenSSLを含むネイティブ側のセキュリティ告知を別途確認してください。NuGet監査だけではネイティブソースを監査できません。

公式情報: [SQLCipher releases](https://github.com/sqlcipher/sqlcipher/releases)、[Community Edition](https://www.zetetic.net/sqlcipher/community/)、[OpenSSL](https://github.com/openssl/openssl)、[LLVM-MinGW](https://github.com/mstorsjo/llvm-mingw)。
