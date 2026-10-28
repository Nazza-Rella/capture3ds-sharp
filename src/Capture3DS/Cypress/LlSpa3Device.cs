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
        private readonly LlSpa3AudioDecoder _audioDecoder = new LlSpa3AudioDecoder();

        private LlSpa3Device(Capture3DSDeviceInfo info)
        {
            Info = info;
        }

        public Capture3DSDeviceInfo Info { get; private set; }

        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var devices = new List<Capture3DSDeviceInfo>();
            using (var list = new USBDeviceList(CyConst.DEVICES_CYUSB))
            {
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

                    var serial = SafeSerial(dev);
                    var deviceId = BuildDeviceId(dev);

                    var description = streaming
                        ? $"LL-SPA3 (CyUSB {dev.VendorID:X4}:{dev.ProductID:X4} bcd={dev.BcdDevice:X4})"
                        : $"LL-SPA3 [要ファーム読込] (CyUSB {dev.VendorID:X4}:{dev.ProductID:X4})";
                    devices.Add(new Capture3DSDeviceInfo(
                        Capture3DSModel.LlSpa3, serial, description, false, deviceId));
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
            _device = FindStreamingDevice(_deviceList, Info);
            if (_device == null)
            {
                var bootDevice = FindBootDevice(_deviceList, Info);
                if (bootDevice == null)
                {
                    throw new Capture3DSException(
                        "The selected LL-SPA3 device was not found. Re-scan the device list and select it again.");
                }

                var previousStreamingDevices = SnapshotStreamingDevices(_deviceList);

                // Self-load the cc3dsfs MIT firmware so the board renumerates from
                // boot mode (04B4:8613) to the streamed device (04B4:1004).
                var firmware = LlSpa3FirmwareLoader.LoadEmbeddedFirmware();
                if (!LlSpa3FirmwareLoader.LoadFirmware(bootDevice, firmware, 0))
                {
                    throw new Capture3DSException("LL-SPA3 firmware loading failed for the selected device.");
                }

                _deviceList.Dispose();
                _deviceList = null;
                WaitForStreamingDevice(Info, previousStreamingDevices, FirmwareRenumerateTimeoutMs);
                _deviceList = new USBDeviceList(CyConst.DEVICES_CYUSB);
                _device = FindReenumeratedStreamingDevice(
                    _deviceList, Info, previousStreamingDevices);
            }

            if (_device == null)
            {
                throw new Capture3DSException("LL-SPA3 streamed device (04B4:1003/1004) was not found through CyUSB, and no boot-mode (04B4:8613) device was available to load firmware.");
            }

			// boot(8613)からstreaming(1004)へ再列挙された場合は、解決後の個体IDを
			// 呼び出し側へ返す。NXの次回再接続でも同じ個体を選べるようにする。
			var resolvedSerial = SafeSerial(_device);
			Info = new Capture3DSDeviceInfo(
				Capture3DSModel.LlSpa3,
				resolvedSerial,
				$"LL-SPA3 (CyUSB {_device.VendorID:X4}:{_device.ProductID:X4} bcd={_device.BcdDevice:X4})",
				false,
				BuildDeviceId(_device));

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

        // cc3dsfs Optimize New 3DS capture start, 2D in the colour depth of
        // ColorMode; the FPGA and capture pipeline are programmed from scratch
        // (required after our own firmware self-load to 1004, where the FPGA is
        // not yet programmed).
        private void StartOptimizeNewCompatibleStream()
        {
            var isRgb888 = ColorMode == LlSpa3ColorMode.Rgb888;
            const bool is3d = false;
            _layout = isRgb888 ? LlSpa3FrameLayout.Rgb888 : LlSpa3FrameLayout.Rgb565;
            _audioDecoder.Layout = _layout;

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

            // Arm the bulk-in read pipeline BEFORE triggering capture DMA. The FX2
            // stalls frame generation when no reads are armed, which on a single
            // synchronous read shows up as a zero-data timeout on EP 0x82.
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

            var raw = _reader.Take(TimeoutMs);
            try
            {
                return LlSpa3Decoder.Decode(raw, raw.Length, _audioDecoder, _layout);
            }
            finally
            {
                _reader.Return(raw);
            }
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
            _audioDecoder.Reset();
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

        private static CyUSBDevice FindStreamingDevice(
            USBDeviceList list,
            Capture3DSDeviceInfo info)
        {
            CyUSBDevice onlyCandidate = null;
            var candidateCount = 0;
            ushort expectedProductId;
            var hasExpectedProductId = TryGetExpectedProductId(info, out expectedProductId);
            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
				if (dev == null || dev.VendorID != VendorId || !IsStreamingPid(dev.ProductID))
                {
                    continue;
                }
				if (hasExpectedProductId && dev.ProductID != expectedProductId)
				{
					continue;
				}

                candidateCount++;
                if (onlyCandidate == null)
                {
                    onlyCandidate = dev;
                }

                if (MatchesDevice(dev, info))
                {
                    return dev;
                }
            }

            // 旧APIで個体IDなしのInfoを生成した呼び出し元だけは、候補が1台なら
            // 従来どおり接続できる。複数台時は誤った個体を掴まない。
			return HasExactSelectionIdentity(info) || candidateCount != 1
                ? null
                : onlyCandidate;
        }

        private static CyUSBDevice FindBootDevice(
            USBDeviceList list,
            Capture3DSDeviceInfo info)
        {
            CyUSBDevice onlyCandidate = null;
            var candidateCount = 0;
            ushort expectedProductId;
            var hasExpectedProductId = TryGetExpectedProductId(info, out expectedProductId);
            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
                if (dev == null || dev.VendorID != VendorId || dev.ProductID != BootProductId)
                {
                    continue;
                }
				if (hasExpectedProductId && dev.ProductID != expectedProductId)
				{
					continue;
				}

                candidateCount++;
                if (onlyCandidate == null)
                {
                    onlyCandidate = dev;
                }

                if (MatchesDevice(dev, info))
                {
                    return dev;
                }
            }

			return HasExactSelectionIdentity(info) || candidateCount != 1
                ? null
                : onlyCandidate;
        }

        private static void WaitForStreamingDevice(
            Capture3DSDeviceInfo info,
            HashSet<string> previousStreamingDevices,
            int timeoutMs)
        {
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                using (var list = new USBDeviceList(CyConst.DEVICES_CYUSB))
                {
                    if (FindReenumeratedStreamingDevice(
                            list, info, previousStreamingDevices) != null)
                    {
                        return;
                    }
                }

                System.Threading.Thread.Sleep(150);
            }
        }

        private static CyUSBDevice FindReenumeratedStreamingDevice(
            USBDeviceList list,
            Capture3DSDeviceInfo info,
            HashSet<string> previousStreamingDevices)
        {
            CyUSBDevice newCandidate = null;
            var newCandidateCount = 0;
            CyUSBDevice onlyCandidate = null;
            var candidateCount = 0;

            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
				// 自己読込したfirmwareは必ず1004として再列挙される。
				// 待機中に別の1003機器が接続されても対象個体として扱わない。
				if (dev == null || dev.VendorID != VendorId || dev.ProductID != RunningProductId)
                {
                    continue;
                }

                candidateCount++;
                if (onlyCandidate == null)
                {
                    onlyCandidate = dev;
                }

                if (MatchesDevice(dev, info))
                {
                    return dev;
                }

                if (!previousStreamingDevices.Contains(SnapshotId(dev)))
                {
                    newCandidateCount++;
                    if (newCandidate == null)
                    {
                        newCandidate = dev;
                    }
                }
            }

            if (newCandidateCount == 1)
            {
                return newCandidate;
            }

            // 起動前にストリーミング機が無かった旧API利用時のみ、単独候補を許容する。
            return previousStreamingDevices.Count == 0 && candidateCount == 1
                ? onlyCandidate
                : null;
        }

        private static HashSet<string> SnapshotStreamingDevices(USBDeviceList list)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (USBDevice usb in list)
            {
                var dev = usb as CyUSBDevice;
                if (dev != null && dev.VendorID == VendorId && IsStreamingPid(dev.ProductID))
                {
                    result.Add(SnapshotId(dev));
                }
            }

            return result;
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

		private static bool HasExactSelectionIdentity(Capture3DSDeviceInfo info)
        {
            return info != null &&
				   ((!string.IsNullOrWhiteSpace(info.DeviceId) &&
					 !info.DeviceId.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) ||
                    !string.IsNullOrWhiteSpace(info.Serial));
        }

		private static bool TryGetExpectedProductId(Capture3DSDeviceInfo info, out ushort productId)
		{
			productId = 0;
			if (info == null || string.IsNullOrWhiteSpace(info.DeviceId)
				|| !info.DeviceId.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) return false;
			return ushort.TryParse(info.DeviceId.Substring(4),
				System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture,
				out productId);
		}

        private static bool MatchesDevice(CyUSBDevice dev, Capture3DSDeviceInfo info)
        {
            if (dev == null || info == null)
            {
                return false;
            }

			if (HasExactSelectionIdentity(info) &&
				!string.IsNullOrWhiteSpace(info.DeviceId) &&
                string.Equals(BuildDeviceId(dev), info.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(info.Serial) &&
                   string.Equals(SafeSerial(dev), info.Serial, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDeviceId(CyUSBDevice dev)
        {
            // CyUSBのPathは通常
            // \\?\usb#vid_xxxx&pid_xxxx#<instance>#{driver-guid} の形。
            // VID/PIDとGUIDを除いたinstance部分は、同じ物理ポート上の8613→1004
            // 再列挙でも維持され、シリアルを持たない複数台を区別できる。
            var connectionId = ConnectionIdFromPath(SafePath(dev));
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                return "path:" + connectionId;
            }

            var serial = SafeSerial(dev);
			return string.IsNullOrWhiteSpace(serial)
				? "pid:" + dev.ProductID.ToString("X4")
                : "serial:" + serial.Trim();
        }

        private static string ConnectionIdFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var value = path.Trim();
            var first = value.IndexOf('#');
            var second = first < 0 ? -1 : value.IndexOf('#', first + 1);
            if (second >= 0)
            {
                var third = value.IndexOf('#', second + 1);
                var end = third >= 0 ? third : value.Length;
                if (end > second + 1)
                {
                    return value.Substring(second + 1, end - second - 1);
                }
            }

            // 想定外の書式でも、同一列挙中の個体選択にはフルPathを使える。
            return value;
        }

        private static string SnapshotId(CyUSBDevice dev)
        {
            var path = SafePath(dev);
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path.Trim();
            }

            return $"{dev.VendorID:X4}:{dev.ProductID:X4}:{dev.BcdDevice:X4}:{BuildDeviceId(dev)}";
        }

        private static string SafePath(CyUSBDevice dev)
        {
            try
            {
                return dev.Path ?? string.Empty;
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
