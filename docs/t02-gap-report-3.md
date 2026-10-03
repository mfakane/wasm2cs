# T02 第3回: Rust std の集計モジュール

2026-10-03 に、`master` の基準コミット `d2f33ab` と今回追加した入力・測定スクリプトで確認した。対象は `samples/RustStd/` の2モジュールである。T02 第1回・第2回の既存入力を再測定した記録は [T08 第2回](t08-remeasure-2.md) にあり、今回の測定には含めていない。

## 入力と用途

従来の Rust 入力は、1.85.0 の `no_std` ライブラリであった。今回は、標準ライブラリで整数の読み取り・ソート・集計・文字列出力を行うライブラリを、Rust 1.85.0 と 1.90.0 でそれぞれコンパイルした。`Vec`、`str::parse::<i64>`、`sort_unstable`、`BTreeMap`、`String`、標準アロケータを使う。ホストが入力メモリへ数値列を書き込み、export を呼んで集計結果を読む構成で、ホスト I/O は必要としない。

これは、このプロジェクトで作成した用途別の入力である。第三者のアプリケーションやライブラリをそのまま検証した結果ではない。ソース、Cargo 設定、バイナリ、コンパイラのバージョン、フラグ、ハッシュ、再生成手順は [入力の README](../samples/RustStd/README.md) に記録した。ターゲット機能は各コンパイラの既定値を使い、生成後に命令や関数を除去していない。

## 比較方法

環境は .NET SDK 10.0.400、Node.js 22.17.0、WABT 1.0.41。翻訳 profile は `portable-netstandard2.0` で、生成した全ソースを C# 9 と netstandard2.0 の runtime ABI でコンパイルし、.NET 10 上で実行した。

```sh
node scripts/measure-rust-std.mjs --inventory
```

各バイナリについて、同じインスタンスで18回の `analyze` 呼び出しを実行した。正常入力、空入力、不正 UTF-8、不正整数、i64 範囲外、i64 加算の wrap、各種 ASCII 空白、整列済み・逆順・重複・混合値、出力容量超過、入力容量ちょうど、入力長超過、エラー後の再実行を含む。最大2万件の整数を入力した。

正常結果は、入力数値列から JavaScript の BigInt と Map で別途計算した sum・unique・median・histogram と Node の WASM 実行を照合した。その上で、生成 C# の戻り値、各集計 export、出力バイト列、入力バイト列のハッシュ、メモリページ数、毎回の線形メモリ全体の SHA-256 を Node と照合した。メモリ拡張後は Node の `memory.buffer` を取得し直す。Rust の配列境界チェックによる `unreachable` と、新規インスタンスの初期メモリも比較した。

命令数は、CLI の最初のエラーから推定せず、`wasm-objdump -d` で全関数を走査する既存の `scripts/wasm-opcodes.mjs` で数えた。ローカル変数宣言は数に含めない。ログ、生成 C#、consumer、Node/C# の全ケース結果は `artifacts/t02-rust-std/` に保存する。比較スクリプトは regression workflow にも追加した。CI はチェックインしたバイナリを使い、再生成や WABT による inventory は行わない。

## 結果

| 入力 | バイト数 | 関数数 | 全命令数 | 命令の種類数 | 翻訳・C# コンパイル・実行 |
|---|---:|---:|---:|---:|---|
| `RustStd185.wasm` | 29997 | 61 | 13087 | 72 | 成功、18ケースと全メモリが Node と一致 |
| `RustStd190.wasm` | 27413 | 55 | 11850 | 74 | 成功、18ケースと全メモリが Node と一致 |

両入力とも import と start セクションはない。セクションは type、function、table、memory、global、export、element、code、data。メモリは21ページで始まり、標準アロケータの実行で最大41ページまで拡張した。容量超過などのエラー時は指定した負のステータスと初期化済みの結果メタデータが返り、その後の正常呼び出しも一致した。境界外アクセスは両方の実行で `Unreachable` となり、新規インスタンスは元の初期メモリと一致した。

再生成は別の空の Cargo target ディレクトリでも行い、両バイナリの SHA-256 が README の値と一致することを確認した。

`dotnet build Wasm2Cs.slnx -m:1 -p:UseSharedCompilation=false --nologo` は警告・エラーなしで成功し、既存の `Wasm2Cs.Tests` 実行も成功した。比較スクリプトは inventory 付きと CI と同じ inventory なしの両方で実行し、一致を確認した。GitHub Actions 上の実行結果は今回のローカル記録には含めない。

## 次のタスクと測定範囲

この2入力には Core の読取り・型検証・生成 C#・実行結果の不足が見つからなかった。T03 の次の一群は選ばない。Rust std を含む約1.2万〜1.3万命令のこの構成は、従来の約7千命令までの入力より大きい構成として確認できた。

SIMD の19命令、WASI の追加 import、start 中の所有メモリをホストから参照する問題、配布用 WASI ホストについては、新しい根拠は得ていない。T04/T05 と T07 の追加反復は条件付きのままとする。

未測定は、他の Rust std API、Rust 1.85.0/1.90.0 以外のバージョン、C++、第三者製モジュール、Unity Editor、IL2CPP。この入力の成功を任意の Rust 出力への対応とはみなさない。次の T01/T02 は、利用予定の実モジュールか Unity で使う入力を固定して比較する。
