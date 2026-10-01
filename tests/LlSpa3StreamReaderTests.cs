// Compile the real LlSpa3StreamReader against this managed-only slice endpoint.
// No CyUSB or native DLL is referenced, and no USB/device operation is performed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Capture3DS;
using Capture3DS.Cypress;

internal sealed class FakeSliceEndpoint : ILlSpa3SliceEndpoint
{
    internal const int Slots = 8;
    internal const int SliceSize = 0x4000;

    private readonly object sync = new object();
    private readonly Queue<byte[]> script;
    private readonly byte[][] buffers = new byte[Slots][];
    private readonly byte[][] completed = new byte[Slots][];
    private readonly bool[] armed = new bool[Slots];

    internal readonly ManualResetEventSlim Drained = new ManualResetEventSlim(false);
    internal bool FailArm;
    internal int Violations, CancelCalls, ResetCalls, CancelThreadId;

    // A null entry completes the read with a USB transfer error.
    internal FakeSliceEndpoint(IEnumerable<byte[]> slices)
    {
        script = new Queue<byte[]>(slices);
        for (int i = 0; i < Slots; i++) buffers[i] = new byte[SliceSize];
    }

    public int SlotCount { get { return Slots; } }

    public byte[] SlotBuffer(int slot) { return buffers[slot]; }

    public bool Arm(int slot)
    {
        if (FailArm) return false;
        if (armed[slot]) Violations++;
        armed[slot] = true;
        return true;
    }

    public bool Wait(int slot, int timeoutMs)
    {
        if (!armed[slot]) Violations++;
        byte[] next = null;
        bool has;
        lock (sync)
        {
            has = script.Count > 0;
            if (has) next = script.Dequeue();
            else Drained.Set();
        }
        if (!has)
        {
            Thread.Sleep(Math.Min(timeoutMs, 20));
            return false;
        }
        completed[slot] = next;
        return true;
    }

    public bool Finish(int slot, out int length)
    {
        if (!armed[slot]) Violations++;
        armed[slot] = false;
        byte[] data = completed[slot];
        completed[slot] = null;
        if (data == null)
        {
            length = 0;
            return false;
        }
        Buffer.BlockCopy(data, 0, buffers[slot], 0, data.Length);
        length = data.Length;
        return true;
    }

    public void CancelAll()
    {
        CancelCalls++;
        CancelThreadId = Thread.CurrentThread.ManagedThreadId;
        for (int i = 0; i < Slots; i++) armed[i] = false;
    }

    public void ResetPipe() { ResetCalls++; }
}

internal static class LlSpa3StreamReaderTests
{
    private static int cases, checks;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static byte[] MakeFrame(byte id, bool extraHeader)
    {
        int columns = extraHeader ? LlSpa3Decoder.NumColumns + 1 : LlSpa3Decoder.NumColumns;
        int size = extraHeader ? LlSpa3Decoder.ExtraHeaderFrameSize : LlSpa3Decoder.FrameSize;
        var frame = new byte[size];
        for (int i = 0; i < size; i++) frame[i] = id;
        for (int column = 0; column < columns; column++)
        {
            int offset = column * LlSpa3Decoder.ColumnStride;
            int info = column | (column == 0 && extraHeader ? 0x8000 : 0);
            frame[offset] = 0x33;
            frame[offset + 1] = 0xCC;
            frame[offset + 2] = (byte)info;
            frame[offset + 3] = (byte)(info >> 8);
            for (int k = 4; k < 16; k++) frame[offset + k] = 0;
        }
        return frame;
    }

    private static byte[] Concat(List<byte[]> frames)
    {
        int total = 0;
        foreach (var f in frames) total += f.Length;
        var stream = new byte[total];
        int pos = 0;
        foreach (var f in frames)
        {
            Buffer.BlockCopy(f, 0, stream, pos, f.Length);
            pos += f.Length;
        }
        return stream;
    }

