# Third-party notices

配布版はMicrosoft .NET RuntimeおよびWindows Desktop RuntimeをSelf-contained形式で同梱します。各コンポーネントにはMicrosoftのライセンスとThird Party Noticesが適用され、公開スクリプトは `DOTNET-LICENSE.txt` と `DOTNET-THIRD-PARTY-NOTICES.txt` を配布物へ同梱します。

- .NET: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
- .NET Third Party Notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

暗号化SQLiteにはSQLCipher Community 4.19.0（SQLite 3.53.4）とOpenSSL 3.5.8をソースビルドした `pcactivity_sqlcipher.dll` を同梱します。本プロジェクト独自のビルドであり、Zeteticの公式配布バイナリ／商用サポート対象ではありません。ソース取得先・commit・SHA-256は `.config/native-sources.json`、配布物内の記録は `runtimes/<CPU>/native/native-build.json` にあります。

- OpenSSL: https://github.com/openssl/openssl
- LLVM-MinGW: https://github.com/mstorsjo/llvm-mingw
- SQLCipher: https://github.com/sqlcipher/sqlcipher
- Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0

SQLCipher: Copyright (c) 2025, ZETETIC LLC（使用したタグの本文に準拠）。BSD-styleライセンスは `licenses/SQLCipher-LICENSE.txt`、OpenSSLのApache-2.0は `licenses/OpenSSL-LICENSE.txt`、LLVMランタイムのApache-2.0 with LLVM Exceptionsは `licenses/LLVM-LICENSE.txt`、MinGW-w64ランタイムの通知は `licenses/MinGW-RUNTIME-LICENSE.txt` に同梱します。SQLite本体はPublic Domainです。第三者ライセンスは本プロジェクトのMITライセンスとは別に適用されます。

SQLitePCLRawと旧SQLCipher 4.5.2は製品から除去しました。旧DLLは互換性テストだけで使用し、ビルド用LLVM-MinGW／Git for Windows／Perl一式とともにアプリには同梱しません。

OllamaとAIモデルは本アプリへ同梱しません。利用者が別途導入するOllamaおよび各モデルのライセンスが適用されます。
