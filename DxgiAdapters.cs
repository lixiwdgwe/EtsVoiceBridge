using System.Runtime.InteropServices;

namespace EtsVoiceBridge;

internal sealed record GpuAdapter(uint Index, string Name, uint VendorId);

internal static class DxgiAdapters
{
    private static readonly Guid Factory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DxgiNotFound = unchecked((int)0x887A0002);

    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        var id = Factory1Id;
        var hr = CreateDXGIFactory1(ref id, out var factory);
        Marshal.ThrowExceptionForHR(hr);
        var result = new List<GpuAdapter>();
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapters1>(Method(factory, 12));
            for (uint index = 0; ; index++)
            {
                hr = enumerate(factory, index, out var adapter);
                if (hr == DxgiNotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var getDescription = Marshal.GetDelegateForFunctionPointer<GetDesc1>(Method(adapter, 10));
                    Marshal.ThrowExceptionForHR(getDescription(adapter, out var description));
                    result.Add(new GpuAdapter(index, description.Description.TrimEnd('\0'), description.VendorId));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return result;
    }

    private static IntPtr Method(IntPtr instance, int index) =>
        Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size);

    private static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<ReleaseObject>(Method(instance, 2))(instance);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1(IntPtr self, out AdapterDesc1 description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseObject(IntPtr self);
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
}
