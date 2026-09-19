# SH-10 Mono 起動と Hello World

状態: 完了。前提: [SH-09](SH-09-host.md)。次: [SH-11](SH-11-managed-runtime.md)。

## 目的・変更対象

生成 C# 版 runtime を初期化し、ゲスト C# のエントリーポイントを実行する。対象は .NET 専用ホストの起動・終了処理、DLL 供給、外側の実行ハーネスである。

## 実施手順

1. SH-01 で固定した実物から、memory/table のリンク、WASM 初期化、native constructor、runtime 設定、DLL 登録、Mono 起動、managed entry point の順序を確定する。古い版の公開 API 名を推測して使用しない。
2. bundle のハッシュと依存関係を確認してから起動する。CoreLib、BCL、ゲスト DLL を所定の経路で供給し、外側の通常の assembly loader でゲストを実行しない。
3. 起動から終了までの状態を管理する。初期化失敗後に部分的なインスタンスを実行可能として返さず、start や entry point を二重に呼ばない。
4. ゲストの Hello World と整数計算を呼び、標準出力・戻り値・終了コードを外側へ返す。必要な callback queue を処理し、未実装 import 到達は失敗にする。
5. 外側の実行プロセスから参照 WASM エンジンと通常の `Wasm2Cs` 変換器への依存を除く。起動状態・DLL 供給・entry point 呼び出しを記録する。

## 検証

この段階で追加する予定コマンド。

```sh
node scripts/self-hosting.mjs hello
```

[共通回帰](../self-hosting-design.md) と、CoreLib 欠落、ゲスト DLL 欠落・破損、import 不足、初期化の失敗、異常終了を検証する。参照結果は `reference` の別プロセスで作り、`hello` の実行を代行しない。

## 合格条件

- [x] 生成 runtime が初期化され、ゲストの Hello World と整数計算が一致する。
- [x] 必要な DLL を欠落させると、供給・ロードの失敗として検出する。
- [x] 通常終了と異常終了を区別でき、初期化・実行が二重に発生しない。
- [x] 外側の WASM／JavaScript エンジンや通常 CLR によるゲスト実行の代替がない。

## 非対象・失敗時の扱い

Hello World が動いたことを BCL 全体や wasm2cs 自己実行の成功としない。SH-09 に不足が判明した場合は ABI の再現テストを先に追加し、起動ハーネスだけの特殊な回避処理にしない。

## コミットと記録

起動・DLL 供給、entry point・終了処理、負のテストに分ける。終了コミット例: `feat: boot translated mono runtime`。

- 実行コマンド・結果: `prepare --skip-workload-install`、`inventory`、`reference`、`generate`、`compile`、`host`、`hello` が成功。`hello` は正常系で174 assembly登録、managed Mainの stdout、戻り値0、終了コード0を確認し、CoreLib欠落、ゲストDLL欠落・破損、必須 import 欠落、native 異常終了を別シナリオで検出した。
- bundle ハッシュ・起動状態・DLL 供給ログ: bundle `2b89bf1bd90a650fe81dad2de5ebcd4a8dd339b21f0b7d465895287251bf3d1a`、runtime `d531922c75237648c1f643077c13ba0da8f9583ecfa3304476ab196984c3efe4`。生成C#は `artifacts/self-hosting/generated/` と `generated-manifest.json` に保存し、compile結果は `artifacts/self-hosting/compiled/bin/Release/net10.0/Generated.dll` と `compile-results.json` に保存する。正常系と負のシナリオは `artifacts/self-hosting/hello-results.json`、ハーネス自体の失敗は `artifacts/self-hosting/hello-failure.json` に記録する。起動ログは `results.phases` に保持する。
- 未解決事項: なし。
- 終了コミット: `2e6913ad7eebea081febb532a9a37365055cee9e`
