using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Capture3DS.Ftd3
{
    /// <summary>
    /// N3DSXL (FTDI FT60x / FTD3) キャプボの接続・読み出し。
    /// 列挙/オープン/接続シーケンスは cc3dsfs (MIT) のドライバ経路、
    /// コマンド列とフレーム読みは ponkan-python (MIT) に準拠した 2D 実装。
    /// </summary>
    public sealed class Ftd3N3dsxlDevice : ICapture3DSDevice
    {
        private const byte BulkOut = 0x02;
        private const byte BulkIn = 0x82;
        private const int CfgWaitMs = 200;
        private const int CommandTimeoutMs = 1000;
        private const int ReadRetryAttempts = 8;
        private const int ReadRetrySleepMs = 2;
        // 取り込み(ストリーミング)読みのパイプタイムアウト。これを設定しておかないと、
        // SetStreamPipe がタイムアウトを 0(無限)に戻した状態でデバイスが一瞬止まると
        // FT_ReadPipe が永久にブロックし、プロセスが taskkill でも殺せないゾンビ化→
        // USB 抜き差ししか復帰手段が無くなる。有限値にして必ず返るようにする。
        private const int StreamReadTimeoutMs = 1000;
        // Connect の自動リトライ。前回セッションの残り状態で初回が失敗しても、
        // クリーンに閉じてやり直せば 2,3 回目で繋がることが多い(手動の抜き差しを減らす)。
        private const int ConnectAttempts = 3;
        private const int ConnectRetrySleepMs = 150;

        // 2D サイズ(ponkan protocol/sizes と一致)
        private const int VideoSize2D = N3dsxlDecoder.VideoSize2D; // 518400
        private const int AudioSize = 1096 * 16 * 2;               // 35072
        private const int UnusedBuffer = 1024;
        private const int ErrorBuffer = 1024;
        private const int AlignBytes = 1024;
        private static readonly int CaptureSize2D =
            ((VideoSize2D + AudioSize + UnusedBuffer + ErrorBuffer) / AlignBytes) * AlignBytes; // 555008
        private static readonly string[] ValidDescriptions = { "N3DSXL", "N3DSXL.2" };

        private const int RawFrameReadAttempts = 16;

        private IntPtr _handle = IntPtr.Zero;
        private readonly byte[] _frameBuffer = new byte[CaptureSize2D];

        // デバイス固有の整列フレーム長(短パケットで終端される 1 フレーム分の転送長)。
        // 接続後の最初の正常読みで学習し、以後この長さ以外は「フレーム途中から始まった
        // ミスアライン」とみなして再同期・破棄する。0=未学習。
        private uint _expectedFrameSize;

        public Capture3DSDeviceInfo Info { get; }

        private Ftd3N3dsxlDevice(Capture3DSDeviceInfo info)
        {
            Info = info;
        }

        /// <summary>接続中の FTD3 デバイスを列挙する。</summary>
        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var result = new List<Capture3DSDeviceInfo>();
            uint numDevs;
            if (Ftd3Native.Failed(Ftd3Native.FT_CreateDeviceInfoList(out numDevs)) || numDevs == 0)
                return result;

            for (uint i = 0; i < numDevs; i++)
            {
                var serialBuf = new byte[17];
                var descBuf = new byte[65];
                uint flags, type, id;
                IntPtr h;
                if (Ftd3Native.Failed(Ftd3Native.FT_GetDeviceInfoDetail(
                        i, out flags, out type, out id, IntPtr.Zero, serialBuf, descBuf, out h)))
                    continue;

                string desc = CString(descBuf);
                if (Array.IndexOf(ValidDescriptions, desc) < 0)
                    continue;

                string serial = CString(serialBuf);
                bool superSpeed = (flags & Ftd3Native.FT_FLAGS_SUPERSPEED) != 0;
                result.Add(new Capture3DSDeviceInfo(Capture3DSModel.N3dsxl, serial, desc, superSpeed));
            }
            return result;
        }

        /// <summary>
        /// 診断用: FTD3 列挙結果をフィルタせず生のまま返す。
        /// numDevs と各デバイスの type/id/flags/serial/description を確認して、
        /// Description フィルタ("N3DSXL"/"N3DSXL.2")に一致しているかを切り分ける。
        /// </summary>
        public static IReadOnlyList<string> DescribeRawDevices()
        {
            var lines = new List<string>();
            uint numDevs;
            int listStatus = Ftd3Native.FT_CreateDeviceInfoList(out numDevs);
            lines.Add($"FT_CreateDeviceInfoList: status=0x{listStatus:X}, numDevs={numDevs}");
            if (Ftd3Native.Failed(listStatus) || numDevs == 0)
                return lines;

            for (uint i = 0; i < numDevs; i++)
            {
                var serialBuf = new byte[17];
                var descBuf = new byte[65];
                uint flags, type, id;
                IntPtr h;
                int st = Ftd3Native.FT_GetDeviceInfoDetail(
                    i, out flags, out type, out id, IntPtr.Zero, serialBuf, descBuf, out h);
                if (Ftd3Native.Failed(st))
                {
                    lines.Add($"  [{i}] GetDeviceInfoDetail failed: 0x{st:X}");
                    continue;
                }
                string desc = CString(descBuf);
                string serial = CString(serialBuf);
                bool match = Array.IndexOf(ValidDescriptions, desc) >= 0;
                lines.Add($"  [{i}] type={type} id=0x{id:X8} flags=0x{flags:X} " +
                          $"serial='{serial}' desc='{desc}' filterMatch={match}");
            }
            return lines;
        }

        public static Ftd3N3dsxlDevice Open(Capture3DSDeviceInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Model != Capture3DSModel.N3dsxl)
                throw new Capture3DSException("not an N3DSXL device");
            return new Ftd3N3dsxlDevice(info);
        }

        public void Connect()
        {
            Capture3DSException last = null;
            for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
            {
                try
                {
                    // 1回目: ドレイン用に開いて即閉じる
                    OpenHandle();
                    DrainData();
                    CloseHandle();

                    // 2回目: 本接続
                    OpenHandle();
                    Spi3dsCcStuff();
                    Load3dsCcFirmware(1);
                    Read3dsConfig3d();
                    SetStreamPipe2D();
                    // 取り込み読みが無限待ちでゾンビ化しないよう、必ず有限タイムアウトを設定。
                    // SetStreamPipe2D の後に設定すること(SetStreamPipe がタイムアウトを戻すため)。
                    Ftd3Native.FT_SetPipeTimeout(_handle, BulkIn, StreamReadTimeoutMs);
                    _expectedFrameSize = 0; // 再接続ごとに整列長を学習し直す
                    return;
                }
                catch (Capture3DSException ex)
                {
                    // 途中の SPI 失敗で PreemptiveClose 済みのこともあるが、確実に閉じてやり直す。
                    last = ex;
                    ForceClose();
                    if (attempt < ConnectAttempts)
                        Thread.Sleep(ConnectRetrySleepMs);
                }
            }
            throw last ?? new Capture3DSException("connect failed");
        }

        /// <summary>例外を投げずに両パイプを Abort してハンドルを閉じる(リトライ前の後始末)。</summary>
        private void ForceClose()
        {
            if (_handle == IntPtr.Zero) return;
            try { Ftd3Native.FT_AbortPipe(_handle, BulkIn); } catch { }
            try { Ftd3Native.FT_AbortPipe(_handle, BulkOut); } catch { }
            try { Ftd3Native.FT_Close(_handle); } catch { }
            _handle = IntPtr.Zero;
        }

        public Capture3DSFrame ReadFrame()
        {
            EnsureOpen();
            // ホスト読みが遅れて FIFO が溢れると、デバイスは短パケット(フレーム境界)を早めに
            // 出してフレームを切り詰める。その読みは整列長より短く=映像が縦ずれ・色ずれする。
            // 重要: その短い読みも短パケットで終端されているので、次の読みは自然にフレーム先頭へ
            // 再整列する(AbortPipe は不要。むしろブロッキング読みを無限待ちにしてハングする)。
            // したがって整列長と一致しないフレームは「捨ててもう一度読む」だけでよい。
            // 整列長に一致したフレームだけをデコードして返す。
            uint reseedSize = 0;   // 直近の不一致長(同じ長さが連続すればデバイスが恒久的に
            int reseedCount = 0;   // フレーム長を変えた=3D切替等とみなし整列長を学習し直す)
            for (int attempt = 0; attempt < RawFrameReadAttempts; attempt++)
            {
                int status = Ftd3Native.FT_ReadPipe(_handle, BulkIn, _frameBuffer,
                    (uint)CaptureSize2D, out uint transferred, IntPtr.Zero);
                if (Ftd3Native.Failed(status))
                {
                    // I/O 保留/未完了は一過性。パイプを整えてもう一度読む。
                    if (Ftd3Native.IsTransient(status))
                    {
                        Ftd3Native.FT_AbortPipe(_handle, BulkIn);
                        Ftd3Native.FT_SetStreamPipe(_handle, false, false, BulkIn, (uint)CaptureSize2D);
                        continue;
                    }
                    throw new Capture3DSException($"FT_ReadPipe failed: 0x{status:X}");
                }

                if (transferred < VideoSize2D)
                    continue; // 映像分に満たない切り詰めフレーム。捨てて読み直す(次は再整列する)。

                if (_expectedFrameSize == 0)
                {
                    // 接続後の最初の正常フレームで整列長を学習。
                    _expectedFrameSize = transferred;
                    return N3dsxlDecoder.DecodeRgb8_2D(_frameBuffer, (int)transferred);
                }

                if (transferred == _expectedFrameSize)
                    return N3dsxlDecoder.DecodeRgb8_2D(_frameBuffer, (int)transferred);

                // 整列長と不一致 = ミスアライン。捨てて読み直す。ただし同じ長さが連続するなら
                // デバイスが恒久的にフレーム長を変えた(3D 切替等)とみなして整列長を更新する。
                if (transferred == reseedSize && ++reseedCount >= 3)
                {
                    _expectedFrameSize = transferred;
                    return N3dsxlDecoder.DecodeRgb8_2D(_frameBuffer, (int)transferred);
                }
                if (transferred != reseedSize) { reseedSize = transferred; reseedCount = 1; }
            }
            // 16 回読んでも整列フレームが得られない = 信号喪失(本体電源OFF/映像なし)か
            // 一時的な整列乱れ。ここで例外を投げると上位ループがデバイスを破棄して再接続するが、
            // その再接続中の DrainData 読み(FT_ReadPipe)が本体電源ON(=ボードのその場リセット)の
            // 瞬間に割り込み不能なカーネル待ちでゾンビ化し、取り込みスレッドごと永久に固まっていた
            // (USB 抜き差ししか復帰手段が無くなる)。N3DSXL は USB 給電で動き続けるため、ハンドルを
            // 保持したまま null(=今回フレーム無し)を返せば、電源OFF→ON を跨いでも信号が戻り次第
            // そのまま取り込みが再開する(実測で復帰を確認)。本当の切断(ケーブル抜け)は
            // FT_ReadPipe が非一過性エラーを返す経路で別途例外になり、再接続で復帰する。
            return null;
        }

        /// <summary>
        /// 診断用: 1 回の FT_ReadPipe(CaptureSize2D)を行い、デコードせず転送長を返す。
        /// 一過性(0x20/0x21)はパイプを張り直して 0 を返す(その読みは無効)。
        /// 縦ずれの切り分け: 正常フレームは映像+音声=≒553472 で短パケット終端されるはず。
        /// 毎回 CaptureSize2D=555008 が返るなら、フレーム境界で区切れず連結読み=整列崩れ。
        /// </summary>
        public uint ReadRawTransferSize()
        {
            EnsureOpen();
            int status = Ftd3Native.FT_ReadPipe(_handle, BulkIn, _frameBuffer,
                (uint)CaptureSize2D, out uint transferred, IntPtr.Zero);
            if (Ftd3Native.Failed(status))
            {
                if (Ftd3Native.IsTransient(status))
                {
                    Ftd3Native.FT_AbortPipe(_handle, BulkIn);
                    Ftd3Native.FT_SetStreamPipe(_handle, false, false, BulkIn, (uint)CaptureSize2D);
                    return 0;
                }
                throw new Capture3DSException($"FT_ReadPipe failed: 0x{status:X}");
            }
            return transferred;
        }

        /// <summary>診断用の参照値(映像分/転送上限/error 手前の上限)。</summary>
        public static (int videoSize, int captureSize, int maxNonError) DiagnosticSizes()
            => (VideoSize2D, CaptureSize2D, CaptureSize2D - ErrorBuffer);

        /// <summary>
        /// 診断用: 1 回だけ FT_ReadPipe して転送長を out で返し、有効ならデコードして返す。
        /// 一過性(0x20/0x21)はパイプを張り直して transferred=0/null を返す。
        /// ReadFrame のリトライ無し版。連続ループでの転送長と縦ずれを対応づけるのに使う。
        /// </summary>
        public Capture3DSFrame ReadFrameDiagnostic(out uint transferred)
        {
            EnsureOpen();
            int status = Ftd3Native.FT_ReadPipe(_handle, BulkIn, _frameBuffer,
                (uint)CaptureSize2D, out transferred, IntPtr.Zero);
            if (Ftd3Native.Failed(status))
            {
                if (Ftd3Native.IsTransient(status))
                {
                    Ftd3Native.FT_AbortPipe(_handle, BulkIn);
                    Ftd3Native.FT_SetStreamPipe(_handle, false, false, BulkIn, (uint)CaptureSize2D);
                    transferred = 0;
                    return null;
                }
                throw new Capture3DSException($"FT_ReadPipe failed: 0x{status:X}");
            }
            if (transferred < VideoSize2D)
                return null;
            return N3dsxlDecoder.DecodeRgb8_2D(_frameBuffer, (int)transferred);
        }

        // ---- 接続シーケンス（cc3dsfs connect_ftd3 / ponkan n3dsxl.connect 準拠） ----

        private void DrainData()
        {
            // cc3dsfs drain_data 準拠: 開いたばかりのハンドルへ SPI アクセスを有効化し、
            // 残データを 1 回だけ(有限タイムアウトで)読み捨てる。
            // 注意: 以前は冒頭で両パイプを FT_AbortPipe していたが、開いた直後のハンドル
            // (特に前セッションがまだ完全に解放されていない再接続時)への abort が
            // タイムアウトの効かないカーネル呼び出しで永久ブロック=プロセスのゾンビ化を
            // 招いていたため撤去。0x20 等の一過性失敗は Connect 側のリトライで回復させる。
            SetSpiAccess(true, ignoreError: true);
            var buf = new byte[0x100000];
            uint transferred;
            Ftd3Native.FT_SetPipeTimeout(_handle, BulkIn, CfgWaitMs);
            Ftd3Native.FT_ReadPipe(_handle, BulkIn, buf, (uint)buf.Length, out transferred, IntPtr.Zero); // 結果は無視
            Thread.Sleep(CfgWaitMs);
        }

        private void Spi3dsCcStuff()
        {
            SetSpiAccess(true);
            Write(BulkOut, new byte[] { 0x80, 0x01, 0xAB, 0x00 });
            Write(BulkOut, new byte[] { 0x90, 0x08, 0x03, 0x02, 0x00, 0x00, 0x00, 0x00 });
            Read(BulkIn, 0x10);
            Write(BulkOut, new byte[] { 0x80, 0x01, 0xAB, 0x00 });
            SetSpiAccess(false);
        }

        private void Load3dsCcFirmware(byte firmwareId)
        {
            if (firmwareId >= 2) firmwareId = 1;
            SetSpiAccess(true);
            Write(BulkOut, new byte[] { (byte)(0x42 + firmwareId), 0x00, 0x00, 0x00 });
            Thread.Sleep(CfgWaitMs);
            SetSpiAccess(false);
        }

        private void Read3dsConfig3d()
        {
            SetSpiAccess(true);
            Write(BulkOut, new byte[] { 0x98, 0x05, 0x9F, 0x00 });
            Read(BulkIn, 0x10);
            SetSpiAccess(false);
        }

        private void SetStreamPipe2D()
        {
            SetStreamPipe(BulkIn, CaptureSize2D);
            AbortPipe(BulkIn);
            SetStreamPipe(BulkIn, CaptureSize2D);
        }

        private void SetSpiAccess(bool enable, bool ignoreError = false)
        {
            var buf = new byte[] { 0x40, (byte)(enable ? 0x80 : 0x00), 0x00, 0x00 };
            Write(BulkOut, buf, ignoreError);
        }

        // ---- 低レベルラッパ ----

        private void OpenHandle()
        {
            // 本体の電源OFF→ONやケーブル抜き差しでデバイスが再列挙されると、FTD3XX
            // ドライバ内部のデバイステーブルが古いまま残り、シリアル指定の FT_Create が
            // FT_DEVICE_NOT_OPENED(0x3)で失敗する。FT_Create の前にこれを呼んでテーブルを
            // 作り直すと、再列挙後の同一デバイスをそのまま開き直せる(=再接続が復帰する)。
            Ftd3Native.FT_CreateDeviceInfoList(out _);

            byte[] serial = Encoding.ASCII.GetBytes((Info.Serial ?? string.Empty) + "\0");
            IntPtr h;
            int status = Ftd3Native.FT_Create(serial, Ftd3Native.FT_OPEN_BY_SERIAL_NUMBER, out h);
            if (Ftd3Native.Failed(status) || h == IntPtr.Zero)
                throw new Capture3DSException($"FT_Create failed: 0x{status:X}");
            _handle = h;
        }

        private void CloseHandle()
        {
            if (_handle != IntPtr.Zero)
            {
                Ftd3Native.FT_Close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void EnsureOpen()
        {
            if (_handle == IntPtr.Zero)
                throw new Capture3DSException("device is not connected");
        }

        private void Write(byte pipe, byte[] data, bool ignoreError = false)
        {
            EnsureOpen();
            Ftd3Native.FT_SetPipeTimeout(_handle, pipe, CommandTimeoutMs);
            uint transferred;
            int status = Ftd3Native.FT_WritePipe(_handle, pipe, data, (uint)data.Length, out transferred, IntPtr.Zero);
            if (!ignoreError && (Ftd3Native.Failed(status) || transferred != data.Length))
            {
                PreemptiveClose();
                throw new Capture3DSException($"FT_WritePipe(0x{pipe:X}) failed: status=0x{status:X}, transferred={transferred}/{data.Length}");
            }
        }

        private byte[] Read(byte pipe, int length)
        {
            EnsureOpen();
            Ftd3Native.FT_SetPipeTimeout(_handle, pipe, CommandTimeoutMs);
            var buf = new byte[length];
            uint transferred = 0;
            int status = 0;
            // I/O 保留(FT_IO_PENDING 等)は一過性。数回リトライしてから諦める。
            for (int attempt = 0; attempt < ReadRetryAttempts; attempt++)
            {
                status = Ftd3Native.FT_ReadPipe(_handle, pipe, buf, (uint)length, out transferred, IntPtr.Zero);
                if (!Ftd3Native.Failed(status)) break;
                if (!Ftd3Native.IsTransient(status))
                {
                    PreemptiveClose();
                    throw new Capture3DSException($"FT_ReadPipe(0x{pipe:X}) failed: 0x{status:X}");
                }
                Ftd3Native.FT_AbortPipe(_handle, pipe);
                Thread.Sleep(ReadRetrySleepMs);
            }
            if (Ftd3Native.Failed(status))
            {
                PreemptiveClose();
                throw new Capture3DSException($"FT_ReadPipe(0x{pipe:X}) failed after retries: 0x{status:X}");
            }
            if (transferred != length)
            {
                var trimmed = new byte[transferred];
                Buffer.BlockCopy(buf, 0, trimmed, 0, (int)transferred);
                return trimmed;
            }
            return buf;
        }

        private void SetStreamPipe(byte pipe, int length)
        {
            int status = Ftd3Native.FT_SetStreamPipe(_handle, false, false, pipe, (uint)length);
            if (Ftd3Native.Failed(status))
            {
                PreemptiveClose();
                throw new Capture3DSException($"FT_SetStreamPipe(0x{pipe:X}) failed: 0x{status:X}");
            }
        }

        private void AbortPipe(byte pipe)
        {
            Ftd3Native.FT_AbortPipe(_handle, pipe); // 失敗は致命ではないので無視
        }

        private void PreemptiveClose()
        {
            if (_handle != IntPtr.Zero)
            {
                Ftd3Native.FT_AbortPipe(_handle, BulkIn);
                Ftd3Native.FT_Close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            // cc3dsfs end_connection 準拠で FT_Close のみ。以前は閉じる前に BulkIn を
            // FT_AbortPipe していたが、ストリーミング中のパイプへの abort がタイムアウトの
            // 効かないカーネル呼び出しでブロックすると、ハンドルが閉じずデバイスがビジーの
            // まま残り、次の接続が USB 抜き差し必須になっていた。FT_Close が内部で
            // 保留 I/O を片付けるので abort は不要。
            if (_handle != IntPtr.Zero)
            {
                Ftd3Native.FT_Close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private static string CString(byte[] ansi)
        {
            int len = Array.IndexOf(ansi, (byte)0);
            if (len < 0) len = ansi.Length;
            return Encoding.ASCII.GetString(ansi, 0, len);
        }
    }
}
