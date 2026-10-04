# T02 第4回: Unity Editor と Windows x64 IL2CPP

2026-10-04 に、Rust `std` 集計 fixture の `RustStd190.wasm` を、梱包済み UPM パッケージ経由で Unity 上で実行した。第3回の .NET 比較に続き、Unity Editor と Windows x64 IL2CPP の両 backend を Node.js の WebAssembly 実行と比較した。

## 再現

リポジトリルートで Node.js 22 と Unity 6000.6.0f1、Windows Build Support (IL2CPP) が利用できる環境で実行する。

```sh
node scripts/measure-rust-std.mjs --unity-oracle
node scripts/pack.mjs
pwsh scripts/test-unity-rust-std.ps1 -PackagePath ./artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz
```

PowerShell harness は一時 Unity project を作成し、UPM tarball をインストールする。fixture を Unity asset として追加し、生成された C# をプロジェクト内でビルド・実行する。Editor は Mono、Player は Windows x64 IL2CPP である。今回の実行ログと JSON 結果は `C:\Users\fumika\AppData\Local\Temp\wasm2cs-rust-std-t02-4` に保存した。

## 固定入力と環境

| 項目 | 値 |
|---|---|
| Unity | 6000.6.0f1 |
| Package | `com.mfakane.wasm2cs` 0.1.0-preview.1 |
| Package tarball SHA-256 | `8e97d1eadfae3c11f508d7df9b4bd4b179e90cb44ef6f8f58fe86bd98b577ff4` |
| WASM | `samples/RustStd/RustStd190.wasm` |
| WASM SHA-256 | `02a2e53a0694d8176207ec0758589efe9e8b4ca45ad986ce7cd448dd30eb943a` |
| Node oracle | Node.js 22.17.0 |
| Oracle SHA-256 | `029ba292c09f05e7f6dec624303c4a13a5659d21ad6bc61bf3f1adb76f190c00` |

## 比較と結果

Editor Mono と Windows x64 IL2CPP の両方で、18 回の `analyze` 呼び出しを Node oracle と照合した。各呼び出しで status、sum、unique count、median、出力 bytes、入力 hash、memory page count、および線形メモリ全体の SHA-256 が一致した。初期メモリ状態、21 から最大41ページへの拡張、容量境界外アクセス時の `Unreachable` trap、新規インスタンスの初期メモリも一致した。

結果はいずれも成功した。

| Backend | 結果 |
|---|---|
| Unity Editor Mono | 18ケースと全メモリ状態が Node と一致 |
| Windows x64 IL2CPP | 18ケースと全メモリ状態が Node と一致 |

今回の入力について、Unity 向けの package bridge、C# 生成、実行に Core の不足は見つからなかった。T03 は実施しない。

## 範囲

これは Rust 1.90.0 のこの用途別 fixture、Unity 6000.6.0f1、Editor Mono、Windows x64 IL2CPP の測定である。Rust 1.85.0 の Unity 実行、他の Rust `std` API、他バージョン、第三者製モジュール、C++、他の Unity platform は未測定。Unity 実行はローカルで行い、Windows CI では確認していない。
