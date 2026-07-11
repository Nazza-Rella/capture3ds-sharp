using System;
using System.IO;
using System.Runtime.InteropServices;
using CyUSB;

namespace Capture3DS.Cypress
{
    // Port of cc3dsfs (MIT) Optimize_3DS capture_start() for the Old 3DS device.
    // cc3dsfs documents this protocol as developed from Wireshark USB captures.
    //
    // This runs the full FPGA programmable-logic upload and device-id handshake
    // that brings the freshly firmware-loaded 04B4:1004 device up to a streaming
    // state. The product key / device id are used only for the protocol and are
    // never logged.
    public sealed partial class LlSpa3Device
    {
        private const string Fpga888ResourceName = "Capture3DS.optimize_old_3ds_888_fpga_pl.bin";
        private const int OptimizeEepromNewSize = 0x80;
        private const int OptimizeEepromOperationSize = 0x10;

        private static byte[] _fpga888Cache;

        // Diagnostic only: true/false once capture_start has read the live device
        // id and compared it against the product key. Null if no key was supplied.
        // The underlying ids are never exposed.
        public bool? DeviceIdMatchesKey { get; private set; }

        // Only cc3dsfs Optimize New 3DS (bcd 0xFE00) has an EEPROM (has_eeprom=true).
        // The board is an Optimize Old 3DS: our self-loaded firmware
        // (optimize_old_3ds_fw.bin) renumerates to bcd base 0xFD00, so has_eeprom is
        // false. The EEPROM read and the two "first unknown value" reads in
        // read_device_id_serial must then be skipped, or the FX2 command stream
        // desyncs and EP 0x82 streams zero data. Set from bcdDevice in Connect().
        private const ushort OptimizeNew3dsWantedValueBase = 0xFE00;
        private const ushort OptimizeBcdDeviceMask = 0xFF00;
        private bool _hasEeprom;

        // cc3dsfs capture_start(is_first_load=true). is_rgb888 selects the FPGA
        // bitstream; the existing decoder consumes the packed RGB8 (888) layout.
        private void RunOptimizeCaptureStart(bool isRgb888, bool is3d, string key)
        {
            if (!isRgb888)
            {
                throw new Capture3DSException("LL-SPA3 capture_start currently supports only the RGB8 (888) FPGA bitstream.");
            }

            // Stop any prior capture session so the FX2 returns to idle before we
            // start. cc3dsfs always pairs capture_start with a preceding capture_end;
            // without it a previously started session (e.g. one that crashed, or a
            // probe run that exited without stopping) leaves the FX2 free-running.
            // The next start then begins mid-frame, so the slice stream carries a
            // constant byte offset that shears/tears the decoded image.
            TryCaptureEnd();

            for (var i = 0; i < 3; i++)
            {
                ResetEndpoint(_ctrlBulkIn);
                ResetEndpoint(_bulkOut);
            }

            ulong deviceId = ReadDeviceIdSerial(isFirstLoad: true);

            // start_command_send (not old firmware).
            SendBulkOut(new byte[] { 0x65 });

            // is_new_device && has_eeprom: cc3dsfs reads the EEPROM here. The v2
            // device (our self-loaded firmware) has no EEPROM, so this block must be
            // skipped to keep the FX2 command stream in sync.
            if (_hasEeprom)
            {
                ReadDeviceEeprom(OptimizeEepromNewSize);
            }

            ResetEndpoint(_ctrlBulkIn);
            ResetEndpoint(_bulkOut);
            deviceId = ReadDeviceIdSerial(isFirstLoad: true);

            DeviceIdMatchesKey = ComputeDeviceIdMatchesKey(key, deviceId);

            FpgaPlLoad(LoadEmbeddedFpga888());
            InsertDeviceId(deviceId);

            // final_capture_start_transfer.
            SendBulkOut(new byte[] { 0x5B, 0x59, 0x03 });

            ResetEndpoint(_bulkIn);
        }

