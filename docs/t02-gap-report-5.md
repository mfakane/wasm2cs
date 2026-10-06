# T02 第5回: 残っていた未測定項目（Rust 1.85.0 の Unity、第三者製モジュール、C++）

2026-10-06 に、`master` `555752f`（第4回の Unity 検証の後）から作業ブランチ `feat/t02-remaining-measurements` で測定した。第4回の範囲の節で未測定とされた3項目だけを対象とする。第1回〜第4回の入力は再測定していない（既存テストは下記のとおり通過）。

環境は Linux x64、.NET SDK 10.0.401、Node.js v22.19.0、wasmtime 28.0.1、wasi-sdk-24.0（Clang 18.1.2-wasi-sdk）、WABT 1.0.41。翻訳は T02 と同じく `dotnet run --project src/Wasm2Cs.Cli -- <file.wasm>`（既定 profile `portable-netstandard2.0`）で行った。測定スクリプトは同じ CLI を `src/Wasm2Cs.Cli/bin/Debug/net10.0/Wasm2Cs.Cli.dll` として直接起動する。

## 結果の要約

| 項目 | 結果 |
|---|---|
| (1) Rust 1.85.0 fixture の Unity Editor / IL2CPP 実行 | **未検証**。この環境には Windows と Unity Editor がない。手順と oracle を用意した（下記） |
| (2) 第三者製モジュール | hash-wasm 4.12.0 の22モジュールと xxhash-wasm 1.1.0 の1モジュール、計23。全モジュールが翻訳・C# コンパイルでき、各パッケージ自身の JS が行った 2,396 回の export 呼び出しで、戻り値・trap・呼び出しごとの全メモリが Node と一致 |
| (3) C++ | 新しい fixture `samples/CppWorkload/CppWorkload.wasm`（wasi-sdk-24.0 clang++ `-O2`、libc++）。翻訳・C# コンパイルができ、47 呼び出しと最終メモリが Node と一致し、trap の種類が wasmtime と一致 |

Core の不足は見つからなかった。T03 は引き続き実施しない。新しい SIMD・WASI の不足もない。新しく見つかったのは、生成 C# の命名に関する低優先度の制約1件である（下記「確認できた不足」）。

## (1) Rust 1.85.0 の Unity 実行（未検証）

Unity/IL2CPP の実行には Windows と Unity 6000.6.0f1（Windows Build Support (IL2CPP)）が必要で、この Linux 環境にはどちらもない。代用の実行はしていない。.NET 上の `RustStd185.wasm` の比較は第3回のとおりで、CI の `node scripts/measure-rust-std.mjs` でも毎回 Node と照合されている。

第4回の harness は `RustStd190` に固定されていたので、fixture を選べるようにした。既定値は従来どおり `RustStd190` で、既存の手順は変わらない。

- `scripts/measure-rust-std.mjs --unity-oracle --unity-fixture=RustStd185` が `artifacts/t02-rust-std/unity-oracle-RustStd185.json` を書く。
- `scripts/test-unity-rust-std.ps1 -Fixture RustStd185` が、その oracle と `samples/RustStd/RustStd185.wasm` を使う。Unity project へのコピー時に、`unity/RustStd/RustStdRunner.cs` 中の生成クラス名 `RustStd190` を `RustStd185` に置き換える。

この環境で確認したのは次の範囲に限る。oracle を生成し、fixture SHA-256 `a4d566a5…421f95`、18ケース、`Unreachable` trap、21→41ページの拡張を確認した。また PowerShell 7.6.6（Linux）で `.ps1` を構文解析し、エラーは0件だった。実行はしていない。oracle の SHA-256 は Node のバージョン文字列を含むため環境ごとに変わる（今回 `59de4699…53420b`、Node v22.19.0）。

Windows で人が実行する手順（リポジトリルート、Node.js 22、Unity 6000.6.0f1 + Windows Build Support (IL2CPP)）:

```sh
node scripts/measure-rust-std.mjs --unity-oracle --unity-fixture=RustStd185
node scripts/pack.mjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-unity-rust-std.ps1 -PackagePath artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz -Fixture RustStd185
```

Unity のパスが異なる場合は `-Editor <Unity.exe>` を付ける。成功時は `PASS: Rust std Unity Editor and Windows x64 IL2CPP; …` が表示され、`WorkDirectory` に `editor.json` と `player.json` が残る。その結果を本書に追記するまで、Rust 1.85.0 の Unity 対応は主張しない。

