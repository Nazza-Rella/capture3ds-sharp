using System;
using System.Collections.Generic;
using System.IO;
using CyUSB;

namespace Capture3DS.Cypress
{
    public sealed partial class LlSpa3Device : ICapture3DSDevice
    {
        public const int VendorId = 0x04B4;
        public const int StreamingProductId = 0x1003;
        public const int RunningProductId = 0x1004;
        public const int BootProductId = 0x8613;

        // After we self-load the cc3dsfs MIT firmware from boot mode (04B4:8613),
        // the device renumerates to 04B4:1004. The earlier n3DS_view-loaded state
        // was 04B4:1003. Both expose the same Optimize streamed endpoints.
        private static bool IsStreamingPid(int pid) => pid == StreamingProductId || pid == RunningProductId;

        private const int TimeoutMs = 1000;
        private const int FirmwareRenumerateTimeoutMs = 4000;

        private USBDeviceList _deviceList;
        private CyUSBDevice _device;
        private CyBulkEndPoint _bulkIn;
        private CyBulkEndPoint _ctrlBulkIn;
        private CyBulkEndPoint _bulkOut;
        private bool _streamStarted;

        private LlSpa3Device(Capture3DSDeviceInfo info)
        {
            Info = info;
        }

        public Capture3DSDeviceInfo Info { get; }

        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var devices = new List<Capture3DSDeviceInfo>();
            using (var list = new USBDeviceList(CyConst.DEVICES_CYUSB))
            {
                var index = 0;
                foreach (USBDevice usb in list)
                {
                    var dev = usb as CyUSBDevice;
                    if (dev == null || dev.VendorID != VendorId)
                    {
                        continue;
                    }

                    // streaming(1003/1004)はそのまま掴める。boot(8613)はファーム未読込の
                    // コールドブート状態で、Connect() が自己読込して 1004 へ renumerate する。
                    // boot も一覧に出さないと、電源投入直後/他ツール使用後に選択肢から消え、
                    // 自己読込の経路に到達できなくなる。
                    var streaming = IsStreamingPid(dev.ProductID);
                    var boot = dev.ProductID == BootProductId;
                    if (!streaming && !boot)
                    {
                        continue;
                    }

                    index++;
                    var serial = SafeSerial(dev);
                    if (string.IsNullOrWhiteSpace(serial))
                    {
                        serial = $"{dev.BcdDevice:X4}-{index}";
                    }

                    var description = streaming
                        ? $"LL-SPA3 (CyUSB {dev.VendorID:X4}:{dev.ProductID:X4} bcd={dev.BcdDevice:X4})"
                        : $"LL-SPA3 [要ファーム読込] (CyUSB {dev.VendorID:X4}:{dev.ProductID:X4})";
                    devices.Add(new Capture3DSDeviceInfo(Capture3DSModel.LlSpa3, serial, description, false));
                }
            }

            return devices;
        }

        public static ICapture3DSDevice Open(Capture3DSDeviceInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            if (info.Model != Capture3DSModel.LlSpa3)
            {
                throw new ArgumentException("Device info is not for LL-SPA3.", nameof(info));
            }

            return new LlSpa3Device(info);
        }

