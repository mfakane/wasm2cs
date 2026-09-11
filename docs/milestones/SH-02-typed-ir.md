# SH-02 型付き内部表現と検証基盤

状態: 完了。前提: [SH-01](SH-01-runtime-profile.md)。次: [SH-03](SH-03-i64.md)。

## 目的・変更対象

現在の i32 前提を取り除き、以後の命令追加を型付きで検証できるようにする。対象は `src/Wasm2Cs` の Reader、Decoder、Module、Validator、CSharpEmitter と、既存の実行・適合性テストである。

## 実施手順

1. 関数の引数・結果、ローカル、global、命令結果を値型として保持する。i32、i64、f32、f64 と、後続で必要になる参照型を区別する。表現できる型と実行できる命令は別に管理する。
2. 検証スタックを高さだけでなく値型の列にする。制御フレームには開始時の高さ、引数型、結果型、到達不能状態を保持する。型の異なる値を到達不能状態を理由に無条件に受け入れない。
3. block type の型インデックス、ブロック引数、複数結果を decode・検証・生成へ通す。分岐先が loop か block かで引数と結果の受け渡しを区別する。
4. C# の一時変数、合流時の値転送、関数署名を型付きにする。複数結果は静的な型で受け渡し、object 配列と実行時の型判定へ置き換えない。
5. `Transpiler.Translate`、既存の生成クラス・関数呼び出しと i32 fixture の動作を維持する。未実装の数値命令は従来どおり診断にする。
6. 適合性テストの入力形式を型付きの引数・結果・trap に拡張する。既存 `scripts/conformance/prepare.mjs` は固定 i32 ファイル向けのため、他の WAST を扱えたと仮定しない。新しい command 形式を明示的に処理し、未知の形式は失敗させる。

## 検証

[共通回帰とパッケージ検証](../self-hosting-design.md) を実行する。新規テストは既存 console test 実行へ登録し、追加しただけで未実行にならないようにする。

- 同じ高さだが異なる型のスタック、引数・戻り値の型不一致。
- 到達不能コード、分岐後の合流、loop 引数、複数結果、`br_table` の署名不一致。
- 複数結果を持つ i32 fixture の参照実行と生成 C# の比較。
- 原始型のビット表現を保持するテストデータの往復と、未知の WAST command の拒否。

## 合格条件

- [x] 既存 i32 の回帰が通り、公開呼び出しを壊していない。
- [x] 同じ高さの型不一致を検出できる。
- [x] ブロック引数と複数結果が decode から生成コードまで通る。
- [x] 後続の数値型を扱える参照テスト形式があり、未対応命令は明示的に拒否される。

## 非対象・失敗時の扱い

i64／浮動小数点の全演算、table、WASM 例外の実行は後続で行う。型の定義を追加しただけで対応済みと記録しない。静的な型検証と参照テストの両方が通るまで段階を完了しない。

## 実装結果

関数署名、ローカル、global の宣言型と初期化式の型、命令がスタックへ積む値の型を保持するようにした。値型は i32、i64、f32、f64、funcref、externref を区別する。数値定数・演算と global の初期化は引き続き i32 に限定し、未実装の数値命令・参照命令は到達不能コードでも診断する。

検証スタックは値型と多相的なスタック底を区別する。制御フレームには開始時の高さ、引数・結果の型、到達不能状態を保持し、loop への分岐では引数、それ以外では結果を確認する。`br_table` は各値を各分岐先の型と照合する。スタック底だけなら同じ個数の異なる型にも適合するが、具体的な値が積まれていれば到達不能状態でも型不一致を検出する。[検証アルゴリズム](https://webassembly.github.io/spec/core/appendix/algorithm.html)

生成する関数・import delegate の署名と一時変数を型付きにし、複数結果には C# タプルを使用する。loop の引数転送では転送元を一時変数へ保存してから代入する。型インデックスの s33、ブロック引数、複数結果、else を省略した if の引数保持を decode・検証・生成へ通した。`Transpiler.Translate` と既存の i32 呼び出し形式は維持した。

適合性テストの JSON は `SchemaVersion: 2` とし、引数・複数結果は型と固定桁の16進ビット列、trap は種類を保存する。浮動小数点の正確なビット列と canonical/arithmetic NaN の期待条件を区別する。固定 i32 用 parser に加え、WABT の command JSON を明示的に処理する `prepare-typed.mjs` を追加した。対応する command と制限、再生成手順は [fixture の説明](../../tests/Conformance/README.md) に記録した。

SH-01 の保存済み inventory では、対象 runtime の176個の関数型に i32、i64、f32、f64 があり、制御命令では空の型と各数値型の単一結果を確認した。型インデックスと複数結果の検証には、この段階の専用 fixture を使用した。runtime 全体の変換・実行は後続段階で検証する。

## コミットと記録

内部表現・検証、生成処理・テスト基盤の順に分ける。終了コミット例: `refactor: introduce typed wasm IR and validation`。

- 実行コマンド・結果: `dotnet build Wasm2Cs.slnx -m:1 -p:UseSharedCompilation=false --nologo` 成功（SDK 10.0.400、警告・エラー0件）。`dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll`、`dotnet samples/Smoke/bin/Debug/net10.0/Smoke.dll`、`node scripts/test-build.mjs`、`node scripts/pack.mjs`、`node scripts/test-package.mjs` はすべて成功。
- 追加検証: `node scripts/conformance/prepare-typed.mjs tests/Conformance/typed-ir.wast tests/Conformance/typed-ir.json` 成功。32件の結果と2件の trap を Node と生成 C# で照合し、23件の不正モジュールを両方の検証器で拒否。console test には s33 の境界、型付き import・参照型、ビット列の往復、未知の command の拒否も登録した。生成 C# 9 は .NET Standard 2.0 参照アセンブリでコンパイルし、NuGet consumer から複数結果を直接呼び出して確認した。Unity Editor／IL2CPP はこの段階では未実行。
- 既存回帰: 公式 i32 の350件の結果、9件の trap、54件の不正モジュール、29件の明示的 skip を維持した。旧 JSON と全 command を照合し、バイナリ・引数・期待値を変更していないことを確認した。既存の固定 seed を使う Node 差分テストも成功。
- fixture・参照エンジンの固定情報: `typed-ir.wast` と `typed-ir.json`。WABT `1.0.41`、Node.js `v22.17.0`、V8 `12.4.254.21-node.26`。WAST の SHA-256 は `7f78ba0f82be129dface841b10547f3a7660207c60112ebebfc6bd8d734e651d`、主モジュールの WASM は `2416ca4067a45c4d08e22a04359d5a0bcc6164b2f44c246c1488dbb432863e20`、JSON は `fb9e976c12ab125f676881ad48aa6f19a138a84e6aa486f0f94363524da9ecae`。
- 未解決事項: SH-02 の合格条件についてはなし。i64／浮動小数点の定数・演算・global 初期化、参照命令と table は後続段階。WABT 1.0.41 の複数結果を持つ `assert_trap` の JSON 出力不具合は、fixture の void wrapper と parser の厳格な拒否で扱った。
- 終了コミット: `52614b7`（型付き IR・検証・署名変更に伴う生成処理）、`b4ac1d7`（型付き生成テスト・適合性テスト基盤・NuGet consumer）。
