using System;

namespace Capture3DS.Loopy
{
    /// <summary>
    /// Loopy old-3DS RGB888 frame decoder. The USB frame stores the top
    /// screen's 400 columns first and the bottom screen's 320 columns second;
    /// every column contains 240 pixels in the opposite vertical direction.
    /// </summary>
    public static class LoopyOld3dsDecoder
    {
        public const int TopWidth = 400;
        public const int BottomWidth = 320;
        public const int Height = 240;
        public const int VideoSize = (TopWidth + BottomWidth) * Height * 3;

        public static Capture3DSFrame DecodeRgb8(byte[] raw, int rawLength)
        {
            if (raw == null) throw new ArgumentNullException(nameof(raw));
            if (rawLength < VideoSize)
            {
                throw new Capture3DSException(
                    $"Loopy old 3DS raw video too short: {rawLength} < {VideoSize}");
            }

            var top = new byte[TopWidth * Height * 3];
            var bottom = new byte[BottomWidth * Height * 3];

            DecodeScreen(raw, 0, top, TopWidth);
            DecodeScreen(raw, TopWidth * Height * 3, bottom, BottomWidth);

            return new Capture3DSFrame(top, TopWidth, Height, bottom, BottomWidth, Height);
        }

        private static void DecodeScreen(byte[] raw, int rawOffset, byte[] output, int width)
        {
            for (var x = 0; x < width; x++)
            {
                var columnOffset = rawOffset + (x * Height * 3);
                for (var y = 0; y < Height; y++)
                {
                    var source = columnOffset + ((Height - 1 - y) * 3);
                    var destination = ((y * width) + x) * 3;
                    output[destination] = raw[source];
                    output[destination + 1] = raw[source + 1];
                    output[destination + 2] = raw[source + 2];
                }
            }
        }
    }
}