        public void Connect()
        {
            DisposeHandle();

            _deviceList = new USBDeviceList(CyConst.DEVICES_CYUSB);
            _device = FindStreamingDevice(_deviceList, Info.Serial);
            if (_device == null && ContainsBootDevice(_deviceList))
            {
                // Self-load the cc3dsfs MIT firmware so the board renumerates from
                // boot mode (04B4:8613) to the streamed device (04B4:1004).
                _deviceList.Dispose();
                _deviceList = null;
                LlSpa3FirmwareLoader.LoadFirmwareToBootDevice();
                WaitForStreamingDevice(FirmwareRenumerateTimeoutMs);
                _deviceList = new USBDeviceList(CyConst.DEVICES_CYUSB);
                _device = FindStreamingDevice(_deviceList, Info.Serial);
            }

            if (_device == null)
            {
                throw new Capture3DSException("LL-SPA3 streamed device (04B4:1003/1004) was not found through CyUSB, and no boot-mode (04B4:8613) device was available to load firmware.");
            }

            // Optimize New 3DS (bcd 0xFE00) has an EEPROM; the v2 device our
            // firmware self-loads (bcd 0xFA00) does not. The capture_start sequence
            // differs between them.
            _hasEeprom = (_device.BcdDevice & OptimizeBcdDeviceMask) == OptimizeNew3dsWantedValueBase;

            _bulkIn = FindBestBulkIn(_device, null);
            _ctrlBulkIn = FindBestBulkIn(_device, 0x81);
            _bulkOut = FindBestBulkOut(_device, 0x01);
            if (_bulkIn == null)
            {
                throw new Capture3DSException("LL-SPA3 has no usable bulk-in endpoint.");
            }

            _bulkIn.TimeOut = TimeoutMs;
            if (_ctrlBulkIn != null) _ctrlBulkIn.TimeOut = TimeoutMs;
            if (_bulkOut != null) _bulkOut.TimeOut = TimeoutMs;
            ResetEndpoint(_bulkIn);
            ResetEndpoint(_ctrlBulkIn);
            ResetEndpoint(_bulkOut);
            _streamStarted = false;

            // FPGA 書き込み+ストリーム開始(初回約700ms)を接続フェーズで済ませる。
            // 初回 ReadFrame に遅延を残すと、呼び出し側では「接続済みなのに最初だけ
            // 映像が止まって見える」挙動になるため、接続(=「接続中」表示)側へ前倒しする。
            StartOptimizeNewCompatibleStream();
        }

        // cc3dsfs Optimize New 3DS capture start. Always RGB8 / 2D; the FPGA and
        // capture pipeline are programmed from scratch (required after our own
        // firmware self-load to 1004, where the FPGA is not yet programmed).
        private void StartOptimizeNewCompatibleStream()
        {
            const bool isRgb888 = true;
            const bool is3d = false;

            EnsureConnected();
            if (_bulkOut == null || _ctrlBulkIn == null)
            {
                throw new Capture3DSException("LL-SPA3 has no usable Cypress control bulk endpoints.");
            }

            // The product key is optional. cc3dsfs captures fine without it (the key
            // only folds 3 bytes into the setup buffer and feeds the device-id
            // diagnostic); a missing key just skips both. If one is cached we use it.
            if (!LlSpa3OptimizeProtocol.TryLoadProductKey(out string key, out _))
            {
                key = null;
            }

            byte[] setupKeyBuffer = LlSpa3OptimizeProtocol.BuildOldSetupKeyBuffer(isRgb888, is3d, key);

            RunOptimizeCaptureStart(isRgb888, is3d, key);

            // Arm the bulk-in read pipeline BEFORE triggering capture DMA.
            // cc3dsfs schedules NUM_CONCURRENTLY_RUNNING_BUFFERS reads
            // (TOTAL_WANTED 0x100000 / SLICE 0x4000 = 64) ahead of StartCaptureDma;
            // the FX2 stalls frame generation when no reads are armed, which on a
            // single synchronous read shows up as a zero-data timeout on EP 0x82.
            ArmOptimizePipeline();

            // Public cc3dsfs Optimize New StartCaptureDma sequence. The product
            // key is used only to populate the setup buffer and is never logged.
            SendBulkOut(new byte[] { 0x40 });
            SendBulkOut(new byte[] { 0x64, 0x60, 0x02, 0x00, 0xFF, 0x00, 0xFF, 0x60, 0x02, 0x30, 0xFF, 0x60, 0xC2, 0x60, 0x01, 0x20, 0xFF });
            SendBulkOut(setupKeyBuffer);
            ReadControlBulkIn(7);
            SendBulkOut(new byte[] { 0x65 });
            _streamStarted = true;
        }

        private byte[] ReadControlBulkIn(int requestedLength)
        {
            var buffer = new byte[requestedLength];
            var length = requestedLength;
            if (!_ctrlBulkIn.XferData(ref buffer, ref length) || length <= 0)
            {
                throw new Capture3DSException($"LL-SPA3 control bulk-in failed: length={length} lastError={_ctrlBulkIn.LastError}");
            }

            if (length == buffer.Length)
            {
                return buffer;
            }

            var copy = new byte[length];
            Buffer.BlockCopy(buffer, 0, copy, 0, length);
            return copy;
        }

