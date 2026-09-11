# SH-01 対象成果物の固定と参照実行

状態: 完了。前提: 既存11段階の完了。次: [SH-02](SH-02-typed-ir.md)。

## 目的・変更対象

変換器が対応すべき実物を固定する。対象は新設する自己実行用ゲストサンプル、専用ツールチェーンの準備処理、`scripts/self-hosting.mjs`、成果物・必要機能の記録である。共通の条件は [対象プロファイル](../self-hosting-design.md) に従う。

この段階では .NET ランタイムを wasm2cs で実行しない。公式 Node.js 環境でゲストが動くことを先に確認し、以後の失敗を変換器・ホストの問題と切り分ける。

## 実施手順

1. `dotnet --info`、workload 一覧、Node.js、利用可能なビルドツールを確認する。基準 SDK は `10.0.400` とし、WASM 用 SDK/workload は専用の環境に配置する。既存の Nix store やユーザーのグローバル設定を書き換えない。
2. `browser-wasm` の Hello World と、既存 `Wasm2Cs` プロジェクトを DLL 参照するゲストドライバーを追加する。ドライバーは入力バイト列とクラス名を受け、`Transpiler.Translate` のテキストまたは変換エラーを返す。
3. AOT、Jiterpreter、SIMD、threads、Webcil、managed trimming を無効化し、WASM 例外処理と invariant globalization を有効にする。必要な native rebuild/relink は公式のビルド設定で行う。ランタイムのソース改変はしない。
4. SDK、workload set、runtime pack、テンプレート、Emscripten、Node.js、調査ツールのバージョンとビルド引数を固定する。成果物の相対パス・サイズ・SHA-256 と、実効ビルド設定を記録する。runtime と managed assembly を別項目にする。
5. 固定版 WABT などの独立した調査ツールで全関数を調べる。命令・型・セクション・import/export・table・memory・data・start の一覧と、managed DLL・設定・JavaScript 側の起動処理を記録する。各必要機能に担当 SH と状態を割り当てる。
6. 公式 Node.js 実行経路で Hello World、既存 Arithmetic fixture、既存 Clang fixture の変換と不正入力の診断を保存する。host、guest、入力、生成設定の各バージョンを対応づける。
7. プロファイルの設定だけでなく、SIMD／共有 memory／動的 WASM 生成への依存が残らないことを確認する。Jiterpreter の trace・thunk 設定も記録する。未確定項目を後続文書へ反映する。

## 検証

この段階で追加したコマンド。

```sh
node scripts/self-hosting.mjs prepare
node scripts/self-hosting.mjs inventory
node scripts/self-hosting.mjs reference
```

`prepare` は専用の CLI home／NuGet／MSBuild 拡張領域で workload と bundle を作成する。`inventory` は同じ bundle を入力に固定版 WABT の全関数逆アセンブルを機械可読の一覧と人が読める要約へ保存する。`reference` は公式 Node.js 実行環境で参照結果を採取する。別々の bundle が混ざった場合はハッシュ照合で失敗させる。既存コードを参照するサンプル追加後は [共通回帰](../self-hosting-design.md) も実行する。

## 合格条件

- [x] Hello World とゲスト内 wasm2cs の変換が公式 Node.js 環境で動く。
- [x] 再取得可能なツールチェーン固定情報、build log、成果物ハッシュ、依存 DLL 一覧がある。
- [x] 全関数を調査した必要機能一覧があり、最初の未対応命令で途切れていない。
- [x] 設定と実物の照合が終わり、すべての必要機能に後続の担当段階がある。
- [x] 参照結果の採取は成功しているが、wasm2cs によるランタイム実行はまだ未検証であることが明記されている。

## 非対象・失敗時の扱い

WASI への切替、別の runtime バージョンへの無断変更、Mono fork は行わない。SDK/workload の組合せが取得できない場合は準備の阻害要因として報告する。最大256 MiBの初期設定が不足する場合も、使用量と失敗を記録してプロファイル変更を確認する。大きな WASM/DLL は `artifacts/self-hosting/` に置き、バージョン管理するのは手順・固定情報・一覧・ハッシュとする。

## コミットと記録

準備・サンプル、一覧化・参照テストの順に検証可能なコミットへ分ける。終了コミット例: `test: pin dotnet wasm self-hosting baseline`。

- 実行コマンド・結果: `prepare` 成功（SDK `10.0.400`、workload set `10.0.400-manifests.330ea142`、`wasm-tools` `10.0.111/10.0.100`、runtime pack `10.0.11`）。`reference` 成功（Hello World、Arithmetic、Clang、不正入力）。`inventory` 成功（[WABT `1.0.41`](https://github.com/WebAssembly/wabt/releases/tag/1.0.41)、runtime native WASM の `12,356` 関数）。
- 対象 bundle ハッシュ・ログ: `10e2f5cee5fecd86681ef9e043082a04df131a0d7eb76855ac895ea0cbf3c9e6`。成果物とログは `artifacts/self-hosting/` の `bundle-manifest.json`、`toolchain.json`、`prepare.log`、`reference-results.json`、`inventory.json`、`inventory.md` に保存。
- 未解決事項: なし。Wasm2Cs による生成 runtime の実行は、この段階ではまだ検証しない。
- 終了コミット: 未完了（コミット後に記録）