    // Slice the stream from startOffset. errorAt replaces the slice covering that
    // offset with a transfer error; dropAt removes it without any error.
    private static List<byte[]> Slices(byte[] stream, int startOffset, int errorAt = -1, int dropAt = -1)
    {
        var slices = new List<byte[]>();
        for (int pos = startOffset; pos < stream.Length; pos += FakeSliceEndpoint.SliceSize)
        {
            int length = Math.Min(FakeSliceEndpoint.SliceSize, stream.Length - pos);
            bool covers(int at) { return at >= pos && at < pos + length; }
            if (covers(dropAt)) continue;
            if (covers(errorAt))
            {
                slices.Add(null);
                continue;
            }
            var slice = new byte[length];
            Buffer.BlockCopy(stream, pos, slice, 0, length);
            slices.Add(slice);
        }
        return slices;
    }

    private static int OffsetOf(List<byte[]> frames, int index)
    {
        int offset = 0;
        for (int i = 0; i < index; i++) offset += frames[i].Length;
        return offset;
    }

    private static void ExpectFrames(LlSpa3StreamReader reader, List<byte[]> frames, params int[] indices)
    {
        foreach (int index in indices)
        {
            byte[] got = reader.Take(2000);
            byte[] want = frames[index];
            Check(got.Length == want.Length, "frame " + index + " length " + got.Length + " != " + want.Length);
            for (int i = 0; i < want.Length; i++)
            {
                if (got[i] != want[i]) Check(false, "frame " + index + " differs at byte " + i);
            }
            checks++;
            reader.Return(got);
        }
    }

    private static string ExpectThrow(Action action)
    {
        try
        {
            action();
        }
        catch (Capture3DSException ex)
        {
            checks++;
            return ex.Message;
        }
        throw new Exception("expected Capture3DSException");
    }

    private static List<byte[]> Frames(params bool[] extraHeaders)
    {
        var frames = new List<byte[]>();
        for (int i = 0; i < extraHeaders.Length; i++) frames.Add(MakeFrame((byte)(0x10 + i), extraHeaders[i]));
        return frames;
    }

    private static void StartsMidFrameAndQueuesAlignedFrames()
    {
        cases++;
        var frames = Frames(false, false, false, false, false);
        var endpoint = new FakeSliceEndpoint(Slices(Concat(frames), 100000));
        var reader = new LlSpa3StreamReader(endpoint, 2000);
        reader.Start();
        ExpectFrames(reader, frames, 1, 2, 3, 4);
        Check(reader.TransferErrors == 0 && reader.TornFrames == 0 && reader.OverflowDrops == 0, "clean stream counted a failure");
        reader.Dispose();
        Check(endpoint.Violations == 0, "slot armed/collected out of order");
        Check(endpoint.CancelThreadId != Thread.CurrentThread.ManagedThreadId, "reader did not cancel its own transfers");
    }

    private static void TransferErrorRecoversWithoutFault()
    {
        cases++;
        var frames = Frames(false, false, false, false, false);
        int errorAt = OffsetOf(frames, 2) + 200000;
        var endpoint = new FakeSliceEndpoint(Slices(Concat(frames), 100000, errorAt: errorAt));
        var reader = new LlSpa3StreamReader(endpoint, 2000);
        reader.Start();
        ExpectFrames(reader, frames, 1, 3, 4);
        Check(reader.TransferErrors == 1 && reader.Recoveries == 1, "error was not recovered exactly once");
        Check(endpoint.ResetCalls == 1, "halted pipe was not reset");
        string message = ExpectThrow(() => reader.Take(100));
        Check(message.Contains("timed out"), "reader faulted after a recoverable error: " + message);
        reader.Dispose();
        Check(endpoint.Violations == 0, "slot armed/collected out of order after recovery");
    }

    private static void TornFrameIsDropped()
    {
        cases++;
        var frames = Frames(false, false, false, false, false);
        int dropAt = OffsetOf(frames, 2) + 200000;
        var endpoint = new FakeSliceEndpoint(Slices(Concat(frames), 100000, dropAt: dropAt));
        var reader = new LlSpa3StreamReader(endpoint, 2000);
        reader.Start();
        ExpectFrames(reader, frames, 1, 3, 4);
        Check(reader.TornFrames == 1 && reader.TransferErrors == 0, "silent gap was not detected as one torn frame");
        reader.Dispose();
    }