        private void SendBulkOut(byte[] payload)
        {
            var buffer = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);
            var length = buffer.Length;
            if (!_bulkOut.XferData(ref buffer, ref length) || length != payload.Length)
            {
                throw new Capture3DSException($"LL-SPA3 bulk-out command failed: transferred={length}/{payload.Length} lastError={_bulkOut.LastError}");
            }
        }
        public Capture3DSFrame ReadFrame()
        {
            EnsureConnected();
            if (!_streamStarted)
            {
                StartOptimizeNewCompatibleStream();
            }

            var raw = ReadOptimizeFrameFromPipeline(LlSpa3Decoder.FrameSize, TimeoutMs);
            return LlSpa3Decoder.Decode(raw, raw.Length);
        }

        public void Dispose()
        {
            DisposeHandle();
            GC.SuppressFinalize(this);
        }

        private void EnsureConnected()
        {
            if (_bulkIn == null)
            {
                Connect();
            }
        }

        private void DisposeHandle()
        {
            TeardownOptimizePipeline();
            if (_bulkIn != null)
            {
                ResetEndpoint(_bulkIn);
            }

            _bulkIn = null;
            _ctrlBulkIn = null;
            _bulkOut = null;
            _streamStarted = false;
            _device = null;

            if (_deviceList != null)
            {
                _deviceList.Dispose();
                _deviceList = null;
            }
        }

        private static CyUSBDevice FindStreamingDevice(USBDeviceList list, string serial)
        {
            CyUSBDevice fallback = null;
            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
                if (dev == null || dev.VendorID != VendorId || !IsStreamingPid(dev.ProductID))
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = dev;
                }

                if (!string.IsNullOrWhiteSpace(serial) && string.Equals(SafeSerial(dev), serial, StringComparison.OrdinalIgnoreCase))
                {
                    return dev;
                }
            }

            return fallback;
        }

        private static bool ContainsBootDevice(USBDeviceList list)
        {
            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
                if (dev != null && dev.VendorID == VendorId && dev.ProductID == BootProductId)
                {
                    return true;
                }
            }

            return false;
        }

        private static void WaitForStreamingDevice(int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                using (var list = new USBDeviceList(CyConst.DEVICES_CYUSB))
                {
                    foreach (USBDevice usb in list)
                    {
                        var dev = usb as CyUSBDevice;
                        if (dev != null && dev.VendorID == VendorId && IsStreamingPid(dev.ProductID))
                        {
                            return;
                        }
                    }
                }

                System.Threading.Thread.Sleep(150);
            }
        }

        private static CyBulkEndPoint FindBestBulkIn(CyUSBDevice dev, byte? preferredAddress)
        {
            CyBulkEndPoint best = null;
            foreach (CyUSBEndPoint ep in dev.EndPoints)
            {
                if (ep == null || !ep.bIn || ep.Address == 0 || (ep.Attributes & 0x03) != 0x02)
                {
                    continue;
                }

                var bulk = ep as CyBulkEndPoint;
                if (bulk != null && preferredAddress.HasValue && bulk.Address == preferredAddress.Value)
                {
                    return bulk;
                }

                if (bulk != null && (best == null || bulk.Address > best.Address))
                {
                    best = bulk;
                }
            }

            return best;
        }
        private static CyBulkEndPoint FindBestBulkOut(CyUSBDevice dev, byte preferredAddress)
        {
            CyBulkEndPoint fallback = null;
            foreach (CyUSBEndPoint ep in dev.EndPoints)
            {
                if (ep == null || ep.bIn || ep.Address == 0 || (ep.Attributes & 0x03) != 0x02)
                {
                    continue;
                }

                var bulk = ep as CyBulkEndPoint;
                if (bulk != null && bulk.Address == preferredAddress)
                {
                    return bulk;
                }

                if (bulk != null && fallback == null)
                {
                    fallback = bulk;
                }
            }

            return fallback;
        }
        private static void ResetEndpoint(CyBulkEndPoint endpoint)
        {
            if (endpoint == null)
            {
                return;
            }

            try { endpoint.Abort(); } catch (IOException) { } catch (InvalidOperationException) { }
            try { endpoint.Reset(); } catch (IOException) { } catch (InvalidOperationException) { }
        }

        private static string SafeSerial(CyUSBDevice dev)
        {
            try
            {
                return dev.SerialNumber ?? string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }
    }
}