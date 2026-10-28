using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Capture3DS;

// Pure frame conversion tests: no device enumeration, firmware, GDI, or capture
// library initialization. May compile with the DTO source or reference a built DLL.
internal static class Capture3DSFrameCopyTests
{
    private const int Guard = 16;
    private const byte Sentinel = 0xA5;
    private static int _cases;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Pixels(int count, int seed)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++) result[i] = (byte)((i * 73 + seed * 19) & 255);
        return result;
    }

    private static Capture3DSFrame Frame(int topWidth, int topHeight, int bottomWidth, int bottomHeight, int extra = 0)
    {
        return new Capture3DSFrame(Pixels(topWidth * topHeight * 3 + extra, 1), topWidth, topHeight,
            Pixels(bottomWidth * bottomHeight * 3 + extra, 2), bottomWidth, bottomHeight);
    }

    private static void Verify(Capture3DSFrame frame, int extraStride, bool minimalLength)
    {
        int width, height;
        byte[] mosaic = frame.ToMosaic(out width, out height);
        int rowBytes = width * 3;
        int stride = rowBytes + extraStride;
        int total = Guard + stride * height + Guard;
        byte[] before = Enumerable.Repeat(Sentinel, total).ToArray();
        byte[] expected = (byte[])before.Clone();
        byte[] topBefore = (byte[])frame.Top.Clone();
        byte[] bottomBefore = (byte[])frame.Bottom.Clone();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int source = (y * width + x) * 3;
            int target = Guard + y * stride + x * 3;
            expected[target] = mosaic[source + 2];
            expected[target + 1] = mosaic[source + 1];
            expected[target + 2] = mosaic[source];
        }

        IntPtr allocation = Marshal.AllocHGlobal(total);
        try
        {
            Marshal.Copy(before, 0, allocation, total);
            int length = minimalLength ? (height - 1) * stride + rowBytes : stride * height;
            frame.CopyMosaicBgr24To(IntPtr.Add(allocation, Guard), stride, length);
            var actual = new byte[total];
            Marshal.Copy(allocation, actual, 0, total);
            Check(actual.SequenceEqual(expected), "Pixel/margin/padding/guard mismatch for " + width + "x" + height + ", stride " + stride);
            Check(frame.Top.SequenceEqual(topBefore) && frame.Bottom.SequenceEqual(bottomBefore), "Source arrays changed.");
            // Reusing a destination must overwrite image pixels and margins, while
            // preserving padding again; no source or pointer ownership is retained.
            frame.CopyMosaicBgr24To(IntPtr.Add(allocation, Guard), stride, length);
            Marshal.Copy(allocation, actual, 0, total);
            Check(actual.SequenceEqual(expected), "Repeated copy differs.");
            _cases++;
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static void Reject(string name, Type exceptionType, Action<IntPtr> operation)
    {
        var before = Enumerable.Repeat(Sentinel, 128).ToArray();
        var after = new byte[before.Length];
        IntPtr allocation = Marshal.AllocHGlobal(before.Length);
        try
        {
            Marshal.Copy(before, 0, allocation, before.Length);
            Exception observed = null;
            try { operation(IntPtr.Add(allocation, Guard)); }
            catch (Exception error) { observed = error; }
            Check(observed != null && observed.GetType() == exceptionType,
                name + ": expected " + exceptionType.Name + ", observed " + (observed == null ? "success" : observed.GetType().Name));
            Marshal.Copy(allocation, after, 0, after.Length);
            Check(after.SequenceEqual(before), name + ": destination changed before rejection.");
            _cases++;
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static void InvalidInputs()
    {
        Capture3DSFrame valid = Frame(2, 2, 2, 2);
        Reject("null destination", typeof(ArgumentNullException), p => valid.CopyMosaicBgr24To(IntPtr.Zero, 6, 24));
        Reject("zero stride", typeof(ArgumentOutOfRangeException), p => valid.CopyMosaicBgr24To(p, 0, 24));
        Reject("negative stride", typeof(ArgumentOutOfRangeException), p => valid.CopyMosaicBgr24To(p, -6, 24));
        Reject("negative buffer length", typeof(ArgumentOutOfRangeException), p => valid.CopyMosaicBgr24To(p, 6, -1));
        Reject("short stride", typeof(ArgumentException), p => valid.CopyMosaicBgr24To(p, 5, 24));
        Reject("short buffer", typeof(ArgumentException), p => valid.CopyMosaicBgr24To(p, 6, 23));
        Reject("last row short", typeof(ArgumentException), p => valid.CopyMosaicBgr24To(p, 9, 32));
        Reject("large stride overflow", typeof(ArgumentException), p => valid.CopyMosaicBgr24To(p, int.MaxValue, int.MaxValue));

        var malformed = new Dictionary<string, Capture3DSFrame>
        {
            { "null top", new Capture3DSFrame(null, 2, 2, new byte[12], 2, 2) },
            { "null bottom", new Capture3DSFrame(new byte[12], 2, 2, null, 2, 2) },
            { "short top", new Capture3DSFrame(new byte[11], 2, 2, new byte[12], 2, 2) },
            { "short bottom", new Capture3DSFrame(new byte[12], 2, 2, new byte[11], 2, 2) },
            { "negative width", new Capture3DSFrame(new byte[0], -1, 2, new byte[12], 2, 2) },
            { "negative height", new Capture3DSFrame(new byte[0], 2, -1, new byte[12], 2, 2) },
            { "empty canvas width", new Capture3DSFrame(new byte[0], 0, 2, new byte[0], 0, 2) },
            { "empty canvas height", new Capture3DSFrame(new byte[0], 2, 0, new byte[0], 2, 0) },
            { "height sum overflow", new Capture3DSFrame(new byte[0], 1, int.MaxValue, new byte[0], 1, 1) },
            { "row byte overflow", new Capture3DSFrame(new byte[0], int.MaxValue, 1, new byte[3], 1, 1) },
            { "source byte overflow", new Capture3DSFrame(new byte[0], 700000000, 2, new byte[3], 1, 1) },
            { "null zero-height screen", new Capture3DSFrame(null, 2, 0, new byte[12], 2, 2) }
        };
        foreach (var item in malformed)
            Reject(item.Key, typeof(InvalidOperationException), p => item.Value.CopyMosaicBgr24To(p, 12, 64));
    }

    private static void AllocationCase()
    {
        MethodInfo method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
        if (method == null)
        {
            Console.WriteLine("Allocation counter unavailable on this runtime; pixel/bounds tests still apply.");
            return;
        }
        var allocated = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        Capture3DSFrame frame = Frame(400, 240, 320, 240);
        IntPtr output = Marshal.AllocHGlobal(400 * 480 * 3);
        try
        {
            for (int i = 0; i < 8; i++) frame.CopyMosaicBgr24To(output, 1200, 576000);
            allocated();
            long before = allocated();
            for (int i = 0; i < 128; i++) frame.CopyMosaicBgr24To(output, 1200, 576000);
            long copiedBytes = allocated() - before;
            Check(copiedBytes == 0, "Successful copies allocated managed memory: " + copiedBytes);
            // Compare only the allocation being eliminated; source decode/Bitmap
            // allocations are deliberately outside this pure API measurement.
            frame.ToMosaic(out int width, out int height);
            before = allocated();
            for (int i = 0; i < 128; i++) frame.ToMosaic(out width, out height);
            long mosaicBytes = allocated() - before;
            Check(mosaicBytes >= 128L * 576000, "Legacy mosaic allocation counter did not observe expected payload.");
            Console.WriteLine("Allocation sample (128 3DS frames): direct copy=" + copiedBytes + " B, ToMosaic=" + mosaicBytes + " B.");
            _cases++;
        }
        finally { Marshal.FreeHGlobal(output); }
    }

    private static int Main()
    {
        try
        {
            Capture3DSFrame[] frames = {
                Frame(400, 240, 320, 240), Frame(256, 192, 256, 192), Frame(320, 240, 400, 240),
                Frame(5, 3, 2, 2), Frame(2, 2, 7, 4), Frame(1, 1, 1, 1), Frame(4, 1, 1, 3),
                Frame(0, 3, 5, 4), Frame(5, 3, 0, 4), Frame(5, 0, 3, 4), Frame(3, 4, 5, 0),
                Frame(0, 0, 5, 4), Frame(5, 4, 0, 0), Frame(7, 5, 4, 2, 17)
            };
            foreach (Capture3DSFrame frame in frames)
            foreach (int extraStride in new[] { 0, 1, 7 })
            {
                Verify(frame, extraStride, false);
                Verify(frame, extraStride, true);
            }
            byte[] shared = Pixels(12, 7);
            Verify(new Capture3DSFrame(shared, 2, 2, shared, 2, 2), 3, true);
            InvalidInputs();
            AllocationCase();
            Console.WriteLine("PASS: " + _cases + " frame-copy cases (pixels, black margins, padding, guards, ownership, strict rejection).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
