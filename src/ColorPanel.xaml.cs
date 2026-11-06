using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TimelapseShorts;

/// <summary>
/// ④ テキストの「自由に選ぶ（RGB）」。文字の色と、フチ・帯の色（どちらも "RRGGBB"）を、見本の色・R/G/B のバーと数値・
/// #RRGGBB の入力で選ぶ。上の「文字の色」「フチ・帯の色」で、どちらを変えるかを切り替える。
/// </summary>
public partial class ColorPanel : UserControl
{
    /// <summary>見本の色（字幕でよく使う色。左から 白・黒・灰・赤・橙・黄・緑・青緑・水色・青・藍・紫・ピンク・茶）。</summary>
    internal static readonly string[] SwatchColors =
        ["FFFFFF", "000000", "808080", "FF3B30", "FF9500", "FFE600", "34C759", "00C7BE", "5AC8FA", "007AFF", "5856D6", "AF52DE",
         "FF5FA2", "8B5A2B"];

    Color _text = Colors.White, _outline = Colors.Black;
    bool _updating; // 表示をこちらで書き換えている間（打った値・動かした値として扱わない）

    /// <summary>色が変わった（見本・バー・数値・#RRGGBB で）。SetColors では出さない。</summary>
    public event Action? Changed;

    public ColorPanel()
    {
        InitializeComponent();
        var style = (Style)Resources["Swatch"];
        foreach (string hex in SwatchColors)
        {
            var color = Parse(hex)!.Value;
            var swatch = new Button { Style = style, Background = new SolidColorBrush(color), ToolTip = "#" + hex };
            AutomationProperties.SetName(swatch, "#" + hex);
            swatch.Click += (_, _) => SetCurrent(color);
            Swatches.Children.Add(swatch);
        }
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
        Show();
    }

    bool EditingOutline => OutlineTarget.IsChecked == true;
    Color Current => EditingOutline ? _outline : _text;

    /// <summary>選んでいる方（文字／フチ・帯）の色を変える。</summary>
    void SetCurrent(Color color)
    {
        if (color == Current) return;
        if (EditingOutline) _outline = color;
        else _text = color;
        Show();
        Changed?.Invoke();
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
        RSlider.Value = c.R;
        GSlider.Value = c.G;
        BSlider.Value = c.B;
        SetText(RBox, Channel(c.R));
        SetText(GBox, Channel(c.G));
        SetText(BBox, Channel(c.B));
        SetText(HexBox, Hex(c));
        // バーの後ろに、その値だけを 0〜255 に変えたときの色の変わり方を描く
        RTrack.Background = new LinearGradientBrush(Color.FromRgb(0, c.G, c.B), Color.FromRgb(255, c.G, c.B), 0);
        GTrack.Background = new LinearGradientBrush(Color.FromRgb(c.R, 0, c.B), Color.FromRgb(c.R, 255, c.B), 0);
        BTrack.Background = new LinearGradientBrush(Color.FromRgb(c.R, c.G, 0), Color.FromRgb(c.R, c.G, 255), 0);
        _updating = false;
    }

    static void SetText(TextBox box, string text)
    {
        if (!box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
    }

    static string Channel(byte value) => value.ToString(CultureInfo.InvariantCulture);

    void Target_Checked(object sender, RoutedEventArgs e)
    {
        if (BBox == null) return; // 読み込みの途中（まだ部品がそろっていない）
        Show();
    }

    void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || BBox == null) return;
        SetCurrent(Color.FromRgb((byte)Math.Round(RSlider.Value), (byte)Math.Round(GSlider.Value), (byte)Math.Round(BSlider.Value)));
    }

    void ChannelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || BBox == null) return;
        // 3 つとも 0〜255 で読めるときだけ変える（打っている途中の読めない値は、打ち終わったら元に戻す）
        if (TryChannel(RBox.Text, out byte r) && TryChannel(GBox.Text, out byte g) && TryChannel(BBox.Text, out byte b))
            SetCurrent(Color.FromRgb(r, g, b));
    }

    void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || BBox == null) return;
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
}
