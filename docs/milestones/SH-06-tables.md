# SH-06 table と間接呼び出し

状態: 完了。前提: [SH-05.5](SH-05.5-lowering.md)。次: [SH-07](SH-07-exceptions.md)。

## 目的・変更対象

Mono が使用する関数ポインターを、生成 C# の型付き呼び出しとして実行する。対象は table／element の内部表現、Decoder、Validator、生成関数と import バインドである。

## 実施手順

1. table の宣言・import/export と element segment を追加する。対象に現れる active/passive/declarative 形式を一覧と照合する。
2. 必要な `ref.null`、`ref.func`、参照型の検証、table の操作を追加する。funcref と、必要な場合の opaque な externref を区別する。
3. `call_indirect` の型を関数署名の構造で照合する。同じ署名を持つ異なる type index を誤って不一致にしない。
4. table 要素の null、範囲、署名を検査してから静的な型で呼び出す。host delegate も WASM 関数と同じ署名規則で扱い、`DynamicInvoke` や reflection による dispatch を使わない。
5. table を共有する場合の要素更新・grow を一つの状態として管理する。必要な table copy/init/drop には状態遷移のテストを追加する。
6. 小型の Emscripten／C 関数ポインター fixture を用意し、後のホスト実装とは独立して実行する。

## 検証

[共通回帰とパッケージ検証](../self-hosting-design.md) と、小型 fixture の Unity Editor／IL2CPP 実行を行う。

- 正常な間接呼び出しと、異なる type index だが構造が同じ署名。
- null 要素、範囲外、引数または結果の型不一致。
- i64／浮動小数点／複数結果を含む呼び出し。
- imported table、element の初期化順、更新後の呼び出し、grow の失敗。
- host callback からの再入と、元の呼び出しへの復帰。

## 合格条件

- [x] 対象の table／element／参照型命令と間接呼び出しが対応表に揃う。
- [x] 型付き呼び出しと異常時の trap が参照実行と一致する。
- [x] Unity IL2CPP で reflection に依存せず小型 fixture が動く。

## 非対象・失敗時の扱い

任意の JavaScript オブジェクト操作や WASM GC 拡張は実装しない。externref が現れた場合も、ブラウザー全体を模倣する前提にはしない。必要な意味とホスト側の責務を SH-09 へ引き継ぐ。

## コミットと記録

table と element、間接呼び出し、host／Unity 検証に分ける。終了コミット例: `feat: support wasm tables and indirect calls`。

- 実行コマンド・結果: `dotnet build Wasm2Cs.slnx --nologo`、`dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll` が成功。table／element／参照型、構造的な署名比較、null／範囲外／型不一致 trap、passive element の init/drop、grow／size／fill／copy、imported table／host delegate を検証。`pwsh.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-unity.ps1 -Editor "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -PackagePath artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz` も成功。
- 署名・table 比較、Unity ログ: Unity Editor と Windows x64 IL2CPP で `WASM2CS_TABLE_PASS`／`WASM2CS_SMOKE_PASS` を確認。ログ: `C:\Users\fumika\AppData\Local\Temp\wasm2cs-unity-3cc93a9f21d1428f91067efee746cf42`。fixture `samples/Tables/FunctionPointers.wasm` SHA-256: `2f263a219b98a9ee50c5f4e8559d3293f407e9e5d9253dc4b447bbcd0e91934d`。
- 未解決事項: なし
- 終了コミット: `37946f3`