## (2) 第三者製モジュール

### 入力の出所

| パッケージ | バージョン | ライセンス | 取得元 | tarball SHA-256 |
|---|---|---|---|---|
| hash-wasm | 4.12.0 | MIT | `https://registry.npmjs.org/hash-wasm/-/hash-wasm-4.12.0.tgz` | `1db32a125fb46177932ec8ac438d3cd8214ebdfaccb5d6611b657d88eb586f92` |
| xxhash-wasm | 1.1.0 | MIT | `https://registry.npmjs.org/xxhash-wasm/-/xxhash-wasm-1.1.0.tgz` | `cddd90f792012cd253728e11a643e15251268a7a9588851ccc51a5c8f16340ad` |

hash-wasm は C を Clang で WASM にしたハッシュ関数群で、モジュールは JS bundle に base64 で埋め込まれている。xxhash-wasm は手書きの WebAssembly テキストから生成された xxHash32/64 である。いずれも import と start セクションはなく、SIMD 命令も含まない。

バイナリはチェックインしていない。パッケージ自体は MIT だが、hash-wasm の C ソースには由来の異なる実装（BLAKE3、Argon2、bcrypt など）が含まれる。各ライセンスの扱いをこの作業では確認していないため、固定した URL と SHA-256 から取得して検証する方式にした。

```sh
node scripts/measure-third-party.mjs            # 取得、検証、翻訳、C# 実行、比較
node scripts/measure-third-party.mjs --inventory # WABT による命令数とセクション一覧も記録
```

tarball は `artifacts/t02-third-party/cache/` に保存し、ある場合は再利用する。どちらの場合も SHA-256 を照合し、一致しなければ失敗する。結果は `artifacts/t02-third-party/results.json` に、trace、Node と C# の全行、consumer は `artifacts/t02-third-party/consumer/` に出力する。ネットワークが必要なため、regression workflow には追加していない。

### 比較方法

用途を手で再現するのではなく、各パッケージが公開している JS API をそのまま Node で動かした。その間 `WebAssembly.compile` / `instantiate` を包み、次を trace に記録した。

- export の各呼び出し、引数（`null`・BigInt を含む）、戻り値または trap
- 呼び出し前に JS が線形メモリへ書いたバイト列（前回の呼び出し後との差分）
- JS からの `memory.grow`（xxhash-wasm は大きい入力でこれを行う）
- 呼び出しごとの全メモリの SHA-256 とページ数

この trace を新しい Node インスタンスで再生し、記録と一致すること（再生の正しさ）を確認した。その後、生成 C# で同じ trace を再生し、呼び出しごとに戻り値・trap の種類・ページ数・全メモリ SHA-256 を Node 再生と比較した。引数は JS と同じ規則で変換した（`ToInt32`、`null`→0、不足引数は 0、余分な引数は無視）。生成 C# は C# 9 と netstandard2.0 の runtime ABI でコンパイルし、.NET 10 で実行した。

パッケージ側の処理の正しさは、WASM 実行とは別に次の方法で確認した（81件、すべて一致）。

- `node:crypto` との照合: MD5、SHA-1/224/256/384/512、SHA3-224/256/384/512、RIPEMD-160、SM3、BLAKE2b-512、BLAKE2s-256、PBKDF2-SHA256、scrypt。Node v22.19.0 の OpenSSL には whirlpool と MD4 がないため、この2つは照合していない（両モジュールの C# と Node の一致は下表のとおり）。
- 既知の値: CRC32 `cbf43926`、BLAKE3("abc")、xxHash64("abc")、Adler-32("Wikipedia")、HMAC-SHA256 の例。
- 独立した2実装の照合: xxhash-wasm の h32/h64 と hash-wasm の xxhash32/64 を、5入力（0〜200,000 バイト）で比較した。
- ライブラリ内の照合: SHA-256 の save/load、argon2i/d/id の verify、bcrypt の verify（正しいパスワードと誤ったパスワード）。

入力は 0、3、1,000、40,000 バイト（hash-wasm の 16 KiB バッファを超える分割更新を含む）。trap は、パッケージの通常利用では起きないため、各 trace の最後に合成呼び出しを足して確かめた。`Hash_Update(16777216)` と `xxh32(0x7ffffff0, 64, 0)` は Node と C# の両方で `MemoryOutOfBounds` となった。sha3 は最後の呼び出しが `Hash_Final` の後で、更新を行わず trap しない。この点も両者で一致した。その後に `Hash_GetBuffer` を呼び、trap 後もインスタンスを使えることを確認した。合成呼び出しは計42回で、2,396 回に含まれる。

