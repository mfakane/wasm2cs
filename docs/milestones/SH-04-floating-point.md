# SH-04 浮動小数点と数値変換

状態: 完了。前提: [SH-03](SH-03-i64.md)。次: [SH-05](SH-05-memory-data.md)。

## 目的・変更対象

f32／f64 を型付き演算として扱い、.NET の数値動作と WASM の差を補う。対象は Reader、数値演算の生成、memory/global、型付き関数境界と適合性テストである。

## 実施手順

1. 定数・load/store をリトルエンディアンのビット列として扱う。C# のリテラル丸めや現在の culture によって入力を変えない。
2. 四則演算、比較、min/max、abs/neg/copysign、丸め、sqrt と必要な変換命令を追加する。NaN と符号付きゼロの処理は WASM の規則を確認して実装する。
3. i32／i64 への変換では NaN・無限大・範囲外を先に判定し、通常の C# cast の挙動に依存しない。対象 runtime が使う non-trapping conversion も別命令として扱う。
4. promote/demote と reinterpret を区別する。f32 の演算結果が意図せず f64 の中間値のまま保持されないようにする。
5. 関数の引数・結果、import/export、local、global、memory を通す。使用 API は .NET Standard 2.0 の参照 assembly で確認する。
6. 公式の浮動小数点・変換テストと固定 seed のビット列テストを追加する。厳密比較するビット列と、演算で許容される NaN の集合を分ける。

## 検証

[共通回帰とパッケージ検証](../self-hosting-design.md) に加え、小型 fixture を既存 Unity smoke に追加して Editor／IL2CPP で確認する。

- ±0、±∞、subnormal、NaN、整数へ変換可能な範囲の直前・直後。
- 負数を含む nearest の ties-to-even、min/max のゼロと NaN。
- reinterpret のビット保存と、変換命令の trap／飽和の区別。
- JSON に NaN や Infinity を number として渡さない参照プロトコル。

## 合格条件

- [x] MVP の f32／f64 演算・変換と、必要な追加変換の参照テストが通る。
- [x] 許容される NaN の違いを不正な差とせず、保存すべきビットは一致させる。
- [x] .NET Standard 2.0 参照コンパイルと小型の Unity IL2CPP 実行が通る。

## 非対象・失敗時の扱い

SIMD と高速化のための近似演算は追加しない。プラットフォーム差が見つかった場合は再現 fixture を残し、許容誤差を広げるだけで処理しない。

## コミットと記録

浮動小数点演算、数値変換、プラットフォーム検証に分ける。終了コミット例: `feat: support wasm floating-point semantics`。

- 実行コマンド・結果: 共通6コマンド（solution build、console tests、Smoke、MSBuild 回帰、pack、NuGet consumer）はすべて成功。build は警告・エラー0件。C# 9・checked・.NET Standard 2.0 参照コンパイルで公式8スイートと固定 seed 差分テストが成功し、既存 i32/i64/型付き IR の回帰も通った。[機械可読の検証記録](../self-hosting/SH-04-validation.json) にコマンドとログをまとめた。
- 参照エンジン・Unity 版・ログ: Node.js `v22.17.0`、V8 `12.4.254.21-node.26`、WABT `1.0.41`。公式 commit は `fffc6e12fa454e475455a7b58d3b5dc343980c10`。Unity `6000.6.0f1 (f7f8ed4d1e24)` の Editor と Windows x64 IL2CPP Player がともに終了コード0、`WASM2CS_FLOATING_PASS` と `WASM2CS_SMOKE_PASS` を出力した。UPM archive を `scripts/test-unity.ps1 -PackagePath` で導入し、移動・更新・削除の bridge 回帰も成功。ログは `artifacts/self-hosting/sh04/unity/`、Windows の検証環境は `C:\Users\fumika\AppData\Local\Temp\wasm2cs-sh04-final`。
- 成果物 SHA-256: `Floating.wasm` は `af635fa1ddf62279a9ac3797998a373c49517fa41fcb246438fbb3ec50ef761b`、検証した UPM archive は `a0c242d2eb28f900f01826e3859f25347c94500848109bad677ac12fbffd4753`。公式 JSON、generator DLL、NuGet、Unity Player のハッシュは検証記録を参照。
- 未解決事項: SH-04 の合格条件についてはなし。native float/double の signaling NaN はホスト実装の影響を受けるため、完全なビット保存には追加のビット列 API を使う。全 runtime の実行、SIMD、memory64 はこの検証範囲に含めない。
- 実装コミット: `eb6b362`（定数・memory・浮動小数点演算）、`1479630`（数値変換・飽和変換）、`f715882`（Mono のビット保存・丸めへの対策）。検証・記録の終了コミットは次の文書更新で追記する。

