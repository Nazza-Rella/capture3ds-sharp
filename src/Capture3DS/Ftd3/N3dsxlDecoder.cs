using System;

namespace Capture3DS.Ftd3
{
    /// <summary>
    /// N3DSXL の 2D RGB8 生ビデオ領域を上下画面へデコードする。
    /// ponkan-python (MIT) の protocol/layout_3ds.decode_rgb8_2d を移植。
    ///
    /// 生データは「積み上げ幅 720 x 高さ 240」の列優先配置で、
    ///  - 列 0..79      : 上画面のみ
    ///  - 列 80 以降    : 下/上 が 1 列おきに交互(下=偶数側, 上=奇数側)
    /// として格納されている。各画面を rot90(反時計回り)して通常の画像向きにする。
    /// </summary>
    public static class N3dsxlDecoder
    {
        public const int TopWidth = 400;
        public const int BottomWidth = 320;
        public const int Height = 240;
        public const int StackedWidth = TopWidth + BottomWidth; // 720
        public const int VideoSize2D = StackedWidth * Height * 3; // 518400

        /// <summary>
        /// 生ビデオ(先頭 VideoSize2D バイト)を上下画面 RGB8 にデコードする。
        /// raw は VideoSize2D 以上の長さが必要(余剰は無視)。
        /// </summary>
        public static Capture3DSFrame DecodeRgb8_2D(byte[] raw, int rawLength)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (rawLength < VideoSize2D)
                throw new Capture3DSException($"raw video too short: {rawLength} < {VideoSize2D}");

            byte[] top = new byte[TopWidth * Height * 3];
            byte[] bottom = new byte[BottomWidth * Height * 3];
            const int widthDelta = TopWidth - BottomWidth; // 80

            // rot90(k=1): out[i][j] = column[j][H-1-i]
            for (int j = 0; j < TopWidth; j++)
            {
                int srcCol = (j < widthDelta) ? j : (widthDelta + 1) + ((j - widthDelta) * 2);
                int colBase = srcCol * Height * 3;
                for (int i = 0; i < Height; i++)
                {
                    int s = colBase + (Height - 1 - i) * 3;
                    int d = (i * TopWidth + j) * 3;
                    top[d] = raw[s];
                    top[d + 1] = raw[s + 1];
                    top[d + 2] = raw[s + 2];
                }
            }

            for (int j = 0; j < BottomWidth; j++)
            {
                int srcCol = widthDelta + (j * 2);
                int colBase = srcCol * Height * 3;
                for (int i = 0; i < Height; i++)
                {
                    int s = colBase + (Height - 1 - i) * 3;
                    int d = (i * BottomWidth + j) * 3;
                    bottom[d] = raw[s];
                    bottom[d + 1] = raw[s + 1];
                    bottom[d + 2] = raw[s + 2];
                }
            }

            return new Capture3DSFrame(top, TopWidth, Height, bottom, BottomWidth, Height);
        }
    }
}