### モジュールごとの結果

翻訳は全23モジュールで成功した。bcrypt と scrypt は既定のクラス名（ファイル名）では拒否されたため、`-n ThirdParty_<name>` を付けて翻訳した。命令数は `scripts/wasm-opcodes.mjs` による全関数の走査で、ローカル宣言は含まない。

| モジュール | バイト数 | 命令数（種類） | SHA-256 | 既定名での CLI | インスタンス / 呼び出し | 合成 trap | C# と Node |
|---|---:|---:|---|---|---:|---|---|
| hash-wasm `adler32` | 1452 | 689 (24) | `7aa030fcf233eb001ee09036e9ea025c40754d3792d7d10c605a3b81a39f7a54` | 成功 | 1 / 12 | OOB trap 一致 | 一致 |
| hash-wasm `argon2` | 6660 | 2781 (50) | `83b5829d20b4312aca1a56819f95eef20492e058c70f282c9ed6929f579b20ed` | 成功 | 6 / 25 | — | 一致 |
| hash-wasm `bcrypt` | 16944 | 6287 (40) | `6a204dc0bc5d7ebfe386a4969095b5319627397f3c3cc1cb0a16ea0e7fbaf313` | 既定名で拒否（下記） | 3 / 7 | — | 一致 |
| hash-wasm `blake2b` | 7442 | 4101 (38) | `b478c0d889d97d7a8db4d10501457ad78dd406d02dcb4c892c0d844805ef05bb` | 成功 | 20 / 1255 | OOB trap 一致 | 一致 |
| hash-wasm `blake2s` | 6652 | 3696 (38) | `89704350070c32c1ea055c06cada4d1b29d2a66e94f49efa9ca1eaab4ef7fe9b` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `blake3` | 11891 | 5728 (46) | `984b12e3b76a670fe58f43aa965658cdfefe0867f88c4935a292f68bdf3c55e1` | 成功 | 1 / 25 | OOB trap 一致 | 一致 |
| hash-wasm `crc32` | 1231 | 528 (25) | `e2223e87187457beaaaf58af50a88772141c5a83bc68d0340608215423ba901d` | 成功 | 1 / 20 | OOB trap 一致 | 一致 |
| hash-wasm `crc64` | 1210 | 529 (32) | `4ff0b9eb1efab52973e27f1aea8810a2f752997390eb0e7a8061e67aaf2340a7` | 成功 | 1 / 16 | OOB trap 一致 | 一致 |
| hash-wasm `md4` | 2853 | 1368 (32) | `3ac6d44a150d6e51afec98a6552ea313067162429066486ed3c5fc8698937197` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `md5` | 3521 | 1729 (32) | `588135c4a5dff4fa3c653e60edf777f1b8085b96a07ab4d083c0f6f2df6c21e8` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `ripemd160` | 6765 | 3634 (33) | `ce819c4a2404b47180d9eee9fb1ea0bf4ace1bc5a1e54c71cb6a1af4ac4e731e` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `scrypt` | 5099 | 2633 (39) | `435767cf3c676211d6c8b479e219e5298c2a3fc84f929cbf47389d713cdd9b4d` | 既定名で拒否（下記） | 1 / 5 | — | 一致 |
| hash-wasm `sha1` | 5592 | 3019 (34) | `17266992619f52e5af20f90738dc9a955b694873e4d8a937db53b9abad752bb9` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `sha256` | 9689 | 5628 (35) | `c44604aaa9d054401459b0d07f3d6deeb440fa7afdcb0cfd900ef2596d55ce55` | 成功 | 6 / 720 | OOB trap 一致 | 一致 |
| hash-wasm `sha3` | 4018 | 1635 (35) | `4b3e3ab7973037bfdf21c776085c617dd15d8faad72c073f2eb4ca022a233f8b` | 成功 | 17 / 59 | — | 一致 |
| hash-wasm `sha512` | 13522 | 7511 (37) | `60afdfbea19ee8ad976da15ef9f557778e1c5de6ee54407e04269da72f5727e5` | 成功 | 2 / 20 | OOB trap 一致 | 一致 |
| hash-wasm `sm3` | 4056 | 1901 (34) | `126cc3271d1a0ad2e347494af526339945d81ea0a30e59286abeaa49c51d727d` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| hash-wasm `whirlpool` | 5817 | 1635 (40) | `58308a6f83dd912cb7c16045ebba13ce507a2554ea0fc008b12582822c812d26` | 成功 | 1 / 11 | OOB trap 一致 | 一致 |
| xxhash-wasm `xxhash.wasm` | 3105 | 1201 (44) | `70c5a91a447af44fa45f11a7d707d1850ecc44f20d5deea58cdd3bfb33213c2a` | 成功 | 1 / 21 | OOB trap 一致 | 一致 |
| hash-wasm `xxhash128` | 10302 | 4672 (49) | `a02165ba1a90d88728f125e1e58b8821f5e46d4a7602b66b4a0458dc250c5fd1` | 成功 | 1 / 16 | OOB trap 一致 | 一致 |
| hash-wasm `xxhash3` | 8866 | 4001 (49) | `2a8d44107f4641e0e7ca169d52cd3890365c9c83877cb95e35d381625e0c3b74` | 成功 | 1 / 16 | OOB trap 一致 | 一致 |
| hash-wasm `xxhash32` | 2475 | 1011 (36) | `0cd750338eee542f087493e656c67f7358120e201a23cc5c0faa8f1fd60c1058` | 成功 | 1 / 42 | OOB trap 一致 | 一致 |
| hash-wasm `xxhash64` | 2386 | 835 (37) | `de1f937a9c1d82dc347dd6abb38833f73ef77bebe68495b2ca3c51b584df79be` | 成功 | 1 / 60 | OOB trap 一致 | 一致 |

