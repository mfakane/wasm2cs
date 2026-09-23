# SH-07 WASM 例外処理

状態: 完了（legacy EH 対応）。前提: [SH-06](SH-06-tables.md)。次: [SH-08](SH-08-large-codegen.md)。

## 目的・変更対象

対象 runtime の native WASM 例外処理を C# で実現する。対象は tag、例外命令、型検証、制御フローの生成、ホスト境界の例外支援とテストである。

## 実施手順

1. SH-01 の実物で使用する EH の形式と命令を確認し、その形式に対応する仕様・参照ツールを固定する。legacy と新しい命令形式を同一として decode しない。
2. tag とその payload 型、import/export、throw、catch、再送出と必要な制御命令を追加する。tag は同じ署名だけで同一視せず、import された tag の同一性を保持する。
3. WASM 例外、WASM trap、ホスト由来の例外を別の経路として扱う。catch がどの種類を捕捉するかは採用仕様と ABI に基づいて決め、単一の無条件 `catch (Exception)` で置き換えない。
4. 例外領域を越える分岐と値の受け渡しを、C# で合法な制御フローへ変換する。既存の goto をそのまま try/catch 内外へ挿入しない。
5. 間接呼び出し・host callback・再入を通る例外を接続し、発生元と tag/payload を診断で追えるようにする。

## 検証

[共通回帰とパッケージ検証](../self-hosting-design.md) と、例外を使う小型 fixture の Unity Editor／IL2CPP 実行を行う。

- 入れ子の捕捉、捕捉されない例外、再送出、複数型の payload。
- 同じ署名だが異なる tag、共有された imported tag。
- 例外領域を出る分岐、loop、return とスタック検証。
- direct／indirect call と host callback を越える例外。
- メモリ範囲外などの trap が、誤って WASM の通常の例外として処理されないこと。
- EH 追加前から保証している host 例外の伝播・同一性が、EH を使わないコードで維持されること。

## 合格条件

- [x] 実物にある legacy EH 命令・tag 形式をすべて decode・検証・生成できる。
- [x] 例外分類、payload、捕捉範囲が参照実行と一致する。
- [x] C# 9 コンパイルと Unity IL2CPP の小型例外テストが通る。

## 非対象・失敗時の扱い

使用されていない別世代の EH 形式まで一般化することは必須にしない。参照エンジンが対象の EH を実行できない場合は、対応する版を用意するまで未検証とする。EH を無効化して目標構成を変えない。

## コミットと記録

tag と検証、制御フローと生成、ホスト境界と Unity テストに分ける。終了コミット例: `feat: support target wasm exception handling`。

- 実行コマンド・結果: `wat2wasm --enable-exceptions` と `wasm-validate --enable-exceptions` で `Exceptions.wat`、`Imported.wat`、`HostException.wat`、`HostTagException.wat` を検証。`dotnet build tests/Wasm2Cs.Tests/Wasm2Cs.Tests.csproj --nologo` と `dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll` が成功し、legacy tag、payload、tag identity、rethrow、branch escape、direct／indirect call、trap、host exception propagation、host-thrown WASM tag、Node.js 参照実行を検証。`node scripts/pack.mjs` と `pwsh.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-unity.ps1 -Editor "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -PackagePath "artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz"` も成功。
- EH 形式・仕様・参照エンジン・Unity ログ: SH-01 実物の section 13／legacy opcode（`try`、`catch`、`throw`、`rethrow`、`catch_all`）を対象とし、Node.js v22.17.0 と Unity 6000.6.0f1 Windows x64 IL2CPP で確認した。最新の Unity marker とログ hash は [SH-12.5 Unity 記録](../self-hosting/SH-12.5-unity.json)に保存する。fixture `Exceptions.wasm` SHA-256: `2928941dd284cddafbf07d37bfc32d27c7a99823fd1f3d5b7e35f828699a87df`、`Imported.wasm`: `fb34686ce030c7dd733983f8a74ca21a617cac8a0d38b507c5b8ccb114143e0d`、`HostException.wasm`: `c061a921db54de1c58f078429250dc05fc0393925ea1fb66c14f01d14d603f2f`、`HostTagException.wasm`: `cbc323494e88664ae3ea152812fbdc32e2051d4ef2220da1ae764588806ddb9f`。
- 未解決事項: SH-01 実物にない `delegate`、新世代の `try_table`／`throw_ref` は対象外。
- 終了コミット: `13e386b` (`feat: support target wasm exception handling`)
