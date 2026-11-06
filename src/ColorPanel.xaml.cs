using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimelapseShorts;

/// <summary>
/// ④ テキストの「自由に選ぶ（RGB）」。文字の色と、フチ・帯の色（どちらも "RRGGBB"）を、円形のカラーパレット
/// （色の輪で色合いと鮮やかさ、バーで明るさ）・R/G/B の数値・#RRGGBB・よく使う色で選ぶ。
/// 上の「文字の色」「フチ・帯の色」で、どちらを変えるかを切り替える。
/// </summary>
public partial class ColorPanel : UserControl
{
    /// <summary>よく使う色（左から 白・黒・灰・赤・橙・黄・緑・青緑・水色・青・藍・紫・ピンク・茶）。</summary>
    internal static readonly string[] SwatchColors =
        ["FFFFFF", "000000", "808080", "FF3B30", "FF9500", "FFE600", "34C759", "00C7BE", "5AC8FA", "007AFF", "5856D6", "AF52DE",
         "FF5FA2", "8B5A2B"];

    internal const double WheelSize = 180;     // 色の輪の大きさ（XAML の Wheel と同じ）
    const int WheelPixels = 360;               // 輪の絵の細かさ（高解像度の画面でもなめらかに見えるよう 2 倍）

    Color _text = Colors.White, _outline = Colors.Black;
    // 今変えている方の色の、色合い（0〜360。上が赤で右回り）・鮮やかさ（0〜1。輪の真ん中が 0）・明るさ（0〜1）。
    // 白・灰・黒では色合い（黒なら鮮やかさも）が決まらないので、前の値を残して、印が急に動かないようにする
    double _h, _s, _v = 1;
    readonly WriteableBitmap _wheel = new(WheelPixels, WheelPixels, 96, 96, PixelFormats.Bgra32, null);
    double _wheelV = -1;                       // 輪の絵を描いたときの明るさ
    bool _updating;                            // 表示をこちらで書き換えている間（打った値・動かした値として扱わない）

    /// <summary>色が変わった（輪・明るさ・数値・#RRGGBB・よく使う色で）。SetColors では出さない。</summary>
    public event Action? Changed;

    public ColorPanel()
    {
        InitializeComponent();
        WheelImage.Source = _wheel;
        var style = (Style)Resources["Swatch"];
        foreach (string hex in SwatchColors)
        {
            var color = Parse(hex)!.Value;
            var swatch = new Button { Style = style, Background = new SolidColorBrush(color), ToolTip = "#" + hex };
            AutomationProperties.SetName(swatch, "#" + hex);
            swatch.Click += (_, _) => SetCurrent(color);
            Swatches.Children.Add(swatch);
        }
        LoadHsv(Current);
        Show();
    }

    /// <summary>文字の色（"RRGGBB"）。</summary>
    public string TextColor => Hex(_text);

    /// <summary>フチ（背景ありなら帯）の色（"RRGGBB"）。</summary>
    public string OutlineColor => Hex(_outline);

    /// <summary>色を入れる（"RRGGBB"。読めない方は変えない）。</summary>
    public void SetColors(string? text, string? outline)
    {
        if (Parse(text) is { } t) _text = t;
        if (Parse(outline) is { } o) _outline = o;
        LoadHsv(Current);
        Show();
    }

    bool EditingOutline => OutlineTarget.IsChecked == true;
    Color Current => EditingOutline ? _outline : _text;

    /// <summary>選んでいる方（文字／フチ・帯）の色を変える。fromHsv なら、輪・明るさで決めた色（色合い・鮮やかさはそのまま使う）。</summary>
    void SetCurrent(Color color, bool fromHsv = false)
    {
        if (!fromHsv) LoadHsv(color);
        bool changed = color != Current;
        if (EditingOutline) _outline = color;
        else _text = color;
        Show();
        if (changed) Changed?.Invoke();
    }

    /// <summary>色から、色合い・鮮やかさ・明るさを読む（白・灰・黒では決まらない値は前のまま）。</summary>
    void LoadHsv(Color c)
    {
        var (h, s, v) = ToHsv(c);
        if (v > 0 && s > 0) _h = h;
        if (v > 0) _s = s;
        _v = v;
    }

    /// <summary>今の色を表示に出す（打っている途中の欄は書き換えない）。</summary>
    void Show()
    {
        _updating = true;
        TextChip.Background = new SolidColorBrush(_text);
        OutlineChip.Background = new SolidColorBrush(_outline);
        TextHex.Text = "#" + Hex(_text);
        OutlineHex.Text = "#" + Hex(_outline);
        var c = Current;
        SetText(RBox, Channel(c.R));
        SetText(GBox, Channel(c.G));
        SetText(BBox, Channel(c.B));
        SetText(HexBox, Hex(c));
        // 輪は今の明るさで描く（明るさが変わったときだけ描き直す）。印は色合い・鮮やかさの所
        if (Math.Abs(_wheelV - _v) > 0.001) DrawWheel(_v);
        var (x, y) = WheelPoint(_h, _s);
        Canvas.SetLeft(Marker, x - Marker.Width / 2);
        Canvas.SetTop(Marker, y - Marker.Height / 2);
        Marker.Fill = new SolidColorBrush(c);
        BrightSlider.Value = Math.Round(_v * 100);
        BrightText.Text = $"{Math.Round(_v * 100).ToString(CultureInfo.InvariantCulture)}%";
        BrightTrack.Background = new LinearGradientBrush(Colors.Black, FromHsv(_h, _s, 1), 0);
        _updating = false;
    }

