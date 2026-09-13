using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Capture3DS.Ftd3
{
    /// <summary>
    /// One caller-owned N3DS read ring. Keep USB requests pending while that
    /// caller decodes/publishes the previous frame. No additional handle owner,
    /// background thread, unbounded queue, audio transformation, or finalizer.
    /// </summary>
    internal sealed class Ftd3ReadPipeline : IDisposable
    {
        internal const int SlotCount = 12;
        private const int CompletionDeadlineMs = 2500;
        private readonly IntPtr handle;
        private readonly byte pipe;
        private readonly int bufferSize;
        private readonly Slot[] slots = new Slot[SlotCount];
        private int next;
        private bool started, faulted, disposed;

        private sealed class Slot
        {
            internal IntPtr Buffer, Overlapped, Count;
            internal WaitHandle CompletionEvent;
            internal bool Initialized, Pending;
            internal uint Transferred;
        }

        private sealed class BorrowedEvent : WaitHandle
        {
            internal BorrowedEvent(IntPtr eventHandle)
            {
                SafeWaitHandle = new SafeWaitHandle(eventHandle, false);
            }
        }

        internal Ftd3ReadPipeline(IntPtr handle, byte pipe, int bufferSize)
        {
            if (handle == IntPtr.Zero) throw new ArgumentException("Missing device handle.", nameof(handle));
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            this.handle = handle;
            this.pipe = pipe;
            this.bufferSize = bufferSize;
        }

        private void Start(CancellationToken cancellationToken)
        {
            // Install each slot before allocating/initializing it, so Dispose
            // can recover partial startup without losing native ownership.
            for (int i = 0; i < slots.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slot = new Slot();
                slots[i] = slot;
                slot.Buffer = Marshal.AllocHGlobal(bufferSize);
                slot.Count = Marshal.AllocHGlobal(sizeof(uint));
                int overlappedSize = Marshal.SizeOf(typeof(Ftd3Native.Overlapped));
                slot.Overlapped = Marshal.AllocHGlobal(overlappedSize);
                for (int offset = 0; offset < overlappedSize; offset += sizeof(int))
                    Marshal.WriteInt32(slot.Overlapped, offset, 0);
                int status = Ftd3Native.FT_InitializeOverlapped(handle, slot.Overlapped);
                if (status == Ftd3Native.FT_OK) slot.Initialized = true;
                cancellationToken.ThrowIfCancellationRequested();
                if (status != Ftd3Native.FT_OK)
                    throw new Capture3DSException($"FT_InitializeOverlapped failed: 0x{status:X}");
                IntPtr eventHandle = ((Ftd3Native.Overlapped)Marshal.PtrToStructure(
                    slot.Overlapped, typeof(Ftd3Native.Overlapped))).Event;
                if (eventHandle == IntPtr.Zero || eventHandle == new IntPtr(-1))
                    throw new Capture3DSException("FT_InitializeOverlapped returned no completion event.");
                // The vendor owns hEvent; only FT_ReleaseOverlapped closes it.
                slot.CompletionEvent = new BorrowedEvent(eventHandle);
            }
            for (int i = 0; i < slots.Length; i++) Submit(slots[i], cancellationToken);
            started = true;
        }

        private void Submit(Slot slot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot.Pending) throw new InvalidOperationException("Cannot reuse an in-flight USB read.");
            Marshal.WriteInt32(slot.Count, 0);
            // Conservatively retain the storage if a native invocation throws.
            slot.Pending = true;
            int status = Ftd3Native.FT_ReadPipeAsync(handle, pipe, slot.Buffer,
                (uint)bufferSize, slot.Count, slot.Overlapped);
            if (status == Ftd3Native.FT_OK)
            {
                slot.Transferred = unchecked((uint)Marshal.ReadInt32(slot.Count));
                slot.Pending = false;
            }
            else if (status != Ftd3Native.FT_IO_PENDING)
            {
                // Ordinary submission failures have not accepted a request.
                // 25 (incomplete) and 32 (catch-all unknown native error) do not
                // establish that guarantee; let shutdown confirm their result.
                slot.Pending = status == Ftd3Native.FT_IO_INCOMPLETE || status == 32;
                cancellationToken.ThrowIfCancellationRequested();
                throw new Capture3DSException($"FT_ReadPipe asynchronous submit failed: 0x{status:X}");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private int Complete(Slot slot, CancellationToken cancellationToken, Stopwatch deadline)
        {
            while (slot.Pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint transferred;
                int status = Ftd3Native.FT_GetOverlappedResult(handle, slot.Overlapped, out transferred, false);
                bool completed = status == Ftd3Native.FT_OK || status == 19 || status == 20 || status == 30;
                if (completed)
                {
                    slot.Transferred = transferred;
                    slot.Pending = false;
                }
                // Preserve the native ownership outcome before observing stop;
                // cancellation then wins over an error returned at that boundary.
                cancellationToken.ThrowIfCancellationRequested();
                if (status != Ftd3Native.FT_IO_PENDING && status != Ftd3Native.FT_IO_INCOMPLETE)
                {
                    // These statuses are completed Win32 I/O results in D3XX.
                    // Unknown/invalid-handle/query errors are NOT proof that the
                    // kernel has stopped using this slot: retain it on failure.
                    if (!completed)
                        throw new Capture3DSException($"Cannot confirm N3DS USB completion: 0x{status:X}; native storage is retained.");
                    return status;
                }
                if (deadline.ElapsedMilliseconds >= CompletionDeadlineMs)
                    throw new Capture3DSException("Timed out waiting for an N3DS USB read to finish; its native storage is retained.");
                // Finite waits only. Cancellation never closes/aborts from the UI.
                slot.CompletionEvent.WaitOne(5);
            }
            return Ftd3Native.FT_OK;
        }

        internal uint Read(byte[] destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed) throw new ObjectDisposedException(nameof(Ftd3ReadPipeline));
            if (faulted) throw new Capture3DSException("The N3DS read ring must be closed before reconnecting.");
            if (destination == null || destination.Length < bufferSize)
                throw new ArgumentException("The destination must hold one complete USB transfer.", nameof(destination));
            try
            {
                if (!started) Start(cancellationToken);
                Slot slot = slots[next];
                int status = Complete(slot, cancellationToken, Stopwatch.StartNew());
                cancellationToken.ThrowIfCancellationRequested();
                if (status != Ftd3Native.FT_OK)
                    throw new Capture3DSException($"FT_GetOverlappedResult failed: 0x{status:X}");
                uint transferred = slot.Transferred;
                if (transferred > bufferSize)
                    throw new Capture3DSException("N3DS USB transfer exceeds its allocated buffer.");
                // Copy before reusing this slot. The decoder returns independent
                // RGB/PCM arrays; neither it nor NX ever sees an in-flight buffer.
                Marshal.Copy(slot.Buffer, destination, 0, (int)transferred);
                Submit(slot, cancellationToken);
                next = (next + 1) % slots.Length;
                return transferred;
            }
            catch
            {
                faulted = true;
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            faulted = true;
            // First drain ALL accepted requests, without submitting replacements.
            // FT_SetPipeTimeout remains finite. Never release an in-flight slot,
            // and do not reintroduce the old AbortPipe-on-shutdown kernel hang.
            var deadline = Stopwatch.StartNew();
            foreach (Slot slot in slots)
                if (slot != null && slot.Pending) Complete(slot, CancellationToken.None, deadline);
            foreach (Slot slot in slots)
            {
                if (slot == null) continue;
                if (slot.Initialized)
                {
                    int status = Ftd3Native.FT_ReleaseOverlapped(handle, slot.Overlapped);
                    if (status != Ftd3Native.FT_OK)
                        throw new Capture3DSException($"FT_ReleaseOverlapped failed: 0x{status:X}");
                    slot.Initialized = false;
                }
                if (slot.CompletionEvent != null) { slot.CompletionEvent.Dispose(); slot.CompletionEvent = null; }
                if (slot.Buffer != IntPtr.Zero) { Marshal.FreeHGlobal(slot.Buffer); slot.Buffer = IntPtr.Zero; }
                if (slot.Count != IntPtr.Zero) { Marshal.FreeHGlobal(slot.Count); slot.Count = IntPtr.Zero; }
                if (slot.Overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(slot.Overlapped); slot.Overlapped = IntPtr.Zero; }
            }
            // The device may FT_Close only after this succeeds. Failure leaves
            // its existing device lease and all still-owned storage intact.
            disposed = true;
        }
    }
}
