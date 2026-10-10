using System.Runtime.InteropServices;

namespace AnyCam.Emulator;

/// <summary>Minimal ICodecAPI access (SetValue only) through the COM vtable.</summary>
internal static unsafe class CodecApi
{
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");          // VT_UI4
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");   // VT_BOOL
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");  // VT_UI4 (0 = CBR)
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");      // VT_UI4
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");    // VT_UI4
    public static readonly Guid BFrameCount = new("8a5e8d34-9a69-4747-ac21-2f9e8b4c1eda");      // VT_UI4 (CODECAPI_AVEncMPVDefaultBPictureCount)

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public short Bool;
    }

    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;

    public static bool SetUInt32(IntPtr unknown, Guid key, uint value)
    {
        var variant = new Variant { Type = VtUi4, UInt32 = value };
        return Set(unknown, key, &variant);
    }

    public static bool SetBool(IntPtr unknown, Guid key, bool value)
    {
        var variant = new Variant { Type = VtBool, Bool = (short)(value ? -1 : 0) };
        return Set(unknown, key, &variant);
    }

    private static bool Set(IntPtr unknown, Guid key, Variant* value)
    {
        var iid = IidCodecApi;
        if (Marshal.QueryInterface(unknown, in iid, out var codecApi) < 0)
            return false;
        try
        {
            // IUnknown (3) + IsSupported, IsModifiable, GetParameterRange, GetParameterValues,
            // GetDefaultValue, GetValue, SetValue → slot 9.
            var vtable = *(IntPtr**)codecApi;
            var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtable[9];
            return setValue(codecApi, &key, value) >= 0;
        }
        finally
        {
            Marshal.Release(codecApi);
        }
    }
}
