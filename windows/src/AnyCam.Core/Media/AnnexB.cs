namespace AnyCam.Core.Media;

/// <summary>Helpers for H.264 / HEVC Annex-B byte streams (NAL units separated by start codes).</summary>
public static class AnnexB
{
    /// <summary>Returns (offset, length) of each NAL unit payload, excluding start codes.</summary>
    public static List<(int Offset, int Length)> SplitNalUnits(ReadOnlySpan<byte> data)
    {
        var units = new List<(int, int)>();
        var start = -1;
        var i = 0;
        while (i + 3 <= data.Length)
        {
            var startCodeLength = StartCodeLength(data, i);
            if (startCodeLength > 0)
            {
                if (start >= 0)
                    units.Add((start, TrimTrailingZeros(data, start, i) - start));
                i += startCodeLength;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < data.Length)
            units.Add((start, data.Length - start));
        return units;
    }

    private static int StartCodeLength(ReadOnlySpan<byte> data, int i)
    {
        if (data[i] != 0 || data[i + 1] != 0)
            return 0;
        if (data[i + 2] == 1)
            return 3;
        if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            return 4;
        return 0;
    }

    private static int TrimTrailingZeros(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
            end--;
        return end;
    }

    public static int NalType(byte header, bool hevc) => hevc ? (header >> 1) & 0x3F : header & 0x1F;

    public static bool IsParameterSet(int nalType, bool hevc) =>
        hevc ? nalType is 32 or 33 or 34 : nalType is 7 or 8;

    public static bool IsKeyframeNal(int nalType, bool hevc) =>
        hevc ? nalType is >= 16 and <= 21 : nalType == 5;

    /// <summary>True if the access unit contains an IDR (H.264) or IRAP (HEVC) picture.</summary>
    public static bool ContainsKeyframe(ReadOnlySpan<byte> accessUnit, bool hevc)
    {
        foreach (var (offset, length) in SplitNalUnits(accessUnit))
        {
            if (length > 0 && IsKeyframeNal(NalType(accessUnit[offset], hevc), hevc))
                return true;
        }
        return false;
    }

    /// <summary>Concatenates the parameter sets (VPS/SPS/PPS) of an access unit, each with a 4-byte start code.</summary>
    public static byte[] ExtractParameterSets(ReadOnlySpan<byte> accessUnit, bool hevc)
    {
        using var output = new MemoryStream();
        foreach (var (offset, length) in SplitNalUnits(accessUnit))
        {
            if (length == 0 || !IsParameterSet(NalType(accessUnit[offset], hevc), hevc))
                continue;
            output.Write([0, 0, 0, 1]);
            output.Write(accessUnit.Slice(offset, length));
        }
        return output.ToArray();
    }
}
