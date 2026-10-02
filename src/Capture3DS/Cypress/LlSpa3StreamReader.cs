using System;
using System.Collections.Generic;
using System.Threading;

namespace Capture3DS.Cypress
{
    // Ring of overlapped EP 0x82 reads. Only the reader thread calls these
    // methods, so every transfer is issued and cancelled by the same thread.
    internal interface ILlSpa3SliceEndpoint
    {
        int SlotCount { get; }

        byte[] SlotBuffer(int slot);

        // Queue a read into the slot. False when the request could not be issued.
        bool Arm(int slot);

        // True once the queued read has completed (successfully or not).
        bool Wait(int slot, int timeoutMs);

        // Collect a completed read. False when the USB transfer itself failed.
        bool Finish(int slot, out int length);

        // Cancel every queued read and wait for the cancellations to complete.
        void CancelAll();

        // Clear a halted pipe after a failed transfer.
        void ResetPipe();
    }

    // Receives the LL-SPA3 video stream on a dedicated high-priority thread, so
    // decoding and drawing on the caller's thread never delay re-arming reads.
    // The FX2 free-runs and emits frames back-to-back as one continuous stream;
    // this thread locks onto the column-0 header, cuts out aligned frames and
    // queues them for ReadFrame.
    //
    // A failed transfer leaves a gap in the byte stream and may halt the pipe.
    // It is recovered in place (cancel, clear the halt, re-arm, resync on the
    // next frame) instead of being surfaced as a disconnect. Only a stream that
    // stays silent for stallTimeoutMs, or transfers that keep failing without
    // any data in between, are reported to the caller as a fault.
    internal sealed class LlSpa3StreamReader : IDisposable
    {
        // Frames waiting for the caller. When the caller falls behind, the
        // oldest frame is dropped so the delay never exceeds this many frames.
        internal const int QueueCapacity = 4;

        private const int PollMs = 50;
        private const int MaxRecoveriesWithoutData = 3;
        private const int JoinTimeoutMs = 5000;
        private const ushort SyncMagic = 0xCC33;

        private readonly ILlSpa3SliceEndpoint _endpoint;
        private readonly int _stallTimeoutMs;
        private readonly LlSpa3FrameLayout _layout;
        private readonly object _sync = new object();
        private readonly Queue<byte[]> _frames = new Queue<byte[]>();
        private readonly Stack<byte[]> _pool = new Stack<byte[]>();
        private Thread _thread;
        private volatile bool _stopRequested;
        private bool _armed;
        private bool _exited;
        private Exception _fault;

        // Owned by the reader thread.
        private byte[] _stream = Array.Empty<byte>();
        private int _streamLen;
        private bool _synced;
        private int _head;

        private long _transferErrors;
        private long _recoveries;
        private long _tornFrames;
        private long _overflowDrops;

        internal LlSpa3StreamReader(ILlSpa3SliceEndpoint endpoint, int stallTimeoutMs, LlSpa3FrameLayout layout)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _stallTimeoutMs = stallTimeoutMs;
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }

        internal long TransferErrors => Interlocked.Read(ref _transferErrors);
        internal long Recoveries => Interlocked.Read(ref _recoveries);
        internal long TornFrames => Interlocked.Read(ref _tornFrames);
        internal long OverflowDrops => Interlocked.Read(ref _overflowDrops);

