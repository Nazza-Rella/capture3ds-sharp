// Compile the real Ftd3ReadPipeline with this managed-only transport. No native
// DLL is referenced or loaded, and no USB/device operation is performed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Capture3DS.Ftd3;

namespace Capture3DS
{
    public sealed class Capture3DSException : Exception
    {
        public Capture3DSException(string message) : base(message) { }
    }
}

namespace Capture3DS.Ftd3
{
    internal static class Ftd3Native
    {
        internal const int FT_OK = 0, FT_IO_PENDING = 24, FT_IO_INCOMPLETE = 25;
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped
        {
            internal IntPtr Internal, InternalHigh;
            internal uint Offset, OffsetHigh;
            internal IntPtr Event;
        }

        internal sealed class Entry
        {
            internal int Index, ReleaseAttempts, Releases;
            internal bool Initialized, Pending;
            internal EventWaitHandle Event;
            internal IntPtr Buffer, Count;
        }

        internal static readonly IntPtr Handle = new IntPtr(12345);
        internal static readonly Dictionary<IntPtr, Entry> Entries = new Dictionary<IntPtr, Entry>();
        internal static int InitializeCalls, SubmitCalls, QueryCalls, ReleaseCalls;
        internal static int InitFailure, ReleaseFailure, SubmitThrow, SubmitStatus, QueryStatus;
        internal static bool ReleaseFailedOnce;
        internal static Action<string, int> After;

        internal static void Reset()
        {
            foreach (Entry entry in Entries.Values)
                if (entry.Event != null) entry.Event.Dispose();
            Entries.Clear();
            InitializeCalls = SubmitCalls = QueryCalls = ReleaseCalls = 0;
            InitFailure = ReleaseFailure = SubmitThrow = -1;
            SubmitStatus = FT_IO_PENDING;
            QueryStatus = 0;
            ReleaseFailedOnce = false;
            After = null;
        }

        private static void CheckHandle(IntPtr handle)
        {
            if (handle != Handle) throw new Exception("Native handle ownership changed.");
        }

        internal static int FT_InitializeOverlapped(IntPtr handle, IntPtr pointer)
        {
            CheckHandle(handle);
            int index = InitializeCalls++;
            var entry = new Entry { Index = index };
            Entries.Add(pointer, entry);
            if (index == InitFailure) return 5;
            entry.Event = new EventWaitHandle(false, EventResetMode.AutoReset);
            entry.Initialized = true;
            Marshal.StructureToPtr(new Overlapped { Event = entry.Event.SafeWaitHandle.DangerousGetHandle() }, pointer, false);
            if (After != null) After("initialize", index);
            return 0;
        }

        internal static int FT_ReadPipeAsync(IntPtr handle, byte pipe, IntPtr buffer,
            uint length, IntPtr count, IntPtr pointer)
        {
            CheckHandle(handle);
            Entry entry = Entries[pointer];
            if (pipe != 0x82 || length != 64 || !entry.Initialized || entry.Pending || entry.Releases != 0)
                throw new Exception("Invalid asynchronous submission ownership.");
            SubmitCalls++;
            entry.Buffer = buffer;
            entry.Count = count;
            entry.Pending = true;
            Marshal.WriteInt32(count, 0);
            for (int i = 0; i < 64; i++) Marshal.WriteByte(buffer, i, (byte)entry.Index);
            if (After != null) After("submit", entry.Index);
            if (entry.Index == SubmitThrow) throw new InvalidOperationException("Injected native-call exception.");
            return SubmitStatus;
        }

        internal static int FT_GetOverlappedResult(IntPtr handle, IntPtr pointer, out uint transferred, bool wait)
        {
            CheckHandle(handle);
            Entry entry = Entries[pointer];
            if (wait || !entry.Initialized || !entry.Pending || entry.Releases != 0)
                throw new Exception("Invalid completion query ownership or infinite native wait.");
            QueryCalls++;
            int status = QueryStatus;
            transferred = status == 0 ? 64u : 0u;
            if (status == 0 || status == 19 || status == 20 || status == 30)
            {
                entry.Pending = false;
                Marshal.WriteInt32(entry.Count, (int)transferred);
            }
            if (After != null) After("query", entry.Index);
            return status;
        }

        internal static int FT_ReleaseOverlapped(IntPtr handle, IntPtr pointer)
        {
            CheckHandle(handle);
            Entry entry = Entries[pointer];
            foreach (Entry other in Entries.Values)
                if (other.Pending) throw new Exception("Released storage while an accepted request is still pending.");
            if (!entry.Initialized || entry.Releases != 0) throw new Exception("Double release.");
            ReleaseCalls++;
            entry.ReleaseAttempts++;
            if (entry.Index == ReleaseFailure && !ReleaseFailedOnce)
            {
                ReleaseFailedOnce = true;
                return 32;
            }
            entry.Releases++;
            entry.Initialized = false;
            entry.Event.Dispose();
            entry.Event = null;
            if (After != null) After("release", entry.Index);
            return 0;
        }
    }
}

