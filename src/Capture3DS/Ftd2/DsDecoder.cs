using System;

namespace Capture3DS.Ftd2
{
    /// <summary>
    /// DS キャプボ(FTD2)の生フレームを上下画面 RGB8 へデコードする。
    /// cc3dsfs (MIT) DSCapture_FTD2 準拠。上下とも 256x192、RGB565。
    ///
    /// 生バッファの画素並び(cc3dsfs conversions.cpp):
    ///   映像領域は u16(RGB565,LE) が連続し、偶数番目=下画面 / 奇数番目=上画面。
    ///   1 ペア = (下画素, 上画素)。各画面は 256x192 を行優先で埋める。
    ///   ★色順(R/G/B のビット配置)は cc3dsfs の "reversed" コメント箇所で、
    ///     実機での確認が必要(R=bit0-4, G=bit5-10, B=bit11-15 を第一候補とする)。
    /// </summary>
    public static class DsDecoder
    {
        public const int Width = 256;
        public const int Height = 192;
        public const int ScreenPixels = Width * Height;           // 49152
        public const int VideoSize = ScreenPixels * 2 * 2;        // 196608 (2画面 * u16)

        /// <summary>映像領域(先頭 VideoSize バイト)を上下画面 RGB8 にデコードする。</summary>
        /// <param name="raw">生バッファ。<paramref name="videoOffset"/> から少なくとも VideoSize バイト必要。</param>
        /// <param name="videoOffset">先頭の同期値(0x4321)を読み飛ばしたあとの映像開始位置。</param>
        public static Capture3DSFrame DecodeRgb8(byte[] raw, int videoOffset)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (videoOffset < 0 || videoOffset + VideoSize > raw.Length)
                throw new Capture3DSException(
                    $"DS フレームが短すぎます: offset={videoOffset}, need={VideoSize}, have={raw.Length}");

            var top = new byte[ScreenPixels * 3];
            var bottom = new byte[ScreenPixels * 3];

            int src = videoOffset;
            int ot = 0, ob = 0;
            for (int i = 0; i < ScreenPixels; i++)
            {
                // 偶数 u16 = 下画面、奇数 u16 = 上画面。
                ushort bv = (ushort)(raw[src] | (raw[src + 1] << 8));
                ushort tv = (ushort)(raw[src + 2] | (raw[src + 3] << 8));
                src += 4;

                Rgb565To888(tv, top, ref ot);
                Rgb565To888(bv, bottom, ref ob);
            }

            return new Capture3DSFrame(top, Width, Height, bottom, Width, Height);
        }

        /// <summary>標準 RGB565 (R=bit11-15, G=bit5-10, B=bit0-4) を RGB8 へ展開。
        /// ★実機(2026-06-28)で reversed(R/B入替)だと色が反転したため標準並びに確定。</summary>
        private static void Rgb565To888(ushort v, byte[] dst, ref int off)
        {
            int b5 = v & 0x1F;
            int g6 = (v >> 5) & 0x3F;
            int r5 = (v >> 11) & 0x1F;
            dst[off++] = (byte)((r5 << 3) | (r5 >> 2));
            dst[off++] = (byte)((g6 << 2) | (g6 >> 4));
            dst[off++] = (byte)((b5 << 3) | (b5 >> 2));
        }
    }
}