        // Starts the thread and returns once every slot is armed. The capture
        // DMA must not be triggered before that: the FX2 stalls frame generation
        // when no reads are queued.
        internal void Start()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "LlSpa3UsbReader",
                Priority = ThreadPriority.Highest
            };
            _thread = thread;
            thread.Start();

            lock (_sync)
            {
                while (!_armed && !_exited)
                {
                    Monitor.Wait(_sync);
                }

                if (!_armed)
                {
                    throw new Capture3DSException(
                        "LL-SPA3 pipeline could not be armed: " + (_fault != null ? _fault.Message : "reader stopped"), _fault);
                }
            }
        }

        // Next aligned frame, exactly as long as the frame layout requires. Hand
        // it back with Return once it has been decoded.
        internal byte[] Take(int timeoutMs)
        {
            var start = Environment.TickCount;
            lock (_sync)
            {
                while (_frames.Count == 0)
                {
                    if (_fault != null)
                    {
                        throw new Capture3DSException("LL-SPA3 USB reader stopped: " + _fault.Message, _fault);
                    }

                    if (_exited)
                    {
                        throw new Capture3DSException("LL-SPA3 USB reader is not running.");
                    }

                    var remaining = timeoutMs - unchecked(Environment.TickCount - start);
                    if (remaining <= 0)
                    {
                        throw new Capture3DSException($"LL-SPA3 frame wait timed out after {timeoutMs} ms.");
                    }

                    Monitor.Wait(_sync, remaining);
                }

                return _frames.Dequeue();
            }
        }

        internal void Return(byte[] frame)
        {
            if (frame == null)
            {
                return;
            }

            lock (_sync)
            {
                if (_pool.Count < QueueCapacity + 2)
                {
                    _pool.Push(frame);
                }
            }
        }

        // Stops the thread. The thread cancels its own transfers before it exits,
        // so the slot buffers may be released afterwards. Throws if the thread
        // does not stop; the caller must then keep the buffers pinned.
        public void Dispose()
        {
            var thread = _thread;
            _stopRequested = true;
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(JoinTimeoutMs))
            {
                throw new Capture3DSException("LL-SPA3 USB reader thread did not stop.");
            }

            _thread = null;
        }

        private void Run()
        {
            try
            {
                ArmAll();
                lock (_sync)
                {
                    _armed = true;
                    Monitor.PulseAll(_sync);
                }

                var lastDataTicks = Environment.TickCount;
                var recoveriesWithoutData = 0;
                while (!_stopRequested)
                {
                    if (!_endpoint.Wait(_head, PollMs))
                    {
                        if (unchecked(Environment.TickCount - lastDataTicks) >= _stallTimeoutMs)
                        {
                            throw new Capture3DSException($"LL-SPA3 stream stalled: no USB data for {_stallTimeoutMs} ms.");
                        }

                        continue;
                    }

                    if (_endpoint.Finish(_head, out var length))
                    {
                        if (length > 0)
                        {
                            lastDataTicks = Environment.TickCount;
                            recoveriesWithoutData = 0;
                            AppendSlice(_endpoint.SlotBuffer(_head), length);
                        }

                        if (_endpoint.Arm(_head))
                        {
                            _head = (_head + 1) % _endpoint.SlotCount;
                            continue;
                        }
                    }

                    Interlocked.Increment(ref _transferErrors);
                    if (++recoveriesWithoutData > MaxRecoveriesWithoutData)
                    {
                        throw new Capture3DSException("LL-SPA3 USB transfers keep failing; the device may have been disconnected.");
                    }

                    _endpoint.CancelAll();
                    _endpoint.ResetPipe();
                    _streamLen = 0;
                    _synced = false;
                    ArmAll();
                    Interlocked.Increment(ref _recoveries);
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (!_stopRequested)
                    {
                        _fault = ex;
                    }
                }
            }
            finally
            {
                try { _endpoint.CancelAll(); } catch { }
                lock (_sync)
                {
                    _exited = true;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        private void ArmAll()
        {
            for (var i = 0; i < _endpoint.SlotCount; i++)
            {
                if (!_endpoint.Arm(i))
                {
                    throw new Capture3DSException($"LL-SPA3 pipeline could not queue read slot {i}.");
                }
            }

            _head = 0;
        }

        // Append one slice and queue every complete, aligned frame it finishes.
        // Every frame is re-verified at offset 0 and on all column headers, so a
        // gap in the stream drops the torn frame and rescans for the next one
        // instead of shearing the picture.
        private void AppendSlice(byte[] slice, int length)
        {
            EnsureStreamCapacity(_streamLen + length);
            Buffer.BlockCopy(slice, 0, _stream, _streamLen, length);
            _streamLen += length;

            while (true)
            {
                if (!_synced)
                {
                    var pos = FindFrameStart(_stream, _streamLen);
                    if (pos < 0)
                    {
                        // No header yet. Keep a short even-length tail in case a
                        // header straddles the slice boundary; the even shift keeps
                        // the 16-bit header alignment.
                        var keep = _streamLen >= 4 ? 4 : (_streamLen & ~1);
                        ShiftStream(_streamLen - keep);
                        return;
                    }

                    ShiftStream(pos);
                    _synced = true;
                }

                if (_streamLen < 4)
                {
                    return;
                }

                // The column-0 header states whether this frame carries the extra
                // 401st column instead of the header-less trailing block.
                var hasExtraHeader = LlSpa3Decoder.HasExtraHeaderColumn(_stream, 0);
                var targetLength = hasExtraHeader ? _layout.ExtraHeaderFrameSize : _layout.FrameSize;
                if (_streamLen < targetLength)
                {
                    return;
                }

                if (!IsFrameStart(_stream, 0))
                {
                    _synced = false;
                    continue;
                }

                if (!IsFrameInternallyAligned(_stream, 0, hasExtraHeader, _layout.ColumnStride))
                {
                    Interlocked.Increment(ref _tornFrames);
                    ShiftStream(2);
                    _synced = false;
                    continue;
                }

                QueueFrame(targetLength);
                ShiftStream(targetLength);
            }
        }

        private void QueueFrame(int length)
        {
            byte[] frame = null;
            lock (_sync)
            {
                while (frame == null && _pool.Count > 0)
                {
                    var pooled = _pool.Pop();
                    if (pooled.Length == length)
                    {
                        frame = pooled;
                    }
                }
            }

            if (frame == null)
            {
                frame = new byte[length];
            }

            Buffer.BlockCopy(_stream, 0, frame, 0, length);

            lock (_sync)
            {
                if (_frames.Count >= QueueCapacity)
                {
                    var dropped = _frames.Dequeue();
                    if (_pool.Count < QueueCapacity + 2)
                    {
                        _pool.Push(dropped);
                    }

                    Interlocked.Increment(ref _overflowDrops);
                }

                _frames.Enqueue(frame);
                Monitor.PulseAll(_sync);
            }
        }

        // Every column starts with the 16-bit magic 0xCC33, followed by a
        // column_info word whose low 10 bits are the column index. Frame start is
        // column 0.
        private static bool IsFrameStart(byte[] buf, int pos)
        {
            var magic = (ushort)(buf[pos] | (buf[pos + 1] << 8));
            if (magic != SyncMagic)
            {
                return false;
            }

            var columnInfo = (ushort)(buf[pos + 2] | (buf[pos + 3] << 8));
            return (columnInfo & 0x3FF) == 0;
        }

        private static int FindFrameStart(byte[] buf, int len)
        {
            for (var pos = 0; pos + 4 <= len; pos += 2)
            {
                if (IsFrameStart(buf, pos))
                {
                    return pos;
                }
            }

            return -1;
        }

        // A gap mid-frame shifts every later column, so the offset-0 header still
        // matches while the right-side columns are sheared. All column headers
        // must line up before the frame is accepted.
        private static bool IsFrameInternallyAligned(byte[] buf, int frameStart, bool hasExtraHeader, int columnStride)
        {
            var columnCount = hasExtraHeader ? LlSpa3Decoder.NumColumns + 1 : LlSpa3Decoder.NumColumns;
            for (var col = 0; col < columnCount; col++)
            {
                var pos = frameStart + (col * columnStride);
                var magic = (ushort)(buf[pos] | (buf[pos + 1] << 8));
                if (magic != SyncMagic)
                {
                    return false;
                }

                var columnInfo = (ushort)(buf[pos + 2] | (buf[pos + 3] << 8));
                if ((columnInfo & 0x3FF) != col)
                {
                    return false;
                }
            }

            return true;
        }

        private void EnsureStreamCapacity(int needed)
        {
            if (_stream.Length >= needed)
            {
                return;
            }

            var newCap = _stream.Length == 0 ? needed : _stream.Length;
            while (newCap < needed)
            {
                newCap *= 2;
            }

            var bigger = new byte[newCap];
            Buffer.BlockCopy(_stream, 0, bigger, 0, _streamLen);
            _stream = bigger;
        }

        private void ShiftStream(int n)
        {
            if (n <= 0)
            {
                return;
            }

            if (n >= _streamLen)
            {
                _streamLen = 0;
                return;
            }

            Buffer.BlockCopy(_stream, n, _stream, 0, _streamLen - n);
            _streamLen -= n;
        }
    }
}
