using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Capture3DS.Loopy
{
    /// <summary>
    /// Loopy's original-3DS capture board (16D0:06A3), ported from the
    /// cc3dsfs usb_ds_3ds_capture backend (MIT). The board already contains
    /// its firmware; each frame is requested with vendor command 0x40 and is
    /// returned as RGB888 plus a variable-length audio tail on bulk IN 0x82.
    /// </summary>
    public sealed class LoopyOld3dsDevice : ICapture3DSDevice
    {
        private const ushort VendorId = 0x16D0;
        private const ushort ProductId = 0x06A3;
        private const int Configuration = 1;
        private const int CaptureInterface = 0;
        private const byte BulkIn = 0x82;
        private const byte CaptureStartRequest = 0x40;
        private const uint ControlTimeoutMs = 30;
        private const uint BulkTimeoutMs = 50;
        private const int Usb2PacketSize = 512;

        // cc3dsfs reserves up to 1096 * 16 16-bit audio samples after RGB data.
        // The extra packet makes the final rounded bulk read memory-safe.
        private const int MaxAudioBytes = 1096 * 16 * 2;
        private const int MaxCaptureSize = LoopyOld3dsDecoder.VideoSize + MaxAudioBytes;
        private readonly byte[] _captureBuffer = new byte[MaxCaptureSize + Usb2PacketSize];

        private IntPtr _context;
        private IntPtr _handle;
        private bool _interfaceClaimed;
        private bool _kernelDriverDetached;

        private LoopyOld3dsDevice(Capture3DSDeviceInfo info)
        {
            Info = info;
        }

        public Capture3DSDeviceInfo Info { get; }

        public static IReadOnlyList<Capture3DSDeviceInfo> ListDevices()
        {
            var result = new List<Capture3DSDeviceInfo>();
            IntPtr context;
            if (LibUsbNative.Init(out context) != LibUsbNative.Success || context == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                IntPtr list;
                var countValue = LibUsbNative.GetDeviceList(context, out list);
                var count = countValue.ToInt64();
                if (count < 0 || list == IntPtr.Zero)
                {
                    return result;
                }

                try
                {
                    for (long index = 0; index < count; index++)
                    {
                        var device = Marshal.ReadIntPtr(list, checked((int)(index * IntPtr.Size)));
                        LibUsbNative.DeviceDescriptor descriptor;
                        if (device == IntPtr.Zero ||
                            LibUsbNative.GetDeviceDescriptor(device, out descriptor) != LibUsbNative.Success ||
                            descriptor.VendorId != VendorId || descriptor.ProductId != ProductId)
                        {
                            continue;
                        }

                        IntPtr handle;
                        if (LibUsbNative.Open(device, out handle) != LibUsbNative.Success ||
                            handle == IntPtr.Zero)
                        {
                            // libusb cannot communicate through the currently installed driver.
                            continue;
                        }

                        try
                        {
                            var serial = ReadSerial(handle, descriptor.SerialIndex);
                            var pathId = BuildPhysicalPathId(device);
                            var deviceId = !string.IsNullOrWhiteSpace(serial)
                                ? "serial:" + serial.Trim()
                                : pathId;
                            var description =
                                $"Loopy Old 3DS (libusb {VendorId:X4}:{ProductId:X4} bcd={descriptor.BcdDevice:X4})";
                            result.Add(new Capture3DSDeviceInfo(
                                Capture3DSModel.LoopyOld3ds,
                                serial,
                                description,
                                false,
                                deviceId));
                        }
                        finally
                        {
                            LibUsbNative.Close(handle);
                        }
                    }
                }
                finally
                {
                    LibUsbNative.FreeDeviceList(list, 1);
                }
            }
            finally
            {
                LibUsbNative.Exit(context);
            }

            return result;
        }

        public static LoopyOld3dsDevice Open(Capture3DSDeviceInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (info.Model != Capture3DSModel.LoopyOld3ds)
            {
                throw new Capture3DSException("not a Loopy old 3DS capture device");
            }
            return new LoopyOld3dsDevice(info);
        }

        public void Connect()
        {
            CloseConnection();

            var initStatus = LibUsbNative.Init(out _context);
            if (initStatus != LibUsbNative.Success || _context == IntPtr.Zero)
            {
                _context = IntPtr.Zero;
                throw new Capture3DSException($"libusb_init failed: {initStatus}");
            }

            try
            {
                _handle = FindAndOpenSelectedDevice(_context, Info);
                if (_handle == IntPtr.Zero)
                {
                    throw new Capture3DSException(
                        "The selected Loopy old 3DS capture board was not found or cannot be opened through libusb.");
                }

                var active = LibUsbNative.KernelDriverActive(_handle, CaptureInterface);
                if (active == 1 &&
                    LibUsbNative.DetachKernelDriver(_handle, CaptureInterface) == LibUsbNative.Success)
                {
                    _kernelDriverDetached = true;
                }

                int currentConfiguration;
                var configurationStatus = LibUsbNative.GetConfiguration(_handle, out currentConfiguration);
                if (configurationStatus != LibUsbNative.Success)
                {
                    throw new Capture3DSException(
                        $"Loopy old 3DS get-configuration failed: {configurationStatus}");
                }
                if (currentConfiguration != Configuration)
                {
                    configurationStatus = LibUsbNative.SetConfiguration(_handle, Configuration);
                    if (configurationStatus != LibUsbNative.Success)
                    {
                        throw new Capture3DSException(
                            $"Loopy old 3DS set-configuration failed: {configurationStatus}");
                    }
                }

                var claimStatus = LibUsbNative.ClaimInterface(_handle, CaptureInterface);
                if (claimStatus != LibUsbNative.Success)
                {
                    throw new Capture3DSException(
                        $"Loopy old 3DS interface claim failed: {claimStatus}");
                }
                _interfaceClaimed = true;

                // Firmware workaround used by Loopy's sample/cc3dsfs: the first
                // request can time out, so prime capture once before normal reads.
                CaptureStart();
                Thread.Sleep((int)BulkTimeoutMs);
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }

        public Capture3DSFrame ReadFrame()
        {
            if (_handle == IntPtr.Zero || !_interfaceClaimed)
            {
                throw new Capture3DSException("Loopy old 3DS device is not connected");
            }

            var startStatus = CaptureStart();
            if (startStatus == LibUsbNative.ErrorTimeout)
            {
                return null;
            }
            if (startStatus < LibUsbNative.Success)
            {
                throw new Capture3DSException(
                    $"Loopy old 3DS capture-start failed: {startStatus}");
            }

            var bytesIn = 0;
            var lastTransfer = 0;
            var transferStatus = LibUsbNative.Success;
            var pinned = GCHandle.Alloc(_captureBuffer, GCHandleType.Pinned);
            try
            {
                do
                {
                    var remaining = MaxCaptureSize - bytesIn;
                    var transferSize = (remaining + Usb2PacketSize - 1) & ~(Usb2PacketSize - 1);
                    var destination = IntPtr.Add(pinned.AddrOfPinnedObject(), bytesIn);
                    transferStatus = LibUsbNative.BulkTransfer(
                        _handle, BulkIn, destination, transferSize, out lastTransfer, BulkTimeoutMs);
                    if (transferStatus == LibUsbNative.Success)
                    {
                        bytesIn += lastTransfer;
                    }
                }
                while (bytesIn < MaxCaptureSize &&
                       transferStatus == LibUsbNative.Success &&
                       lastTransfer > 0);
            }
            finally
            {
                pinned.Free();
            }

            if (transferStatus == LibUsbNative.ErrorPipe)
            {
                LibUsbNative.ClearHalt(_handle, BulkIn);
                throw new Capture3DSException("Loopy old 3DS bulk pipe stalled");
            }
            if (transferStatus == LibUsbNative.ErrorTimeout ||
                (transferStatus == LibUsbNative.Success &&
                 bytesIn < LoopyOld3dsDecoder.VideoSize))
            {
                return null;
            }
            if (transferStatus != LibUsbNative.Success)
            {
                throw new Capture3DSException(
                    $"Loopy old 3DS bulk read failed: {transferStatus}");
            }

            return LoopyOld3dsDecoder.DecodeRgb8(_captureBuffer, bytesIn);
        }

        private int CaptureStart()
        {
            return LibUsbNative.ControlTransfer(
                _handle,
                LibUsbNative.RequestTypeVendor,
                CaptureStartRequest,
                0,
                0,
                IntPtr.Zero,
                0,
                ControlTimeoutMs);
        }

        private static IntPtr FindAndOpenSelectedDevice(
            IntPtr context,
            Capture3DSDeviceInfo info)
        {
            IntPtr list;
            var countValue = LibUsbNative.GetDeviceList(context, out list);
            var count = countValue.ToInt64();
            if (count < 0 || list == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var hasExactIdentity =
                !string.IsNullOrWhiteSpace(info.Serial) ||
                !string.IsNullOrWhiteSpace(info.DeviceId);
            IntPtr onlyCandidate = IntPtr.Zero;
            var candidateCount = 0;

            try
            {
                for (long index = 0; index < count; index++)
                {
                    var device = Marshal.ReadIntPtr(list, checked((int)(index * IntPtr.Size)));
                    LibUsbNative.DeviceDescriptor descriptor;
                    if (device == IntPtr.Zero ||
                        LibUsbNative.GetDeviceDescriptor(device, out descriptor) != LibUsbNative.Success ||
                        descriptor.VendorId != VendorId || descriptor.ProductId != ProductId)
                    {
                        continue;
                    }

                    IntPtr handle;
                    if (LibUsbNative.Open(device, out handle) != LibUsbNative.Success ||
                        handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    var serial = ReadSerial(handle, descriptor.SerialIndex);
                    var pathId = BuildPhysicalPathId(device);
                    if (Matches(info, serial, pathId))
                    {
                        if (onlyCandidate != IntPtr.Zero)
                        {
                            LibUsbNative.Close(onlyCandidate);
                            onlyCandidate = IntPtr.Zero;
                        }
                        return handle;
                    }

                    candidateCount++;
                    if (candidateCount == 1)
                    {
                        onlyCandidate = handle;
                    }
                    else
                    {
                        LibUsbNative.Close(handle);
                    }
                }

                if (!hasExactIdentity && candidateCount == 1)
                {
                    var selected = onlyCandidate;
                    onlyCandidate = IntPtr.Zero;
                    return selected;
                }
                return IntPtr.Zero;
            }
            finally
            {
                if (onlyCandidate != IntPtr.Zero)
                {
                    LibUsbNative.Close(onlyCandidate);
                }
                LibUsbNative.FreeDeviceList(list, 1);
            }
        }

        private static bool Matches(
            Capture3DSDeviceInfo info,
            string serial,
            string pathId)
        {
            if (!string.IsNullOrWhiteSpace(info.Serial) &&
                string.Equals(info.Serial.Trim(), serial, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(info.DeviceId))
            {
                return false;
            }
            if (info.DeviceId.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(
                    info.DeviceId.Substring("serial:".Length),
                    serial,
                    StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(info.DeviceId, pathId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadSerial(IntPtr handle, byte serialIndex)
        {
            if (handle == IntPtr.Zero || serialIndex == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[256];
            var length = LibUsbNative.GetStringDescriptorAscii(
                handle, serialIndex, bytes, bytes.Length - 1);
            return length > 0
                ? Encoding.ASCII.GetString(bytes, 0, length).TrimEnd('\0').Trim()
                : string.Empty;
        }

        private static string BuildPhysicalPathId(IntPtr device)
        {
            if (device == IntPtr.Zero)
            {
                return string.Empty;
            }

            var ports = new byte[8];
            int count;
            try
            {
                count = LibUsbNative.GetPortNumbers(device, ports, ports.Length);
            }
            catch (EntryPointNotFoundException)
            {
                return string.Empty;
            }

            if (count <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("usb:");
            builder.Append(LibUsbNative.GetBusNumber(device));
            builder.Append(':');
            for (var index = 0; index < count; index++)
            {
                if (index != 0) builder.Append('.');
                builder.Append(ports[index]);
            }
            return builder.ToString();
        }

        private void CloseConnection()
        {
            if (_handle != IntPtr.Zero)
            {
                if (_interfaceClaimed)
                {
                    LibUsbNative.ReleaseInterface(_handle, CaptureInterface);
                    _interfaceClaimed = false;
                }
                if (_kernelDriverDetached)
                {
                    try { LibUsbNative.AttachKernelDriver(_handle, CaptureInterface); }
                    catch (EntryPointNotFoundException) { }
                    _kernelDriverDetached = false;
                }
                LibUsbNative.Close(_handle);
                _handle = IntPtr.Zero;
            }

            if (_context != IntPtr.Zero)
            {
                LibUsbNative.Exit(_context);
                _context = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            CloseConnection();
        }
    }
}
