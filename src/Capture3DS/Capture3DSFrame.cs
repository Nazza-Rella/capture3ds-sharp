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
        /// 1280x720 固定の RGB8 キャンバスへ、モザイクをアスペクト維持・中央配置(レターボックス)で
        /// 焼き込んで返す。NX 等の 720p パイプラインへそのまま渡せる。
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
