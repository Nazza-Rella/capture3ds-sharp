using System;
using System.Runtime.InteropServices;

namespace Capture3DS.Loopy
{
    /// <summary>libusb-1.0 ABI used by the Loopy old-3DS USB backend.</summary>
    internal static class LibUsbNative
    {
        private const string DllName = "libusb-1.0.dll";

        internal const int Success = 0;
        internal const int ErrorNoDevice = -4;
        internal const int ErrorTimeout = -7;
        internal const int ErrorPipe = -9;

        internal const byte EndpointIn = 0x80;
        internal const byte RequestTypeVendor = 0x40;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct DeviceDescriptor
        {
            internal byte Length;
            internal byte DescriptorType;
            internal ushort BcdUsb;
            internal byte DeviceClass;
            internal byte DeviceSubClass;
            internal byte DeviceProtocol;
            internal byte MaxPacketSize0;
            internal ushort VendorId;
            internal ushort ProductId;
            internal ushort BcdDevice;
            internal byte ManufacturerIndex;
            internal byte ProductIndex;
            internal byte SerialIndex;
            internal byte ConfigurationCount;
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_init")]
        internal static extern int Init(out IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_exit")]
        internal static extern void Exit(IntPtr context);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_device_list")]
        internal static extern IntPtr GetDeviceList(IntPtr context, out IntPtr list);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_free_device_list")]
        internal static extern void FreeDeviceList(IntPtr list, int unrefDevices);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_device_descriptor")]
        internal static extern int GetDeviceDescriptor(IntPtr device, out DeviceDescriptor descriptor);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_open")]
        internal static extern int Open(IntPtr device, out IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_close")]
        internal static extern void Close(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_string_descriptor_ascii")]
        internal static extern int GetStringDescriptorAscii(
            IntPtr handle, byte descriptorIndex, byte[] data, int length);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_bus_number")]
        internal static extern byte GetBusNumber(IntPtr device);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_port_numbers")]
        internal static extern int GetPortNumbers(IntPtr device, byte[] ports, int portCount);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_get_configuration")]
        internal static extern int GetConfiguration(IntPtr handle, out int configuration);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_set_configuration")]
        internal static extern int SetConfiguration(IntPtr handle, int configuration);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_kernel_driver_active")]
        internal static extern int KernelDriverActive(IntPtr handle, int interfaceNumber);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_detach_kernel_driver")]
        internal static extern int DetachKernelDriver(IntPtr handle, int interfaceNumber);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_attach_kernel_driver")]
        internal static extern int AttachKernelDriver(IntPtr handle, int interfaceNumber);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_claim_interface")]
        internal static extern int ClaimInterface(IntPtr handle, int interfaceNumber);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_release_interface")]
        internal static extern int ReleaseInterface(IntPtr handle, int interfaceNumber);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_control_transfer")]
        internal static extern int ControlTransfer(
            IntPtr handle,
            byte requestType,
            byte request,
            ushort value,
            ushort index,
            IntPtr data,
            ushort length,
            uint timeoutMilliseconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_bulk_transfer")]
        internal static extern int BulkTransfer(
            IntPtr handle,
            byte endpoint,
            IntPtr data,
            int length,
            out int transferred,
            uint timeoutMilliseconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "libusb_clear_halt")]
        internal static extern int ClearHalt(IntPtr handle, byte endpoint);
    }
}
