using System;
using System.Linq;
using System.Reflection;
using Capture3DS;
using Capture3DS.Ftd2;
using Capture3DS.Ftd3;
using Capture3DS.Loopy;

// Synthetic packets only. No device instances, device enumeration, USB, firmware,
// playback, native DLLs, GUI, or recorded/user-owned capture data are exercised.
internal static class Capture3DSAudioTests
{
    private static int checks;
    private static readonly Assembly Library = typeof(Capture3DSFrame).Assembly;
    private static readonly Type PcmType = Library.GetType("Capture3DS.Capture3DSAudioDecoder", true);
    private static readonly Type LlAudioType = Library.GetType("Capture3DS.Cypress.LlSpa3AudioDecoder", true);
    private static readonly Type LlVideoType = Library.GetType("Capture3DS.Cypress.LlSpa3Decoder", true);
    private const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    private const int Video3ds = 518400;
    private const int VideoDs = 196608;
    private const int MaxAudio3ds = 35072;
    private const int ColumnStride = 1456;
    private const int LlFrameSize = 583840;
    private const int LlExtraSize = 583856;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Word(byte[] raw, int offset, int value)
    {
        raw[offset] = (byte)value;
        raw[offset + 1] = (byte)(value >> 8);
    }
    private static Capture3DSAudioChunk Tail(byte[] raw, int length, int offset, int maximum)
    {
        return (Capture3DSAudioChunk)PcmType.GetMethod("DecodePcm16StereoTail", Internal)
            .Invoke(null, new object[] { raw, length, offset, maximum });
    }
    private static object LlDecoder() { return Activator.CreateInstance(LlAudioType, true); }
    private static Capture3DSAudioChunk LlAudio(object decoder, byte[] raw, int length)
    {
        return (Capture3DSAudioChunk)LlAudioType.GetMethod("Decode", Internal)
            .Invoke(decoder, new object[] { raw, length });
    }
    private static Capture3DSFrame LlVideo(byte[] raw, int length, object decoder)
    {
        return (Capture3DSFrame)LlVideoType.GetMethods(Internal)
            .Single(method => method.Name == "Decode" && method.GetParameters().Length == 3)
            .Invoke(null, new object[] { raw, length, decoder });
    }
    private static byte[] LlPacket(bool extra, int constantIndex)
    {
        var raw = new byte[extra ? LlExtraSize : LlFrameSize];
        for (int i = 0; i < raw.Length; i++) raw[i] = (byte)(i * 31 + 7);
        for (int column = 0; column < (extra ? 401 : 400); column++)
        {
            Word(raw, column * ColumnStride, 0xCC33);
            Word(raw, column * ColumnStride + 2, column | (extra ? 0x8000 : 0));
            LlSample(raw, column * 2, constantIndex, -12345, 23456);
            LlSample(raw, column * 2 + 1, constantIndex, -12345, 23456);
        }
        return raw;
    }
    private static void LlSample(byte[] raw, int record, int index, int left, int right)
    {
        int offset = (record / 2) * ColumnStride + 4 + (record % 2) * 6;
        Word(raw, offset, index); Word(raw, offset + 2, right); Word(raw, offset + 4, left);
    }
    private static void Samples(Capture3DSAudioChunk audio, params short[] expected)
    {
        Check(audio != null && audio.Samples.SequenceEqual(expected), "PCM sample contents/order");
        Check(audio.SampleRate == 32728 && audio.Channels == 2, "PCM output format");
        Check(audio.SampleRateNumerator == 67027964 && audio.SampleRateDenominator == 2048, "Exact sample clock");
    }
    private static void DtoAndPcmBounds()
    {
        var source = new short[] { -32768, 32767, -1, 0 };
        var audio = new Capture3DSAudioChunk(source);
        source[0] = 1;
        Samples(audio, -32768, 32767, -1, 0);
        Throws<ArgumentNullException>(() => new Capture3DSAudioChunk(null));
        Throws<ArgumentException>(() => new Capture3DSAudioChunk(new short[0]));
        Throws<ArgumentException>(() => new Capture3DSAudioChunk(new short[3]));
        Check(new Capture3DSFrame(new byte[3], 1, 1, new byte[3], 1, 1).Audio == null, "Legacy DTO constructor");
        var raw = Enumerable.Repeat((byte)0xDA, 80).ToArray();
        Word(raw, 11, -32768); Word(raw, 13, 32767); Word(raw, 15, -1); Word(raw, 17, 0);
        for (int tailLength = 0; tailLength < 12; tailLength++)
        {
            var decoded = Tail(raw, 11 + tailLength, 11, 8);
            int sampleCount = Math.Min(tailLength, 8) / 4 * 2;
            Check(sampleCount == 0 ? decoded == null : decoded.Samples.Length == sampleCount, "Partial pair and cap " + tailLength);
        }
        Samples(Tail(raw, 19, 11, 8), -32768, 32767, -1, 0);
        var owned = Tail(raw, 19, 11, 8);
        Array.Clear(raw, 0, raw.Length);
        Samples(owned, -32768, 32767, -1, 0);
        Check(Tail(null, 0, 0, 8) == null, "Null packet");
        Check(Tail(raw, -1, 0, 8) == null, "Negative packet length");
        Check(Tail(raw, raw.Length + 1, 0, 8) == null, "Packet exceeds allocation");
        Check(Tail(raw, 4, 5, 8) == null, "Tail before video complete");
        Check(Tail(raw, 4, -1, 8) == null, "Negative offset");
        Check(Tail(raw, 4, 0, -1) == null, "Negative cap");
    }
    private static void VideoAndTailDecoders()
    {
        var raw = new byte[555520];
        for (int i = 0; i < Video3ds; i++) raw[i] = (byte)(i * 37 + 9);
        Word(raw, Video3ds, -30001); Word(raw, Video3ds + 2, 30002);
        foreach (bool ftd3 in new[] { false, true })
        {
            Func<int, Capture3DSFrame> decode = length => ftd3
                ? N3dsxlDecoder.DecodeRgb8_2D(raw, length) : LoopyOld3dsDecoder.DecodeRgb8(raw, length);
            var videoOnly = decode(Video3ds);
            Check(videoOnly.Audio == null, "Video-only packet");
            for (int extra = 0; extra < 4; extra++)
            {
                var frame = decode(Video3ds + 4 + extra);
                Samples(frame.Audio, -30001, 30002);
                Check(frame.Top.SequenceEqual(videoOnly.Top) && frame.Bottom.SequenceEqual(videoOnly.Bottom), "Video unchanged by partial audio");
            }
            Check(decode(Video3ds + 3).Audio == null, "No complete stereo pair");
            Check(decode(Video3ds + MaxAudio3ds + 512).Audio.Samples.Length == MaxAudio3ds / 2, "Tail maximum excludes USB rounding padding");
            Check(decode(raw.Length + 1).Audio == null, "Stale capacity cannot authorize audio");
        }
        Check(N3dsxlDecoder.DecodeRgb8_2D(raw, 555008).Audio == null, "FTD3 error region is never audio");
        var dsRaw = new byte[198900];
        const int prefix = 6;
        for (int i = 0; i < prefix; i += 2) Word(dsRaw, i, 0x4321);
        for (int i = prefix; i < prefix + VideoDs; i++) dsRaw[i] = (byte)(i * 17 + 5);
        Word(dsRaw, prefix + VideoDs, -32000); Word(dsRaw, prefix + VideoDs + 2, 1234);
        for (int i = prefix + VideoDs + 4; i < dsRaw.Length; i += 2) Word(dsRaw, i, 0x4321);
        var ds = DsDecoder.DecodeRgb8(dsRaw, prefix, prefix + VideoDs + 4);
        Samples(ds.Audio, -32000, 1234);
        var dsLegacy = DsDecoder.DecodeRgb8(dsRaw, prefix);
        Check(dsLegacy.Audio == null, "DS legacy overload does not infer tail length from capacity");
        Check(ds.Top.SequenceEqual(dsLegacy.Top) && ds.Bottom.SequenceEqual(dsLegacy.Bottom), "DS video unchanged");
        Check(DsDecoder.DecodeRgb8(dsRaw, prefix, prefix + VideoDs - 1).Audio == null, "DS incomplete packet audio rejected");
        Check(DsDecoder.DecodeRgb8(dsRaw, 0, dsRaw.Length).Audio.Samples.Length == 1142, "DS upstream max sample cap");
    }
    private static void OptimizePackets()
    {
        foreach (bool extra in new[] { false, true })
        {
            var decoder = LlDecoder();
            var raw = LlPacket(extra, 1);
            LlSample(raw, 0, 510, -32768, 32767);
            LlSample(raw, 1, 510, 12, 34); // Duplicate index, different stale values.
            LlSample(raw, 2, 511, -2, 2);
            LlSample(raw, 3, 0, -3, 3);
            LlSample(raw, 4, 0, 55, 66);
            var decoded = LlAudio(decoder, raw, raw.Length);
            Samples(decoded, -32768, 32767, -2, 2, -3, 3, -12345, 23456);
            Array.Clear(raw, 0, raw.Length);
            Samples(decoded, -32768, 32767, -2, 2, -3, 3, -12345, 23456);
            var next = LlPacket(extra, 2);
            LlSample(next, 0, 1, 99, 88); // Duplicate across frame boundary.
            Samples(LlAudio(decoder, next, next.Length), -12345, 23456);
            Check(LlAudio(decoder, LlPacket(extra, 2), next.Length) == null, "All-duplicate frame has no audio");

            var bad = LlPacket(extra, 9);
            Check(LlAudio(decoder, bad, bad.Length - 1) == null, "Partial Optimize frame rejected");
            Check(LlAudio(decoder, bad, bad.Length + 1) == null, "Optimize allocation bounds");
            Word(bad, (extra ? 400 : 399) * ColumnStride, 0);
            Check(LlAudio(decoder, bad, bad.Length) == null, "Last-column magic validation");
            bad = LlPacket(extra, 9);
            Word(bad, 200 * ColumnStride + 2, 199);
            Check(LlAudio(decoder, bad, bad.Length) == null, "Column sequence validation");
            Check(LlAudio(decoder, LlPacket(extra, 2), next.Length) == null, "Malformed input does not advance index");
            LlAudioType.GetMethod("Reset", Internal).Invoke(decoder, null);
            Samples(LlAudio(decoder, LlPacket(extra, 2), next.Length), -12345, 23456);

            var picture = LlPacket(extra, 2);
            var plain = LlVideo(picture, picture.Length, null);
            var withAudio = LlVideo(picture, picture.Length, LlDecoder());
            Check(plain.Audio == null && withAudio.Audio != null, "Optimize video decoder audio overload");
            Check(plain.Top.SequenceEqual(withAudio.Top) && plain.Bottom.SequenceEqual(withAudio.Bottom), "Optimize picture unaffected");
        }
        var onlyExtra = LlPacket(true, 10);
        LlSample(onlyExtra, 800, 11, -111, 111);
        LlSample(onlyExtra, 801, 12, -222, 222);
        Samples(LlAudio(LlDecoder(), onlyExtra, onlyExtra.Length), -12345, 23456, -111, 111, -222, 222);
        var wrap = LlPacket(false, 0);
        LlSample(wrap, 0, 512, -12345, 23456);
        Samples(LlAudio(LlDecoder(), wrap, wrap.Length), -12345, 23456);
        Check(LlAudio(LlDecoder(), null, 0) == null, "Null Optimize data");
    }
    private static void BuiltDsSynchronizationEnvelope()
    {
        // Built-DLL mode also invokes the existing pure static synchronization
        // helpers. Their type initializer only creates two description arrays;
        // no instance/handle is created and no native method is invoked.
        var deviceType = Library.GetType("Capture3DS.Ftd2.DsFtd2Device", false);
        if (deviceType == null) return;
        var trim = deviceType.GetMethod("RemoveSynchFromFinalLength", Internal);
        var initial = deviceType.GetMethod("InitialSynchOffset", Internal);
        foreach (int prefix in new[] { 0, 2, 6 })
        {
            var packet = new byte[198900];
            for (int i = 0; i < prefix; i += 2) Word(packet, i, 0x4321);
            Word(packet, prefix + VideoDs, -1357);
            Word(packet, prefix + VideoDs + 2, 2468);
            for (int i = prefix + VideoDs + 4; i < packet.Length; i += 2) Word(packet, i, 0x4321);
            int realLength = (int)trim.Invoke(null, new object[] { packet, packet.Length });
            int offset = (int)initial.Invoke(null, new object[] { packet, realLength });
            Check(realLength == prefix + VideoDs + 4 && offset == prefix, "Real DS helpers trim only the synchronization envelope");
            Samples(DsDecoder.DecodeRgb8(packet, offset, realLength).Audio, -1357, 2468);
        }
    }
    public static int Main()
    {
        try
        {
            DtoAndPcmBounds(); VideoAndTailDecoders(); OptimizePackets(); BuiltDsSynchronizationEnvelope();
            Console.WriteLine("PASS: " + checks + " hardware-free PCM/packet/ownership checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
