using System;
using System.Windows.Media;

namespace MozartBrowser.Utils
{
    /// <summary>
    /// Hex ↔ RGB ↔ HSV conversion for ColorPickerDialog's saturation/value
    /// square + hue bar. Kept dependency-free (no third-party color libs) —
    /// this is standard, well-known math, not worth pulling in a package for.
    /// </summary>
    public static class ColorConversion
    {
        public record Hsv(double H, double S, double V); // H: 0-360, S/V: 0-1

        public static Hsv HexToHsv(string hex) => RgbToHsv(HexToColor(hex));

        public static string HsvToHex(double h, double s, double v) => ColorToHex(HsvToRgb(h, s, v));

        public static Color HexToColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch
            {
                return Colors.Gray; // malformed/partial hex (e.g. mid-typing) shouldn't crash the picker
            }
        }

        public static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static Hsv RgbToHsv(Color c)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            double h;
            if (delta < 0.00001) h = 0;
            else if (max == r) h = 60 * (((g - b) / delta) % 6);
            else if (max == g) h = 60 * (((b - r) / delta) + 2);
            else h = 60 * (((r - g) / delta) + 4);
            if (h < 0) h += 360;

            double s = max <= 0 ? 0 : delta / max;
            double v = max;

            return new Hsv(h, s, v);
        }

        public static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360; // normalize negative/overflowing hue
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;

            var (r, g, b) = h switch
            {
                < 60 => (c, x, 0.0),
                < 120 => (x, c, 0.0),
                < 180 => (0.0, c, x),
                < 240 => (0.0, x, c),
                < 300 => (x, 0.0, c),
                _ => (c, 0.0, x)
            };

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        /// <summary>Pure hue color at full saturation/value — used as the SV square's base layer for a given hue.</summary>
        public static Color HueToPureColor(double h) => HsvToRgb(h, 1.0, 1.0);
    }
}
