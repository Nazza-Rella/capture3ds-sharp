using System;

namespace Capture3DS
{
    /// <summary>
    /// Owned signed PCM16 samples from a DS/3DS capture packet, interleaved L/R.
    /// Playback must consume these separately from preview/UI frame scheduling.
    /// </summary>
    public sealed class Capture3DSAudioChunk
    {
        public const int DsSampleRateNumerator = 67027964;
        public const int DsSampleRateDenominator = 2048;
        public int SampleRate { get { return 32728; } }
        public int SampleRateNumerator { get { return DsSampleRateNumerator; } }
        public int SampleRateDenominator { get { return DsSampleRateDenominator; } }
        public int Channels { get { return 2; } }
        /// <summary>Owned L/R sample pairs; never aliases the reusable USB buffer.</summary>
        public short[] Samples { get; }

        public Capture3DSAudioChunk(short[] samples) : this(samples, false) { }

        private Capture3DSAudioChunk(short[] samples, bool owned)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (samples.Length == 0 || (samples.Length & 1) != 0)
                throw new ArgumentException("Complete, nonempty stereo sample pairs are required.", nameof(samples));
            Samples = owned ? samples : (short[])samples.Clone();
        }

        internal static Capture3DSAudioChunk FromOwnedSamples(short[] samples)
        {
            return new Capture3DSAudioChunk(samples, true);
        }
    }

    /// <summary>
    /// デコード済みの 1 フレーム。上下画面を RGB8(行優先, 1px=3byte: R,G,B)で保持する。
    /// 画面サイズは機種で異なるため各画面ごとに幅/高さを持つ。
    ///  - N3DSXL : Top 400x240, Bottom 320x240
    ///  - DS     : Top 256x192, Bottom 256x192
    /// </summary>
    public sealed class Capture3DSFrame
    {
        // 3DS の既定サイズ(参照用の定数として残す)。
        public const int N3dsTopWidth = 400;
        public const int N3dsBottomWidth = 320;
        public const int N3dsScreenHeight = 240;

        /// <summary>上画面 RGB8。長さ = TopWidth*TopHeight*3。row*TopWidth*3 + col*3 + ch。</summary>
        public byte[] Top { get; }
        /// <summary>下画面 RGB8。長さ = BottomWidth*BottomHeight*3。</summary>
        public byte[] Bottom { get; }

        public int TopWidth { get; }
        public int TopHeight { get; }
        public int BottomWidth { get; }
        public int BottomHeight { get; }

        /// <summary>PCM from this capture packet, or null when no complete audio is available.</summary>
        public Capture3DSAudioChunk Audio { get; }

        public Capture3DSFrame(byte[] top, int topWidth, int topHeight,
                               byte[] bottom, int bottomWidth, int bottomHeight)
            : this(top, topWidth, topHeight, bottom, bottomWidth, bottomHeight, null)
        {
        }

        public Capture3DSFrame(byte[] top, int topWidth, int topHeight,
                               byte[] bottom, int bottomWidth, int bottomHeight,
                               Capture3DSAudioChunk audio)
        {
            Top = top;
            Bottom = bottom;
            TopWidth = topWidth;
            TopHeight = topHeight;
            BottomWidth = bottomWidth;
            BottomHeight = bottomHeight;
            Audio = audio;
        }

        /// <summary>
        /// 上画面を上、下画面を中央寄せで下に積んだ RGB8 モザイクを返す。
        /// 拡大・縮小や補間は行わず、各画面をドットバイドットで配置する。
        /// 幅 = max(上幅, 下幅)、高さ = 上高 + 下高。余白は黒。
        /// </summary>
        public byte[] ToMosaic(out int width, out int height)
        {
            width = Math.Max(TopWidth, BottomWidth);
            height = TopHeight + BottomHeight;
            byte[] dst = new byte[width * height * 3];

            int topX = (width - TopWidth) / 2;
            for (int y = 0; y < TopHeight; y++)
            {
                int srcOff = y * TopWidth * 3;
                int dstOff = (y * width + topX) * 3;
                Buffer.BlockCopy(Top, srcOff, dst, dstOff, TopWidth * 3);
            }

            int bottomX = (width - BottomWidth) / 2;
            for (int y = 0; y < BottomHeight; y++)
            {
                int srcOff = y * BottomWidth * 3;
                int dstOff = ((TopHeight + y) * width + bottomX) * 3;
                Buffer.BlockCopy(Bottom, srcOff, dst, dstOff, BottomWidth * 3);
            }
            return dst;
        }

        /// <summary>
        /// Copies the native-size, vertically stacked screens into caller-owned
        /// unmanaged BGR24 memory without allocating an intermediate mosaic.
        /// Width is max(TopWidth, BottomWidth); height is TopHeight + BottomHeight.
        /// Each screen is horizontally centered and its side margins are black.
        /// No resizing or interpolation is performed.
        /// </summary>
        /// <param name="destination">Address of the first (top) output row.</param>
        /// <param name="stride">Positive byte distance between output rows;
        /// must be at least the output width multiplied by three.</param>
        /// <param name="bufferLength">Writable bytes available from destination;
        /// must include every image byte through the last row.</param>
        /// <remarks>
        /// Row padding after width * 3 bytes is left untouched. The caller must
        /// own writable memory of the stated size for the entire call, and must
        /// not overlap it with either source array or mutate those arrays during
        /// the call. No pointer is retained, and source ownership is unchanged.
        /// Negative stride is not supported. All dimensions and buffer bounds are
        /// validated before any output is written; a nonzero pointer alone cannot
        /// prove that the caller actually owns the stated unmanaged memory.
        /// Screen dimensions may be zero, but the combined canvas must be nonempty.
        /// </remarks>
        /// <exception cref="ArgumentNullException">destination is zero.</exception>
        /// <exception cref="ArgumentOutOfRangeException">stride is not positive,
        /// or bufferLength is negative.</exception>
        /// <exception cref="ArgumentException">The stride or destination buffer
        /// is too small for the complete image.</exception>
        /// <exception cref="InvalidOperationException">The frame dimensions or
        /// source arrays do not describe a complete RGB8 frame.</exception>
        public unsafe void CopyMosaicBgr24To(IntPtr destination, int stride, int bufferLength)
        {
            if (destination == IntPtr.Zero)
                throw new ArgumentNullException(nameof(destination));
            if (stride <= 0)
                throw new ArgumentOutOfRangeException(nameof(stride), "A positive stride is required.");
            if (bufferLength < 0)
                throw new ArgumentOutOfRangeException(nameof(bufferLength));

            long width = Math.Max(TopWidth, BottomWidth);
            long height = (long)TopHeight + BottomHeight;
            if (TopWidth < 0 || TopHeight < 0 || BottomWidth < 0 || BottomHeight < 0
                || width <= 0 || height <= 0 || width > int.MaxValue / 3 || height > int.MaxValue)
                throw new InvalidOperationException("The frame has invalid mosaic dimensions.");

            // Bounds above keep these products in Int64 even for malformed input.
            long topBytes = (long)TopWidth * TopHeight * 3;
            long bottomBytes = (long)BottomWidth * BottomHeight * 3;
            if (Top == null || Bottom == null || Top.LongLength < topBytes || Bottom.LongLength < bottomBytes)
                throw new InvalidOperationException("The frame has incomplete RGB8 source buffers.");

            long rowBytes = width * 3;
            if (stride < rowBytes)
                throw new ArgumentException("Stride is smaller than the image row.", nameof(stride));
            long requiredBytes = (height - 1) * stride + rowBytes;
            if (bufferLength < requiredBytes)
                throw new ArgumentException("The destination buffer is too small for the image.", nameof(bufferLength));

            byte* output = (byte*)destination.ToPointer();
            CopyScreenBgr24(Top, TopWidth, TopHeight, output, stride, (int)width, 0);
            CopyScreenBgr24(Bottom, BottomWidth, BottomHeight, output, stride, (int)width, TopHeight);
        }

        private static unsafe void CopyScreenBgr24(byte[] source, int screenWidth, int screenHeight,
                                                  byte* destination, int stride, int canvasWidth, int firstRow)
        {
            int leftBytes = ((canvasWidth - screenWidth) / 2) * 3;
            int screenBytes = screenWidth * 3;
            int rowBytes = canvasWidth * 3;
            fixed (byte* sourceBase = source)
            {
                for (int y = 0; y < screenHeight; y++)
                {
                    byte* output = destination + (long)(firstRow + y) * stride;
                    for (int offset = 0; offset < leftBytes; offset++) output[offset] = 0;
                    byte* input = sourceBase + (long)y * screenBytes;
                    for (int offset = 0; offset < screenBytes; offset += 3)
                    {
                        output[leftBytes + offset] = input[offset + 2];
                        output[leftBytes + offset + 1] = input[offset + 1];
                        output[leftBytes + offset + 2] = input[offset];
                    }
                    for (int offset = leftBytes + screenBytes; offset < rowBytes; offset++) output[offset] = 0;
                }
            }
        }

        /// <summary>
        /// 1280x720 固定の RGB8 キャンバスへ、モザイクを拡大しながらアスペクト維持・中央配置
        /// (レターボックス)で焼き込んで返す。720p 固定の既存パイプラインとの互換用。
        /// </summary>
        public byte[] ToLetterbox720() => ToLetterbox(1280, 720);

        public byte[] ToLetterbox(int canvasW, int canvasH)
        {
            int mw, mh;
            byte[] mosaic = ToMosaic(out mw, out mh);
            byte[] canvas = new byte[canvasW * canvasH * 3]; // 黒で初期化

            double scale = Math.Min((double)canvasW / mw, (double)canvasH / mh);
            int dw = Math.Max(1, (int)(mw * scale));
            int dh = Math.Max(1, (int)(mh * scale));
            int dx = (canvasW - dw) / 2;
            int dy = (canvasH - dh) / 2;

            // 最近傍。整数倍に近いので十分。必要なら後段で高品質縮小に差し替え可。
            for (int y = 0; y < dh; y++)
            {
                int sy = (int)((long)y * mh / dh);
                if (sy >= mh) sy = mh - 1;
                int srcRow = sy * mw * 3;
                int dstRow = ((dy + y) * canvasW + dx) * 3;
                for (int x = 0; x < dw; x++)
                {
                    int sx = (int)((long)x * mw / dw);
                    if (sx >= mw) sx = mw - 1;
                    int s = srcRow + sx * 3;
                    int d = dstRow + x * 3;
                    canvas[d] = mosaic[s];
                    canvas[d + 1] = mosaic[s + 1];
                    canvas[d + 2] = mosaic[s + 2];
                }
            }
            return canvas;
        }
    }
}
