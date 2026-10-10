namespace AnyCam.Core.Media;

/// <summary>Media Foundation GUIDs and HRESULTs not exposed by Vortice.</summary>
internal static class MfConstants
{
    // MF_LOW_LATENCY
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    // MF_MT_MINIMUM_DISPLAY_APERTURE (visible area when the decoded surface is padded, e.g. 1920x1088)
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");

    public static readonly Guid MpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");

    public const int NeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT
    public const int StreamChange = unchecked((int)0xC00D6D61);   // MF_E_TRANSFORM_STREAM_CHANGE
    public const int NotAccepting = unchecked((int)0xC00D36B5);   // MF_E_NOTACCEPTING
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);    // MF_E_NO_MORE_TYPES

    // MFT_ENUM_FLAG_*
    public const uint EnumSyncMft = 0x00000001;
    public const uint EnumAsyncMft = 0x00000002;
    public const uint EnumHardware = 0x00000004;
    public const uint EnumLocalMft = 0x00000010;
    public const uint EnumSortAndFilter = 0x00000040;

    // MFT_OUTPUT_STREAM_INFO flags
    public const int OutputProvidesSamples = 0x00000100;
    public const int OutputCanProvideSamples = 0x00000200;

    public static ulong PackSize(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;

    public static ulong PackRatio(int numerator, int denominator) => ((ulong)(uint)numerator << 32) | (uint)denominator;

    public static (int Width, int Height) UnpackSize(ulong value) => ((int)(value >> 32), (int)(value & 0xFFFFFFFF));

    /// <summary>Frame rate as a rational, e.g. 29.97 → 30000/1001.</summary>
    public static (int Numerator, int Denominator) FpsRatio(double fps)
    {
        if (fps <= 0)
            return (30, 1);
        var rounded = Math.Round(fps);
        if (Math.Abs(fps - rounded) < 0.01)
            return ((int)rounded, 1);
        return ((int)Math.Round(fps * 1001), 1001);
    }
}
