# capture3ds-sharp

3DS/DSキャプチャボードの映像と音声を、純正ビューアを起動せずにUSBから直接読み取るC#（.NET Framework 4.8）ライブラリです。キャプチャプロトコルはMITライセンスの[cc3dsfs](https://github.com/Lorenzooone/cc3dsfs)をC#に移植したものです。

フレームは実解像度のRGB8バッファ（上画面・下画面を別々に）で取得でき、上下を縦に並べたモザイク画像や1280x720レターボックス画像への変換ヘルパーも用意しています。

## 対応デバイス

| デバイス | チップ | プロトコル |
|---|---|---|
| New 3DS XL用キャプチャボード（3dscapture.com「N3DSXL」） | FTDI FT600（D3XX） | `3DSCapture_FTD3`移植 |
| DS用キャプチャボード | FTDI FT232H + Lattice FPGA | `DSCapture_FTD2`移植 |
| 3DS LL用キャプチャボード「LL-SPA3」（non-standard.com） | Cypress FX2LP | `Optimize_3DS`移植（CyUSB.NET経由） |
| Loopy製 初代3DS用キャプチャボード | USB 2.0（VID:PID `16D0:06A3`） | `usb_ds_3ds_capture`移植（libusb経由、実機未検証） |

画面サイズ: 3DSは上400x240/下320x240、DSは256x192が上下2枚。2Dキャプチャのみ対応しています。

## ビルド方法

前提: Windows、.NET Framework 4.8 developer pack。

次のサードパーティ製コンポーネントは本リポジトリに含めていません。使用する機種に合わせて、ビルド前に各自で配置してください。

