# SH-08 巨大モジュールの C# 生成

状態: 進行中。前提: [SH-07](SH-07-exceptions.md)。次: [SH-09](SH-09-host.md)。

## 目的・変更対象

対象 runtime 全体を変換してコンパイルする。対象は CSharpEmitter、Transpiler API、CLI、Source Generator、MSBuild／Unity 入力設定と生成コードのテストである。ここでは runtime の起動成功を条件にしない。

## 実施手順

1. 必要機能一覧を照合し、未対応の命令・型・セクションをゼロにする。追加が必要なら該当段階へテストを追加し、全関数を decode・検証する。
2. 生成名とソース本文の組を安定した順で返す複数ソース API を追加する。生成の全工程へ cancellation token を伝える。既存 `Translate(byte[], string)` の単一テキスト契約は維持する。
3. `dotnet.native.wasm` に対して `DotnetRuntime` を指定できるようにし、元の export 名と C# 識別子の対応を保持する。予約名・正規化後の衝突は決定的に解決または診断する。
4. 関数群、初期化、data の出力を分割する。大きな一つの関数・大量のローカル・文字列や metadata の制限はファイル分割だけでは解決しないため、合成 fixture と実物で検査し、必要な lowering を追加する。
5. 大量 import の生成バインド用オブジェクトを追加する。元の module/name と署名を保持した型付きメンバーにし、実行時の動的呼び出しへ変えない。
6. CLI、Generator、Unity 用入力ブリッジから同じオプション・複数ソースを扱う。入力変更で出力数が減った場合に、古い出力をコンパイルへ残さない。
7. runtime 全体を生成・コンパイルするドライバーを追加し、生成サイズ、生成／コンパイル時間、ピークメモリ、ツール版を記録する。

## 検証

この段階で追加する予定コマンド。

```sh
node scripts/self-hosting.mjs generate
node scripts/self-hosting.mjs compile
```

[共通回帰とパッケージ検証](../self-hosting-design.md) に加え、複数出力、同名の衝突、出力削減、途中キャンセル、初期化の巨大化、単一巨大関数、大量ローカルを検証する。新しい生成形式は .NET Standard 2.0 の参照 assembly と小型 Unity smoke でも確認する。

## 合格条件

- [ ] 対象 runtime の全関数を変換し、すべての出力を含めて外側でコンパイルできる。
- [ ] 出力が再現可能で、未対応部分の削除・解釈実行への切替がない。
- [ ] CLI と Source Generator の設定が一致し、既存の利用方法も通る。
- [ ] キャンセルと制限到達を成功した生成として扱わない。
- [ ] 本段階の成功は「全体の変換・コンパイル」であり、Mono 起動ではないと記録している。

## 非対象・失敗時の扱い

実行されない関数の省略、runtime の再ビルドによる必要関数の削除、ランタイム専用の変換結果の手修正は行わない。コンパイラーの制約に達した場合は入力と制約を記録し、生成側で解決する。

## コミットと記録

API と分割生成、各入力経路の統合、実物のコンパイル検証に分ける。終了コミット例: `feat: generate and compile large wasm modules`。

- 実行コマンド・結果: `dotnet build Wasm2Cs.slnx --nologo` と `dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll` が成功。`TranslateSources` の deterministic な scaffold／関数 source 分割、partial class の同時コンパイル、Source Generator の複数出力、入力変更時の再分割、事前キャンセルを検証。
- bundle ハッシュ・生成サイズ・時間・メモリ: 未取得
- 未解決事項: CLI の出力ディレクトリ、`DotnetRuntime` class-name override、初期化／data／import の分割、self-hosting `generate`／`compile`、実物全体の変換・コンパイル。
- 終了コミット: 未完了