    /// <summary>色合い・鮮やかさの、輪の上の位置（輪の左上から。上が赤で右回り、真ん中が鮮やかさ 0）。</summary>
    internal static (double X, double Y) WheelPoint(double h, double s)
    {
        double r = WheelSize / 2, a = h * Math.PI / 180;
        return (r + s * r * Math.Sin(a), r - s * r * Math.Cos(a));
    }

    /// <summary>輪の絵を描く（v は明るさ）。ふちは少しぼかしてなめらかにする。</summary>
    void DrawWheel(double v)
    {
        var pixels = new byte[WheelPixels * WheelPixels * 4];
        double center = (WheelPixels - 1) / 2.0, radius = WheelPixels / 2.0 - 1;
        for (int y = 0; y < WheelPixels; y++)
        {
            for (int x = 0; x < WheelPixels; x++)
            {
                double dx = x - center, dy = y - center, d = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(radius - d + 0.5, 0, 1);
                if (alpha <= 0) continue;
                double h = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
                var c = FromHsv(h, Math.Min(1, d / radius), v);
                int i = (y * WheelPixels + x) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = (byte)Math.Round(alpha * 255);
            }
        }
        _wheel.WritePixels(new Int32Rect(0, 0, WheelPixels, WheelPixels), pixels, WheelPixels * 4, 0);
        _wheelV = v;
    }

    /// <summary>輪の上の位置 p（輪の左上から）の色合い・鮮やかさにする。輪の外なら、ふちの色。黒のままなら明るくする。</summary>
    internal void PickWheel(Point p)
    {
        double r = WheelSize / 2, dx = p.X - r, dy = p.Y - r, d = Math.Sqrt(dx * dx + dy * dy);
        _s = Math.Min(1, d / r);
        if (d > 0.5) _h = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        if (_v < 0.05) _v = 1; // 黒のままでは輪のどこを選んでも黒なので、いちばん明るくする
        SetCurrent(FromHsv(_h, _s, _v), fromHsv: true);
    }

    void Wheel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Wheel.CaptureMouse();
        PickWheel(e.GetPosition(Wheel));
        e.Handled = true;
    }

    void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (Wheel.IsMouseCaptured) PickWheel(e.GetPosition(Wheel));
    }

    void Wheel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Wheel.ReleaseMouseCapture();

    void Bright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || HexBox == null) return;
        _v = BrightSlider.Value / 100;
        SetCurrent(FromHsv(_h, _s, _v), fromHsv: true);
    }

    static void SetText(TextBox box, string text)
    {
        if (!box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
    }

    static string Channel(byte value) => value.ToString(CultureInfo.InvariantCulture);

    void Target_Checked(object sender, RoutedEventArgs e)
    {
        if (HexBox == null) return; // 読み込みの途中（まだ部品がそろっていない）
        LoadHsv(Current);
        Show();
    }

    void ChannelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || HexBox == null) return;
        // 3 つとも 0〜255 で読めるときだけ変える（打っている途中の読めない値は、打ち終わったら元に戻す）
        if (TryChannel(RBox.Text, out byte r) && TryChannel(GBox.Text, out byte g) && TryChannel(BBox.Text, out byte b))
            SetCurrent(Color.FromRgb(r, g, b));
    }

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || HexBox == null) return;
        if (Parse(HexBox.Text) is { } c) SetCurrent(c);
    }

    /// <summary>打ち終わったら、今の色の書き方にそろえる（読めない値も元に戻る）。</summary>
    void Box_LostFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var c = Current;
        _updating = true;
        box.Text = box == HexBox ? Hex(c) : box == RBox ? Channel(c.R) : box == GBox ? Channel(c.G) : Channel(c.B);
        _updating = false;
    }

    /// <summary>0〜255 の数（全角でも読める）。</summary>
    static bool TryChannel(string text, out byte value)
    {
        value = 0;
        if (!int.TryParse(text.Normalize(NormalizationForm.FormKC).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            || n > 255) return false;
        value = (byte)n;
        return true;
    }

    /// <summary>"RRGGBB"（先頭の "#"・全角・小文字も可）を色にする。読めなければ null。</summary>
    internal static Color? Parse(string? text)
    {
        if (text == null) return null;
        string t = text.Normalize(NormalizationForm.FormKC).Trim().TrimStart('#');
        if (t.Length != 6 || !t.All(Uri.IsHexDigit)) return null;
        int v = int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>色を "RRGGBB"（大文字）に。</summary>
    internal static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>色合い（0〜360）・鮮やかさ（0〜1）・明るさ（0〜1）から色を作る。</summary>
    internal static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
        static byte Byte(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
    }

    /// <summary>色から、色合い（0〜360）・鮮やかさ（0〜1）・明るさ（0〜1）を読む。</summary>
    internal static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }
}