1. **CyUSB.dll**（LL-SPA3用）: [kategray/CyUSB](https://github.com/kategray/CyUSB)を`external/CyUSB`にcloneし、`library/c_sharp`をビルドして`external/CyUSB/library/c_sharp/lib/CyUSB.dll`にアセンブリが置かれる状態にしてください（Cypress Software Licenseのためソースを再配布できません）。
2. **FTDIネイティブDLL**: [FTDI](https://ftdichip.com/)から入手して`native/`に配置してください。
   - `native/FTD3XX.dll`（D3XX、N3DSXL用）
   - `native/ftd2xx.dll`（D2XX、DSキャプチャボード用）
3. **libusb-1.0.dll**（Loopy製初代3DS用）: [libusb v1.0.26](https://github.com/libusb/libusb/releases/tag/v1.0.26)の64-bit Windows版を`native/libusb-1.0.dll`に配置してください。

配置後、次でビルドできます。

```
dotnet build src/Capture3DS.sln -c Release
```

`firmware/`以下のファームウェア（DS用FPGAビットストリームとOptimize用FX2ファームウェア）はMITライセンスのcc3dsfsリポジトリ由来で、ビルド時にアセンブリへ埋め込まれます。

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

`ListDevices()`はネイティブDLLが見つからないバックエンドを黙ってスキップするので、上記DLLの一部しか無い環境でもそのまま動作します。

## USB音声

`ReadFrame()`の戻り値にある`frame.Audio`から、同じUSBパケットに含まれる音声を取得できます。ライブラリ自身はスピーカー再生を行わず、追加の音声デバイスやUSB接続も開きません。

対応する取得経路は、N3DSXL（FTD3）、DS（FTD2）、LL-SPA3（Cypress）、Loopy製初代3DS（libusb）です。DSは本ライブラリが対応するFTD2モデルに限り、USB音声非対応の旧世代DSボードを音声対応にするものではありません。音声は合成パケットによるテスト済みですが、各機種での実機再生は未検証です。

```csharp
Capture3DSAudioChunk audio = frame.Audio;
if (audio != null)
{
    short[] pcm = audio.Samples; // signed PCM16: L, R, L, R, ...
    int channels = audio.Channels; // 2
    int nominalRate = audio.SampleRate; // 32728 Hz
    double sourceRate = (double)audio.SampleRateNumerator
                      / audio.SampleRateDenominator; // 67027964 / 2048 Hz
    // 必要なら、呼び出し側の上限付き音声キューへ渡して別スレッドで再生する。
}
```

`Samples`はそのチャンクが所有する配列で、次の`ReadFrame()`やデバイスの破棄で上書きされません。公開コンストラクター`new Capture3DSAudioChunk(samples)`も入力配列をコピーします。配列の変更は、同じチャンクを使う別の処理と競合しないよう呼び出し側で管理してください。

完全なステレオサンプルが一つもない場合や、音声の境界・ヘッダー検証に失敗した場合は`Audio`が`null`になります。末尾の不完全なステレオの組は捨て、それ以前の完全な組は返します。転送バッファの未使用領域や末尾の同期パディングは音声として扱いません。LL-SPA3は通常フレームと追加ヘッダー付きフレームを区別し、フレームをまたぐサンプル番号の重複を除き、再接続時に番号をリセットします。

既存の6引数の`Capture3DSFrame`コンストラクターと映像APIはそのまま使えます。音声の再生・リサンプリング・音量・遅延制御は利用側の責任です。`SampleRate`は丸めた整数値なので、長時間の同期が必要な場合は分子・分母から正確なレートを使ってください。音声を映像プレビューの描画間隔に合わせて間引くと途切れるため、取得した音声はUI描画とは独立して処理してください。

ハードウェアを使わない回帰テストの実行方法は[tests/README.md](tests/README.md)を参照してください。

## コマンドラインツール

`CaptureProbe.exe [出力フォルダ] [フレーム数]`はデバイスを列挙し、最初の1台に接続してPNG（上画面/下画面/720pレターボックス）を保存します。

診断モード:

- `--cypress`: Cypress FX2デバイスの生列挙（VID/PID/bcdDevice）
- `--ftd3raw`: FTD3デバイスのフィルタ前ダンプ（ドライバ/排他オープンの切り分け）
- `--ftd3sizes [n]`: 転送長の統計（フレーム整列の切り分け）
- `--ftd3stream <dir> <n>`: 高速連続読みテスト、ずれたフレームを保存
- `--ftd3verify <dir> <n>`: 実運用相当の負荷での`ReadFrame`検証

## 注意事項

- **純正ビューアは先に終了してください。** キャプチャボードは排他オープンのデバイスなので、`3ds_capture.exe`や`n3DS_view.exe`の起動中は列挙から消えるか、接続に失敗します。
- **N3DSXL**にはFTDI D3XXドライバ（純正の3ds_captureが使うもの）が必要です。
- **LL-SPA3**はメーカー純正の`cyusb3`ドライバのままで動きます。Zadig/WinUSBへのドライバ入れ替えも、手動でのファームウェア書き込みも不要です。素のFX2（`04B4:8613`）として認識されている場合は、ライブラリがcc3dsfsのOptimizeファームウェアを自動で転送し、デバイスは`04B4:1004`として再列挙されます。
- **LL-SPA3のプロダクトキー**: 無くても動作します（キー無しの場合、約260秒ごとに映像の再初期化が入ります）。キーは実行ファイルの隣の`Capture3DS.json`から読み込みます。無い場合でも、n3DS_viewを使ったことがあるPCならそのキャッシュから自動で読み込み、`Capture3DS.json`へ保存します。手動で設定するときはこのJSONを直接編集するか、`Capture3DSApi.SetLlSpa3ProductKey`を呼んでください。
- **Loopy製初代3DS**は、libusbからアクセスできるWindowsドライバと`libusb-1.0.dll`が必要です。ホスト側からのファームウェア転送は行いません。実装はcc3dsfsとCuteCaptureを参照していますが、実機未所持のため動作未検証です。

## ライセンスと出典

MIT License。[LICENSE](LICENSE)を参照してください。

本プロジェクトは次のプロジェクトからプロトコル実装を移植しています。

- [cc3dsfs](https://github.com/Lorenzooone/cc3dsfs)（MIT）: キャプチャプロトコルおよび`firmware/`以下のファームウェア
- [CuteCapture](https://github.com/Gotos/CuteCapture)（Apache-2.0）: Loopy製初代3DSのUSBプロトコルと画面配置の参考実装

任天堂、3dscapture.com、non-standard.com、Loopy、FTDI、Infineon/Cypressとは一切関係ありません。
