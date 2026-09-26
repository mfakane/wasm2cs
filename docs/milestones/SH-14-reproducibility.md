# SH-14 再現性と継続検証

状態: 実施中。前提: [SH-13](SH-13-unity.md)。次: 今回の最終合格判定。

## 目的・変更対象

固定された手順で自己実行を再検証できる状態にする。対象は自己実行ドライバー、ツールチェーン・bundle 固定情報、CI 設定、Unity 検証手順と各段階の実績記録である。

## 実施手順

1. 新しい作業ディレクトリで対象 bundle の再生成とハッシュ照合を行う。バイナリの完全一致を再現できない項目は、差の原因と固定済み bundle の取得・検証方法を記録し、再現したと偽らない。
2. 準備済み bundle に対する inventory、生成、コンパイル、ABI、managed 起動、自己実行を一括実行する `verify` を追加する。各段階の終了コードを検査し、最初の失敗を最終結果へ反映する。
3. 通常の回帰・小型差分・NuGet 消費テストと、runtime 全体の生成・自己実行を別ジョブにする。bundle の版・ハッシュが異なるキャッシュを再利用しない。
4. Unity はライセンスと Windows IL2CPP ツールチェーンを備えた実行環境に限定する。自動実行環境が確保できない場合は手動の実行記録を残し、CI 成功とは区別する。
5. 生成量、ビルド／起動／変換時間、ゲスト memory と外側メモリを保存する。環境付きの基準値を作ってから回帰の閾値を設定する。
6. 未対応機能、対象版、再生成手順、実際に通したテストを文書へ反映する。全 SH の結果・終了コミットを揃え、自己実行の最終条件を再判定する。

## 検証

この段階で `verify` を追加する。他の予定コマンドは前段階で実装済みであること。

```sh
node scripts/self-hosting.mjs prepare
node scripts/self-hosting.mjs reference
node scripts/self-hosting.mjs verify
```

Windows では SH-13 の専用 PowerShell スクリプトを実行する。クリーン環境、キャッシュあり、bundle 破損・不一致、ツール不足、途中のテスト失敗を検証する。`verify` は隠れて workload を更新したり対象 runtime を差し替えたりしない。

## 合格条件

- [ ] 固定版の準備から .NET 自己実行までを一連のコマンドで再実行できる。
- [x] Unity Editor と Windows x64 IL2CPP の最終自己実行結果が保存されている。
- [x] 不一致・欠落・未実行・失敗を成功として集計しない。
- [ ] キャッシュキー、ハッシュ、環境情報、測定結果が検証対象に対応づいている。
- [ ] 全 SH の記録が揃い、現在の対応範囲と未対応範囲を文書から判別できる。

## CI と手動

通常の回帰は `.github/workflows/regression.yml`。runtime 全体の `prepare` / `reference` / `verify` は `.github/workflows/self-hosting.yml` で、`workflow_dispatch` のときだけ動く。キャッシュキーは `wasm2cs-self-hosting-` にプロファイルファイルのハッシュを足したもので、`restore-keys` は使わない。未実行の dispatch は成功ではない。

Unity はライセンスと Windows IL2CPP がある機械で `scripts/test-unity-self-hosting.ps1` を実行する。SH-13 の記録は手動の実績であり、CI の成功ではない。

## 非対象・失敗時の扱い

公開レジストリへの配布、ゲスト内 Roslyn、runtime 自身の再変換は行わない。CI 用のアカウント・ライセンス・外部 runner が必要になった場合は、その設定に必要な権限を別途確認する。ローカルの手動成功を CI での実績として扱わない。

## コミットと記録

一括ドライバー、CI／手動手順、最終実績の順に分ける。終了コミット例: `ci: make dotnet wasm self-hosting reproducible`。

- 実行コマンド・結果: `verify` を追加し、欠落・破損・不一致 bundle、別ハッシュのキャッシュ、ツール不足、途中失敗を成功にしていない。`node scripts/test-self-hosting-verify.mjs` は成功。クリーンな作業ディレクトリでの `prepare` は正典 hash と一致せず、成功扱いにしていない。正典 bundle に対する `verify` の再実行は未了。
- クリーン環境・キャッシュ条件・ハッシュ・測定値: 再生成先 `/tmp/wasm2cs-sh14-prepare`。再生成 hash `f42132f46e44ebdeb9f94f173b4dadeeebd0e6a9f8a1e12cce3080ef6f6db3d2`。正典は `a00695da11c3383f4990d938dab120e47816b8ab1dc7927eb0c2210909015bba`。記録は [SH-14 再生成](../self-hosting/SH-14-regeneration.json) と [SH-14 基準値](../self-hosting/SH-14-baseline.json)。回帰閾値は未設定。
- 未解決事項: 同じ toolchain でも publish の5ファイルがバイト一致しない。正典 bundle はローカル成果物を hash で検証する。`verify` の再実行は未了。
- 終了コミット: 未完了
