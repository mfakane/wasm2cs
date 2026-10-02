# WebAssembly 対応範囲の拡充タスク

## 目的と進め方

小規模な C/Rust モジュールを、命令を手で削らずに .NET プロジェクトへ取り込める範囲を増やす。仕様全体を順に実装するのではなく、固定した入力で不足を測定してから対象を決める。現状の対応範囲は [supported-features.md](supported-features.md) に記載されている。

以下の ID は作業単位であり、上から一括で実装する指示ではない。各タスクは受入条件を満たした時点で閉じる。未対応入力を削ったり、呼ばれない関数を除外したりして成功としない。SIMD と WASI の順序は T02 の結果で決める。threads、shared memory、memory64、GC、Component Model、WASI Preview2、実行サンドボックスは対象外とする。

## T01: 再現可能な入力を固定する

- **前提:** なし。
- **作業:** `samples/` と `tests/Conformance/` に既にある入力を確認し、不足する場合に限り Clang freestanding、Rust `wasm32-unknown-unknown`、WASI Preview1 の小型プログラムをそれぞれ一つ追加する。整数・メモリ・標準出力など、用途を区別できる例にする。ソース、生成した `.wasm`、コンパイラのバージョン・ターゲット・フラグ・再生成コマンドを記録する。利用できないツールチェーンやターゲットがあれば、その入力は未作成として記録し、別の生成物で代用しない。
- **成果物:** チェックインした入力と再生成手順（既存 fixture で足りる場合は参照一覧のみ）。
  - Clang freestanding: `samples/CAlgorithms/`（既存）。
  - Rust `wasm32-unknown-unknown`: `samples/RustWasm32UnknownUnknown/`。
  - WASI Preview1: `samples/WasiPreview1/`（`HostAbi.wat` は WASI とみなさない）。
- **受入条件:** 各入力の出所・バイナリ・再生成手順が対応し、既存の `dotnet run --project tests/Wasm2Cs.Tests` が成功する。

## T02: 未対応項目と基準結果を記録する

- **前提:** T01。
- **作業:** 各入力を `dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>` で変換し、成功した入力は `samples/Smoke/` を参考に一時 consumer でコンパイル・実行する。失敗をセクション／命令・即値／型検証／import／生成 C# のコンパイル／実行結果に分類する。最初のエラーだけで全不足を推定せず、必要なら小型モジュールに分けて確かめる。通過する入力も Node.js 等の参照 WebAssembly 実行と、戻り値・メモリ・trap を比較する。WASI 入力は、適切な Preview1 参照ランタイムがない場合、実行結果を未比較と記録する。
- **成果物:** 入力ごとの結果、再現コマンド、確認できた不足項目、Core/SIMD/WASI の分類と優先順位を記した一覧。
  - 2026-10-03 の測定: [t02-gap-report.md](t02-gap-report.md)。次の Core 一群はなし（T03 は実施しない）。
- **受入条件:** 次に扱う Core 機能を一つ選べる。不足の頻度は確認した入力の範囲でのみ集計し、初回エラーを全出現回数として数えない。

## T03: Core 機能を一群実装する

- **前提:** T02。対象とする命令・セクションの一群を一覧で指定する。候補がなければこのタスクは実施しない。
- **作業:** `src/Wasm2Cs/Decoder.cs` の読取り、`Validator.cs` の型検証、必要な `CanonicalIr.cs`・`*Operations.cs`・`LoweringBackend.cs`・`CSharpEmitter.cs` の生成処理を変更する。既存の表現で足りる箇所は変更しない。未 export 関数も検証する。`tests/Wasm2Cs.Tests/` に正常値、境界値、trap または不正な入力のうち対象に該当するケースを追加し、可能なら `tests/Conformance/` の既存 adapter と公式 fixture を使う。
- **成果物:** 選んだ一群の実装・テストと `docs/supported-features.md` の更新。
- **受入条件:** T02 で失敗していた対象入力が参照実行と一致し、`dotnet run --project tests/Wasm2Cs.Tests`、`node scripts/test-build.mjs`、`node scripts/pack.mjs`、`node scripts/test-package.mjs` が成功する。残る不足は一覧に残す。次の一群が必要なら新しい T03 として切り出す。

## T04: SIMD の次の一群を選ぶ（条件付き）

