# capture3ds-sharp

3DS / DS キャプチャボードの映像を、純正ビューアを起動せずに USB から直接読み取る
C# (.NET Framework 4.8) ライブラリです。キャプチャプロトコルは MIT ライセンスの
[cc3dsfs](https://github.com/Lorenzooone/cc3dsfs) を C# に移植したもので、
[ponkan-python](https://github.com/niart120/ponkan-python) の方針に倣っています。

フレームは実解像度の RGB8 バッファ(上画面・下画面を別々に)で取得でき、
上下を縦に並べたモザイク画像や 1280x720 レターボックス画像への変換ヘルパーも
用意しています。

## 対応デバイス

| デバイス | チップ | プロトコル | 状態 |
|---|---|---|---|
| New 3DS XL 用キャプチャボード (3dscapture.com "N3DSXL") | FTDI FT600 (D3XX) | `3DSCapture_FTD3` 移植 | 実機動作確認済み |
| DS 用キャプチャボード | FTDI FT232H + Lattice FPGA | `DSCapture_FTD2` 移植 | 実機動作確認済み |
| 3DS LL 用キャプチャボード「LL-SPA3」(non-standard.com) | Cypress FX2LP | `Optimize_3DS` 移植 (CyUSB.NET 経由) | 実機動作確認済み |

画面サイズ: 3DS は上 400x240 / 下 320x240、DS は 256x192 が上下 2 枚。
2D キャプチャのみ対応しています。

## ビルド方法

前提: Windows、.NET Framework 4.8 developer pack。

ライセンス上の理由で、次の 2 つのサードパーティ製コンポーネントは本リポジトリに
含めていません。ビルド前に各自で配置してください。

1. **CyUSB.dll**(LL-SPA3 用)—
   [kategray/CyUSB](https://github.com/kategray/CyUSB) を `external/CyUSB` に
   clone し、`library/c_sharp` をビルドして
   `external/CyUSB/library/c_sharp/lib/CyUSB.dll` にアセンブリが置かれる状態に
   してください。(Cypress Software License のためソースを再配布できません)
2. **FTDI ネイティブ DLL** — [FTDI](https://ftdichip.com/) から入手して
   `native/` に配置してください。
   - `native/FTD3XX.dll`(D3XX、N3DSXL 用)
   - `native/ftd2xx.dll`(D2XX、DS キャプチャボード用)

配置後、次でビルドできます。

```
dotnet build src/Capture3DS.sln -c Release
```

`firmware/` 以下のファームウェア(DS 用 FPGA ビットストリームと Optimize 用
FX2 ファームウェア)は MIT ライセンスの cc3dsfs リポジトリ由来で、ビルド時に
アセンブリへ埋め込まれます。

## 使い方

```csharp
using Capture3DS;

var devices = Capture3DSApi.ListDevices();
using (var dev = Capture3DSApi.Open(devices[0]))
{
    dev.Connect();
    Capture3DSFrame frame = dev.ReadFrame();

    // frame.Top / frame.Bottom : RGB8、行優先、1px = 3byte
    byte[] mosaic = frame.ToMosaic(out int w, out int h);
    byte[] canvas = frame.ToLetterbox720(); // 1280x720 RGB8
}
```

`ListDevices()` はネイティブ DLL が見つからないバックエンドを黙ってスキップする
ので、上記 DLL の一部しか無い環境でもそのまま動作します。

## コマンドラインツール

`CaptureProbe.exe [出力フォルダ] [フレーム数]` — デバイスを列挙し、最初の 1 台に
接続して PNG(上画面 / 下画面 / 720p レターボックス)を保存します。

診断モード:

- `--cypress` — Cypress FX2 デバイスの生列挙(VID/PID/bcdDevice)
- `--ftd3raw` — FTD3 デバイスのフィルタ前ダンプ(ドライバ / 排他オープンの切り分け)
- `--ftd3sizes [n]` — 転送長の統計(フレーム整列の切り分け)
- `--ftd3stream <dir> <n>` — 高速連続読みテスト、ずれたフレームを保存
- `--ftd3verify <dir> <n>` — 実運用相当の負荷での `ReadFrame` 検証

## 注意事項

- **純正ビューアは先に終了してください。** キャプチャボードは排他オープンの
  デバイスなので、`3ds_capture.exe` や `n3DS_view.exe` の起動中は列挙から
  消えるか、接続に失敗します。
- **N3DSXL** には FTDI D3XX ドライバ(純正の 3ds_capture が使うもの)が必要です。
- **LL-SPA3** はメーカー純正の `cyusb3` ドライバのままで動きます。Zadig/WinUSB
  へのドライバ入れ替えも、手動でのファームウェア書き込みも不要です。素の FX2
  (`04B4:8613`)として認識されている場合は、ライブラリが cc3dsfs の Optimize
  ファームウェアを自動で転送し、デバイスは `04B4:1004` として再列挙されます。
- **LL-SPA3 のプロダクトキー**は任意です。存在する場合は `n3DS_view` の
  EEPROM キャッシュ(`%APPDATA%\non-standard.com\VROM_*.bin`)か、実行ファイルの
  隣に置いた `llspa3_key.txt` から読み込みます。キーはキャプチャ開始用の
  セットアップバッファに畳み込まれるだけで、ログ等には出力されません。

## ライセンスと出典

MIT License。[LICENSE](LICENSE) を参照してください。

本プロジェクトは次のプロジェクトからプロトコル実装を移植しています。

- [cc3dsfs](https://github.com/Lorenzooone/cc3dsfs) (MIT) — キャプチャ
  プロトコルおよび `firmware/` 以下のファームウェア
- [ponkan-python](https://github.com/niart120/ponkan-python) (MIT)

任天堂、3dscapture.com、non-standard.com、FTDI、Infineon/Cypress とは
一切関係ありません。
