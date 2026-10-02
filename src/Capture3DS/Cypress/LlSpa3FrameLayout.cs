namespace Capture3DS.Cypress
{
    public enum LlSpa3ColorMode
    {
        // 16-bit colour, about two thirds of the USB bandwidth of Rgb888.
        Rgb565,
        Rgb888
    }

    // Byte layout of one 2D frame. Every column is a 16-byte header (magic,
    // column info, audio samples) followed by 240 heights of interleaved
    // { bottom pixel, top pixel }. A normal frame ends with a header-less
    // bottom-only column; an extra-header frame carries a 401st column instead.
    internal sealed class LlSpa3FrameLayout
    {
        internal static readonly LlSpa3FrameLayout Rgb888 = new LlSpa3FrameLayout(true, 3);
        internal static readonly LlSpa3FrameLayout Rgb565 = new LlSpa3FrameLayout(false, 2);

        private LlSpa3FrameLayout(bool isRgb888, int bytesPerPixel)
        {
            IsRgb888 = isRgb888;
            PixelBytesPerColumn = LlSpa3Decoder.Height * 2 * bytesPerPixel;
            ColumnStride = LlSpa3Decoder.ColumnHeaderSize + PixelBytesPerColumn;
            BottomOnlyOffset = LlSpa3Decoder.NumColumns * ColumnStride;
            FrameSize = BottomOnlyOffset + PixelBytesPerColumn;
            ExtraHeaderFrameSize = (LlSpa3Decoder.NumColumns + 1) * ColumnStride;
        }

        internal bool IsRgb888 { get; }

        internal int PixelBytesPerColumn { get; }

        internal int ColumnStride { get; }

        internal int BottomOnlyOffset { get; }

        internal int FrameSize { get; }

        internal int ExtraHeaderFrameSize { get; }
    }
}
