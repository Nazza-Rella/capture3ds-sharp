using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Capture3DS.Ftd3;

// Compile with the REAL Ftd3Native.cs, not the transport test stand-in.
// Only constants and managed predicates are used; no P/Invoke is ever called.
internal static class Ftd3NativeStatusTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }

    public static int Main()
    {
        try
        {
            // FTDI AN_379 type definitions and vendor FTD3XX.h agree on these
            // decimal values. Keep expectations independent of the fake driver.
            Check(Ftd3Native.FT_IO_PENDING == 24, "Real FT_IO_PENDING must be decimal 24, not 32");
            Check(Ftd3Native.FT_IO_INCOMPLETE == 25, "Real FT_IO_INCOMPLETE must be decimal 25, not 33");
            Check(Ftd3Native.IsTransient(24), "Actual pending status enters bounded recovery");
            Check(Ftd3Native.IsTransient(25), "Actual incomplete status enters bounded recovery");
            foreach (int status in new[] { 0, 3, 19, 20, 30, 32, 33, -1 })
                Check(!Ftd3Native.IsTransient(status), "Non-pending code must not enter recovery: " + status);
            Check(!Ftd3Native.Failed(0), "FT_OK is success");
            Check(Ftd3Native.Failed(32), "FT_OTHER_ERROR is failure");
            Type overlap = typeof(Ftd3Native.Overlapped);
            Check(Marshal.SizeOf(overlap) == (IntPtr.Size == 8 ? 32 : 20), "real OVERLAPPED native size");
            Check(Marshal.OffsetOf(overlap, "Internal").ToInt32() == 0, "OVERLAPPED Internal offset");
            Check(Marshal.OffsetOf(overlap, "InternalHigh").ToInt32() == IntPtr.Size, "OVERLAPPED InternalHigh offset");
            Check(Marshal.OffsetOf(overlap, "Offset").ToInt32() == IntPtr.Size * 2, "OVERLAPPED Offset offset");
            Check(Marshal.OffsetOf(overlap, "OffsetHigh").ToInt32() == IntPtr.Size * 2 + 4, "OVERLAPPED OffsetHigh offset");
            Check(Marshal.OffsetOf(overlap, "Event").ToInt32() == (IntPtr.Size == 8 ? 24 : 16), "OVERLAPPED event handle offset");
            MethodInfo read = typeof(Ftd3Native).GetMethod("FT_ReadPipeAsync");
            var readImport = (DllImportAttribute)Attribute.GetCustomAttribute(read, typeof(DllImportAttribute));
            Check(readImport.EntryPoint == "FT_ReadPipe" && readImport.CallingConvention == CallingConvention.StdCall,
                "async wrapper binds the real ReadPipe entry point and calling convention");
            ParameterInfo[] parameters = read.GetParameters();
            Check(parameters.Length == 6 && parameters[2].ParameterType == typeof(IntPtr)
                && parameters[4].ParameterType == typeof(IntPtr) && parameters[5].ParameterType == typeof(IntPtr),
                "async buffers, count and overlap use stable pointers, not marshaller temporaries");
            var wait = typeof(Ftd3Native).GetMethod("FT_GetOverlappedResult").GetParameters()[3];
            var marshal = (MarshalAsAttribute)Attribute.GetCustomAttribute(wait, typeof(MarshalAsAttribute));
            Check(marshal != null && marshal.Value == UnmanagedType.Bool, "completion wait is Win32 BOOL");
            Console.WriteLine("PASS: " + checks + " real Ftd3Native constant/ABI checks; no native calls.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
