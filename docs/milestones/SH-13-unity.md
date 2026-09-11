# SH-13 Unity IL2CPP 自己実行

状態: 未着手。前提: [SH-12](SH-12-self-hosting.md)。次: [SH-14](SH-14-reproducibility.md)。

## 目的・変更対象

通常の .NET と同じ自己実行を Unity Editor と Windows x64 IL2CPP Player で通す。対象は Unity 入力ブリッジ、生成コード、ホストの環境アダプター、専用 smoke と新設する検証スクリプトである。

## 実施手順

1. SH-12 と同じ runtime／BCL／guest bundle を新規 Unity プロジェクトへ供給する。Generator とホストライブラリを導入し、WASM からビルド時に生成したコードをコンパイルへ含める。
2. ゲスト DLL は Mono に渡すデータ asset として格納する。Unity の通常のプラグイン DLL として import／実行しない。拡張子や importer 設定を明示し、バイト列のハッシュを確認する。
3. ファイル、ログ、終了などの環境差だけを Unity アダプターへ実装する。変換器や Mono 起動の別実装を作らない。
4. 型付き delegate、必要な静的参照、stripping、例外処理を確認する。生成クラスを指定 assembly に隔離し、参照 assembly へ重複生成しないことも確認する。
5. Editor と IL2CPP Player 内で SH-12 の入力を変換し、C# テキスト・診断・ハッシュを外側へ保存する。Player 内で C# をコンパイルしない。
6. 保存した生成テキストを外側の .NET ビルド工程でコンパイル・検証する。生成結果そのものの IL2CPP 動作は、取得したソースを使う別の Unity ビルドで確認する。
7. 起動後の入力変更、DLL 欠落、反復実行と、実際のメモリ／起動／変換時間を記録する。既存の小型 Unity smoke も維持する。

## 検証

この段階で追加する予定コマンド。最初の Editor は既存の検証版に合わせる。

```powershell
./scripts/test-unity-self-hosting.ps1 -Editor "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe"
```

スクリプトは新規プロジェクト作成、Editor 実行、Windows x64 IL2CPP ビルド、Player 実行、出力検証を行う。実装時には既存 `scripts/test-unity.ps1` の package 導入・ログ保存・タイムアウト処理を再利用する。Unity package の変更は Package Manager の API を使い、manifest を直接書き換えない。

## 合格条件

- [ ] 同じ bundle を使い、Editor と IL2CPP Player の自己実行が SH-12 と一致する。
- [ ] ゲスト DLL が Unity 側の assembly として実行されていない。
- [ ] Player 内で WASM／JavaScript エンジン、動的 C# コンパイル、reflection dispatch を必要としない。
- [ ] 生成したテキストを取り出し、別のビルド工程で動作検証できる。
- [ ] ログ、Player の終了コード、成果物ハッシュ、測定結果、実際の Unity 版を保存している。

## 非対象・失敗時の扱い

他 OS／モバイル／WebGL Player、UdonSharp は追加しない。`6000.6.0f1` の成功を Unity 6.0 系の検証済みと表現しない。ビルド成功だけでは完了せず、Player の実行と出力を確認する。

## コミットと記録

Unity 供給・アダプター、Editor smoke、IL2CPP と出力の再ビルド検証に分ける。終了コミット例: `feat: verify self-hosting on Unity IL2CPP`。

- 実行コマンド・結果: 未実施
- Unity 版・bundle ハッシュ・Editor／build／Player ログ: 未取得
- 未解決事項: 巨大生成コードとゲスト DLL データ供給の IL2CPP 検証待ち
- 終了コミット: 未完了
