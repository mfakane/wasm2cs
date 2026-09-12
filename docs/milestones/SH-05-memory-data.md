# SH-05 メモリ・global・data の拡張

状態: 実施中。前提: [SH-04](SH-04-floating-point.md)。次: [SH-05.5](SH-05.5-lowering.md)。機能実装と共通回帰は完了し、Unity 実行記録が未完了。

## 目的・変更対象

.NET runtime と C# ホストが同じ memory/global を扱えるようにする。対象は Module、Decoder、Validator、MemoryOperations、生成コンストラクターとホスト向け API である。

## 実施手順

1. memory/global の import を追加する。型、可変性、limit のリンク条件を検証し、所有するインスタンスと import したインスタンスを区別する。
2. memory の backing array が grow で変わっても、import/export とホストからは同じ memory オブジェクトを参照し続ける構造にする。所有 memory の既存コピー API は互換経路として残す。
3. 対象に必要な `memory.copy`、`memory.fill`、`memory.init`、`data.drop`、passive data と data count を追加する。data count が code より前に来る場合も正しく検証する。セクション ID の数値順ではなく仕様の順序を使う。
4. imported immutable global を使う初期化式など、対象に必要な定数式を型付きで評価する。無効な global 参照や、検証時に許されない式は拒否する。
5. コピー API に送受信バッファの範囲を指定する経路を追加し、呼び出しごとの配列確保を避けられるようにする。grow 後に古い backing array を外部が保持する API は提供しない。
6. 最大メモリ量はプロファイルとホストの制限を照合する。宣言された制限とホストの資源制限を別に記録する。失敗した grow の前後で状態を保つ。

## 検証

[共通回帰とパッケージ検証](../self-hosting-design.md) を実行し、参照実行との比較に次を追加する。

- import の型・可変性・limit 不一致、初期化の順序、start から見える data/global。
- 0 byte の操作、終端位置、負の i32 アドレスの unsigned 解釈、足し算の overflow。
- 重複するコピー範囲、drop 後の init、data count 不一致、不正なセクション順。
- 二つのモジュールとホストによる同じ memory/global の参照、grow 後の可視性。
- OOM／上限到達時に以前の内容を保持すること。ホストの配列引数不正と WASM trap の区別。

## 合格条件

- [x] 必要な memory/global import と data 命令が型付きで検証・実行される。
- [x] 各操作後の memory/global が参照実行と一致する。
- [x] 共有した memory オブジェクトの grow 後も、すべての参照元が新しい内容を見る。
- [x] 既存の独立インスタンスと ReadMemory／WriteMemory のテストが通る。

## 非対象・失敗時の扱い

threads の共有メモリ、atomics、memory64 は追加しない。対象プロファイルで想定外の multi-memory が必要なら、一覧と担当範囲を更新してから進める。上限不足を黙って宣言値の変更で回避しない。

## コミットと記録

import と状態共有、bulk memory・初期化、コピー API と検証に分ける。終了コミット例: `feat: extend wasm memory and data support`。

- 実行コマンド・結果: `dotnet build Wasm2Cs.slnx -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false --nologo`、`dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll`、`dotnet run --project samples/Smoke --no-build`、`node scripts/test-build.mjs`、`node scripts/test-package.mjs`、`node scripts/pack.mjs` が成功。SH-05 の共有 memory/global、初期化式、bulk data、範囲付きコピー、overlap copy、fill、data.drop、失敗 grow は `ExecutionChecks.SharedMemoryGlobalsAndBulkData` で検証した。
- memory プロファイル・状態比較ログ: SH-01 の固定プロファイルを参照。SH-05 固有の永続ログは未取得。
- 未解決事項: Unity Editor／IL2CPP の SH-05 専用実行記録が未取得。コミットは未作成。
- 終了コミット: 未完了
