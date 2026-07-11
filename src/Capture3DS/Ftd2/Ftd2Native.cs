using System;
using System.Runtime.InteropServices;

namespace Capture3DS.Ftd2
{
    /// <summary>
    /// FTDI D2XX (ftd2xx.dll) のネイティブ関数バインディング。x64 専用。
    /// DS キャプボは FTDI FT232H を MPSSE モードで使い Lattice FPGA に
    /// ビットストリームを書き込み(SPI) → SYNC_FIFO モードへ切替えて映像を流す。
    /// 実行時に ftd2xx.dll を実行ファイルと同じフォルダ、または System32 で解決する。
    /// 呼び出し列は cc3dsfs (MIT) の DSCapture_FTD2 driver 経路に準拠。
    /// </summary>
    internal static class Ftd2Native
    {
        private const string DLL = "ftd2xx.dll";

        public const int FT_OK = 0;

        // OpenEx flags
        public const uint FT_OPEN_BY_SERIAL_NUMBER = 1;
        public const uint FT_OPEN_BY_DESCRIPTION = 2;

        // Device type (FT_GetDeviceInfoDetail の type)
        public const uint FT_DEVICE_232H = 8;

        // BitMode
        public const byte FT_BITMODE_RESET = 0x00;
        public const byte FT_BITMODE_MPSSE = 0x02;
        public const byte FT_BITMODE_SYNC_FIFO = 0x40;

        // Purge masks
        public const uint FT_PURGE_RX = 1;
        public const uint FT_PURGE_TX = 2;

        // Flow control
        public const ushort FT_FLOW_NONE = 0x0000;
        public const ushort FT_FLOW_RTS_CTS = 0x0100;

        // DeviceInfo flags
        public const uint FT_FLAGS_HISPEED = 0x00000002;

        public static bool Failed(int status) => status != FT_OK;

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_CreateDeviceInfoList(out uint numDevs);

        // SerialNumber: 16 bytes, Description: 64 bytes (ANSI)
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern int FT_GetDeviceInfoDetail(
            uint index, out uint flags, out uint type, out uint id, out uint locId,
            byte[] serialNumber, byte[] description, out IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern int FT_OpenEx(byte[] arg, uint flags, out IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_Close(IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_ResetDevice(IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_Purge(IntPtr ftHandle, uint mask);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetBitMode(IntPtr ftHandle, byte mask, byte mode);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetLatencyTimer(IntPtr ftHandle, byte timer);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetUSBParameters(IntPtr ftHandle, uint inTransferSize, uint outTransferSize);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetTimeouts(IntPtr ftHandle, uint readTimeoutMs, uint writeTimeoutMs);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_GetQueueStatus(IntPtr ftHandle, out uint rxBytes);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_Read(IntPtr ftHandle, byte[] buffer, uint bytesToRead, out uint bytesReturned);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_Write(IntPtr ftHandle, byte[] buffer, uint bytesToWrite, out uint bytesWritten);

        // FT_Write で配列途中(offset)から送るための I4 オーバーロード(MPSSE/SPI のチャンク送出に使用)。
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, EntryPoint = "FT_Write")]
        public static extern int FT_WriteOffset(IntPtr ftHandle, ref byte buffer, uint bytesToWrite, out uint bytesWritten);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetChars(IntPtr ftHandle, byte eventChar, byte eventCharEnabled, byte errorChar, byte errorCharEnabled);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetFlowControl(IntPtr ftHandle, ushort flowControl, byte xonChar, byte xoffChar);

        // EEPROM ワード読み出し(word アドレス指定)。接続確認に EEPROM[1]==0x0403 を使う。
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_ReadEE(IntPtr ftHandle, uint wordOffset, out ushort value);
    }
}
