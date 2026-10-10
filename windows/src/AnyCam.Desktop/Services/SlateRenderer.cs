using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AnyCam.Desktop.Services;

/// <summary>Renders the "waiting for phone" image shown in the preview and the virtual camera.</summary>
public static class SlateRenderer
{
    public static (byte[] Pixels, int Width, int Height) Render(int width, int height, string title, string subtitle)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x14, 0x15, 0x19), Color.FromRgb(0x22, 0x24, 0x2B), 90);
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));

            var unit = Math.Min(width, height) / 12.0;
            var typeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var bodyface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // Camera glyph: rounded body + lens.
            var center = new Point(width / 2.0, height / 2.0 - unit * 1.6);
            var body = new Rect(center.X - unit * 1.2, center.Y - unit * 0.75, unit * 2.4, unit * 1.5);
            context.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), unit * 0.12), body, unit * 0.3, unit * 0.3);
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)), unit * 0.12), center, unit * 0.42, unit * 0.42);

            var titleText = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, unit * 1.1,
                new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)), 1.0);
            context.DrawText(titleText, new Point((width - titleText.Width) / 2, height / 2.0 + unit * 0.1));

            var subtitleText = new FormattedText(subtitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bodyface, unit * 0.4,
                new SolidColorBrush(Color.FromRgb(0xA0, 0xA3, 0xAB)), 1.0)
            {
                MaxTextWidth = width * 0.8,
                TextAlignment = TextAlignment.Center,
            };
            context.DrawText(subtitleText, new Point(width * 0.1, height / 2.0 + unit * 1.55));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }
}
