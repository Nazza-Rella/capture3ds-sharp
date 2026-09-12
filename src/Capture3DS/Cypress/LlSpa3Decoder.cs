namespace Capture3DS.Cypress
{
    // Decoder for the cc3dsfs "Optimize Old 3DS" RGB888 2D frame format.
    //
    // Frame layout (capture_structs.hpp / conversions.cpp, verified):
    //   columns_data[400], each column = 16B header + pixel[240][2]*3B = 1456B
    //   bottom_only_column = pixel[240][2]*3B = 1440B (no header)
    //   total = 400*1456 + 1440 = 583840 bytes
    //
    // Each column's 1440B pixel block is 60 interleaved groups of 24 bytes.
    // A group holds pixels[6][2] uint16: plane 0 = bottom, plane 1 = top.
    // Gathering one plane yields 12 bytes = 4 RGB888 pixels covering heights
    // i*4..i*4+3 of the column.
    //
    // Column->screen mapping (is_n3ds=false, non-special header):
    //   TOP  col j (0..399)  <- columns_data[j] plane1
    //   BOT  col 0..317      <- columns_data[82..399] plane0
    //   BOT  col 318         <- columns_data[80] plane0
    //   BOT  col 319         <- bottom_only_column plane0
    //
    // When bit 15 (has_extra_header_data_2d_only) of the column-0 column_info is
    // set, the frame is USB8883DSOptimizeCaptureReceivedExtraHeader instead:
    // columns_data[401] (583856 bytes), the trailing header-less block is gone and
    // the bottom split shifts by one column (conversions.cpp
    // usb_3DS888OptimizeconvertVideoToOutputLineDirectOpt):
    //   TOP  col j (0..399)  <- columns_data[j] plane1
    //   BOT  col 0..318      <- columns_data[81..399] plane0
    //   BOT  col 319         <- columns_data[78] plane0
    //   columns_data[400] is not part of the picture and is discarded.
    //
    // cc3dsfs stores screen_data column-major with base_rotation=90; the upright
    // image is buffer[col*240 + (239-h)], so output row = 239 - h.
    internal static class LlSpa3Decoder
    {
        public const int Height = 240;
        public const int TopWidth = 400;
        public const int BottomWidth = 320;

        public const int ColumnStride = 1456;
        private const int ColumnHeaderSize = 16;
        public const int NumColumns = 400;
        private const int BottomOnlyOffset = NumColumns * ColumnStride; // 582400
        public const int FrameSize = BottomOnlyOffset + Height * 2 * 3; // 583840
        public const int ExtraHeaderFrameSize = (NumColumns + 1) * ColumnStride; // 583856

        // Old 3DS column->screen split constants (conversions.cpp lines 642-644).
        private const int ColumnPreLastBotPos = 80;  // (40*2)
        private const int ColumnStartBotPos = 82;     // (40*2)+2
        private const int TargetBotColumnPreLast = BottomWidth - 2; // 318

        private const int PlaneBottom = 0;
        private const int PlaneTop = 1;

        // cc3dsfs usb_OptimizeHasExtraHeaderSoundData: bit 15 of the column_info
        // word that follows the 0xCC33 magic.
        public static bool HasExtraHeaderColumn(byte[] raw, int frameStart)
        {
            var columnInfo = (ushort)(raw[frameStart + 2] | (raw[frameStart + 3] << 8));
            return (columnInfo & 0x8000) != 0;
        }

        public static Capture3DSFrame Decode(byte[] raw, int length)
        {
            if (raw == null || length <= 0)
            {
                throw new Capture3DSException("LL-SPA3 returned no capture data.");
            }

            var hasExtraHeader = length >= 4 && HasExtraHeaderColumn(raw, 0);
            var neededLength = hasExtraHeader ? BottomOnlyOffset : FrameSize;
            if (length < neededLength)
            {
                throw new Capture3DSException(
                    $"LL-SPA3 raw frame is too short: {length} bytes; expected at least {neededLength} bytes.");
            }

            var top = new byte[TopWidth * Height * 3];
            var bottom = new byte[BottomWidth * Height * 3];

            for (int j = 0; j < NumColumns; j++)
            {
                FillColumn(raw, (j * ColumnStride) + ColumnHeaderSize, PlaneTop, top, TopWidth, j);
            }

            var startBotPos = hasExtraHeader ? ColumnStartBotPos - 1 : ColumnStartBotPos;
            var preLastBotPos = hasExtraHeader ? ColumnPreLastBotPos - 2 : ColumnPreLastBotPos;
            var targetPreLast = hasExtraHeader ? BottomWidth - 1 : TargetBotColumnPreLast;

            for (int column = startBotPos; column < NumColumns; column++)
            {
                FillColumn(raw, (column * ColumnStride) + ColumnHeaderSize, PlaneBottom,
                    bottom, BottomWidth, column - startBotPos);
            }
            FillColumn(raw, (preLastBotPos * ColumnStride) + ColumnHeaderSize, PlaneBottom,
                bottom, BottomWidth, targetPreLast);
            if (!hasExtraHeader)
            {
                FillColumn(raw, BottomOnlyOffset, PlaneBottom, bottom, BottomWidth, BottomWidth - 1);
            }

            return new Capture3DSFrame(top, TopWidth, Height, bottom, BottomWidth, Height);
        }

        // Deinterleaves one plane of one source column (60 groups x 24B) into the
        // destination output column, mapping height h to output row (239 - h).
        private static void FillColumn(byte[] raw, int pixelBase, int plane, byte[] dst, int dstWidth, int outCol)
        {
            for (int i = 0; i < 60; i++)
            {
                int groupBase = pixelBase + (i * 24);
                for (int k = 0; k < 4; k++)
                {
                    int h = (i * 4) + k;
                    int y = (Height - 1) - h;
                    int dstPix = ((y * dstWidth) + outCol) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int p = (k * 3) + c;
                        int src = groupBase + ((p / 2) * 4) + (plane * 2) + (p % 2);
                        dst[dstPix + c] = raw[src];
                    }
                }
            }
        }
    }
}