## 実装内容とプラットフォーム差

MVP の演算・比較40命令、浮動小数点を含む変換22命令、定数2命令、load/store 4命令と、飽和変換8命令の計76命令を実装した。[命令対応表](../self-hosting/SH-04-floating-coverage.json) に署名、実装・テスト、SH-01 inventory の出現箇所を記録している。固定 bundle 内では、そのうち64命令が5,774箇所に出現した。飽和変換8命令は同 inventory には出現しなかったが、公式 conversions suite の対象としてすべて検証する。

定数と memory はリトルエンディアンのビット列で読み書きする。生成コードの f32/f64 は明示的レイアウトの値型で保持し、abs/neg/copysign/reinterpret、local/global、関数呼び出しでは浮動小数点への変換を挟まない。演算時に数値を取り出し、f32 の結果は32 bitに戻してから保持する。min/max の符号付きゼロ、nearest の ties-to-even、NaN の許容集合は [WebAssembly の数値規則](https://webassembly.github.io/spec/core/exec/numerics.html) に従う。

整数への変換は、NaN 判定と切り捨て後の範囲判定を cast より前に行う。NaN は `InvalidConversionToInteger`、範囲外と無限大は `IntegerOverflow` とし、飽和変換では NaN を0、範囲外を上下限に変える。整数から浮動小数点への変換は、整数のビット列から仮数・指数と丸めを計算し、中間の double への変換を使わない。

Unity Mono では、native float を通すだけで signaling NaN が quiet NaN になる差と、u64 `0x8000008000000001` を f32 に変換すると `0x5f000000` になる二重丸めを確認した。正しい結果は `0x5f000001` である。前者は [native 境界の再現コード](../../tests/Platform/MonoFloatBoundary.cs)、後者は [共有 smoke](../../samples/Floating/FloatingChecks.cs) に残した。最初の Editor の失敗ログは `artifacts/self-hosting/sh04/editor-before-fix.log` に保存した。

通常の float/double API に加え、浮動小数点を含む export には `__wasm_bits_<export名>` を生成する。f32 のビット列は int、f64 は long で渡す。浮動小数点 import があるモジュールでは `__wasm_FromBits` と `__wasm_BitsImportN` を使って同じ形式の callback を設定できる。global にもビット列のプロパティを生成する。native CLR 値を経由すると signaling NaN が変わり得るため、payload の完全な保存が必要なホスト境界にはこの API を使う。WASM 内のビット保存と smoke の期待値は緩和していない。

参照実行でも JavaScript Number を経由させず、WASM の wrapper 内で reinterpret して整数ビット列を受け渡す。公式8スイートは11,145件の結果、67件の trap、65件の不正バイナリを含む。82件の WAT テキスト構文テストは明示的な skip であり、数値・バイナリのケースは skip していない。固定 seed `0xbb67ae8584caa73b` では70命令に対して267,254件、memory/global/local/複数結果に対して2,649件を通常 API とビット列 API の両方で照合する。[出典・再生成方法](../../tests/Conformance/README.md) に固定 commit と各 source hash を示した。