        // cc3dsfs capture_end: tell the FX2 to stop emitting frames (0x41) and reset
        // the command pipes. Best-effort: an idle device may not need it, so failures
        // are ignored.
        private void TryCaptureEnd()
        {
            try { SendBulkOut(new byte[] { 0x41 }); } catch { }
            ResetEndpoint(_ctrlBulkIn);
            ResetEndpoint(_bulkOut);
            ResetEndpoint(_bulkIn);
        }

        private ulong ReadDeviceIdSerial(bool isFirstLoad)
        {
            SendBulkOut(new byte[] { 0x64, 0x60, 0x01, 0xFF, 0xFF, 0x60, 0x02, 0x00, 0xFF, 0x00, 0xFF });

            // cc3dsfs: (is_new_device && has_eeprom) || is_old_firmware. The v2
            // device has no EEPROM and is not old firmware, so these two reads are
            // skipped to avoid desyncing the command stream.
            if (_hasEeprom)
            {
                ReadFirstUnkValue();
                ReadFirstUnkValue();
            }

            ulong deviceId = ReadCachedDeviceId(out bool isFull0s);
            if (isFull0s || isFirstLoad)
            {
                deviceId = ReadDirectSerialDeviceId();
            }

            return deviceId;
        }

        private static bool? ComputeDeviceIdMatchesKey(string key, ulong deviceId)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (LlSpa3OptimizeProtocol.TryGetDeviceIdFromKey(key, true, out var keyId) && keyId == deviceId)
            {
                return true;
            }

            if (LlSpa3OptimizeProtocol.TryGetDeviceIdFromKey(key, false, out keyId) && keyId == deviceId)
            {
                return true;
            }

            return false;
        }

        // cc3dsfs read_device_eeprom: block reads of 0x10 bytes. Result discarded.
        private void ReadDeviceEeprom(int readSize)
        {
            var numReads = (readSize + OptimizeEepromOperationSize - 1) / OptimizeEepromOperationSize;
            for (var i = 0; i < numReads; i++)
            {
                var single = readSize - (i * OptimizeEepromOperationSize);
                if (single > OptimizeEepromOperationSize)
                {
                    single = OptimizeEepromOperationSize;
                }

                SendBulkOut(new byte[] { 0x38, (byte)(i * OptimizeEepromOperationSize), 0x10, 0x30 });
                ReadControlBulkIn(single);
            }
        }

