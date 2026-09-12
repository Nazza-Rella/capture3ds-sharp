using System;

namespace Capture3DS
{
    // cc3dsfs devicecapture.cpp get_audio_n_samples / conversions.cpp
    // convertAudioToOutput, revision e58edc4d34002b095f8fd0cd4f60df532df25fb0.
    // Raw transfer length, not buffer capacity, bounds the audio. Discard an
    // incomplete stereo pair and transport padding beyond the model's maximum.
    internal static class Capture3DSAudioDecoder
    {
        internal const int Max3dsAudioBytes = 1096 * 16 * 2;
        // FTD2 get_max_samples: ((198900 - 196608) / 2) - 4 halfwords.
        // The driver removes its synchronization run before calling us.
        internal const int MaxDsAudioBytes = 2284;

        internal static Capture3DSAudioChunk DecodePcm16StereoTail(
            byte[] raw, int rawLength, int audioOffset, int maximumAudioBytes)
        {
            // Malformed audio must not turn an otherwise usable video frame
            // into a USB failure/reconnect. Never access a stale or missing tail.
            if (raw == null || rawLength < 0 || rawLength > raw.Length
                || audioOffset < 0 || audioOffset > rawLength || maximumAudioBytes < 0)
                return null;
            int byteCount = Math.Min(rawLength - audioOffset, maximumAudioBytes) & ~3;
            if (byteCount == 0) return null;
            var samples = new short[byteCount / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                int offset = audioOffset + i * 2;
                samples[i] = unchecked((short)(raw[offset] | (raw[offset + 1] << 8)));
            }
            return Capture3DSAudioChunk.FromOwnedSamples(samples);
        }
    }
}
