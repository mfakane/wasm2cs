# 自己実行の構成と共通検証

## 実行境界

「外側」は通常の .NET または Unity IL2CPP、「ゲスト」は変換された .NET WASM ランタイム上の managed プログラムを指す。

```text
外側の .NET／Unity IL2CPP
├─ C# ホスト：ファイル・時刻・乱数・入出力・起動支援
└─ wasm2cs が生成した C# 版 .NET WASM ランタイム
   └─ Mono の IL インタープリターとゲスト用 BCL
      └─ Wasm2Cs.dll と小さなゲストドライバー
         └─ 起動後に受け取った WASM → C# テキスト／変換エラー
```

ランタイム WASM は実行前に C# へ変換・コンパイルする。ゲストの IL 解釈はこの C# 版 Mono の処理として実行する。外側で WASM インタープリター、JavaScript エンジン、実行時 C# コンパイラー、`DynamicInvoke` を使わない。これまでの「実行時インタープリター不要」は WASM 実行についての方針であり、ゲストの Mono による IL 解釈を禁止するものではない。

ホスト機能は ABI の実装に限定する。ゲストの変換・LINQ・例外処理などを外側の同等コードへ置き換えない。参照結果を作るプロセスと、生成ランタイムを評価するプロセスを分離する。

## 対象プロファイル

| 項目 | 採用する構成 |
|---|---|
| .NET ターゲット | `browser-wasm`、単一スレッド、managed IL を解釈実行 |
| 起点となる SDK | 調査時の `10.0.400`。workload と runtime pack の実際の組合せは SH-01 で固定 |
| managed AOT／Jiterpreter | 無効。動的な WASM の生成・コンパイルを必要としないことも確認 |
| SIMD／threads | 無効。ビルド設定だけでなく成果物で確認 |
| WASM 例外処理 | 有効。実物に含まれる例外命令の形式・バージョンを記録 |
| managed assembly | Webcil 無効、PE 形式の DLL。CoreLib、依存 DLL、ゲスト DLL を区別 |
| globalization／trimming | invariant globalization、初期段階は managed trimming 無効 |
| memory | memory32。初期プロファイルは最大256 MiBを明示し、実効値を確認。不足時は失敗と測定値を記録して上限変更を検討 |
| 生成コード | C# 9／.NET Standard 2.0 API を基準とし、Unity IL2CPP で検証 |
| Unity の最初の検証版 | 既存テストと同じ `6000.6.0f1`。Unity 6.0 系そのものの検証実績とは区別 |