        private void ReadFirstUnkValue()
        {
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xC9, 0x60, 0x01, 0x20, 0xFF,
                0x61, 0x04, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x80, 0xFF,
                0x60, 0x01, 0x01, 0xFF
            });
            ReadControlBulkIn(4);
        }

        private ulong ReadCachedDeviceId(out bool isFull0s)
        {
            SendBulkOut(new byte[] { 0x70 });
            var data = ReadControlBulkIn(0x10);

            isFull0s = true;
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                {
                    isFull0s = false;
                    break;
                }
            }

            return ReadLe64(data, 0);
        }

        private ulong ReadDirectSerialDeviceId()
        {
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xD0, 0x60, 0x02, 0x30, 0xFF,
                0x60, 0xCB, 0x60, 0x02, 0x00, 0xFF, 0x00, 0xFF
            });
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xF1, 0x60, 0x01, 0x20, 0xFF,
                0x61, 0x08, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF,
                0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x80, 0xFF, 0x60, 0x01,
                0x01, 0xFF, 0x60, 0x02, 0x00, 0xFF, 0x00, 0xFF
            });

            var data = ReadControlBulkIn(8);
            if (data.Length < 8)
            {
                throw new Capture3DSException("LL-SPA3 serial device-id read returned too few bytes.");
            }

            return BytesToSerialDeviceId(data);
        }

        // cc3dsfs fpga_pl_load: framing buffers, then 62-byte payload pieces under
        // a { 0x60, 0x1F } header, then closing framing buffers.
        private void FpgaPlLoad(byte[] fpgaPl)
        {
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x00, 0xFF, 0x00, 0xFF, 0x60, 0x02, 0x30, 0xFF,
                0x60, 0xCB, 0x60, 0x02, 0x30, 0xFF, 0x60, 0xC5, 0x66, 0x64,
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xC5
            });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x20, 0xFF });
            SendBulkOut(new byte[] { 0x60, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00 });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x01, 0xFF });
            SendBulkOut(new byte[] { 0x60, 0x02, 0x30, 0xFF, 0x60, 0xC5 });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x20, 0xFF });

            const int totalSize = 64;
            const int headerSize = 2;
            const int pieceSize = totalSize - headerSize;
            var plBuffer = new byte[totalSize];
            plBuffer[0] = 0x60;
            plBuffer[1] = 0x1F;
            var numIters = (fpgaPl.Length + pieceSize - 1) / pieceSize;
            for (var i = 0; i < numIters; i++)
            {
                var remaining = fpgaPl.Length - (pieceSize * i);
                if (remaining > pieceSize)
                {
                    remaining = pieceSize;
                }

                Buffer.BlockCopy(fpgaPl, pieceSize * i, plBuffer, headerSize, remaining);
                SendBulkOut(plBuffer, headerSize + remaining);
            }

            SendBulkOut(new byte[]
            {
                0x60, 0x0B, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x04,
                0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00,
                0x00, 0x04, 0x80, 0x00
            });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x01, 0xFF });
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xD6, 0x60, 0x02, 0x00, 0xFF,
                0x00, 0xFF, 0x60, 0x02, 0x30, 0xFF, 0x60, 0xFF, 0x60, 0x01,
                0x20, 0xFF
            });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x80, 0x00 });
            SendBulkOut(new byte[] { 0x60, 0x01, 0x01, 0xFF });
        }

        private void InsertDeviceId(ulong deviceId)
        {
            SendBulkOut(new byte[]
            {
                0x60, 0x02, 0x30, 0xFF, 0x60, 0xCC, 0x60, 0x02, 0x00, 0xFF,
                0x00, 0xFF, 0x60, 0x02, 0x30, 0xFF, 0x60, 0xFF, 0x60, 0x02,
                0x30, 0xFF, 0x60, 0xFF
            });

            // device_id_buffer_old_3ds (is_new_device=false). The bytes at [9..12]
            // differ from the New 3DS buffer.
            var idBuffer = new byte[]
            {
                0x71, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A,
                0x0B, 0x01, 0x00, 0x02, 0x30, 0xFF, 0x60, 0x65
            };
            WriteLe64(idBuffer, 1, deviceId);
            SendBulkOut(idBuffer);
        }

        private void SendBulkOut(byte[] payload, int length)
        {
            var buffer = new byte[length];
            Buffer.BlockCopy(payload, 0, buffer, 0, length);
            var len = length;
            if (!_bulkOut.XferData(ref buffer, ref len) || len != length)
            {
                throw new Capture3DSException($"LL-SPA3 bulk-out command failed: transferred={len}/{length} lastError={_bulkOut.LastError}");
            }
        }

        private static byte[] LoadEmbeddedFpga888()
        {
            if (_fpga888Cache != null)
            {
                return _fpga888Cache;
            }

            var assembly = typeof(LlSpa3Device).Assembly;
            using (var stream = assembly.GetManifestResourceStream(Fpga888ResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        $"Embedded FPGA bitstream '{Fpga888ResourceName}' was not found in the assembly.");
                }

                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    _fpga888Cache = ms.ToArray();
                    return _fpga888Cache;
                }
            }
        }

        // cc3dsfs bytes_to_serial_device_id: bit-reverse + cross-byte shift.
        private static ulong BytesToSerialDeviceId(byte[] inBuffer)
        {
            var transformed = new byte[8];
            for (var i = 0; i < 7; i++)
            {
                transformed[i] = (byte)((ReverseU8(inBuffer[8 - 1 - i]) >> 7) | (ReverseU8(inBuffer[8 - 1 - i - 1]) << 1));
            }

            transformed[7] = (byte)(ReverseU8(inBuffer[0]) >> 7);
            return ReadLe64(transformed, 0);
        }

        private static byte ReverseU8(byte value)
        {
            byte result = 0;
            for (var i = 0; i < 8; i++)
            {
                result = (byte)((result << 1) | ((value >> i) & 1));
            }

            return result;
        }

        private static ulong ReadLe64(byte[] data, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                value |= (ulong)data[offset + i] << (8 * i);
            }

            return value;
        }

        private static void WriteLe64(byte[] data, int offset, ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                data[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
            }
        }

        // cc3dsfs reads EP 0x82 as a continuous stream of fixed-size async slices
        // (SINGLE_RING_BUFFER_SLICE_SIZE), keeping NUM_CONCURRENTLY_RUNNING_BUFFERS
        // overlapped reads in flight at all times. The FX2 stalls if the pipeline
        // ever drains, so we keep PipelineDepth reads armed and re-arm each slot
        // immediately after collecting it.
        private const int VideoSliceSize = 0x4000;
        private const int PipelineDepth = 64;

        private sealed class PipeSlot
        {
            public byte[] Cmd;
            public byte[] Buf;
            public byte[] Ov;
            public GCHandle CmdHandle;
            public GCHandle BufHandle;
            public GCHandle OvHandle;
            public IntPtr Evt;
            public bool InFlight;
        }

        private PipeSlot[] _pipe;
        private int _pipeHead;

        // Byte-stream resync buffer. The FX2 free-runs and emits frames back-to-back
        // as one continuous stream (cc3dsfs reads EP 0x82 the same way), so a session
        // always begins mid-frame and slice 0 is never frame 0. We accumulate the
        // stream here and scan for the column-0 header to lock onto the real frame
        // boundary. Naive slice concatenation keeps a constant sub-frame offset that
        // both rolls the image and (when the offset is not a multiple of 3) rotates
        // the RGB channels - the split / duplicated / colour-swapped frame.
        private const ushort OptimizeSyncMagic = 0xCC33;
        private byte[] _stream = Array.Empty<byte>();
        private int _streamLen;
        private bool _synced;

        private void ArmOptimizePipeline()
        {
            TeardownOptimizePipeline();
            _bulkIn.XferSize = VideoSliceSize;
            _pipe = new PipeSlot[PipelineDepth];
            _pipeHead = 0;
            _streamLen = 0;
            _synced = false;
            for (var i = 0; i < PipelineDepth; i++)
            {
                var slot = new PipeSlot();
                var ovSize = Math.Max(CyConst.OverlapSignalAllocSize, Marshal.SizeOf(typeof(OVERLAPPED)));
                slot.Ov = new byte[ovSize];
                slot.OvHandle = GCHandle.Alloc(slot.Ov, GCHandleType.Pinned);
                slot.Evt = PInvoke.CreateEvent(0, 0, 0, 0);
                slot.Buf = new byte[VideoSliceSize];
                var cmdLength = CyConst.SINGLE_XFER_LEN + ((_bulkIn.XferMode == XMODE.BUFFERED) ? VideoSliceSize : 0);
                slot.Cmd = new byte[cmdLength];
                // The kernel DMAs into Buf and reads/writes the SINGLE_TRANSFER in Cmd
                // for the whole lifetime of the overlapped read. They must stay pinned
                // (matches the official CyUSB Streamer sample) or GC compaction during
                // an in-flight transfer corrupts the heap (ExecutionEngineException).
                slot.BufHandle = GCHandle.Alloc(slot.Buf, GCHandleType.Pinned);
                slot.CmdHandle = GCHandle.Alloc(slot.Cmd, GCHandleType.Pinned);
                _pipe[i] = slot;
                ArmSlot(slot);
            }
        }

        private void ArmSlot(PipeSlot slot)
        {
            var ov = (OVERLAPPED)Marshal.PtrToStructure(slot.OvHandle.AddrOfPinnedObject(), typeof(OVERLAPPED));
            ov.hEvent = slot.Evt;
            Marshal.StructureToPtr(ov, slot.OvHandle.AddrOfPinnedObject(), true);

            var len = VideoSliceSize;
            if (!_bulkIn.BeginDataXfer(ref slot.Cmd, ref slot.Buf, ref len, ref slot.Ov))
            {
                slot.InFlight = false;
                throw new Capture3DSException($"LL-SPA3 pipeline BeginDataXfer failed: lastError={_bulkIn.LastError}");
            }

            slot.InFlight = true;
        }

        // Wait for the oldest in-flight slice, copy it out, then re-arm that slot
        // and advance the ring head so the pipeline stays full.
        private int CollectOptimizeSlice(byte[] dest, int destOffset, int timeoutMs)
        {
            var slot = _pipe[_pipeHead];
            if (!_bulkIn.WaitForXfer(slot.Evt, (uint)timeoutMs))
            {
                _bulkIn.Abort();
                PInvoke.WaitForSingleObject(slot.Evt, 500);
                throw new Capture3DSException($"LL-SPA3 pipeline slice timed out: lastError={_bulkIn.LastError}");
            }

            var len = 0;
            if (!_bulkIn.FinishDataXfer(ref slot.Cmd, ref slot.Buf, ref len, ref slot.Ov) || len < 0)
            {
                throw new Capture3DSException($"LL-SPA3 pipeline FinishDataXfer failed: length={len} lastError={_bulkIn.LastError}");
            }

            if (len > 0)
            {
                Buffer.BlockCopy(slot.Buf, 0, dest, destOffset, len);
            }

            ArmSlot(slot);
            _pipeHead = (_pipeHead + 1) % PipelineDepth;
            return len;
        }

        // Pull one aligned frame out of the continuous slice stream. Like cc3dsfs's
        // not-synchronized / synchronized state machine, we first scan for a column-0
        // header to find the frame boundary, then return exactly targetLength bytes
        // starting there. Every subsequent frame is re-verified at offset 0 and we
        // fall back to a rescan if the header is missing, so a dropped slice cannot
        // permanently shear the video.
        private byte[] ReadOptimizeFrameFromPipeline(int targetLength, int timeoutMs)
        {
            while (true)
            {
                if (!_synced)
                {
                    var pos = FindFrameStart(_stream, _streamLen);
                    if (pos < 0)
                    {
                        // No header yet. Drop everything except a short even-length
                        // tail (a header may straddle the slice boundary) and pull
                        // more data. Keeping the shift even preserves 16-bit header
                        // alignment, matching cc3dsfs's i*2 scan.
                        var keep = _streamLen >= 4 ? 4 : (_streamLen & ~1);
                        ShiftStream(_streamLen - keep);
                        FillMoreSlices(timeoutMs);
                        continue;
                    }

                    ShiftStream(pos);
                    _synced = true;
                }

                while (_streamLen < targetLength)
                {
                    FillMoreSlices(timeoutMs);
                }

                if (!IsFrameStart(_stream, 0))
                {
                    // Lost alignment (e.g. a slice was short). Re-scan from scratch.
                    _synced = false;
                    continue;
                }

                if (!IsFrameInternallyAligned(_stream, 0))
                {
                    // A mid-frame stream gap sheared the later columns even though the
                    // offset-0 header still matches. Step past this false start so the
                    // rescan locks onto the next genuine column-0 boundary; the torn
                    // frame is dropped (caller keeps the previous frame) rather than
                    // shown with garbled right-side columns.
                    ShiftStream(2);
                    _synced = false;
                    continue;
                }

                var frame = new byte[targetLength];
                Buffer.BlockCopy(_stream, 0, frame, 0, targetLength);
                ShiftStream(targetLength);
                return frame;
            }
        }

        // cc3dsfs get_is_pos_first_synch_in_buffer (non-old-firmware path). Our board
        // runs the new firmware (bcd 0xFD00): every column begins with the 16-bit
        // SYNCH_VALUE_OPTIMIZE magic 0xCC33, followed by a column_info word whose low
        // 10 bits are the column index. Frame start is column 0. Verified against a
        // raw EP 0x82 dump: column-0 hits land exactly FrameSize (583840) apart.
        private static bool IsFrameStart(byte[] buf, int pos)
        {
            var magic = (ushort)(buf[pos] | (buf[pos + 1] << 8));
            if (magic != OptimizeSyncMagic)
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

        // Every column 0..399 starts with magic 0xCC33 and a column_info word whose
        // low 10 bits equal the column index (the trailing bottom-only block has no
        // header). The free-running EP 0x82 stream can lose a chunk mid-frame when the
        // host pipeline re-arms a hair too late; that gap shifts every column after it,
        // so the offset-0 header still matches while the later (right-side) columns are
        // sheared. Verify all column headers before accepting the frame: a mismatch
        // means the frame is torn, and we drop+resync instead of emitting garbage.
        private const int SyncColumnStride = 1456;
        private const int SyncColumnCount = 400;

        private static bool IsFrameInternallyAligned(byte[] buf, int frameStart)
        {
            for (var col = 0; col < SyncColumnCount; col++)
            {
                var pos = frameStart + (col * SyncColumnStride);
                var magic = (ushort)(buf[pos] | (buf[pos + 1] << 8));
                if (magic != OptimizeSyncMagic)
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

        private void FillMoreSlices(int timeoutMs)
        {
            EnsureStreamCapacity(_streamLen + VideoSliceSize);
            _streamLen += CollectOptimizeSlice(_stream, _streamLen, timeoutMs);
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

        private void TeardownOptimizePipeline()
        {
            if (_pipe == null)
            {
                return;
            }

            // Stop any in-flight DMA before unpinning so the kernel is not still
            // writing into a buffer we are about to release.
            if (_bulkIn != null)
            {
                try { _bulkIn.Abort(); } catch { }
            }

            // Drain the cancelled IRPs: Abort() only requests cancellation, so the
            // overlapped reads are still pending in the kernel. If we close/reopen the
            // device handle before they finish, the first command on the freshly
            // opened pipe returns ERROR_IO_PENDING (997) and reconnect fails. Wait for
            // each in-flight slot's event to signal (completion of the cancelled IRP)
            // before unpinning, then reset the pipe so it is settled for the next open.
            foreach (var slot in _pipe)
            {
                if (slot != null && slot.InFlight && slot.Evt != IntPtr.Zero)
                {
                    PInvoke.WaitForSingleObject(slot.Evt, 500);
                    slot.InFlight = false;
                }
            }

            if (_bulkIn != null)
            {
                try { _bulkIn.Reset(); } catch { }
            }

            foreach (var slot in _pipe)
            {
                if (slot == null)
                {
                    continue;
                }

                if (slot.BufHandle.IsAllocated)
                {
                    slot.BufHandle.Free();
                }

                if (slot.CmdHandle.IsAllocated)
                {
                    slot.CmdHandle.Free();
                }

                if (slot.OvHandle.IsAllocated)
                {
                    slot.OvHandle.Free();
                }

                if (slot.Evt != IntPtr.Zero)
                {
                    PInvoke.CloseHandle(slot.Evt);
                    slot.Evt = IntPtr.Zero;
                }
            }

            _pipe = null;
            _pipeHead = 0;
            _streamLen = 0;
            _synced = false;
        }
    }
}
