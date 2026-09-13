using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Capture3DS;
using Capture3DS.Ftd3;

// This executable compiles the real device/parser sources with this managed-only
// stand-in, NOT Ftd3Native.cs. It cannot load FTD3XX.dll or reach a USB device.
namespace Capture3DS.Ftd3
{
    internal static class Ftd3Native
    {
        public const int FT_OK = 0;
        public const uint FT_OPEN_BY_SERIAL_NUMBER = 1;
        public const uint FT_FLAGS_SUPERSPEED = 4;
        public const int FT_IO_PENDING = 24;
        public const int FT_IO_INCOMPLETE = 25;
        public static bool Failed(int status) => status != FT_OK;
        public static bool IsTransient(int status) => status == FT_IO_PENDING || status == FT_IO_INCOMPLETE;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal IntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }
        internal sealed class NativeSlot
        {
            internal int Index, Submissions;
            internal IntPtr Handle, Overlapped, Buffer, Count;
            internal uint BufferSize;
            internal AutoResetEvent Event;
            internal bool Pending, Released;
        }
        internal static readonly Dictionary<IntPtr, NativeSlot> Slots = new Dictionary<IntPtr, NativeSlot>();
        internal static readonly List<NativeSlot> SlotHistory = new List<NativeSlot>();
        internal static readonly Queue<int> SubmitStatuses = new Queue<int>();
        internal static readonly Queue<int> CompletionStatuses = new Queue<int>();
        internal static readonly List<int> SubmissionOrder = new List<int>();
        internal static readonly List<int> CompletionOrder = new List<int>();
        internal static int SubmitStatus = FT_IO_PENDING, InitializeFailureAt = -1, ReleaseFailureAt = -1;
        internal static int ThrowOnSubmitAt = -1, SubmitCount, InitializeCount;
        internal static Action<NativeSlot> OnSubmitted;
        private static readonly byte[] Zeros = new byte[555008];

