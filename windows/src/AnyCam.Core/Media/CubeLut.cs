using System.Globalization;

namespace AnyCam.Core.Media;

/// <summary>A 3D color lookup table loaded from an Adobe/Resolve <c>.cube</c> file.</summary>
public sealed class CubeLut
{
    private CubeLut(string name, int size, float[] data)
    {
        Name = name;
        Size = size;
        Data = data;
    }

    public string Name { get; }

    /// <summary>Entries per axis (e.g. 33).</summary>
    public int Size { get; }

    /// <summary>RGBA floats, red changing fastest, then green, then blue (Size³ × 4).</summary>
    public float[] Data { get; }

    public static CubeLut Load(string path) => Parse(File.ReadLines(path), Path.GetFileNameWithoutExtension(path));

    public static CubeLut Parse(IEnumerable<string> lines, string name = "LUT")
    {
        var size = 0;
        float[]? data = null;
        var count = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE":
                    var quoted = line.IndexOf('"');
                    if (quoted >= 0)
                        name = line[(quoted + 1)..].TrimEnd('"');
                    continue;
                case "LUT_3D_SIZE":
                    size = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (size < 2 || size > 256)
                        throw new FormatException($"Unsupported LUT size {size}");
                    data = new float[size * size * size * 4];
                    continue;
                case "LUT_1D_SIZE":
                    throw new FormatException("1D LUTs are not supported; export a 3D .cube LUT.");
                case "DOMAIN_MIN":
                case "DOMAIN_MAX":
                    continue; // creator LUTs use the 0..1 input domain; others are sampled as if they did
            }

            if (data is null || parts.Length < 3 || !char.IsAsciiDigit(parts[0][^1]) && parts[0][0] != '-' && parts[0][0] != '.')
                continue;
            if (count >= size * size * size)
                break;

            for (var c = 0; c < 3; c++)
                data[count * 4 + c] = ParseFloat(parts[c]);
            data[count * 4 + 3] = 1;
            count++;
        }

        if (data is null || count != size * size * size)
            throw new FormatException($"Expected {size * size * size} LUT entries, found {count}.");
        return new CubeLut(name, size, data);
    }

    private static float ParseFloat(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
