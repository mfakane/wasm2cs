# SH-11 managed 実行の安定化

状態: 完了。前提: [SH-10](SH-10-mono-startup.md)。次: [SH-12](SH-12-self-hosting.md)。

## 目的・変更対象

wasm2cs が依存する managed の動作を、小さなゲストテストで切り分ける。対象はゲストドライバー、managed probe 群、必要に応じた変換器・ホストの修正である。

## 実施手順

1. wasm2cs の使用 API を調べ、文字列・UTF-8、配列、辞書、generics、LINQ、record の等値性、数値整形、例外をゲスト内で実行する probe を作る。
2. GC 前後の参照、生存オブジェクト、配列・文字列の内容を検証する。明示的な collection と繰り返し確保を使い、managed GC と WASM GC 拡張を混同しない。
3. 例外の送出・捕捉、再帰、delegate、ホストとの再入を検証する。失敗は最小の managed probe または WASM fixture まで縮小する。
4. 一つのインスタンスで繰り返し実行し、独立した複数インスタンスでは memory、global、table、ホスト状態が混ざらないことを確認する。
5. ゲスト heap、linear memory、外側プロセスのメモリ、起動・実行時間を別々に測定する。上限到達や OOM を正常終了へ変換しない。

## 検証

この段階で追加する予定コマンド。

```sh
node scripts/self-hosting.mjs managed
```

[共通回帰](../self-hosting-design.md) を実行し、各 probe の期待値は通常の .NET と公式 WASM 実行で確認する。反復回数と seed は固定して記録し、性能の合否を未測定の数値で決めない。プロセスのタイムアウトとメモリ上限は明示する。

## 合格条件

- [x] wasm2cs が使う managed 機能に対応する probe がすべて通る。
- [x] GC 後と反復実行後も結果・参照が正しく、インスタンス間の状態が分離される。
- [x] 修正に回帰テストがあり、ゲスト処理をホスト側コードへ移して通していない。
- [x] メモリと時間の測定結果が、対象 bundle と環境に対応づいている。

## 非対象・失敗時の扱い

BCL 全体、ネットワーク、マルチスレッド、ゲスト内 JIT の対応は要求しない。最終ゲストに必要な機能が範囲外に達した場合は記録し、成功値の固定や probe の削除で回避しない。

## コミットと記録

probe 群、実装修正、GC・反復・状態分離の順に分ける。終了コミット例: `test: validate managed execution on translated mono`。

- 実行コマンド・結果: `prepare`、`inventory`、`reference`、`generate`、`managed`、`hello`、`host`、共通回帰を実行済み。`managed` は通常 .NET、公式 browser-WASM、変換済み Mono の12 probeを比較し、3回反復・2インスタンス・例外伝播・stdout/stderr・host re-entryを確認した。
- probe 一覧・反復条件・メモリ／時間: 一覧と固定条件は `docs/self-hosting/SH-11-managed.json`、結果は `artifacts/self-hosting/managed-results.json`。seed `24301`、3回／instance、2 instance、timeout 15分、linear memory 256 MiB、translated childのGC heap上限1 GiB。runtime SHA-256は `d531922c75237648c1f643077c13ba0da8f9583ecfa3304476ab196984c3efe4`、bundle SHA-256は `63b9c3d762ac6fc79e659c930371c793973a76df80658c2aafe9e22d21eed3af`。guest heap、linear memory、outer working set、startup／execution時間を別々に記録する。
- 未解決事項: なし。性能値は合否閾値ではなく、固定環境の測定記録である。
- 終了コミット: `3ccce9f17e28237703bfd9c68094df3a49149c2b`
