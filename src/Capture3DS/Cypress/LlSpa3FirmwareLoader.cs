using System;
using System.IO;
using System.Reflection;
using CyUSB;

namespace Capture3DS.Cypress
{
    // EZ-USB FX2 (Cypress) firmware loader for the LL-SPA3 capture board.
    //
    // Port of cc3dsfs (MIT) load_firmware() for the "Optimize Old 3DS" device.
    // The board (a 3DS LL) boots as 04B4:8613. After this loader writes the MIT
    // firmware blob into FX2 RAM and releases the 8051 from reset, the device
    // renumerates as 04B4:1004 (a standard cc3dsfs Optimize device, bcd base 0xFD00).
    //
    // Reference: cypress_optimize_3ds_communications.cpp load_firmware().
    public static class LlSpa3FirmwareLoader
    {
        public const int VendorId = 0x04B4;
        public const int BootProductId = 0x8613;
        public const int RunningProductId = 0x1004;

        // bcd_device_wanted_value base for the Old 3DS instantiated device.
        private const ushort Old3dsWantedValueBase = 0xFD00;

        // FX2 vendor commands.
        private const byte FirmwareLoadRequest = 0xA0; // EZ-USB "Firmware Load" (RW internal RAM)
        private const ushort CpucsAddress = 0xE600;    // CPUCS register: bit0 = 8051 reset hold

        private const string FirmwareResourceName = "Capture3DS.optimize_old_3ds_fw.bin";

        // Loads the embedded MIT firmware blob into the FX2 RAM of the boot-mode
        // (04B4:8613) device and releases it. After this returns true, the device
        // renumerates to 04B4:1004; allow ~1s for Windows to re-enumerate.
        public static bool LoadFirmwareToBootDevice(byte patchId = 0)
        {
            var firmware = LoadEmbeddedFirmware();

            using (var list = new USBDeviceList(CyConst.DEVICES_CYUSB))
            {
                foreach (USBDevice usb in list)
                {
                    var dev = usb as CyUSBDevice;
                    if (dev == null || dev.VendorID != VendorId || dev.ProductID != BootProductId)
                    {
                        continue;
                    }

                    return LoadFirmware(dev, firmware, patchId);
                }
            }

            return false;
        }

        public static byte[] LoadEmbeddedFirmware()
        {
            var assembly = typeof(LlSpa3FirmwareLoader).Assembly;
            using (var stream = assembly.GetManifestResourceStream(FirmwareResourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        $"Embedded firmware '{FirmwareResourceName}' was not found in the assembly.");
                }

                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        // Port of cc3dsfs load_firmware(). Mutates a private copy of the blob.
        public static bool LoadFirmware(CyUSBDevice device, byte[] firmwareSource, byte patchId)
        {
            if (device == null)
            {
                throw new ArgumentNullException(nameof(device));
            }

            if (firmwareSource == null)
            {
                throw new ArgumentNullException(nameof(firmwareSource));
            }

            var fw = (byte[])firmwareSource.Clone();

            // Apply per-instance bcd patches (sets the wanted PID/bcd value).
            int numPatches = ReadLe16(fw, 2);
            for (int i = 0; i < numPatches; i++)
            {
                int posPatch = ReadLe16(fw, 4 + (i * 2));
                WriteLe16(fw, posPatch, (ushort)(patchId | Old3dsWantedValueBase));
            }

            // Hold the 8051 in reset (CPUCS bit0 = 1).
            if (!ControlOut(device, CpucsAddress, 0, new byte[] { 1 }))
            {
                return false;
            }

            // Walk the firmware records: each is offset(2) index(2) len(2) + data.
            bool done = false;
            int fwPos = ReadLe16(fw, 0);
            while (!done)
            {
                int offset = ReadLe16(fw, fwPos);
                int index = ReadLe16(fw, fwPos + 2);
                int length = ReadLe16(fw, fwPos + 4);
                done = (length & 0x8000) != 0;
                length &= 0x7FFF;
                fwPos += 6;

                if (fwPos + length > fw.Length)
                {
                    return false;
                }

                var chunk = new byte[length];
                Array.Copy(fw, fwPos, chunk, 0, length);
                fwPos += length;

                if (!ControlOut(device, (ushort)offset, (ushort)index, chunk))
                {
                    return false;
                }
            }

            // Release the 8051 from reset (CPUCS bit0 = 0). The device renumerates
            // here, so this transfer is expected to fail occasionally; ignore it.
            try
            {
                ControlOut(device, CpucsAddress, 0, new byte[] { 0 });
            }
            catch
            {
                // Renumeration tears down the handle; this is the success path.
            }

            return true;
        }

        private static bool ControlOut(CyUSBDevice device, ushort value, ushort index, byte[] data)
        {
            var ctrl = device.ControlEndPt;
            ctrl.Target = CyConst.TGT_DEVICE;
            ctrl.ReqType = CyConst.REQ_VENDOR;
            ctrl.Direction = CyConst.DIR_TO_DEVICE;
            ctrl.ReqCode = FirmwareLoadRequest;
            ctrl.Value = value;
            ctrl.Index = index;

            var buffer = data ?? Array.Empty<byte>();
            int len = buffer.Length;
            return ctrl.XferData(ref buffer, ref len);
        }

        private static int ReadLe16(byte[] data, int byteOffset)
        {
            return data[byteOffset] | (data[byteOffset + 1] << 8);
        }

        private static void WriteLe16(byte[] data, int byteOffset, ushort value)
        {
            data[byteOffset] = (byte)(value & 0xFF);
            data[byteOffset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
