using System.Runtime.InteropServices;

namespace SmartVol.Services;

/// <summary>
/// Sets the default audio endpoint through the IPolicyConfig COM API.
/// Fix vs the Python version: the default is applied to all three roles
/// (Console, Multimedia, Communications) so a switch reliably affects
/// games, media players and chat apps instead of only Console-role streams.
/// </summary>
internal static class DefaultDevicePolicy
{
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IidPolicyConfig = new("F8679F50-850A-41CF-9C72-430F290290C8");
    private const uint ClsctxInprocServer = 1;

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPolicyConfig ppv);

    public static void SetDefaultEndpoint(string deviceId)
    {
        var clsid = ClsidPolicyConfigClient;
        var iid = IidPolicyConfig;
        CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out IPolicyConfig policy);

        try
        {
            for (uint role = 0; role <= 2; role++)
            {
                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);
            }

            AppLogger.Info($"Default audio device switched to {deviceId}");
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Vtable padding: only the slot positions matter, these are never called.
        [PreserveSig] int P0(IntPtr a, out IntPtr b);
        [PreserveSig] int P1(IntPtr a, int b, out IntPtr c);
        [PreserveSig] int P2(IntPtr a);
        [PreserveSig] int P3(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int P4(IntPtr a, int b, out long c, out long d);
        [PreserveSig] int P5(IntPtr a, ref long b);
        [PreserveSig] int P6(IntPtr a, out IntPtr b);
        [PreserveSig] int P7(IntPtr a, IntPtr b);
        [PreserveSig] int P8(IntPtr a, ref PropertyKey b, out PropVariant c);
        [PreserveSig] int P9(IntPtr a, ref PropertyKey b, ref PropVariant c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }
}
