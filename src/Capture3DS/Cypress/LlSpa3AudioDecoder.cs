namespace Capture3DS.Cypress
{
    // cc3dsfs copyAudioOptimize3DSLE / copyAudioFromSoundDataOptimize3DSLE,
    // revision e58edc4d34002b095f8fd0cd4f60df532df25fb0 (MIT).
    // The 16-byte column header contains magic, column info, then two
    // { index, right, left } little-endian 16-bit records. Indices wrap at 512
    // and repeat across columns AND frames. One decoder belongs to one session.
    internal sealed class LlSpa3AudioDecoder
    {
        private int _lastSampleIndex = -1;

        // Column stride and frame sizes of the colour mode being streamed.
        internal LlSpa3FrameLayout Layout { get; set; } = LlSpa3FrameLayout.Rgb888;

        internal void Reset() { _lastSampleIndex = -1; }

        internal Capture3DSAudioChunk Decode(byte[] raw, int length)
        {
            if (raw == null || length < 4 || length > raw.Length) return null;
            bool extraHeader = LlSpa3Decoder.HasExtraHeaderColumn(raw, 0);
            int columns = extraHeader ? 401 : 400;
            int required = extraHeader ? Layout.ExtraHeaderFrameSize : Layout.FrameSize;
            if (length < required) return null;

            // Validate all headers before updating the cross-frame index. A
            // partial/misaligned packet must neither emit noise nor poison the
            // next packet's duplicate suppression.
            for (int column = 0; column < columns; column++)
            {
                int offset = column * Layout.ColumnStride;
                if (Word(raw, offset) != 0xCC33 || (Word(raw, offset + 2) & 0x3FF) != column)
                    return null;
            }

            int last = _lastSampleIndex;
            int count = 0;
            var samples = new short[columns * 4];
            for (int column = 0; column < columns; column++)
            {
                int offset = column * Layout.ColumnStride + 4;
                for (int sample = 0; sample < 2; sample++, offset += 6)
                {
                    int index = Word(raw, offset) & 0x1FF;
                    if (index == last) continue;
                    samples[count++] = unchecked((short)Word(raw, offset + 4)); // left
                    samples[count++] = unchecked((short)Word(raw, offset + 2)); // right
                    last = index;
                }
            }
            _lastSampleIndex = last;
            if (count == 0) return null;
            if (count != samples.Length) System.Array.Resize(ref samples, count);
            return Capture3DSAudioChunk.FromOwnedSamples(samples);
        }

        private static int Word(byte[] raw, int offset)
        {
            return raw[offset] | (raw[offset + 1] << 8);
        }
    }
}