internal static class Ftd3ReadPipelineFailureTests
{
    private static int checks, cases;
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

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

    private static Ftd3ReadPipeline NewPipeline()
    {
        Ftd3Native.Reset();
        return new Ftd3ReadPipeline(Ftd3Native.Handle, 0x82, 64);
    }

    private static object[] Slots(Ftd3ReadPipeline pipeline)
    {
        return (object[])typeof(Ftd3ReadPipeline).GetField("slots", Fields).GetValue(pipeline);
    }

    private static void Storage(Ftd3ReadPipeline pipeline, bool freed, int start = 0)
    {
        object[] slots = Slots(pipeline);
        for (int i = start; i < slots.Length; i++)
        {
            object slot = slots[i];
            if (slot == null) continue;
            foreach (string name in new[] { "Buffer", "Count", "Overlapped" })
            {
                IntPtr value = (IntPtr)slot.GetType().GetField(name, Fields).GetValue(slot);
                Check((value == IntPtr.Zero) == freed, name + " lifetime mismatch at slot " + i);
            }
        }
    }

    private static void ReleasedExactly(int initialized)
    {
        int released = 0;
        foreach (Ftd3Native.Entry entry in Ftd3Native.Entries.Values)
        {
            Check(!entry.Pending, "Pending request remains after successful drain.");
            Check(entry.Releases <= 1, "Double successful release.");
            released += entry.Releases;
        }
        Check(released == initialized, "Wrong number of initialized slots released.");
    }

    private static void DisposeSuccessfully(Ftd3ReadPipeline pipeline, int initialized)
    {
        Ftd3Native.After = null;
        Ftd3Native.QueryStatus = 0;
        Ftd3Native.SubmitThrow = -1;
        pipeline.Dispose();
        Storage(pipeline, true);
        ReleasedExactly(initialized);
        int releaseCalls = Ftd3Native.ReleaseCalls;
        pipeline.Dispose();
        Check(Ftd3Native.ReleaseCalls == releaseCalls, "Dispose is not idempotent.");
        Throws<ObjectDisposedException>(() => pipeline.Read(new byte[64], CancellationToken.None));
    }

    private static void PartialInitializeFailures()
    {
        for (int index = 0; index < Ftd3ReadPipeline.SlotCount; index++)
        {
            var pipeline = NewPipeline();
            Ftd3Native.InitFailure = index;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            Check(Ftd3Native.InitializeCalls == index + 1 && Ftd3Native.SubmitCalls == 0, "Init failure did not stop startup.");
            Storage(pipeline, false);
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            DisposeSuccessfully(pipeline, index);
            cases++;
        }
    }

    private static void TerminalErrors()
    {
        foreach (int status in new[] { 19, 20, 30 })
        {
            var pipeline = NewPipeline();
            Ftd3Native.QueryStatus = status;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            Check(Ftd3Native.SubmitCalls == 12 && Ftd3Native.ReleaseCalls == 0, "Terminal error reused or freed a slot.");
            pipeline.Dispose();
            Check(Ftd3Native.QueryCalls == 12, "Terminal completion queried twice or skipped.");
            Storage(pipeline, true);
            ReleasedExactly(12);
            cases++;
        }
    }