合計 66,752 命令。メモリは argon2 の `Hash_SetMemorySize`（WASM 内の `memory.grow`）で最大10ページまで拡張し、xxhash-wasm では JS からの grow を1回再生した。セクションは type、function、memory、export、code のほか、モジュールにより global・data。table・element・import・start はない。

再現（各モジュール）: `node scripts/measure-third-party.mjs` を一度実行すると、`artifacts/t02-third-party/consumer/wasm/<name>.wasm` に取り出したモジュールが置かれる。その後 `dotnet run --project src/Wasm2Cs.Cli -- artifacts/t02-third-party/consumer/wasm/<name>.wasm` で翻訳できる。bcrypt と scrypt は既定のファイル名クラスだと衝突する（測定スクリプトは `-n ThirdParty_<name>` を使う）。衝突時のメッセージは次の形（自動リネームはしない）。

```text
Export 'bcrypt' collides with generated class name 'bcrypt'. Rename the class with --class-name (CLI), ClassName metadata on the <Wasm> item (MSBuild), or ClassName on the Unity Wasm importer.
```

## (3) C++

新しい入力 `samples/CppWorkload/`。ソース、ビルドフラグ、ハッシュ、再生成手順は [入力の README](../samples/CppWorkload/README.md) にある。

| 項目 | 値 |
|---|---|
| ツールチェーン | wasi-sdk-24.0、`clang version 18.1.2-wasi-sdk`、libc++/libc++abi/wasi-libc 同梱版 |
| フラグ | `--target=wasm32-wasi -mexec-model=reactor -O2 -g0 -std=c++17 -fno-exceptions -fno-rtti -Wall -Wextra -Werror -Wl,--export-memory -Wl,--strip-all` |
| 再生成 | `bash scripts/create-cpp-fixture.sh`（または `scripts/create-cpp-fixture.ps1`） |
| バイナリ | 26,364 バイト、SHA-256 `e8add49c18f985a3ddc2a6dcc160ea6c7c766aa03f98f867cdd74fdbacfa3807` |
| 構成 | 47関数、12,721 命令（69種類）、import なし、table 13（element 12）、メモリ初期2ページ |

再生成は、`.sh` を別ディレクトリにコピーしたソースで、`.ps1` を Linux の PowerShell 7.6.6 で実行した。どちらも同じハッシュになった。

C++ 固有の要素として、動的初期化される global（`_initialize` → `__wasm_call_ctors`）、仮想関数（`call_indirect` 10）、`std::unique_ptr` 配列と `new`/`delete`（dlmalloc 経由の `memory.grow`）を使う。ほかに `std::sort`（`libc++.a` 内の既成の特殊化）と `std::stable_sort`、`<numeric>`、テンプレートの行列積（`i64.mul` 55）、`br_table` 9 を含む。`memory.copy` 2 と `memory.fill` 1 は wasi-libc の既成の `memcpy` / `memset` に由来する。

