# SH-04 浮動小数点と数値変換

状態: 未着手。前提: [SH-03](SH-03-i64.md)。次: [SH-05](SH-05-memory-data.md)。

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

- [ ] MVP の f32／f64 演算・変換と、必要な追加変換の参照テストが通る。
- [ ] 許容される NaN の違いを不正な差とせず、保存すべきビットは一致させる。
- [ ] .NET Standard 2.0 参照コンパイルと小型の Unity IL2CPP 実行が通る。

## 非対象・失敗時の扱い

SIMD と高速化のための近似演算は追加しない。プラットフォーム差が見つかった場合は再現 fixture を残し、許容誤差を広げるだけで処理しない。

## コミットと記録

浮動小数点演算、数値変換、プラットフォーム検証に分ける。終了コミット例: `feat: support wasm floating-point semantics`。

- 実行コマンド・結果: 未実施
- 参照エンジン・Unity 版・ログ: 未取得
- 未解決事項: SH-01 の追加変換命令の一覧待ち
- 終了コミット: 未完了
