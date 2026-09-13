using System;
using System.Runtime.InteropServices;

namespace Capture3DS.Ftd3
{
    /// <summary>
    /// FTDI D3XX (FTD3XX.dll) のネイティブ関数バインディング。x64 専用。
    /// 実行時に FTD3XX.dll を解決する(実行ファイルと同じフォルダに配置)。
    /// 呼び出し列は cc3dsfs (MIT) の 3dscapture_ftd3 ドライバ経路に準拠。
    /// </summary>
    internal static class Ftd3Native
    {
        private const string DLL = "FTD3XX.dll";

        public const int FT_OK = 0;
        public const uint FT_OPEN_BY_SERIAL_NUMBER = 1;
        public const uint FT_FLAGS_SUPERSPEED = 0x00000004;

        // FTDI AN_379 / FTD3XX.h defines these as decimal 24/25 (0x18/0x19).
        // Decimal 32 (0x20) is FT_OTHER_ERROR, not an incomplete operation.
        // Keep the existing bounded recovery policy, but classify the real codes.
        public const int FT_IO_PENDING = 24;
        public const int FT_IO_INCOMPLETE = 25;

        // Win32 OVERLAPPED has pointer-sized Internal/InternalHigh and hEvent.
        // Its address, and the read buffer, must remain stable until completion.
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal IntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }

        public static bool Failed(int status) => status != FT_OK;

        /// <summary>リトライすれば回復し得る一過性ステータス(I/O 保留/未完了)。</summary>
        public static bool IsTransient(int status) =>
            status == FT_IO_PENDING || status == FT_IO_INCOMPLETE;

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_CreateDeviceInfoList(out uint numDevs);

        // SerialNumber: 16+1, Description: 64+1 (ANSI)
        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern int FT_GetDeviceInfoDetail(
            uint index, out uint flags, out uint type, out uint id, IntPtr locId,
            byte[] serialNumber, byte[] description, out IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        public static extern int FT_Create(byte[] arg, uint flags, out IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_Close(IntPtr ftHandle);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_AbortPipe(IntPtr ftHandle, byte pipe);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetStreamPipe(IntPtr ftHandle, [MarshalAs(UnmanagedType.U1)] bool allWrite,
            [MarshalAs(UnmanagedType.U1)] bool allRead, byte pipe, uint streamSize);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_SetPipeTimeout(IntPtr ftHandle, byte pipe, uint timeoutMs);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_GetPipeTimeout(IntPtr ftHandle, byte pipe, out uint timeoutMs);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_WritePipe(IntPtr ftHandle, byte pipe, byte[] buffer, uint bufferLength,
            out uint bytesTransferred, IntPtr overlapped);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_ReadPipe(IntPtr ftHandle, byte pipe, byte[] buffer, uint bufferLength,
            out uint bytesTransferred, IntPtr overlapped);

        // The byte[] overload above is synchronous only. An asynchronous call
        // cannot retain a marshaller's temporary buffer or stack out parameter.
        [DllImport(DLL, EntryPoint = "FT_ReadPipe", CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_ReadPipeAsync(IntPtr ftHandle, byte pipe, IntPtr buffer, uint bufferLength,
            IntPtr bytesTransferred, IntPtr overlapped);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_InitializeOverlapped(IntPtr ftHandle, IntPtr overlapped);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_GetOverlappedResult(IntPtr ftHandle, IntPtr overlapped,
            out uint bytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport(DLL, CallingConvention = CallingConvention.StdCall)]
        public static extern int FT_ReleaseOverlapped(IntPtr ftHandle, IntPtr overlapped);
    }
}
