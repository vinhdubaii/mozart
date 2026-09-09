using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MozartBrowser.Utils;

namespace MozartBrowser.Dialogs
{
    /// <summary>
    /// Standalone HSV color picker: a saturation/value square + hue bar +
    /// preset swatches + hex box, opened from GradientCanvasControl when
    /// adding a new gradient color stop (and available for editing an
    /// existing stop's color too). Returns the chosen color via
    /// <see cref="SelectedHex"/> when the dialog closes with
    /// <c>DialogResult == true</c>.
    /// </summary>
    public partial class ColorPickerDialog : Window
    {
        private static readonly string[] PresetColors =
        {
            "#FF6B6B", "#FFD93D", "#6BCB77", "#4D96FF",
            "#9B5DE5", "#F15BB5", "#00BBF9", "#FEE440"
        };

        public string SelectedHex { get; private set; } = "#FFFFFF";

        private double _hue;      // 0-360
        private double _sat;      // 0-1
        private double _val;      // 0-1

        private bool _isDraggingSv;
        private bool _isDraggingHue;
        private bool _suppressHexHandler;

        public ColorPickerDialog(string initialHex = "#FFFFFF")
        {
            InitializeComponent();
            Interop.WindowMaximizeFix.Apply(this);

            BuildPresetRow();

            var hsv = ColorConversion.HexToHsv(initialHex);
            _hue = hsv.H; _sat = hsv.S; _val = hsv.V;

            Loaded += (_, _) => RefreshAllFromHsv(); // needs ActualWidth, so wait for layout
        }

        // ============================= Presets =============================

        private void BuildPresetRow()
        {
            PresetRow.Children.Clear();
            foreach (var hex in PresetColors)
            {
                var swatchHex = hex;
                var swatch = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Margin = new Thickness(0, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    Background = SafeBrush(swatchHex),
                    BorderBrush = (Brush)FindResource("ChromeBorderBrush"),
                    BorderThickness = new Thickness(1)
                };
                swatch.MouseLeftButtonDown += (_, _) => SetFromHex(swatchHex);
                PresetRow.Children.Add(swatch);
            }
        }

        // ============================= SV square drag =============================

        private void SvSquare_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSv = true;
            SvSquareBorder.CaptureMouse();
            UpdateSvFromMouse(e.GetPosition(SvSquareBorder));
        }

        private void SvSquare_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSv) UpdateSvFromMouse(e.GetPosition(SvSquareBorder));
        }

        private void SvSquare_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSv = false;
            SvSquareBorder.ReleaseMouseCapture();
        }

        private void UpdateSvFromMouse(Point p)
        {
            var w = SvSquareBorder.ActualWidth;
            var h = SvSquareBorder.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _sat = Math.Clamp(p.X / w, 0, 1);
            _val = 1 - Math.Clamp(p.Y / h, 0, 1); // top of square = full value

            RefreshAllFromHsv();
        }

        // ============================= Hue bar drag =============================

        private void HueBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = true;
            HueBarBorder.CaptureMouse();
            UpdateHueFromMouse(e.GetPosition(HueBarBorder));
        }

        private void HueBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHue) UpdateHueFromMouse(e.GetPosition(HueBarBorder));
        }

        private void HueBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingHue = false;
            HueBarBorder.ReleaseMouseCapture();
        }

        private void UpdateHueFromMouse(Point p)
        {
            var w = HueBarBorder.ActualWidth;
            if (w <= 0) return;

            _hue = Math.Clamp(p.X / w, 0, 1) * 360;
            RefreshAllFromHsv();
        }

        // ============================= Hex box =============================

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressHexHandler) return;

            var hex = HexBox.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return;

            var hsv = ColorConversion.HexToHsv(hex);
            _hue = hsv.H; _sat = hsv.S; _val = hsv.V;
            RefreshAllFromHsv(updateHexBox: false); // avoid feedback loop while user is typing
        }

        private void SetFromHex(string hex)
        {
            var hsv = ColorConversion.HexToHsv(hex);
            _hue = hsv.H; _sat = hsv.S; _val = hsv.V;
            RefreshAllFromHsv();
        }

        // ============================= Render =============================

        private void RefreshAllFromHsv(bool updateHexBox = true)
        {
            var pureHue = ColorConversion.HueToPureColor(_hue);
            HueLayer.Fill = new SolidColorBrush(pureHue);

            var current = ColorConversion.HsvToRgb(_hue, _sat, _val);
            var hex = ColorConversion.ColorToHex(current);
            SelectedHex = hex;

            if (updateHexBox)
            {
                _suppressHexHandler = true;
                HexBox.Text = hex;
                _suppressHexHandler = false;
            }

            PositionSvDot();
            PositionHueThumb();
        }

        private void PositionSvDot()
        {
            var w = SvSquareBorder.ActualWidth;
            var h = SvSquareBorder.ActualHeight;
            if (w <= 0 || h <= 0) return; // before first layout pass

            Canvas.SetLeft(SvDot, _sat * w - SvDot.Width / 2);
            Canvas.SetTop(SvDot, (1 - _val) * h - SvDot.Height / 2);
        }

        private void PositionHueThumb()
        {
            var w = HueBarBorder.ActualWidth;
            if (w <= 0) return;

            Canvas.SetLeft(HueThumb, (_hue / 360) * w - HueThumb.Width / 2);
            Canvas.SetTop(HueThumb, 0);
        }

        private static Brush SafeBrush(string hex)
        {
            try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
            catch { return Brushes.Gray; }
        }

        // ============================= Title bar / actions =============================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