初回のビルドには WASI import（`fd_close`、`fd_seek`、`fd_write`）が3つあった。`wasm-ld --why-extract` で調べると、libc++abi の `__cxa_pure_virtual` が `abort_message` を経由して stdio を引き込んでいた。このライブラリでは呼ばれない経路なので、`__cxa_pure_virtual` と `operator new`/`delete` を trap する実装に置き換え、import のない入力にした。ソースの注記と README に記録してある。命令や関数をリンク後に除去してはいない。

再現と比較:

```sh
dotnet run --project src/Wasm2Cs.Cli -- samples/CppWorkload/CppWorkload.wasm
dotnet run --project tests/Wasm2Cs.Tests   # ExecutionChecks.CppWorkload
```

`samples/CppWorkload/calls.json` の47呼び出しを1インスタンス上で生成 C# と Node（`tests/Wasm2Cs.Tests/sequence-oracle.mjs`）で順に実行し、各戻り値または trap と最終メモリの SHA-256（`c5161c60…4463ee`）を比較した。結果は全行一致した。呼び出し列には次を含む。

- `_initialize` の前の `registry_next`（コンストラクタ未実行で 1）と後（1005、1006…）
- 3種のソートと `reverse`、i64 の prefix sum
- 0 / 1 / 100 / 200,000 / 300,000 個の図形の確保と解放（2→93 ページ）
- 0〜1000 乗の行列積
- 境界外の `checked_at`（`__builtin_trap` → `unreachable`）、`divide(INT_MIN, -1)`、`divide(1, 0)`

trap の種類は C# でそれぞれ `Unreachable`、`IntegerOverflow`、`DivisionByZero` で、wasmtime 28.0.1 の `wasmtime run --invoke` の trap メッセージ（unreachable、integer overflow、integer divide by zero）と一致した。`divide(7,-2)`、`matrix(3,10)`、`shapes(100,3)`、`pages()`、`_initialize` 後の `registry_next()` も wasmtime と同じ値だった。

このテストは `dotnet run --project tests/Wasm2Cs.Tests` に追加したので、CI でも毎回比較される。C++ 例外、RTTI、iostream、threads は含まない。

## 確認できた不足

| 不足 | 分類 | 出現した入力 | 優先度 |
|---|---|---|---|
| Core の読取り・即値・型検証・import・生成 C# のコンパイル・実行結果の不足 | Core | なし（C++、第三者製23モジュール） | — |
| export 名が生成クラス名（既定はファイル名）と同じだと拒否される（C# CS0542）。当時は CLI `-n` のみで、MSBuild `<Wasm>` / Unity bridge にクラス名指定がなかった | 生成 C# の命名（Core 命令ではない） | hash-wasm `bcrypt`、`scrypt` | **対応済み**（`fix/class-name-collision`）: 既定は明確なエラー（自動リネームしない）。CLI `--class-name`、MSBuild `ClassName`、Unity `WasmImporter.ClassName` でクラス名を変更する。Unity Editor/IL2CPP での ClassName 経路は未検証 |
| SIMD | SIMD | 今回の入力にはなし（第2回の19命令のまま） | 変化なし |
| WASI | WASI | 今回の入力にはなし。C++ の初回ビルドの3 import は、未使用の abort 経路に由来していた | 変化なし |

## 既存テスト

`dotnet run --project tests/Wasm2Cs.Tests` と `node scripts/test-build.mjs` は、このブランチで成功した。`node scripts/measure-rust-std.mjs`（CI と同じ inventory なし）も成功した。

## 次のタスク

T02 で未測定として残っていた3項目のうち、2項目は測定した。残るのは Rust 1.85.0 の Unity 実行だけで、これは Windows + Unity の環境で上記コマンドを実行して閉じる。T03 は Core の候補がないため実施しない。測定で具体的な入力があるのは SIMD（`Simd.wasm` の19命令）だけなので、次は T04 で SIMD の最初の一群を絞るのが妥当である。命名衝突は後に `fix/class-name-collision` で対応した（明確なエラー + CLI/MSBuild/Unity の ClassName。自動リネームなし）。
