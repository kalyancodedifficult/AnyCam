namespace AnyCam.Emulator;

/// <summary>
/// Draws NV12 test frames: color bars, a moving bar (shows motion and latency), a big frame counter,
/// and a white flash at every whole second (paired with a beep in the audio, for checking A/V sync).
/// </summary>
internal sealed class TestPattern
{
    private static readonly (byte Y, byte U, byte V)[] Bars =
    [
        (235, 128, 128), // white
        (219, 16, 138),  // yellow
        (188, 154, 16),  // cyan
        (173, 42, 26),   // green
        (78, 214, 230),  // magenta
        (63, 102, 240),  // red
        (32, 240, 118),  // blue
        (16, 128, 128),  // black
    ];

    // 7-segment layout: a b c d e f g
    private static readonly bool[][] Digits =
    [
        [true, true, true, true, true, true, false],     // 0
        [false, true, true, false, false, false, false], // 1
        [true, true, false, true, true, false, true],    // 2
        [true, true, true, true, false, false, true],    // 3
        [false, true, true, false, false, true, true],   // 4
        [true, false, true, true, false, true, true],    // 5
        [true, false, true, true, true, true, true],     // 6
        [true, true, true, false, false, false, false],  // 7
        [true, true, true, true, true, true, true],      // 8
        [true, true, true, true, false, true, true],     // 9
    ];

    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _background;

    public TestPattern(int width, int height)
    {
        _width = width;
        _height = height;
        _background = new byte[width * height * 3 / 2];
        DrawBackground();
    }

    public int FrameSize => _background.Length;

    private void DrawBackground()
    {
        var barsHeight = _height * 2 / 3;
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                byte luma;
                if (y < barsHeight)
                    luma = Bars[Math.Min(x * Bars.Length / _width, Bars.Length - 1)].Y;
                else
                    luma = (byte)(16 + x * 219 / Math.Max(_width - 1, 1)); // gray ramp
                _background[y * _width + x] = luma;
            }
        }
        var chroma = _width * _height;
        for (var y = 0; y < _height / 2; y++)
        {
            for (var x = 0; x < _width; x += 2)
            {
                byte u = 128, v = 128;
                if (y * 2 < barsHeight)
                {
                    var bar = Bars[Math.Min(x * Bars.Length / _width, Bars.Length - 1)];
                    u = bar.U;
                    v = bar.V;
                }
                _background[chroma + y * _width + x] = u;
                _background[chroma + y * _width + x + 1] = v;
            }
        }
    }

    public void Render(byte[] frame, long frameNumber, int fps, string label)
    {
        Buffer.BlockCopy(_background, 0, frame, 0, _background.Length);

        // Moving vertical bar: one full sweep every 2 seconds.
        var barWidth = Math.Max(_width / 40, 4) & ~1;
        var position = (int)(frameNumber % (fps * 2) * (_width - barWidth) / (fps * 2)) & ~1;
        FillRect(frame, position, 0, barWidth, _height, 235);

        // Flash square at every whole second (A/V sync check together with the beep).
        if (frameNumber % fps < Math.Max(fps / 10, 1))
            FillRect(frame, _width - _height / 4 - 20, 20, _height / 4, _height / 4, 235);

        // Frame counter.
        var digitHeight = _height / 6;
        DrawNumber(frame, frameNumber, 24, 24, digitHeight);
        DrawNumber(frame, label.Length, 24, 24 + digitHeight + 24, digitHeight / 3); // tiny marker that the label changed
    }

    private void DrawNumber(byte[] frame, long value, int left, int top, int height)
    {
        var text = value.ToString();
        var width = height / 2;
        var thickness = Math.Max(height / 10, 2);
        FillRect(frame, left - 12, top - 12, text.Length * (width + thickness * 2) + 24, height + 24, 16);
        for (var i = 0; i < text.Length; i++)
            DrawDigit(frame, text[i] - '0', left + i * (width + thickness * 2), top, width, height, thickness);
    }

    private void DrawDigit(byte[] frame, int digit, int x, int y, int w, int h, int t)
    {
        var s = Digits[digit];
        var half = h / 2;
        if (s[0]) FillRect(frame, x, y, w, t, 235);
        if (s[1]) FillRect(frame, x + w - t, y, t, half, 235);
        if (s[2]) FillRect(frame, x + w - t, y + half, t, half, 235);
        if (s[3]) FillRect(frame, x, y + h - t, w, t, 235);
        if (s[4]) FillRect(frame, x, y + half, t, half, 235);
        if (s[5]) FillRect(frame, x, y, t, half, 235);
        if (s[6]) FillRect(frame, x, y + half - t / 2, w, t, 235);
    }

    private void FillRect(byte[] frame, int x, int y, int w, int h, byte luma)
    {
        x = Math.Clamp(x, 0, _width);
        y = Math.Clamp(y, 0, _height);
        var right = Math.Clamp(x + w, 0, _width);
        var bottom = Math.Clamp(y + h, 0, _height);
        for (var row = y; row < bottom; row++)
            frame.AsSpan(row * _width + x, right - x).Fill(luma);

        // Neutral chroma so the rectangle is gray/white.
        var chroma = _width * _height;
        for (var row = y / 2; row < (bottom + 1) / 2; row++)
            frame.AsSpan(chroma + row * _width + (x & ~1), ((right + 1) & ~1) - (x & ~1)).Fill(128);
    }
}
