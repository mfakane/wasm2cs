# SH-09 C# ホスト機能

状態: 未着手。前提: [SH-08](SH-08-large-codegen.md)。次: [SH-10](SH-10-mono-startup.md)。

## 目的・変更対象

runtime WASM が要求するホスト ABI を C# で実装する。対象は新設する .NET runtime 専用のホストライブラリ、型付きバインド、小型 ABI fixture と自己実行ドライバーである。汎用 Decoder／Emitter に .NET 固有の import 名を埋め込まない。

## 実施手順

1. 固定 bundle の全 import を、実装する機能と到達時に明示的に拒否する範囲外機能に分類する。各項目に module/name、署名、元の JavaScript／native 実装への参照、C# の担当処理、テストを記録する。
2. memory と table の参照、間接呼び出し、grow 後の参照更新を接続する。start や初期化中の callback に必要な状態は、その呼び出しより前に結び付ける。
3. 必要な標準出力、仮想ファイル、時刻、乱数、終了コード、環境・引数の取得を実装する。errno／エラー戻り値・文字列 encoding・ポインター幅を ABI と照合する。
4. .NET／Emscripten 固有の初期化支援と、必要なタイマー・callback queue を実装する。同期処理を非同期処理の成功として代用しない。Mono の managed 実行そのものは代行しない。
5. 仮想ファイルには bundle と入力を明示的に供給する。外側のファイルシステム全体を公開しない。必要な出力領域は分離し、パスの正規化と範囲外アクセスを検査する。
6. 小さな WASM fixture から各 ABI を呼び、同じ入力に対する公式環境の戻り値・メモリ・副作用と比較する。fixture が使わない import は未検証と記録する。

## 検証

この段階で追加する予定コマンド。

```sh
node scripts/self-hosting.mjs host
```

[共通回帰](../self-hosting-design.md) に加え、短い read/write、EOF、存在しないファイル、不正な範囲・ポインター、乱数バッファ、時刻の単位、callback 順序、二重終了を検証する。時刻と乱数はテスト時に制御可能にするが、通常の実装を固定値で代用しない。

## 合格条件

- [ ] 全 import に、型の一致する実装または明示的な拒否処理が対応づいている。
- [ ] 起動と対象シナリオに必要な機能には小型 ABI テストがある。
- [ ] 未実装処理へ到達すると import 名・呼び出し状況を示して失敗する。
- [ ] 外側に WASM／JavaScript エンジンを必要としない。
- [ ] ホストの実装と環境依存アダプターが分かれ、Unity と共有できる。

## 非対象・失敗時の扱い

DOM、HTTP、任意の JavaScript interop、WASI ホスト全体は作らない。ただし browser-wasm の成果物が要求する個々のシステム呼び出しは、名前空間だけを理由に除外しない。未使用経路の明示的な拒否は許容するが、必要な呼び出しを成功値だけ返す処理で通さない。

## コミットと記録

import 対応表とバインド、基本入出力、runtime 固有支援と ABI テストに分ける。終了コミット例: `feat: implement dotnet wasm host services`。

- 実行コマンド・結果: 未実施
- bundle ハッシュ・ABI 対応表・比較ログ: 未取得
- 未解決事項: 必要な JavaScript 側支援処理の実物調査待ち
- 終了コミット: 未完了
