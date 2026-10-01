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

        // EP 0x82 is read as a continuous stream of 64 KiB overlapped reads, 16 of
        // them (1 MiB) in flight at all times. The FX2 stalls if the pipeline ever
        // drains; LlSpa3StreamReader keeps the ring full on its own thread.
        private const int VideoSliceSize = 0x10000;
        private const int PipelineDepth = 16;

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

        private CyUsbSliceEndpoint _sliceEndpoint;
        private LlSpa3StreamReader _reader;

        private void ArmOptimizePipeline()
        {
            TeardownOptimizePipeline();
            _bulkIn.XferSize = VideoSliceSize;
            _sliceEndpoint = new CyUsbSliceEndpoint(_bulkIn, PipelineDepth, VideoSliceSize);
            _reader = new LlSpa3StreamReader(_sliceEndpoint, TimeoutMs);
            _reader.Start();
        }

        private void TeardownOptimizePipeline()
        {
            if (_reader != null)
            {
                // Throws if the reader thread does not stop. The slot buffers then
                // stay pinned, because its transfers may still be in flight.
                _reader.Dispose();
                _reader = null;
            }

            if (_sliceEndpoint == null)
            {
                return;
            }

            // Drain any cancelled IRPs before the handle is closed or reopened: a
            // read still pending in the kernel makes the first command on a freshly
            // opened pipe return ERROR_IO_PENDING (997) and reconnect fails.
            _sliceEndpoint.CancelAll();
            if (_bulkIn != null)
            {
                try { _bulkIn.Reset(); } catch { }
            }

            _sliceEndpoint.Dispose();
            _sliceEndpoint = null;
        }

        // Overlapped CyUSB reads on EP 0x82. The kernel DMAs into Buf and updates
        // the SINGLE_TRANSFER in Cmd for the whole lifetime of a transfer, so both
        // stay pinned (matches the official CyUSB Streamer sample) until the
        // transfer is confirmed complete; GC compaction during an in-flight
        // transfer corrupts the heap (ExecutionEngineException).
        private sealed class CyUsbSliceEndpoint : ILlSpa3SliceEndpoint, IDisposable
        {
            private const int CancelDrainMs = 2000;
            private const int WaitObject0 = 0;

            private readonly CyBulkEndPoint _endpoint;
            private readonly PipeSlot[] _slots;
            private bool _transfersUnconfirmed;

            internal CyUsbSliceEndpoint(CyBulkEndPoint endpoint, int slotCount, int sliceSize)
            {
                _endpoint = endpoint;
                _slots = new PipeSlot[slotCount];
                var ovSize = Math.Max(CyConst.OverlapSignalAllocSize, Marshal.SizeOf(typeof(OVERLAPPED)));
                var cmdLength = CyConst.SINGLE_XFER_LEN + ((endpoint.XferMode == XMODE.BUFFERED) ? sliceSize : 0);
                for (var i = 0; i < slotCount; i++)
                {
                    var slot = new PipeSlot();
                    slot.Ov = new byte[ovSize];
                    slot.OvHandle = GCHandle.Alloc(slot.Ov, GCHandleType.Pinned);
                    slot.Evt = PInvoke.CreateEvent(0, 0, 0, 0);
                    slot.Buf = new byte[sliceSize];
                    slot.BufHandle = GCHandle.Alloc(slot.Buf, GCHandleType.Pinned);
                    slot.Cmd = new byte[cmdLength];
                    slot.CmdHandle = GCHandle.Alloc(slot.Cmd, GCHandleType.Pinned);
                    _slots[i] = slot;
                }
            }

            public int SlotCount => _slots.Length;

            public byte[] SlotBuffer(int slot) => _slots[slot].Buf;

            public bool Arm(int slot)
            {
                var s = _slots[slot];
                var ovPtr = s.OvHandle.AddrOfPinnedObject();
                var ov = (OVERLAPPED)Marshal.PtrToStructure(ovPtr, typeof(OVERLAPPED));
                ov.hEvent = s.Evt;
                Marshal.StructureToPtr(ov, ovPtr, true);

                var len = s.Buf.Length;
                s.InFlight = _endpoint.BeginDataXfer(ref s.Cmd, ref s.Buf, ref len, ref s.Ov);
                return s.InFlight;
            }

            public bool Wait(int slot, int timeoutMs)
            {
                return _endpoint.WaitForXfer(_slots[slot].Evt, (uint)timeoutMs);
            }

            public bool Finish(int slot, out int length)
            {
                var s = _slots[slot];
                var len = 0;
                var ok = _endpoint.FinishDataXfer(ref s.Cmd, ref s.Buf, ref len, ref s.Ov);
                s.InFlight = false;
                length = len;
                return ok && len >= 0;
            }

            public void CancelAll()
            {
                try { _endpoint.Abort(); } catch { }

                // Abort() only requests cancellation; wait for each cancelled read
                // to complete before its buffer may be reused or released.
                var deadline = Environment.TickCount + CancelDrainMs;
                foreach (var s in _slots)
                {
                    if (s == null || !s.InFlight)
                    {
                        continue;
                    }

                    var remaining = Math.Max(0, deadline - Environment.TickCount);
                    if (PInvoke.WaitForSingleObject(s.Evt, (uint)remaining) != WaitObject0)
                    {
                        _transfersUnconfirmed = true;
                    }

                    s.InFlight = false;
                }
            }

            public void ResetPipe()
            {
                try { _endpoint.Reset(); } catch { }
            }

            public void Dispose()
            {
                // A read whose completion was never observed may still own its
                // buffer. Leaking it is safer than letting the kernel write into
                // released memory.
                if (_transfersUnconfirmed)
                {
                    return;
                }

                foreach (var s in _slots)
                {
                    if (s == null)
                    {
                        continue;
                    }

                    if (s.BufHandle.IsAllocated)
                    {
                        s.BufHandle.Free();
                    }

                    if (s.CmdHandle.IsAllocated)
                    {
                        s.CmdHandle.Free();
                    }

                    if (s.OvHandle.IsAllocated)
                    {
                        s.OvHandle.Free();
                    }

                    if (s.Evt != IntPtr.Zero)
                    {
                        PInvoke.CloseHandle(s.Evt);
                        s.Evt = IntPtr.Zero;
                    }
                }
            }
        }
    }
}
