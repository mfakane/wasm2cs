# .NET WASM 上で wasm2cs を実行する

最終目標は、wasm2cs が C# に変換した .NET WASM ランタイム上に `Wasm2Cs.dll` をロードし、別の WASM を C# に変換することである。まず通常の .NET で成立させ、次に Windows x64 の Unity IL2CPP で同じ検証を通す。

このディレクトリは今後の実装手順であり、自己実行ができることを示す実績ではない。コードの基準点は `5d0de8d`。既存11段階の完了履歴は [IMPLEMENTATION.md](../IMPLEMENTATION.md) に残す。

## 読む順序と現在地

1. [共通設計](self-hosting-design.md) で対象構成、実行境界、検証規則を確認する。
2. 次の表を上から進める。現在着手できる段階は SH-01 である。
3. 各段階の合格条件を満たしてコミットし、個別文書とこの表の状態を更新する。

| ID | マイルストーン | 前提 | 状態 |
|---|---|---|---|
| [SH-01](milestones/SH-01-runtime-profile.md) | 対象成果物の固定と参照実行 | 既存11段階 | 完了 |
| [SH-02](milestones/SH-02-typed-ir.md) | 型付き内部表現と検証基盤 | SH-01 | 未着手 |
| [SH-03](milestones/SH-03-i64.md) | i64 | SH-02 | 未着手 |
| [SH-04](milestones/SH-04-floating-point.md) | 浮動小数点と数値変換 | SH-03 | 未着手 |
| [SH-05](milestones/SH-05-memory-data.md) | メモリ・global・data の拡張 | SH-04 | 未着手 |
| [SH-06](milestones/SH-06-tables.md) | table と間接呼び出し | SH-05 | 未着手 |
| [SH-07](milestones/SH-07-exceptions.md) | WASM 例外処理 | SH-06 | 未着手 |
| [SH-08](milestones/SH-08-large-codegen.md) | 巨大モジュールの C# 生成 | SH-07 | 未着手 |
| [SH-09](milestones/SH-09-host.md) | C# ホスト機能 | SH-08 | 未着手 |
| [SH-10](milestones/SH-10-mono-startup.md) | Mono 起動と Hello World | SH-09 | 未着手 |
| [SH-11](milestones/SH-11-managed-runtime.md) | managed 実行の安定化 | SH-10 | 未着手 |
| [SH-12](milestones/SH-12-self-hosting.md) | wasm2cs 自己実行 | SH-11 | 未着手 |
| [SH-13](milestones/SH-13-unity.md) | Unity IL2CPP 自己実行 | SH-12 | 未着手 |
| [SH-14](milestones/SH-14-reproducibility.md) | 再現性と継続検証 | SH-13 | 未着手 |

## 合格としないもの

- .NET ランタイムの一部だけを変換し、呼ばれない関数を削って通した結果。
- 外側の WebAssembly／JavaScript エンジンでゲストを実行した結果。これらは参照実行に限って使用する。
- ホスト側の `Transpiler` を呼び、ゲスト内の変換結果として返す処理。
- 未実装 import が成功値を返す仮実装。
- 実行できなかったテストを成功とした記録。

## 実装記録の更新

状態は「未着手」「実施中」「阻害要因あり」「完了」のいずれかとする。「完了」にはコマンド、結果、対象成果物のハッシュ、終了コミットが必要である。ローカル環境の不足とコードの失敗を区別する。

各段階の末尾にある記録欄を使う。テストと実装を小さなコミットに分けてよいが、次の段階には合格条件を満たしてから進む。終了コミットのハッシュは次の文書更新で追記し、コミット自身のハッシュを埋め込むためだけの amend は行わない。

SH-01 で取得する実物の必要機能一覧を、後続段階の作業範囲へ反映する。想定外の機能が残ったときは対応する文書・依存関係を更新する。対象構成の変更が必要なら理由を記録して確認を求め、バージョン変更や機能の除去で黙って回避しない。SH-01 の固定設定は [SH-01 プロファイル](self-hosting/SH-01-profile.json) に置き、生成物と実行ログは `artifacts/self-hosting/` に置く。
