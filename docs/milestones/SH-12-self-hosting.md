# SH-12 wasm2cs 自己実行

状態: 完了。前提: [SH-11](SH-11-managed-runtime.md)。次: [SH-12.5](SH-12.5-audit-cleanup.md)。

## 目的・変更対象

ゲストの `Wasm2Cs.dll` が実際に入力を変換することを実証する。対象はゲストドライバー、入力・出力の転送、外側の結果検証、自己実行ドライバーである。DLL は通常の IL として供給し、変換ロジックをゲストドライバーへ複製しない。

## 実施手順

1. 通常実行用とゲスト供給用の wasm2cs を同じソース版からビルドする。runtime、BCL、guest、入力、生成設定の組合せを記録する。
2. 起動したゲストに入力バイト列とクラス名を渡し、ゲスト内の `Transpiler.Translate` を呼ぶ。既存 Arithmetic、Clang Algorithms、Host fixture と不正入力を使う。
3. UTF-8 の生成テキストまたは構造化した変換エラーを外側へ返す。stdout の runtime ログと変換結果を混ぜない。大きなテキストの長さと範囲を検査する。
4. 通常実行の出力と比較する。パス・時刻などの環境依存情報は生成結果へ入れず、同じ入力・設定ではテキストを一致させる。予期しない差を正規化して隠さない。
5. ゲスト生成 C# を外側の別ビルド工程でコンパイルして実行する。関数結果、trap、必要な memory/global と host callback を参照 WASM と比較する。
6. 起動後に新しい入力や定数を変更した入力を渡して結果の変化を確認する。`Wasm2Cs.dll` の欠落・破損時はロードに失敗させる。
7. 生成 runtime を評価するプロセスには外側の変換器を参照させない。外側の比較・コンパイル担当プロセスと分離し、ゲスト DLL 供給と呼び出しの記録を保存する。

## 検証

この段階で追加したコマンド。

```sh
node scripts/self-hosting.mjs translate
```

[共通回帰](../self-hosting-design.md) を実行し、同一インスタンスでの複数入力、入力の破損、変換例外、出力バッファ境界、ゲスト DLL 欠落を確認する。固定出力、外側の変換器への委譲、参照 WASM エンジンによる代行では通らない構成にする。

実測結果は [SH-12 translate 記録](../self-hosting/SH-12-translate.json) と `artifacts/self-hosting/translate-results.json` に保存する。ゲストの string JSExport は .NET 10 の by-reference marshaling (`mono_wasm_register_root`、`mono_wasm_string_from_utf16_ref`、`mono_wasm_string_get_data_ref`) を使い、WASM engine や外側の `Transpiler` は使用しない。

## 合格条件

- [x] ゲストの wasm2cs が起動後の入力を変換し、通常実行と生成テキストが一致する。
- [x] ゲスト生成 C# をコンパイル・実行した結果が参照 WASM と一致する。Arithmetic、Clang Algorithms、HostAbi を別工程で実行した。
- [x] 不正入力の診断が一致し、DLL 欠落・破損は変換成功にならない。
- [x] 入力変更・反復実行・出力転送のテストが通る。
- [x] 外側での代替変換を行わない実行構成とログを保存している。

## 非対象・失敗時の扱い

ゲスト内 Roslyn／Source Generator と、runtime WASM 自身の再変換は含めない。現在の最終条件は「別の WASM を C# に変換するコアの自己実行」である。テキストの一致だけで終えず、生成結果の動作も検証する。

## コミットと記録

ゲスト接続と転送、差分検証、代替実行を防ぐ負のテストに分ける。終了コミット例: `feat: run wasm2cs inside translated dotnet wasm`。

- 実行コマンド・結果: `node scripts/self-hosting.mjs translate` — 6入力、ゲスト出力一致、Arithmetic／Clang／HostAbi生成C#実行成功
- 共通回帰: `node scripts/self-hosting.mjs hello` — SH-10成功、`node scripts/self-hosting.mjs managed` — SH-11成功
- runtime／guest／入力ハッシュ・生成結果・実行ログ: [SH-12 translate 記録](../self-hosting/SH-12-translate.json)、`artifacts/self-hosting/translate-results.json`
- 未解決事項: なし
- 終了コミット: `cc41a6f` (`feat: run wasm2cs inside translated dotnet wasm`)