        public static readonly List<string> Calls = new List<string>();
        public static readonly List<int> CloseThreads = new List<int>();
        public static readonly HashSet<IntPtr> Handles = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, uint> Timeouts = new Dictionary<IntPtr, uint>();
        public static Action<string> OnCall;
        public static int FrameStatus, StreamStatus, TimeoutStatus, CloseStatus, CreateStatus;
        public static uint FrameLength;
        public static byte[] FramePayload;
        public static readonly Queue<Tuple<uint, byte[]>> FrameResponses = new Queue<Tuple<uint, byte[]>>();
        public static bool FailStreamTimeout;
        public static bool Streaming;
        public static int ReadsWithoutTimeout;
        private static int nextHandle;
        public static void Reset()
        {
            if (Slots.Values.Any(slot => !slot.Released)) throw new Exception("Previous fixture leaked an initialized overlap.");
            Calls.Clear(); CloseThreads.Clear(); Handles.Clear(); Timeouts.Clear(); OnCall = null;
            FrameStatus = StreamStatus = TimeoutStatus = CloseStatus = CreateStatus = 0;
            FrameLength = 520592; Streaming = FailStreamTimeout = false; ReadsWithoutTimeout = 0; nextHandle = 0;
            FramePayload = null;
            FrameResponses.Clear();
            Slots.Clear(); SlotHistory.Clear(); SubmitStatuses.Clear(); CompletionStatuses.Clear();
            SubmitStatus = FT_IO_PENDING; InitializeFailureAt = ReleaseFailureAt = ThrowOnSubmitAt = -1;
            SubmitCount = InitializeCount = 0; OnSubmitted = null;
            SubmissionOrder.Clear(); CompletionOrder.Clear();
        }
        private static void Call(string name)
        {
            Calls.Add(name);
            OnCall?.Invoke(name);
        }
        public static int FT_CreateDeviceInfoList(out uint count)
        { count = 1; Call("List"); return 0; }
        public static int FT_GetDeviceInfoDetail(uint index, out uint flags, out uint type,
            out uint id, IntPtr locId, byte[] serial, byte[] description, out IntPtr handle)
        { throw new InvalidOperationException("Enumeration is outside these tests."); }
        public static int FT_Create(byte[] serial, uint flags, out IntPtr handle)
        {
            handle = CreateStatus == 0 ? new IntPtr(++nextHandle) : IntPtr.Zero;
            if (handle != IntPtr.Zero) { Handles.Add(handle); Timeouts[handle] = 0; }
            Streaming = false;
            Call("Create"); return CreateStatus;
        }
        public static int FT_Close(IntPtr handle)
        {
            if (Slots.Values.Any(slot => slot.Handle == handle && !slot.Released))
                throw new Exception("FT_Close preceded overlap release.");
            CloseThreads.Add(Thread.CurrentThread.ManagedThreadId);
            Call("Close");
            if (CloseStatus == 0) { Handles.Remove(handle); Timeouts.Remove(handle); }
            return CloseStatus;
        }
        public static int FT_AbortPipe(IntPtr handle, byte pipe)
        { Call("Abort"); return 0; }
        public static int FT_SetStreamPipe(IntPtr handle, bool allWrite, bool allRead, byte pipe, uint length)
        { Timeouts[handle] = 0; Streaming = true; Call("Stream"); return StreamStatus; }
        public static int FT_SetPipeTimeout(IntPtr handle, byte pipe, uint timeout)
        {
            int status = FailStreamTimeout && Streaming ? 4 : TimeoutStatus;
            if (status == 0 && pipe == 0x82) Timeouts[handle] = timeout;
            Call("Timeout:" + pipe + ":" + timeout);
            return status;
        }
        public static int FT_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length,
            out uint transferred, IntPtr overlapped)
        { transferred = length; Call("Write"); return 0; }
        public static int FT_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, uint length,
            out uint transferred, IntPtr overlapped)
        {
            if (Timeouts[handle] == 0) ReadsWithoutTimeout++;
            transferred = length == 555008 ? FrameLength : length;
            byte[] payload = FramePayload;
            if (length == 555008 && FrameResponses.Count != 0)
            {
                var response = FrameResponses.Dequeue();
                transferred = response.Item1;
                payload = response.Item2;
            }
            Call(length == 555008 ? "FrameRead" : length == 16 ? "CommandRead" : "DrainRead");
            if (length == 555008 && payload != null)
                Array.Copy(payload, buffer, Math.Min(payload.Length, buffer.Length));
            return length == 555008 ? FrameStatus : 0;
        }

        public static int FT_InitializeOverlapped(IntPtr handle, IntPtr overlapped)
        {
            int index = InitializeCount++;
            if (index == InitializeFailureAt) { Call("InitializeFailed"); return 4; }
            var slot = new NativeSlot { Index = index, Handle = handle, Overlapped = overlapped,
                Event = new AutoResetEvent(false) };
            var initial = (Overlapped)Marshal.PtrToStructure(overlapped, typeof(Overlapped));
            if (initial.Internal != IntPtr.Zero || initial.InternalHigh != IntPtr.Zero
                || initial.Offset != 0 || initial.OffsetHigh != 0 || initial.Event != IntPtr.Zero)
                throw new Exception("OVERLAPPED was not initialized to zero.");
            Marshal.StructureToPtr(new Overlapped { Event = slot.Event.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            Slots.Add(overlapped, slot); SlotHistory.Add(slot);
            Call("Initialize"); return 0;
        }

        private static uint Fill(NativeSlot slot)
        {
            uint transferred = FrameLength;
            byte[] payload = FramePayload;
            if (FrameResponses.Count != 0)
            {
                var response = FrameResponses.Dequeue(); transferred = response.Item1; payload = response.Item2;
            }
            Marshal.Copy(Zeros, 0, slot.Buffer, Math.Min(Zeros.Length, (int)slot.BufferSize));
            if (payload != null) Marshal.Copy(payload, 0, slot.Buffer, Math.Min(payload.Length, (int)slot.BufferSize));
            Marshal.WriteInt32(slot.Count, unchecked((int)transferred));
            return transferred;
        }

        public static int FT_ReadPipeAsync(IntPtr handle, byte pipe, IntPtr buffer, uint length,
            IntPtr transferred, IntPtr overlapped)
        {
            NativeSlot slot = Slots[overlapped];
            if (slot.Released || slot.Pending || slot.Handle != handle || pipe != 0x82)
                throw new Exception("Invalid asynchronous slot ownership.");
            if (Timeouts[handle] == 0) ReadsWithoutTimeout++;
            slot.Buffer = buffer; slot.BufferSize = length; slot.Count = transferred;
            SubmissionOrder.Add(slot.Index);
            slot.Submissions++; SubmitCount++;
            slot.Pending = true;
            if (SubmitCount == ThrowOnSubmitAt) { Call("SubmitThrows"); throw new InvalidOperationException("fake native invoke failed"); }
            int status = SubmitStatuses.Count == 0 ? SubmitStatus : SubmitStatuses.Dequeue();
            if (status == 0) { Fill(slot); slot.Pending = false; }
            else if (status != FT_IO_PENDING) slot.Pending = false;
            OnSubmitted?.Invoke(slot);
            Call("Submit"); return status;
        }

        public static int FT_GetOverlappedResult(IntPtr handle, IntPtr overlapped, out uint transferred, bool wait)
        {
            NativeSlot slot = Slots[overlapped];
            if (wait || slot.Released || !slot.Pending || slot.Handle != handle)
                throw new Exception("Completion queried with an invalid owner or blocking wait.");
            int status = CompletionStatuses.Count == 0 ? FrameStatus : CompletionStatuses.Dequeue();
            CompletionOrder.Add(slot.Index);
            transferred = 0;
            if (status == 0 || status == 19 || status == 20 || status == 30)
            {
                transferred = Fill(slot); slot.Pending = false;
            }
            Call("Complete"); return status;
        }

        public static int FT_ReleaseOverlapped(IntPtr handle, IntPtr overlapped)
        {
            NativeSlot slot = Slots[overlapped];
            if (slot.Released || slot.Handle != handle || Slots.Values.Any(value => value.Pending))
                throw new Exception("Released storage before all accepted reads completed.");
            if (slot.Index == ReleaseFailureAt) { Call("ReleaseFailed"); return 4; }
            slot.Event.Dispose(); slot.Released = true;
            Call("Release"); return 0;
        }
    }
}