    private static void ExtraHeaderFramesKeepTheirLength()
    {
        cases++;
        var frames = Frames(false, true, false, true, false);
        var endpoint = new FakeSliceEndpoint(Slices(Concat(frames), 100000));
        var reader = new LlSpa3StreamReader(endpoint, 2000);
        reader.Start();
        ExpectFrames(reader, frames, 1, 2, 3, 4);
        reader.Dispose();
    }

    private static void SlowCallerGetsNewestFrames()
    {
        cases++;
        var frames = Frames(false, false, false, false, false, false, false, false);
        var endpoint = new FakeSliceEndpoint(Slices(Concat(frames), 100000));
        var reader = new LlSpa3StreamReader(endpoint, 5000);
        reader.Start();
        Check(endpoint.Drained.Wait(5000), "script was not consumed");
        ExpectFrames(reader, frames, 4, 5, 6, 7);
        Check(reader.OverflowDrops == 3, "expected 3 dropped frames, got " + reader.OverflowDrops);
        reader.Dispose();
    }

    private static void RepeatedErrorsWithoutDataFault()
    {
        cases++;
        var endpoint = new FakeSliceEndpoint(new byte[][] { null, null, null, null });
        var reader = new LlSpa3StreamReader(endpoint, 5000);
        reader.Start();
        var watch = Stopwatch.StartNew();
        string message = ExpectThrow(() => reader.Take(3000));
        Check(message.Contains("keep failing"), "unexpected fault: " + message);
        Check(watch.ElapsedMilliseconds < 1000, "disconnect was not reported promptly");
        Check(endpoint.ResetCalls == 3 && reader.TransferErrors == 4, "expected 3 recoveries before the fault");
        reader.Dispose();
    }

    private static void SilentStreamStalls()
    {
        cases++;
        var endpoint = new FakeSliceEndpoint(new byte[0][]);
        var reader = new LlSpa3StreamReader(endpoint, 300);
        reader.Start();
        var watch = Stopwatch.StartNew();
        string message = ExpectThrow(() => reader.Take(3000));
        Check(message.Contains("stalled"), "unexpected fault: " + message);
        Check(watch.ElapsedMilliseconds < 1500, "stall took too long: " + watch.ElapsedMilliseconds);
        reader.Dispose();
    }

    private static void ArmFailureFailsStart()
    {
        cases++;
        var endpoint = new FakeSliceEndpoint(new byte[0][]) { FailArm = true };
        var reader = new LlSpa3StreamReader(endpoint, 1000);
        ExpectThrow(() => reader.Start());
        reader.Dispose();
    }

    private static void DisposeStopsPromptly()
    {
        cases++;
        var endpoint = new FakeSliceEndpoint(new byte[0][]);
        var reader = new LlSpa3StreamReader(endpoint, 10000);
        reader.Start();
        var watch = Stopwatch.StartNew();
        reader.Dispose();
        Check(watch.ElapsedMilliseconds < 1000, "dispose took " + watch.ElapsedMilliseconds + " ms");
        Check(endpoint.CancelThreadId != Thread.CurrentThread.ManagedThreadId, "reader did not cancel its own transfers");
        string message = ExpectThrow(() => reader.Take(100));
        Check(message.Contains("not running"), "unexpected message after dispose: " + message);
    }

    public static int Main()
    {
        try
        {
            StartsMidFrameAndQueuesAlignedFrames();
            TransferErrorRecoversWithoutFault();
            TornFrameIsDropped();
            ExtraHeaderFramesKeepTheirLength();
            SlowCallerGetsNewestFrames();
            RepeatedErrorsWithoutDataFault();
            SilentStreamStalls();
            ArmFailureFailsStart();
            DisposeStopsPromptly();
            Console.WriteLine("PASS: LlSpa3StreamReader, " + cases + " cases / " + checks + " checks; hardware-free.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + cases + " cases / " + checks + " checks: " + exception);
            return 1;
        }
    }
}
