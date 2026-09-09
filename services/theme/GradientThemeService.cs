using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using MozartBrowser.Models;

namespace MozartBrowser.Services.Theme
{
    /// <summary>
    /// Builds a WPF Brush from a set of Custom Themes gradient color stops.
    /// Used both for MainWindow's toolbar (Row 0) / tab strip (Row 1) backgrounds
    /// and for GradientCanvasControl's own live preview while editing.
    /// Deliberately returns null (not a fallback brush) when there's nothing to
    /// render — callers decide what "off" should fall back to (normally the
    /// themed ChromeBackgroundBrush).
    /// </summary>
    public static class GradientThemeService
    {
        /// <summary>Convenience overload for MainWindow: honors CustomThemeSettings.IsEnabled.</summary>
        public static Brush? BuildBackgroundBrush(CustomThemeSettings settings)
        {
            if (!settings.IsEnabled) return null;
            return BuildBackgroundBrush(settings.ColorStops);
        }

        /// <summary>
        /// Renders regardless of any "enabled" flag — used by the live preview in
        /// GradientCanvasControl, which should always show the current stops as
        /// the user edits them, even before they've saved/enabled anything.
        /// </summary>
        public static Brush? BuildBackgroundBrush(IReadOnlyList<GradientColorStop> stops)
        {
            switch (stops.Count)
            {
                case 0:
                    return null;

                case 1:
                    return new SolidColorBrush(ParseColor(stops[0].Hex));

                case 2:
                {
                    var a = stops[0];
                    var b = stops[1];
                    return new LinearGradientBrush(ParseColor(a.Hex), ParseColor(b.Hex),
                        new System.Windows.Point(a.X, a.Y), new System.Windows.Point(b.X, b.Y));
                }

                default:
                {
                    // WPF has no native freeform 2D "mesh gradient" like Zen Browser's
                    // WebGL-based picker. Approximation (a deliberate, communicated
                    // simplification — not a bug): order the up-to-3 stops along
                    // whichever axis they're most spread out on, and render a
                    // standard 3-stop LinearGradientBrush along that axis, using the
                    // two extreme stops' own positions as the gradient's start/end.
                    var ordered = OrderByDominantAxis(stops);

                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new System.Windows.Point(ordered[0].X, ordered[0].Y),
                        EndPoint = new System.Windows.Point(ordered[^1].X, ordered[^1].Y)
                    };

                    var count = ordered.Count;
                    for (var i = 0; i < count; i++)
                    {
                        var offset = count == 1 ? 0.0 : (double)i / (count - 1);
                        brush.GradientStops.Add(new GradientStop(ParseColor(ordered[i].Hex), offset));
                    }

                    return brush;
                }
            }
        }

        private static IReadOnlyList<GradientColorStop> OrderByDominantAxis(IReadOnlyList<GradientColorStop> stops)
        {
            var xRange = stops.Max(s => s.X) - stops.Min(s => s.X);
            var yRange = stops.Max(s => s.Y) - stops.Min(s => s.Y);

            return xRange >= yRange
                ? stops.OrderBy(s => s.X).ToList()
                : stops.OrderBy(s => s.Y).ToList();
        }

        public static Color ParseColor(string hex)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex)!;
            }
            catch
            {
                return Colors.Gray; // malformed hex (e.g. mid-edit in the Settings hex box) shouldn't crash rendering
            }
        }
    }
}