internal static class Ftd3ReconnectTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception(message);
    }
    private static Ftd3N3dsxlDevice Device() => Ftd3N3dsxlDevice.Open(
        new Capture3DSDeviceInfo(Capture3DSModel.N3dsxl, "fake", "N3DSXL", true));
    private static Ftd3N3dsxlDevice Connected()
    {
        Ftd3Native.Reset();
        var device = Device(); device.Connect();
        Check(Ftd3Native.ReadsWithoutTimeout == 0, "Connect reads have finite timeouts");
        Ftd3Native.Calls.Clear(); Ftd3Native.CloseThreads.Clear();
        return device;
    }
    private static int Count(string name) => Ftd3Native.Calls.Count(x => x == name);

    private static void PreCancellation()
    {
        Ftd3Native.Reset();
        using (var cts = new CancellationTokenSource())
        using (var device = Device())
        {
            cts.Cancel();
            Throws<OperationCanceledException>(() => device.Connect(cts.Token), "pre-cancelled Connect");
            Throws<OperationCanceledException>(() => device.ReadFrame(cts.Token), "pre-cancelled ReadFrame");
            Check(Ftd3Native.Calls.Count == 0, "pre-cancellation performs no native calls");
        }
    }
    private static void CancelEveryConnectBoundary()
    {
        Ftd3Native.Reset();
        using (var device = Device()) device.Connect();
        // Final Close is the outer using, not part of Connect.
        string[] baseline = Ftd3Native.Calls.Take(Ftd3Native.Calls.Count - 1).ToArray();
        for (int target = 1; target <= baseline.Length; target++)
        {
            Ftd3Native.Reset();
            using (var cts = new CancellationTokenSource())
            using (var device = Device())
            {
                int calls = 0;
                Ftd3Native.OnCall = name => { if (++calls == target) cts.Cancel(); };
                Throws<OperationCanceledException>(() => device.Connect(cts.Token), "Connect boundary " + target);
                Check(Ftd3Native.Handles.Count == 0, "cancelled connect closed acquired handle " + target);
                Check(Ftd3Native.Calls.Take(target).SequenceEqual(baseline.Take(target)), "unchanged prefix " + target);
                Check(Ftd3Native.Calls.Skip(target).All(x => x == "Close"), "only owned close after cancel " + target);
            }
        }
    }
    private static void CancelReadResults()
    {
        foreach (int status in new[] { 0, Ftd3Native.FT_IO_PENDING, Ftd3Native.FT_IO_INCOMPLETE, 3 })
        foreach (uint length in new uint[] { 0, 520592 })
        {
            using (var device = Connected())
            using (var cts = new CancellationTokenSource())
            {
                Ftd3Native.FrameStatus = status; Ftd3Native.FrameLength = length;
                Ftd3Native.OnCall = name => { if (name == "Complete") cts.Cancel(); };
                try
                {
                    Throws<OperationCanceledException>(() => device.ReadFrame(cts.Token), "read cancellation wins result: " + status);
                    Check(Count("Submit") == 12 && Count("Complete") == 1 && Count("Abort") == 0 && Count("Close") == 0,
                        "cancellation prevents replacement submissions, abort and close");
                    Check(Ftd3Native.Handles.Count == 1, "read retains worker-owned handle until Dispose");
                }
                finally { Ftd3Native.FrameStatus = 0; Ftd3Native.OnCall = null; }
            }
            Check(Ftd3Native.Handles.Count == 0, "Dispose releases cancelled read handle");
        }
    }
    private static void CancellationDoesNotCloseInFlightRead()
    {
        var device = Connected();
        using (var cts = new CancellationTokenSource())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Exception failure = null; bool cancelled = false;
            Ftd3Native.OnCall = name =>
            {
                if (name == "Complete")
                {
                    entered.Set();
                    if (!release.Wait(3000)) throw new Exception("test read wait exceeded bound");
                }
            };
            var worker = new Thread(() =>
            {
                try { device.ReadFrame(cts.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { failure = ex; }
                finally { device.Dispose(); }
            });
            worker.IsBackground = true; worker.Start();
            Check(entered.Wait(3000), "fake native read entered");
            cts.Cancel();
            Check(worker.IsAlive, "cancellation cannot interrupt a native completion query that has not returned");
            Check(Ftd3Native.CloseThreads.Count == 0 && Count("Abort") == 0, "caller does not abort/close in flight");
            release.Set();
            Check(worker.Join(3000), "worker returns after native call returns");
            Check(failure == null && cancelled, "worker observes cancellation after read returns");
            Check(Count("Submit") == 12, "no replacement read after cancellation");
            Check(Ftd3Native.CloseThreads.SequenceEqual(new[] { worker.ManagedThreadId }), "only worker closes handle");
        }
    }
    private static void StreamRecovery()
    {
        foreach (int mode in new[] { 1, 2 })
        using (var device = Connected())
        {
            Ftd3Native.FrameStatus = Ftd3Native.FT_IO_PENDING;
            if (mode == 1) Check(device.ReadRawTransferSize() == 0, "raw diagnostic recovery");
            else { uint transferred; Check(device.ReadFrameDiagnostic(out transferred) == null && transferred == 0, "frame diagnostic recovery"); }
            Check(Ftd3Native.Calls.Take(4).SequenceEqual(new[] { "FrameRead", "Abort", "Stream", "Timeout:130:1000" }), "recovery restores timeout");
            Check(Ftd3Native.ReadsWithoutTimeout == 0, "no read after stream timeout reset");
        }
        foreach (bool failTimeout in new[] { false, true })
        foreach (int mode in new[] { 1, 2 })
        using (var device = Connected())
        {
            Ftd3Native.FrameStatus = Ftd3Native.FT_IO_INCOMPLETE;
            Ftd3Native.FailStreamTimeout = failTimeout; Ftd3Native.StreamStatus = failTimeout ? 0 : 4;
            Throws<Capture3DSException>(() =>
            {
                if (mode == 1) device.ReadRawTransferSize();
                else { uint transferred; device.ReadFrameDiagnostic(out transferred); }
            }, "failed recovery configuration must be reported");
            Check(Count("FrameRead") == 1 && Count("Submit") == 0, "diagnostics never retry or start a ring after failed recovery configuration");
            Check(Ftd3Native.ReadsWithoutTimeout == 0, "failed recovery never reaches unbounded read");
        }
    }
    private static void UnchangedBoundsAndLegacyApi()
    {
        using (ICapture3DSDevice device = Connected())
        {
            Ftd3Native.FrameLength = 0;
            Check(device.ReadFrame() == null, "legacy short-frame no-signal result");
            Check(Count("Complete") == 16 && Count("Submit") == 28 && Count("Abort") == 0,
                "legacy 16-attempt short-frame bound is preserved with twelve pending slots");
        }
        Ftd3Native.Reset(); Ftd3Native.CreateStatus = 3;
        using (var device = Device())
            Throws<Capture3DSException>(() => device.Connect(), "create failure reports error");
        Check(Count("Create") == 3 && Count("Abort") == 0, "legacy 3-attempt create bound unchanged");
        Ftd3Native.Reset(); Ftd3Native.TimeoutStatus = 4;
        using (var device = Device())
            Throws<Capture3DSException>(() => device.Connect(), "initial timeout failure reports error");
        Check(Count("Write") == 0 && Count("DrainRead") == 0, "timeout failure prevents writes/drain reads");
        Check(Ftd3Native.Handles.Count == 0 && Count("Abort") == 0, "failed connect cleanup closes without abort");
    }
    private static void CloseFailures()
    {
        var device = Connected();
        Throws<Capture3DSException>(() => device.Connect(), "double connect rejected");
        Check(Ftd3Native.Calls.Count == 0 && Ftd3Native.Handles.Count == 1, "double connect does not replace handle");
        Ftd3Native.CloseStatus = 4;
        Throws<Capture3DSException>(() => device.Dispose(), "failed Dispose is reported");
        Check(Ftd3Native.Handles.Count == 1 && Count("Abort") == 0, "failed close retains handle without abort");
        Ftd3Native.CloseStatus = 0; device.Dispose(); device.Dispose();
        Check(Ftd3Native.Handles.Count == 0 && Count("Close") == 2, "successful cleanup is idempotent");

        foreach (bool recoverOnSecondClose in new[] { false, true })
        {
            Ftd3Native.Reset(); device = Device();
            Ftd3Native.CloseStatus = 4;
            if (recoverOnSecondClose)
                Ftd3Native.OnCall = name => { if (name == "Close" && Count("Close") == 2) Ftd3Native.CloseStatus = 0; };
            Throws<Capture3DSException>(() => device.Connect(), "drain close failure surfaces");
            Check(Count("Create") == 1 && Count("Abort") == 0, "close failure never opens second session");
            Ftd3Native.CloseStatus = 0; device.Dispose();
            Check(Ftd3Native.Handles.Count == 0, "worker can finish owned failed-close cleanup");
        }
        Ftd3Native.Reset(); device = Device();
        using (var cts = new CancellationTokenSource())
        {
            Ftd3Native.CloseStatus = 4;
            Ftd3Native.OnCall = name => { if (name == "Create") cts.Cancel(); };
            Throws<Capture3DSException>(() => device.Connect(cts.Token), "cancellation does not mask failed close");
            Check(Count("Create") == 1 && Ftd3Native.Handles.Count == 1, "cancel cleanup failure retains only owned handle");
            Ftd3Native.CloseStatus = 0; device.Dispose();
            Check(Ftd3Native.Handles.Count == 0 && Count("Abort") == 0, "failed cancellation cleanup can finish on worker");
        }
    }
    private static void AudioDoesNotChangeTransport()
    {
        Capture3DSAudioChunk firstAudio;
        using (var device = Connected())
        {
            const int video = 518400;
            Ftd3Native.FrameLength = video + 548 * 4;
            Ftd3Native.FramePayload = new byte[video + 548 * 4];
            Ftd3Native.FramePayload[video] = 0x00;
            Ftd3Native.FramePayload[video + 1] = 0x80;
            Ftd3Native.FramePayload[video + 2] = 0xFF;
            Ftd3Native.FramePayload[video + 3] = 0x7F;
            var first = device.ReadFrame();
            firstAudio = first.Audio;
            Check(firstAudio != null && firstAudio.Samples.Length == 1096
                && firstAudio.Samples.Take(2).SequenceEqual(new short[] { -32768, 32767 })
                && firstAudio.Samples.Skip(2).All(x => x == 0), "Device publishes PCM from actual transfer length");
            Array.Clear(Ftd3Native.FramePayload, 0, Ftd3Native.FramePayload.Length);
            var second = device.ReadFrame();
            Check(second.Audio.Samples.Length == 1096 && second.Audio.Samples.All(x => x == 0), "Next transfer publishes new samples");
            Check(firstAudio.Samples.Take(2).SequenceEqual(new short[] { -32768, 32767 }), "Audio does not alias reused USB memory");
            Check(Count("Initialize") == 12 && Count("Complete") == 2 && Count("Submit") == 14
                && Count("FrameRead") == 0 && Count("Abort") == 0, "Audio uses queued capture without synchronous reads or recovery");
        }
        Check(firstAudio.Samples.Take(2).SequenceEqual(new short[] { -32768, 32767 }), "Samples survive device disposal");
    }

    private static void PipelineDeviceCleanupFailures()
    {
        var device = Connected();
        try
        {
            Check(device.ReadFrame() != null, "close-failure case starts the real device pipeline");
            Ftd3Native.CloseStatus = 4;
            Throws<Capture3DSException>(() => device.Dispose(), "native close failure after drained pipeline is reported");
            Check(Count("Release") == 12 && Ftd3Native.SlotHistory.All(slot => slot.Released && !slot.Pending)
                && Count("Close") == 1 && Ftd3Native.Handles.Count == 1,
                "pipeline storage releases before failed close, but the native handle remains owned");
            int calls = Ftd3Native.Calls.Count;
            Throws<Capture3DSException>(() => device.Connect(), "failed post-pipeline close cannot open another session");
            Check(Ftd3Native.Calls.Count == calls, "rejected reconnect issues no native calls after close failure");
            Ftd3Native.CloseStatus = 0;
            device.Dispose(); device.Dispose();
            Check(Count("Close") == 2 && Count("Release") == 12 && Ftd3Native.Handles.Count == 0,
                "close retry does not release any overlap twice");
        }
        finally { Ftd3Native.CloseStatus = 0; Ftd3Native.FrameStatus = 0; device.Dispose(); }

        device = Connected();
        try
        {
            Ftd3Native.FrameStatus = 32;
            Throws<Capture3DSException>(() => device.ReadFrame(), "unknown device completion faults the pipeline");
            Throws<Capture3DSException>(() => device.Dispose(), "unknown completion during device disposal retains ownership");
            Check(Ftd3Native.SlotHistory.Count == 12 && Ftd3Native.SlotHistory.All(slot => slot.Pending && !slot.Released)
                && Ftd3Native.Handles.Count == 1 && Count("Release") == 0 && Count("Close") == 0,
                "no overlap or device handle is released while any completion is unconfirmed");
            int calls = Ftd3Native.Calls.Count;
            Throws<Capture3DSException>(() => device.Connect(), "unknown cleanup outcome prevents a second connection");
            Check(Ftd3Native.Calls.Count == calls, "rejected reconnect preserves the original pending ring");
            Ftd3Native.FrameStatus = 0;
            device.Dispose(); device.Dispose();
            Check(Ftd3Native.SlotHistory.All(slot => slot.Released && !slot.Pending)
                && Count("Release") == 12 && Count("Close") == 1 && Ftd3Native.Handles.Count == 0,
                "late confirmed completions allow exactly one release and close per owned resource");
        }
        finally { Ftd3Native.FrameStatus = 0; Ftd3Native.CloseStatus = 0; device.Dispose(); }
    }

    private static byte[] AudioPacket(int pairs, short marker)
    {
        var raw = new byte[518400 + pairs * 4];
        for (int i = 0; i < pairs; i++)
        {
            short left = unchecked((short)(marker + i));
            short right = unchecked((short)(-marker - i));
            int offset = 518400 + i * 4;
            raw[offset] = unchecked((byte)left);
            raw[offset + 1] = unchecked((byte)(left >> 8));
            raw[offset + 2] = unchecked((byte)right);
            raw[offset + 3] = unchecked((byte)(right >> 8));
        }
        return raw;
    }

    private static void VariableAudioLengths()
    {
        using (var device = Connected())
        {
            var retained = new List<Tuple<Capture3DSAudioChunk, short[]>>();
            int reads = 0;
            foreach (int pairs in new[] { 542, 547, 548, 1023, 542, 548, 1023, 900, 547, 548, 547, 548 })
            {
                short marker = (short)(1000 + reads * 1000);
                Ftd3Native.FramePayload = AudioPacket(pairs, marker);
                Ftd3Native.FrameLength = (uint)Ftd3Native.FramePayload.Length;
                Capture3DSFrame frame = device.ReadFrame();
                Check(frame != null, "valid variable-length packet is published");
                Check(Count("Complete") == ++reads, "valid audio-length changes never discard/re-read a packet");
                Check(frame.Top.Length == 400 * 240 * 3 && frame.Bottom.Length == 320 * 240 * 3,
                    "variable audio tails preserve native video dimensions");
                Check(frame.Top.All(x => x == 0) && frame.Bottom.All(x => x == 0), "audio never leaks into RGB");
                short[] expected = Enumerable.Range(0, pairs)
                    .SelectMany(i => new[] { unchecked((short)(marker + i)), unchecked((short)(-marker - i)) }).ToArray();
                Check(frame.Audio != null && frame.Audio.Samples.SequenceEqual(expected),
                    "each packet retains all PCM, sign, L/R order and sequence");
                retained.Add(Tuple.Create(frame.Audio, expected));
            }
            Check(Count("Abort") == 0 && Count("Stream") == 0 && Count("Close") == 0,
                "normal variable audio performs no USB recovery or close");
            foreach (var pair in retained) Check(pair.Item1.Samples.SequenceEqual(pair.Item2), "later reads preserve owned prior PCM");
        }
    }

    private static void PacketShapeBounds()
    {
        uint[] invalid = { 0, 518396, 518399, 518400, 518404, 520564, 520567,
            520569, 520570, 520571, 522493, 522494, 522496, 553472, 553984, 555008 };
        using (var device = Connected())
        {
            foreach (uint length in invalid)
            {
                Ftd3Native.Calls.Clear(); Ftd3Native.FrameLength = length;
                Check(device.ReadFrame() == null, "invalid packet shape returns no frame: " + length);
                Check(Count("Complete") == 16, "invalid reads retain the 16-attempt bound: " + length);
                Check(Count("Abort") == 0 && Count("Close") == 0 && Ftd3Native.Handles.Count == 1,
                    "invalid reads do not abort or replace the owned handle: " + length);
            }
            Check(Ftd3Native.ReadsWithoutTimeout == 0, "shape retries always retain finite timeout");

            Ftd3Native.Calls.Clear();
            Ftd3Native.FrameResponses.Enqueue(Tuple.Create(518396u, (byte[])null));
            Ftd3Native.FrameResponses.Enqueue(Tuple.Create(555008u, (byte[])null));
            Ftd3Native.FrameResponses.Enqueue(Tuple.Create(520570u, (byte[])null));
            byte[] valid = AudioPacket(547, 1234);
            Ftd3Native.FrameResponses.Enqueue(Tuple.Create((uint)valid.Length, valid));
            var recovered = device.ReadFrame();
            Check(recovered != null && recovered.Audio.Samples.Length == 1094,
                "valid short-packet boundary recovers after truncated/full/partial-pair reads");
            Check(Count("Complete") == 4 && Count("Submit") == 4 && Count("Abort") == 0,
                "shape recovery uses successive queued completions, no abort/reconnect");

            Ftd3Native.FramePayload = AudioPacket(1023, 2000);
            Ftd3Native.FrameLength = 522492;
            var atMaximum = device.ReadFrame();
            Check(atMaximum != null && atMaximum.Audio.Samples.Length == 2046,
                "official inclusive upper boundary retains exactly 1023 stereo pairs");
            Check(atMaximum.Audio.Samples.Last() == -3022, "upper boundary preserves the last complete pair");
            Ftd3Native.FramePayload = AudioPacket(542, 3000);
            Ftd3Native.FrameLength = 520568;
            var atMinimum = device.ReadFrame();
            Check(atMinimum != null && atMinimum.Audio.Samples.Length == 1084
                && atMinimum.Audio.Samples.Last() == -3541, "official inclusive lower boundary never replays stale upper-tail bytes");

            Throws<Capture3DSException>(() => device.ReadRawTransferSize(), "raw diagnostics cannot steal an active ring transfer");
            Throws<Capture3DSException>(() => { uint actual; device.ReadFrameDiagnostic(out actual); },
                "frame diagnostics cannot steal an active ring transfer");
            Check(Count("FrameRead") == 0, "rejected mixed mode issues no synchronous read");
        }
        using (var device = Connected())
        {
            foreach (uint length in invalid.Concat(new uint[] { 851968, uint.MaxValue }))
            {
                Ftd3Native.Calls.Clear(); Ftd3Native.FrameLength = length;
                uint actual;
                Check(device.ReadFrameDiagnostic(out actual) == null && actual == length,
                    "diagnostic decoder shares shape guard and retains actual length: " + length);
                Check(Ftd3Native.Calls.SequenceEqual(new[] { "FrameRead" }), "diagnostic remains one read");
            }
        }
        foreach (uint length in new uint[] { 555009, 851968, uint.MaxValue })
        using (var device = Connected())
        {
            Ftd3Native.FrameLength = length;
            Throws<Capture3DSException>(() => device.ReadFrame(), "overallocated completion length fails before any copy");
            Check(Count("Complete") == 1 && Count("Submit") == 12 && Count("Abort") == 0,
                "oversized completion is neither copied nor resubmitted");
            Ftd3Native.FrameLength = 520592;
        }
    }

    private static void CancellationAfterRejectedPacket()
    {
        using (var device = Connected())
        using (var cts = new CancellationTokenSource())
        {
            Ftd3Native.FrameLength = 555008;
            Ftd3Native.OnCall = name => { if (name == "Complete" && Count("Complete") == 2) cts.Cancel(); };
            Throws<OperationCanceledException>(() => device.ReadFrame(cts.Token), "cancellation wins during shape retries");
            Check(Count("Complete") == 2 && Count("Submit") == 13 && Count("Abort") == 0,
                "no replacement or recovery after cancelled invalid completion");
            Check(Ftd3Native.Handles.Count == 1 && Ftd3Native.ReadsWithoutTimeout == 0,
                "shape cancellation preserves bounded worker-owned transport");
        }
    }

    private static void OtherErrorsAreNotPending()
    {
        foreach (int status in new[] { 32, 33 })
        foreach (int mode in new[] { 0, 1, 2 })
        using (var device = Connected())
        {
            Ftd3Native.FrameStatus = status;
            Throws<Capture3DSException>(() =>
            {
                if (mode == 0) device.ReadFrame();
                else if (mode == 1) device.ReadRawTransferSize();
                else { uint actual; device.ReadFrameDiagnostic(out actual); }
            }, "OTHER_ERROR/unknown code is not pending: " + status);
            Check((mode == 0 ? Count("Complete") == 1 && Count("Submit") == 12 : Ftd3Native.Calls.SequenceEqual(new[] { "FrameRead" }))
                && Count("Abort") == 0 && Count("Stream") == 0, "real error never triggers a false pending Abort/Stream/retry");
            Check(Ftd3Native.Handles.Count == 1 && Ftd3Native.ReadsWithoutTimeout == 0,
                "caller retains bounded worker-owned cleanup after error");
            Ftd3Native.FrameStatus = 0;
        }
    }

    private static void PipelineFifoAndOwnership()
    {
        var retained = new List<Capture3DSFrame>();
        using (var device = Connected())
        {
            const int frames = 30;
            for (int i = 0; i < frames; i++)
            {
                byte[] packet = AudioPacket(i % 2 == 0 ? 547 : 548, (short)(1200 + i * 10));
                for (int j = 0; j < 518400; j++) packet[j] = (byte)(40 + i);
                Ftd3Native.FrameResponses.Enqueue(Tuple.Create((uint)packet.Length, packet));
            }
            byte[] poison = Enumerable.Repeat((byte)0xE7, 555008).ToArray();
            Ftd3Native.OnSubmitted = slot =>
            {
                Check(Count("Initialize") == 12, "all twelve overlaps are initialized before the first submission");
                Check(slot.BufferSize == 555008 && slot.Buffer != IntPtr.Zero && slot.Count != IntPtr.Zero,
                    "each submission uses stable native-sized buffer and count storage");
                if (slot.Submissions > 1)
                    Marshal.Copy(poison, 0, slot.Buffer, poison.Length); // Driver may overwrite immediately after re-arm.
            };
            for (int i = 0; i < frames; i++)
            {
                Capture3DSFrame frame = device.ReadFrame();
                Check(frame.Top.All(value => value == (byte)(40 + i)) && frame.Bottom.All(value => value == (byte)(40 + i)),
                    "completed RGB is copied before re-arm can overwrite the native slot");
                Check(frame.Audio.Samples[0] == 1200 + i * 10 && frame.Audio.Samples[1] == -1200 - i * 10
                    && frame.Audio.Samples.Length == (i % 2 == 0 ? 1094 : 1096), "FIFO preserves complete varying stereo tails");
                retained.Add(frame);
                Check(Ftd3Native.SlotHistory.Count(slot => slot.Pending) == 12, "all twelve reads are queued before ReadFrame returns to NX");
            }
            Check(Ftd3Native.SubmissionOrder.SequenceEqual(Enumerable.Range(0, 12).Concat(Enumerable.Range(0, frames).Select(i => i % 12))),
                "ring submissions wrap in FIFO order across more than two revolutions");
            Check(Ftd3Native.CompletionOrder.SequenceEqual(Enumerable.Range(0, frames).Select(i => i % 12)),
                "ring completions consume the oldest queued slot");
            Check(Ftd3Native.SlotHistory.Select(slot => slot.Buffer).Distinct().Count() == 12
                && Ftd3Native.SlotHistory.Select(slot => slot.Overlapped).Distinct().Count() == 12
                && Ftd3Native.SlotHistory.Select(slot => slot.Count).Distinct().Count() == 12,
                "twelve independent stable storage locations are owned until disposal");
            Check(Count("Abort") == 0 && Count("FrameRead") == 0 && Count("Stream") == 0,
                "normal queued capture never aborts, reconfigures, or runs synchronous reads");
            Ftd3Native.OnSubmitted = null;
        }
        for (int i = 0; i < retained.Count; i++)
            Check(retained[i].Top[0] == 40 + i && retained[i].Bottom[0] == 40 + i
                && retained[i].Audio.Samples[0] == 1200 + i * 10,
                "returned RGB and PCM survive later slot reuse and native-storage disposal");
        Check(Ftd3Native.SlotHistory.All(slot => slot.Released && !slot.Pending) && Ftd3Native.Handles.Count == 0,
            "all accepted operations drain and all overlaps release before close");

        using (var device = Connected())
        {
            Ftd3Native.FramePayload = AudioPacket(548, 3210);
            for (int i = 0; i < 12; i++) Ftd3Native.SubmitStatuses.Enqueue(i % 2 == 0 ? 0 : 24);
            for (int i = 0; i < 24; i++)
                Check(device.ReadFrame().Audio.Samples[0] == 3210, "mixed immediate/pending reads preserve data");
            int[] expected = Enumerable.Range(0, 12).Where(i => i % 2 == 1).Concat(Enumerable.Range(0, 12)).ToArray();
            Check(Ftd3Native.CompletionOrder.SequenceEqual(expected), "immediate FT_OK requires no duplicate completion query");
            Check(Ftd3Native.SubmissionOrder.Count == 36 && Count("Abort") == 0,
                "mixed completion modes retain twelve initial submissions plus one replacement per result");
        }
        using (var device = Connected())
        {
            Ftd3Native.CompletionStatuses.Enqueue(24);
            Ftd3Native.CompletionStatuses.Enqueue(25);
            Ftd3Native.CompletionStatuses.Enqueue(0);
            Check(device.ReadFrame() != null, "pending/incomplete polling eventually delivers the same request");
            Check(Ftd3Native.CompletionOrder.SequenceEqual(new[] { 0, 0, 0 }) && Count("Submit") == 13,
                "pending statuses do not skip, duplicate, or resubmit an unfinished slot");
            Check(Count("Abort") == 0 && Count("Stream") == 0 && Ftd3Native.ReadsWithoutTimeout == 0,
                "async waiting retains the finite pipe timeout without recovery side effects");
        }
    }
    public static int Main()
    {
        try
        {
            PreCancellation(); CancelEveryConnectBoundary(); CancelReadResults();
            CancellationDoesNotCloseInFlightRead(); StreamRecovery(); UnchangedBoundsAndLegacyApi(); CloseFailures();
            AudioDoesNotChangeTransport();
            PipelineDeviceCleanupFailures();
            VariableAudioLengths(); PacketShapeBounds(); CancellationAfterRejectedPacket();
            OtherErrorsAreNotPending();
            PipelineFifoAndOwnership();
            Console.WriteLine("PASS: " + checks + " hardware-free FTD3 reconnect checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