.NET 10 の公式資料では `dotnet.native.wasm` が Mono ランタイム本体、JavaScript と managed assembly が別の成果物として説明されている。例外処理を有効にすると WASM 側で例外を扱えるため、JavaScript による代替処理を前提としない構成を採用する。[成果物と機能設定](https://github.com/dotnet/runtime/blob/v10.0.0/src/mono/wasm/features.md)

Jiterpreter は IL 解釈を補助する動的 WASM コンパイラーである。単に managed AOT を無効にするだけでは十分ではなく、trace と thunk の生成設定も確認する。[Jiterpreter の設定](https://github.com/dotnet/runtime/blob/v10.0.0/docs/design/mono/jiterpreter.md)

これらの資料は設計の根拠であり、SH-01 の成果物を検証した証拠ではない。設定名・有効値・起動 API・import 名は、採用した runtime pack と一致するソースおよび実物から確定する。通常の SDK ビルド設定・再リンクを用い、Mono のソースを改変する fork は初期手段にしない。

## 現在の実装との差分

基準点 `5d0de8d` は i32、直接呼び出し、単一の所有 memory、所有 global、active data、型付き関数 import に対応する。SH-09 着手時点では、memory/global の import、共有オブジェクト、passive data と bulk memory 命令、imported immutable global を使う初期化式に加え、table／element、参照型命令、構造的な署名比較による間接呼び出しまで実装済みである。関数署名は引数と結果の個数を持ち、検証スタックは主に高さを追跡する。生成処理は複数 source と型付き import `Bindings` を返す。

自己実行に必要な命令と ABI の全体は未調査である。SH-01 で、全関数の命令・即値・型、セクション、import/export、table、memory、data、起動順序、managed 依存関係を取得する。現在の限定された Decoder が最初の未対応命令で失敗する結果を、完全な一覧として扱わない。

必要機能ごとに「発見箇所」「担当 SH」「未対応／実装済み／実物検証済み」を記録する。全関数を対象にし、起動時に呼ばれない関数も除外しない。想定外の機能が見つかった場合、担当段階とテストを追加するまで、その段階の完了判定を保留する。

## コンポーネントと API 方針

- `src/Wasm2Cs` は汎用の decode、型検証、C# 生成を担当する。.NET 固有の import 名や起動順序を埋め込まない。
- 既存の `Transpiler.Translate(byte[], string)` と単一テキストを返す契約は維持する。複数ファイル出力は別 API にし、生成名と内容の組を安定した順で返す。
- 複数出力 API のオプションにはクラス名・namespace を持たせ、キャンセルを伝播する。CLI、Source Generator、ゲストドライバーは同じ変換処理を使用する。
- `dotnet.native.wasm` は入力名を維持したまま生成クラス名を `DotnetRuntime` として指定する。ASCII 識別子制限を緩めるだけでなく、export 名・予約名・正規化後の衝突も扱う。
- 大量 import は生成された型付きバインド用オブジェクトで受ける。元の module/name と署名を保持し、実行時の動的呼び出しへ逃がさない。既存 fixture のコンストラクター利用は維持する。
- 汎用 memory/table 支援と .NET 専用ホストを分離する。.NET 専用ホストは通常の .NET と Unity が共有する C# 実装とし、環境差は入出力などのアダプターに限定する。
- ゲストドライバーは DLL として `Wasm2Cs` を参照し、入力を渡して生成テキストを返す。変換ロジックを複製しない。

## 検証の区分

### 既存の回帰コマンド

以下は現在存在する。リポジトリルートで実行する。元の環境では `DOTNET_ROOT` と `DOTNET_ROOT_X64` が古い SDK を指していたため、`dotnet --info` で選択された SDK と一致する値をコマンド実行時に設定する。ユーザーのグローバル設定は変更しない。

```sh
dotnet build Wasm2Cs.slnx -m:1 -p:UseSharedCompilation=false --nologo
dotnet tests/Wasm2Cs.Tests/bin/Debug/net10.0/Wasm2Cs.Tests.dll
dotnet samples/Smoke/bin/Debug/net10.0/Smoke.dll
node scripts/test-build.mjs
```

コードを変更する各 SH でこの組を実行する。公開 API、Generator、ビルド・配布設定を変更した場合は既存の `node scripts/pack.mjs` と `node scripts/test-package.mjs` も実行する。既存 Unity 統合の確認には `scripts/test-unity.ps1` を使用する。

### SH-01 で追加したコマンド

SH-01 の3コマンドは実装済みである。`prepare` が作成した bundle 以外を入力にしない。WABT などの調査ツールや SDK がない場合は成功扱いにしない。

| 追加段階 | コマンド | 検証対象 |
|---|---|---|
| SH-01 | `node scripts/self-hosting.mjs prepare` | 専用環境での対象ビルドと成果物固定（実装済み） |
| SH-01 | `node scripts/self-hosting.mjs inventory` | 全体の必要機能・依存関係一覧（実装済み） |
| SH-01 | `node scripts/self-hosting.mjs reference` | 公式 Node.js 環境による参照実行（実装済み） |
| SH-08 | `node scripts/self-hosting.mjs generate` | ランタイム全体の C# 生成 |
| SH-08 | `node scripts/self-hosting.mjs compile` | 生成した全ソースの外側でのコンパイル |
| SH-09 | `node scripts/self-hosting.mjs host` | 小型 WASM による ABI 検証 |
| SH-10 | `node scripts/self-hosting.mjs hello` | 生成ランタイム内での managed 起動 |
| SH-11 | `node scripts/self-hosting.mjs managed` | BCL・GC・例外の検証 |
| SH-12 | `node scripts/self-hosting.mjs translate` | ゲスト変換と生成結果の検証 |
| SH-13 | `scripts/test-unity-self-hosting.ps1` | Editor／Windows x64 IL2CPP の自己実行 |
| SH-14 | `node scripts/self-hosting.mjs verify` | 準備済み成果物からの .NET 側の一括再検証 |

Node.js はドライバーとしてファイルやプロセスを操作するが、生成ランタイムを評価する子プロセスに WebAssembly 実行を代行しない。`reference` だけが公式の WASM 実行を担当する。

### 比較方法と証拠

数値命令は公式テストと参照エンジンで検証する。i64 は JSON の number を介して精度を失わない形式を使い、浮動小数点はビット列と NaN の許容条件を区別する。戻り値だけでなく、必要に応じて memory/global/table の状態と呼び出し順序を比較する。

自己実行では同じ wasm2cs バージョン・入力バイト列・生成設定を使う。パスや時刻を生成テキストへ混ぜず、通常実行との一致を確認する。ゲスト生成 C# のコンパイルは外側の別のビルド工程で行い、Unity Player 内では行わない。

ゲスト DLL の欠落、破損した assembly、未実装 import、変換対象の不正バイナリを負のテストにする。起動後に新しい入力を渡し、固定出力を返すだけの処理では通らないようにする。ホストだけの変換器をロードしない実行構成と、DLL 供給・ゲスト呼び出しの記録を保存する。

### 成果物と制限

生成ソース、runtime bundle、ログ、測定結果は `artifacts/self-hosting/` に置く。巨大な生成物はコミットせず、再生成手順、バージョン固定情報、SHA-256、必要機能一覧をバージョン管理する。参照資料や fixture を取り込む場合はライセンスも保存する。

タイムアウト、ホストのメモリ上限、生成サイズの制限は実行設定として記録する。テスト用プロセスの監視と、ゲストを安全に実行する sandbox は別である。現在の生成コードには任意プログラムを安全に実行できる保証はなく、制限到達は成功として扱わない。

## リスクと範囲外

| リスク | 対応する段階と判断 |
|---|---|
| プロファイルと実際の機能が一致しない | SH-01 で設定と実物を照合。不一致なら後続着手前に解決 |
| WASM／JS の ABI や起動 API がバージョンに依存する | SH-01 で固定、SH-09／10 で必要機能を明示的に実装 |
| 例外を無条件に C# の catch に置き換えて trap を飲み込む | SH-07 で例外の分類と境界テストを先に定義 |
| 巨大なメソッド・初期化・生成ソースがコンパイラーの制約に達する | SH-08 で実物と合成 fixture を使用。ファイル分割だけで解決したとみなさない |
| ゲストと外側の managed メモリが積み重なる | SH-11／12／13 で両方を測定。memory.grow の二重バッファも含める |
| Unity でのみ stripping・AOT・例外の不具合が出る | 小型の追加機能でも早期に確認し、SH-13 で全経路を検証 |
| 専用 SDK／workload／Unity 実行環境がない | 未実行として報告。未検証の成果物で完了しない |

今回の最終条件に含めないものは、WASI ターゲット、ブラウザー API 全体、任意の JavaScript interop、SIMD、threads、WASM GC 拡張、ゲスト内 Roslyn、ランタイム自身の再変換、公開レジストリへの配布である。ゲスト Mono の managed GC は範囲内であり、WASM GC 拡張とは区別する。

## 文書変更の確認

文書だけの変更では、相対リンクと既存コードへの参照、SH の番号・依存順、予定コマンドの追加段階、状態表示を確認し、`git diff --check` を実行する。コードの動作検証を再実行しなかった場合は、その旨を記録する。