    private static void UnknownCompletionRetainsOwnership()
    {
        foreach (int status in new[] { 1, 3, 4, 32 })
        {
            var pipeline = NewPipeline();
            Ftd3Native.QueryStatus = status;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Dispose());
            Check(Ftd3Native.ReleaseCalls == 0 && Ftd3Native.SubmitCalls == 12, "Unknown status released or resubmitted a request.");
            Storage(pipeline, false);
            foreach (Ftd3Native.Entry entry in Ftd3Native.Entries.Values) Check(entry.Pending, "Unknown status lost accepted request ownership.");
            DisposeSuccessfully(pipeline, 12);
            cases++;
        }
    }

    private static void ReleaseFailuresAreRetryable()
    {
        for (int index = 0; index < 12; index++)
        {
            var pipeline = NewPipeline();
            Check(pipeline.Read(new byte[64], CancellationToken.None) == 64, "Initial read failed.");
            Ftd3Native.ReleaseFailure = index;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Dispose());
            Storage(pipeline, false, index);
            foreach (Ftd3Native.Entry entry in Ftd3Native.Entries.Values)
                Check(entry.Releases == (entry.Index < index ? 1 : 0), "Release failure skipped or repeated a slot.");
            DisposeSuccessfully(pipeline, 12);
            Check(Ftd3Native.ReleaseCalls == 13, "Retry did not only retry the failed release.");
            cases++;
        }
    }

    private static void CancellationBoundaries()
    {
        foreach (string boundary in new[] { "initialize", "submit" })
        {
            for (int index = 0; index < 12; index++)
            {
                var pipeline = NewPipeline();
                using (var source = new CancellationTokenSource())
                {
                    int at = index;
                    Ftd3Native.After = (kind, slot) => { if (kind == boundary && slot == at) source.Cancel(); };
                    Throws<OperationCanceledException>(() => pipeline.Read(new byte[64], source.Token));
                    Check(Ftd3Native.SubmitCalls == (boundary == "submit" ? index + 1 : 0), "Cancellation submitted a later read.");
                    Check(Ftd3Native.ReleaseCalls == 0, "Caller cancellation freed native storage.");
                    DisposeSuccessfully(pipeline, boundary == "initialize" ? index + 1 : 12);
                }
                cases++;
            }
        }

        var cancelledPipeline = NewPipeline();
        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            Throws<OperationCanceledException>(() => cancelledPipeline.Read(new byte[64], source.Token));
            Check(Ftd3Native.InitializeCalls == 0, "Pre-cancelled read performed native initialization.");
            DisposeSuccessfully(cancelledPipeline, 0);
        }
        cases++;

        var pollingPipeline = NewPipeline();
        using (var source = new CancellationTokenSource())
        {
            Ftd3Native.QueryStatus = 25;
            Ftd3Native.After = (kind, slot) => { if (kind == "query") source.Cancel(); };
            Throws<OperationCanceledException>(() => pollingPipeline.Read(new byte[64], source.Token));
            Check(Ftd3Native.QueryCalls == 1 && Ftd3Native.ReleaseCalls == 0, "Polling cancellation performed another native operation.");
            Storage(pollingPipeline, false);
            DisposeSuccessfully(pollingPipeline, 12);
        }
        cases++;

        var drainingPipeline = NewPipeline();
        using (var source = new CancellationTokenSource())
        {
            drainingPipeline.Read(new byte[64], source.Token);
            Ftd3Native.After = (kind, slot) => { if (kind == "query") source.Cancel(); };
            drainingPipeline.Dispose();
            Check(source.IsCancellationRequested, "Drain cancellation injection did not run.");
            Storage(drainingPipeline, true);
            ReleasedExactly(12);
        }
        cases++;
    }

    private static void NativeThrowRetainsAcceptedRequest()
    {
        foreach (int index in new[] { 0, 5, 11 })
        {
            var pipeline = NewPipeline();
            Ftd3Native.SubmitThrow = index;
            Throws<InvalidOperationException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            Check(Ftd3Native.SubmitCalls == index + 1 && Ftd3Native.ReleaseCalls == 0, "Native throw lost partial startup ownership.");
            Storage(pipeline, false);
            DisposeSuccessfully(pipeline, 12);
            Check(Ftd3Native.QueryCalls == index + 1, "Ambiguous thrown submission was not drained.");
            cases++;
        }
    }

    private static void AmbiguousSubmissionRetainsStorage()
    {
        foreach (int status in new[] { 25, 32 })
        {
            var pipeline = NewPipeline();
            Ftd3Native.SubmitStatus = status;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Read(new byte[64], CancellationToken.None));
            Check(Ftd3Native.SubmitCalls == 1 && Ftd3Native.ReleaseCalls == 0,
                "Ambiguous submission continued startup or released native storage.");
            Ftd3Native.QueryStatus = 32;
            Throws<Capture3DS.Capture3DSException>(() => pipeline.Dispose());
            Check(Ftd3Native.QueryCalls == 1 && Ftd3Native.ReleaseCalls == 0,
                "Ambiguous submission did not retain a pending query obligation.");
            Storage(pipeline, false);
            DisposeSuccessfully(pipeline, 12);
            Check(Ftd3Native.QueryCalls == 2, "Late completion of ambiguous submission was not drained exactly once.");
            cases++;
        }
    }

    private static void SingleDeadlineAndLateCompletion()
    {
        var pipeline = NewPipeline();
        pipeline.Read(new byte[64], CancellationToken.None);
        Ftd3Native.QueryStatus = 25;
        var elapsed = Stopwatch.StartNew();
        Throws<Capture3DS.Capture3DSException>(() => pipeline.Dispose());
        elapsed.Stop();
        Check(elapsed.ElapsedMilliseconds >= 2400 && elapsed.ElapsedMilliseconds < 10000,
            "Drain did not obey one bounded deadline: " + elapsed.ElapsedMilliseconds);
        Check(Ftd3Native.ReleaseCalls == 0, "Deadline expiration released a pending request.");
        Storage(pipeline, false);
        DisposeSuccessfully(pipeline, 12);
        Console.WriteLine("One real managed drain deadline: " + elapsed.ElapsedMilliseconds + " ms; late completion recovered.");
        cases++;
    }

    public static int Main()
    {
        try
        {
            PartialInitializeFailures();
            TerminalErrors();
            UnknownCompletionRetainsOwnership();
            ReleaseFailuresAreRetryable();
            CancellationBoundaries();
            NativeThrowRetainsAcceptedRequest();
            AmbiguousSubmissionRetainsStorage();
            SingleDeadlineAndLateCompletion();
            Ftd3Native.Reset();
            Console.WriteLine("PASS: Ftd3ReadPipeline failure lifecycle, " + cases + " cases / " + checks + " checks; hardware-free.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + cases + " cases / " + checks + " checks: " + exception);
            return 1;
        }
    }
}