- **前提:** T02。固定入力に SIMD 不足があり、Core と比較して追加する価値がある場合のみ実施する。
- **作業:** 不足する SIMD 命令を一群に絞り、使用する即値・lane・メモリアクセス・数値意味論と対象 profile を記録する。現状の `v128.const`、`f32x4.add`、`f32x4.mul` は `dotnet-vector` と `unity-mathematics` のみ対応する。portable profile への追加は既定の要件にしない。
- **成果物:** 対象 opcode と成功・拒否すべき具体的な WASM 入力、profile ごとの期待動作。
- **受入条件:** T05 のテスト入力と対象 profile が確定する。対象入力がなければ T05 は行わない。

## T05: 選んだ SIMD 命令を実装する（条件付き）

- **前提:** T04。
- **作業:** T03 と同じ decode → validate → lower → emit の流れで一群を追加する。`tests/Wasm2Cs.Tests/ExecutionChecks.cs` の vector/profile テストを拡張し、参照 WASM との lane 値・trap・メモリ境界を比較する。非対応 profile は誤ってコンパイル可能にしない。
- **成果物:** 対象 profile の実装・テストと対応範囲の更新。
- **受入条件:** T03 の .NET/MSBuild/パッケージ検証コマンドが成功する。Unity 向けに対応を宣言する場合は Editor と Windows x64 IL2CPP でも確認する。実機が使えない場合、Unity 対応は未検証として残し、完了を主張しない。

## T06: WASI import とメモリの接続を実証する（条件付き）

- **前提:** T02。WASI Preview1 入力を対象にすると決めた場合のみ実施する。
- **作業:** `tests/Wasm2Cs.Tests/ExecutionChecks.cs` の import/start と共有メモリの例を基に、imported memory と自モジュール所有 memory の両方について、import 関数がメモリにアクセスできるか、特に `start` 中にアクセスできるかを小型モジュールで確認する。既存の `src/Wasm2Cs.DotnetHost/HostEnvironment.cs` には iovec・出力・exit の部品があるが、WASI ABI の結線が完成しているとはみなさない。必要な API 変更をここで決める。
- **成果物:** 実行できる再現テストと、必要なら最小限の import／メモリ接続変更。
  - 2026-10-03 の結果: [t06-start-memory.md](t06-start-memory.md)。imported memory は start 中にホストから読める。owned memory は start 自体はデータを読むが、コンストラクタが返るまでホストへ公開されない。API は変えていない。T07 は未着手。
- **受入条件:** `start` 中を含めホストが正しいメモリを参照できることをテストで確認する。成立しなければ T07 の前に阻害要因として記録する。

## T07: WASI Preview1 の必要な import を一つ接続する（条件付き・反復）

- **前提:** T06。T02 の入力が要求する関数を一つ指定する。`fd_write` と `proc_exit` は候補であって自動的な実装対象ではない。
- **作業:** import の module/name と署名を照合し、`Wasm2Cs.DotnetHost` の既存部品を再利用して Preview1 ABI のポインタ・長さ・エラー番号・終了処理を実装する。`HostChecks.cs` の単体テストに加え、実モジュールから呼ぶテストを追加する。標準出力やファイルアクセスは明示設定とし、未対応 import を成功値の仮実装にしない。
- **成果物:** 一つの import の結線・正常系／境界エラーテストと利用方法の文書。
- **受入条件:** 固定 WASI 入力の該当 import が参照ランタイムと同じ結果になり、`dotnet run --project tests/Wasm2Cs.Tests` が成功する。別の import が必要なら T07 を別タスクとして反復する。WASI 対応を配布する段階では、現在 NuGet に含まれない `Wasm2Cs.DotnetHost` の提供方法を決め、`node scripts/pack.mjs` と `node scripts/test-package.mjs` でも消費者側の接続を検証する。

## T08: 実用範囲を再測定して公開する

- **前提:** 実装した T03/T05/T07 の各タスク。未着手の条件付きタスクを待つ必要はない。
- **作業:** T02 と同じ入力・比較方法で通過／失敗を再測定し、`docs/supported-features.md` と `docs/usage.md` を実測した範囲に合わせる。新しい機能の依存 profile、必要なホスト設定、未対応機能を明示する。
- **受入条件:** 変更前後の結果と未対応入力が一覧に残る。変更した配布経路のテストが成功し、Unity/IL2CPP など環境不足で実行できない項目は未検証と記録する。

WASI を実装しても、実行時間・メモリ・再帰深度に制限を設けるサンドボックスにはならない。新しい依存や profile は、既存の部品と標準 API では必要な意味論を実現できない場合に限って追加する。
