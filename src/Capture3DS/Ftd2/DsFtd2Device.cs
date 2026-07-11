using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Capture3DS.Ftd2
{
    /// <summary>
    /// DS キャプボ (FTDI FT232H / D2XX) の接続・読み出し。cc3dsfs (MIT) DSCapture_FTD2 driver 経路の移植。
    ///
    /// 接続手順:
    ///   1) FT_OpenEx(serial) でオープン
    ///   2) MPSSE 初期化(クロック 6MHz 設定)
    ///   3) Lattice FPGA へビットストリーム(埋め込み ftd2_ds2_fw_N.bin)を SPI 転送
    ///   4) EEPROM[1]==0x0403 を確認
    ///   5) SYNC_FIFO モードへ切替 → enable_capture {0x80,0x01}
    ///   6) 1 フレーム=198900 バイトを読み、同期値 0x4321 で整列して上下画面へデコード
    ///
    /// ★実機未検証。FTDI Description は "NDS.1"/"NDS.2"(=ファーム 1/2)を想定。色順は要確認。
    /// ftd2xx.dll(x64) が必要(native\ にはまだ未配置)。
    /// </summary>
    public sealed class DsFtd2Device : ICapture3DSDevice
    {
        // FTDI Description → ファーム番号(cc3dsfs shared.cpp: valid_descriptions / descriptions_firmware_ids)
        private static readonly string[] ValidDescriptions = { "NDS.1", "NDS.2" };
        private static readonly int[] FirmwareIds = { 1, 2 };

        // MPSSE クロック分周(0x86 引数): CLKDIV=(30/6)-1=4 → SCK 6MHz
        private const byte ClkDivLo = 0x04;
        private const byte ClkDivHi = 0x00;

        // USB 転送サイズ
        private const uint UsbSizeDefault = 0x10000;   // 64KB
        private const uint UsbSizeLarge = 0x40000;     // 256KB(IN 用に試行、失敗時は default)
        private const int SpiChunkMax = 0x10000;       // TX_SPI_SIZE
        private const int SpiTxOffset = 3;             // 0x11, lenLo, lenHi

        private const ushort SynchValue = 0x4321;      // FTD2_OLDDS_SYNCH_VALUES

        // 1 フレーム読み出しサイズ(get_capture_size)= 198900。映像 196608 + 音声 2192 + 同期パディング。
        private const int FullSize = 198900;
        private const int VideoSize = DsDecoder.VideoSize; // 196608

        private IntPtr _handle = IntPtr.Zero;
        private readonly int _firmwareId;
        private byte[] _bufA = new byte[FullSize];
        private byte[] _bufB = new byte[FullSize];

        public Capture3DSDeviceInfo Info { get; }

        private DsFtd2Device(Capture3DSDeviceInfo info, int firmwareId)
        {
            Info = info;
            _firmwareId = firmwareId;
        }

        /// <summary>接続中の DS(FT232H)キャプボを列挙する。</summary>
        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var result = new List<Capture3DSDeviceInfo>();
            uint numDevs;
            if (Ftd2Native.Failed(Ftd2Native.FT_CreateDeviceInfoList(out numDevs)) || numDevs == 0)
                return result;

            for (uint i = 0; i < numDevs; i++)
            {
                var serialBuf = new byte[16];
                var descBuf = new byte[64];
                uint flags, type, id, locId;
                IntPtr h;
                if (Ftd2Native.Failed(Ftd2Native.FT_GetDeviceInfoDetail(
                        i, out flags, out type, out id, out locId, serialBuf, descBuf, out h)))
                    continue;

                bool hiSpeed = (flags & Ftd2Native.FT_FLAGS_HISPEED) != 0;
                if (!hiSpeed || type != Ftd2Native.FT_DEVICE_232H)
                    continue;

                string desc = CString(descBuf);
                int fwIndex = Array.IndexOf(ValidDescriptions, desc);
                if (fwIndex < 0)
                    continue;

                string serial = CString(serialBuf);
                result.Add(new Capture3DSDeviceInfo(Capture3DSModel.DsFtd2, serial, desc, hiSpeed));
            }
            return result;
        }

        public static DsFtd2Device Open(Capture3DSDeviceInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Model != Capture3DSModel.DsFtd2)
                throw new Capture3DSException("not a DS (FTD2) device");
            int fwIndex = Array.IndexOf(ValidDescriptions, info.Description);
            int fwId = fwIndex >= 0 ? FirmwareIds[fwIndex] : 1;
            return new DsFtd2Device(info, fwId);
        }

        public void Connect()
        {
            OpenHandle();
            InitMpsse();
            FpgaConfig(LoadFirmwareBlob(_firmwareId));

            ushort ee;
            Check(Ftd2Native.FT_ReadEE(_handle, 1, out ee), "FT_ReadEE(1)");
            if (ee != 0x0403)
            {
                PreemptiveClose();
                throw new Capture3DSException($"EEPROM 確認失敗: EEPROM[1]=0x{ee:X4} (期待 0x0403)");
            }

            // SYNC_FIFO へ切替えて映像ストリームを受ける。
            Check(Ftd2Native.FT_SetBitMode(_handle, 0x00, Ftd2Native.FT_BITMODE_SYNC_FIFO), "FT_SetBitMode(syncfifo)");
            Check(Ftd2Native.FT_SetTimeouts(_handle, 50, 50), "FT_SetTimeouts(50)");
            EnsureQueueEmpty();

            // enable_capture
            WriteAll(new byte[] { 0x80, 0x01 }, "enable_capture");
        }

        public Capture3DSFrame ReadFrame()
        {
            EnsureOpen();

            // 同期整列: FullSize 読む → synchronization_check。out-of-sync なら末尾を次バッファ先頭へ
            // 退避し不足分だけ追加で読んで境界に合わせる(cc3dsfs driver_acq の移植, special_check=true)。
            byte[] buf = _bufA;
            byte[] next = _bufB;
            int needOffset = 0; // buf 先頭からの既存有効バイト(=FullSize-nextSize)
            int nextSize = FullSize;

            for (int guard = 0; guard < 64; guard++)
            {
                uint transferred = ReadInto(buf, needOffset, nextSize);
                if (transferred < nextSize)
                {
                    // ショートリード: 読めた分だけ前進して残りを読み足す。
                    needOffset += (int)transferred;
                    nextSize -= (int)transferred;
                    continue;
                }
                needOffset = 0;
                nextSize = FullSize;

                int newNextSize;
                if (SynchronizationCheck(buf, FullSize, next, out newNextSize))
                {
                    int realLen = RemoveSynchFromFinalLength(buf, FullSize);
                    int initialOffset = InitialSynchOffset(buf, realLen);
                    return DsDecoder.DecodeRgb8(buf, initialOffset);
                }

                // 再整列: next の先頭に退避済み。buf/next を入替え、不足分(newNextSize)だけ読み足す。
                var tmp = buf; buf = next; next = tmp;
                needOffset = FullSize - newNextSize;
                nextSize = newNextSize;
            }
            throw new Capture3DSException("DS フレーム同期に失敗しました(再整列が収束せず)。");
        }

        // ---- 接続シーケンス ----

        private void InitMpsse()
        {
            Check(Ftd2Native.FT_ResetDevice(_handle), "FT_ResetDevice");
            Check(Ftd2Native.FT_SetUSBParameters(_handle, UsbSizeDefault, UsbSizeDefault), "FT_SetUSBParameters");
            Check(Ftd2Native.FT_SetChars(_handle, 0, 0, 0, 0), "FT_SetChars");
            Check(Ftd2Native.FT_SetTimeouts(_handle, 300, 300), "FT_SetTimeouts(300)");
            Check(Ftd2Native.FT_SetLatencyTimer(_handle, 3), "FT_SetLatencyTimer(3)");
            Check(Ftd2Native.FT_SetFlowControl(_handle, Ftd2Native.FT_FLOW_RTS_CTS, 0, 0), "FT_SetFlowControl");
            Check(Ftd2Native.FT_SetBitMode(_handle, 0x00, Ftd2Native.FT_BITMODE_RESET), "FT_SetBitMode(reset)");
            Check(Ftd2Native.FT_SetBitMode(_handle, 0x00, Ftd2Native.FT_BITMODE_MPSSE), "FT_SetBitMode(mpsse)");

            // IN 転送サイズを拡張(失敗時は既定へフォールバック)。
            if (Ftd2Native.Failed(Ftd2Native.FT_SetUSBParameters(_handle, UsbSizeLarge, UsbSizeDefault)))
                Check(Ftd2Native.FT_SetUSBParameters(_handle, UsbSizeDefault, UsbSizeDefault), "FT_SetUSBParameters(fallback)");

            // MPSSE 設定: ループバック/3相/分周5/適応クロック OFF + クロック分周設定。
            byte[] cmd = { 0x85, 0x8D, 0x8A, 0x97, 0x86, ClkDivLo, ClkDivHi };
            // ウォームアップ(cc3dsfs): 先頭 1 バイトだけ書く→sleep→Purge→全書き。
            WriteAll(SubBuffer(cmd, 0), 1, "mpsse-warmup");
            Thread.Sleep(10);
            Check(Ftd2Native.FT_Purge(_handle, Ftd2Native.FT_PURGE_RX | Ftd2Native.FT_PURGE_TX), "FT_Purge");
            FullWrite(cmd, "mpsse-setup");
        }

        private void FpgaConfig(byte[] bitstream)
        {
            Check(Ftd2Native.FT_SetTimeouts(_handle, 300, 300), "FT_SetTimeouts(fpga)");

            // RESETDELAY=0 → 0x8F,0x00,0x00 / INITDELAY=900 → 0x8F,0x84,0x03 / LE16(1)=0x01,0x00
            byte[] cmd0 = {
                0x82, 0x7F, 0x80,   // C ピン: reset(C7)=0, dir out
                0x80, 0xF0, 0x0B,   // D ピン: SS=0, clk idle low, TDI/TCK out
                0x8F, 0x00, 0x00,   // reset パルス
                0x82, 0xFF, 0x00,   // reset 解除
                0x8F, 0x84, 0x03,   // init delay
                0x80, 0xF8, 0x0B,   // SS=1
                0x8F, 0x01, 0x00,   // 16 ダミークロック
                0x80, 0xF0, 0x0B,   // SS=0
            };
            FullWrite(cmd0, "fpga-cmd0");
            CheckCdone(false, "config 前(CDONE=0)");

            SpiTx(bitstream);

            byte[] cmd1 = {
                0x80, 0xF8, 0x0B,   // SS=1
                0x8F, 0x14, 0x00,   // >150 ダミークロック (=(20+1)*8)
                0x80, 0x00, 0x00,   // D ピン float
                0x82, 0x00, 0x00,   // C ピン float
            };
            FullWrite(cmd1, "fpga-cmd1");
            CheckCdone(true, "config 後(CDONE=1)");
        }

        /// <summary>ビットストリームを MPSSE SPI(クロックアウト, MSB first)で送る。</summary>
        private void SpiTx(byte[] data)
        {
            int pos = 0;
            while (pos < data.Length)
            {
                int len = Math.Min(SpiChunkMax, data.Length - pos);
                var chunk = new byte[SpiTxOffset + len];
                chunk[0] = 0x11;                       // clock data out on +ve edge, MSB first
                chunk[1] = (byte)((len - 1) & 0xFF);
                chunk[2] = (byte)(((len - 1) >> 8) & 0xFF);
                Array.Copy(data, pos, chunk, SpiTxOffset, len);
                FullWrite(chunk, "fpga-spi");
                pos += len;
            }
        }

        /// <summary>GPIO を読み CDONE(D6=0x40)を検査。want=true で 1, false で 0 を期待。</summary>
        private void CheckCdone(bool want, string what)
        {
            WriteAll(new byte[] { 0x81, 0x83 }, "read-gpio"); // D 低位 / C 高位 読み
            var buf = new byte[2];
            uint got;
            Check(Ftd2Native.FT_Read(_handle, buf, 2, out got), "FT_Read(cdone)");
            if (got < 2 || buf[0] == 0xFA)
            {
                PreemptiveClose();
                throw new Capture3DSException($"CDONE 読み出し失敗({what}): got={got}, b0=0x{buf[0]:X2}");
            }
            bool cdone = (buf[0] & 0x40) != 0;
            if (cdone != want)
            {
                PreemptiveClose();
                throw new Capture3DSException($"CDONE 不一致({what}): {(cdone ? 1 : 0)}");
            }
        }

        // ---- 同期(0x4321)処理: cc3dsfs shared.cpp 移植 ----

        private static ushort Word(byte[] b, int byteIndex)
            => (ushort)(b[byteIndex] | (b[byteIndex + 1] << 8));

        /// <summary>整列判定。true=このバッファは正しく整列。false 時は末尾を next 先頭へ退避し nextSize を返す。</summary>
        private static bool SynchronizationCheck(byte[] data, int sizeBytes, byte[] next, out int nextSize)
        {
            int words = sizeBytes / 2;
            nextSize = sizeBytes;
            ushort first = Word(data, 0);
            ushort last = Word(data, (words - 1) * 2);

            // 正常: 先頭が同期値でない かつ 末尾が同期値(実データの後ろに同期パディング)。
            if (first != SynchValue && last == SynchValue) return true;
            // special_check(driver 経路): 先頭・末尾とも同期値でも整列とみなす。
            if (first == SynchValue && last == SynchValue) return true;

            // out-of-sync: 最初の同期値(=実データ終端)を探し、その同期ランの後ろ=次フレーム先頭まで進める。
            int samples = 0;
            while (samples < words && Word(data, samples * 2) != SynchValue) samples++;
            while (samples < words && Word(data, samples * 2) == SynchValue) samples++;

            // 末尾 [samples..words) を next の先頭へ退避。次回はこの prefix 分だけ読み足す。
            int tailWords = words - samples;
            Array.Copy(data, samples * 2, next, 0, tailWords * 2);
            nextSize = samples * 2;
            return false;
        }

        /// <summary>末尾の同期パディング(0x43214321 / 0x4321)を取り除いた実バイト長。</summary>
        private static int RemoveSynchFromFinalLength(byte[] buf, int length)
        {
            while (length >= 4 && Word(buf, length - 4) == SynchValue && Word(buf, length - 2) == SynchValue)
                length -= 4;
            while (length >= 2 && Word(buf, length - 2) == SynchValue)
                length -= 2;
            if (length < 2 && Word(buf, 0) == SynchValue)
                length = 0;
            return length;
        }

        /// <summary>先頭の同期値(実データは先頭に SYNCH が付く)を読み飛ばした映像開始位置。</summary>
        private static int InitialSynchOffset(byte[] buf, int realLen)
        {
            int off = 0;
            while (off + 2 <= realLen && Word(buf, off) == SynchValue) off += 2;
            return off;
        }

        // ---- 低レベルラッパ ----

        private void OpenHandle()
        {
            byte[] serial = Encoding.ASCII.GetBytes((Info.Serial ?? string.Empty) + "\0");
            IntPtr h;
            int status = Ftd2Native.FT_OpenEx(serial, Ftd2Native.FT_OPEN_BY_SERIAL_NUMBER, out h);
            if (Ftd2Native.Failed(status) || h == IntPtr.Zero)
                throw new Capture3DSException($"FT_OpenEx failed: 0x{status:X}");
            _handle = h;
        }

        /// <summary>読み出しキューが空であることを確認(cc3dsfs pass_if_FT_queue_empty)。</summary>
        private void EnsureQueueEmpty()
        {
            uint rx;
            Check(Ftd2Native.FT_GetQueueStatus(_handle, out rx), "FT_GetQueueStatus");
            if (rx != 0)
            {
                PreemptiveClose();
                throw new Capture3DSException($"読み出しキューが空ではありません: {rx} バイト残留");
            }
        }

        /// <summary>キュー空確認 → 全バイト書き込み(cc3dsfs full_ftd2_write)。</summary>
        private void FullWrite(byte[] data, string what)
        {
            EnsureQueueEmpty();
            WriteAll(data, what);
        }

        private void WriteAll(byte[] data, string what) => WriteAll(data, data.Length, what);

        private void WriteAll(byte[] data, int count, string what)
        {
            uint written;
            int rc = Ftd2Native.FT_Write(_handle, data, (uint)count, out written);
            if (Ftd2Native.Failed(rc) || written != count)
            {
                PreemptiveClose();
                throw new Capture3DSException($"{what}: FT_Write 失敗 rc=0x{rc:X} sent={written}/{count}");
            }
        }

        private static byte[] SubBuffer(byte[] src, int offset)
        {
            // FT_Write は配列先頭からしか扱えないため offset>0 の場合は切り出す。
            if (offset == 0) return src;
            var s = new byte[src.Length - offset];
            Array.Copy(src, offset, s, 0, s.Length);
            return s;
        }

        /// <summary>buf の offset 位置へ count バイト読み込む(FT_Read は先頭からのみのため必要なら一時経由)。</summary>
        private uint ReadInto(byte[] buf, int offset, int count)
        {
            uint transferred;
            if (offset == 0)
            {
                int rc0 = Ftd2Native.FT_Read(_handle, buf, (uint)count, out transferred);
                if (Ftd2Native.Failed(rc0))
                    throw new Capture3DSException($"FT_Read failed: 0x{rc0:X}");
                return transferred;
            }
            var tmp = new byte[count];
            int rc = Ftd2Native.FT_Read(_handle, tmp, (uint)count, out transferred);
            if (Ftd2Native.Failed(rc))
                throw new Capture3DSException($"FT_Read failed: 0x{rc:X}");
            Array.Copy(tmp, 0, buf, offset, (int)transferred);
            return transferred;
        }

        private static byte[] LoadFirmwareBlob(int firmwareId)
        {
            string name = $"Capture3DS.ftd2_ds2_fw_{firmwareId}.bin";
            var asm = typeof(DsFtd2Device).GetTypeInfo().Assembly;
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null)
                    throw new Capture3DSException($"埋め込みファーム {name} が見つかりません。");
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private void EnsureOpen()
        {
            if (_handle == IntPtr.Zero)
                throw new Capture3DSException("device is not connected");
        }

        private void Check(int status, string what)
        {
            if (Ftd2Native.Failed(status))
            {
                PreemptiveClose();
                throw new Capture3DSException($"{what} failed: 0x{status:X}");
            }
        }

        private void PreemptiveClose()
        {
            if (_handle != IntPtr.Zero)
            {
                Ftd2Native.FT_ResetDevice(_handle);
                Ftd2Native.FT_Close(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public void Dispose() => PreemptiveClose();

        private static string CString(byte[] ansi)
        {
            int len = Array.IndexOf(ansi, (byte)0);
            if (len < 0) len = ansi.Length;
            return Encoding.ASCII.GetString(ansi, 0, len);
        }
    }
}
