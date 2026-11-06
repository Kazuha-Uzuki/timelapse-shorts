using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TimelapseShorts;

/// <summary>
/// ② 冒頭の横長のバー（秒の目盛りつき）。ViewStart〜ViewEnd 秒を横いっぱいに描き、選んだ範囲・今の位置を重ねる。
/// CanSelect なら、ドラッグで範囲を選ぶ・範囲の中をつかんで横にずらす・両端をつまんで調整ができる。
/// ドラッグせずにクリックすると、今の位置が動く。CanSelect でなければ、クリック・ドラッグで今の位置だけが動く。
/// 範囲の長さは冒頭の秒数と連動していて、持ち主（IntroPanel）が決める。ここでは、どう動かしたかだけを知らせる。
/// 値は持ち主が決めて Redraw で描き直す。
/// </summary>
public sealed class RangeBar : FrameworkElement
{
    public double ViewStart, ViewEnd = 1;     // 横いっぱいに描く範囲（秒）
    public double SelStart, SelEnd;           // 選んだ範囲（SelEnd が SelStart 以下なら、範囲なし）
    public double Position;                   // 今の位置
    public double FrameStart = -1, FrameEnd = -1; // 「全体」のバーに描く、拡大のバーの範囲（無ければ -1）
    public bool CanSelect { get; set; } = true;

    /// <summary>今の位置を動かした（クリック、または選べないときのドラッグ）。</summary>
    public event Action<double>? PositionDragged;
    /// <summary>範囲の端を動かした（新しく選ぶときも）。(動かさない端, 動かしている所)。動かしている所が端を越えたら、向きが逆になる。</summary>
    public event Action<double, double>? RangeEdited;
    /// <summary>範囲ごと横にずらした。値はずらしたあとの範囲のはじめ。</summary>
    public event Action<double>? RangeMoved;
    /// <summary>マウスのホイール。(倍率, その位置の秒)。1 より小さければ拡大する。</summary>
    public event Action<double, double>? Zoomed;

    const double Track = 26;      // バーの高さ（その下に目盛りの字）
    const double Grip = 7;        // 端をつまめる幅（ピクセル）
    enum Drag { None, Scrub, Pending, PendingMove, New, Edge, Move }
    Drag _drag;
    double _anchor;   // 押した所の秒
    double _fixed;    // 端をつまんだときの、動かさない方の端
    double _moveFrom; // 範囲ごとずらすときの、ずらす前のはじめ

    public RangeBar()
    {
        Height = Track + 18;
        SnapsToDevicePixels = true;
    }

    public void Redraw() => InvalidateVisual();

    bool HasRange => SelEnd > SelStart;
    double X(double t) => (t - ViewStart) / Math.Max(1e-9, ViewEnd - ViewStart) * ActualWidth;
    double T(double x) => Math.Clamp(ViewStart + x / Math.Max(1, ActualWidth) * (ViewEnd - ViewStart), ViewStart, ViewEnd);

    Drag HitTest(double x, out double fixedEnd)
    {
        fixedEnd = 0;
        if (!CanSelect) return Drag.Scrub;
        if (!HasRange) return Drag.Pending;
        double s = X(SelStart), e = X(SelEnd);
        // 範囲が狭くて両端が近いときは、近い方をつまむ
        if (Math.Abs(x - e) <= Grip && Math.Abs(x - e) <= Math.Abs(x - s))
        {
            fixedEnd = SelStart;
            return Drag.Edge;
        }
        if (Math.Abs(x - s) <= Grip)
        {
            fixedEnd = SelEnd;
            return Drag.Edge;
        }
        return x > s && x < e ? Drag.PendingMove : Drag.Pending;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        double x = e.GetPosition(this).X;
        _drag = HitTest(x, out _fixed);
        _anchor = T(x);
        _moveFrom = SelStart;
        CaptureMouse();
        if (_drag == Drag.Scrub) PositionDragged?.Invoke(_anchor);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (!IsMouseCaptured)
        {
            Cursor = HitTest(x, out _) switch
            {
                Drag.Edge => Cursors.SizeWE,
                Drag.PendingMove => Cursors.SizeAll,
                _ => null,
            };
            return;
        }
        double t = T(x);
        bool moved = Math.Abs(x - X(_anchor)) > 4; // 少し動いたらドラッグ（それまではクリック）
        switch (_drag)
        {
            case Drag.Scrub:
                PositionDragged?.Invoke(t);
                break;
            case Drag.Pending when moved:
                _drag = Drag.New;
                RangeEdited?.Invoke(_anchor, t);
                break;
            case Drag.New:
                RangeEdited?.Invoke(_anchor, t);
                break;
            case Drag.Edge:
                RangeEdited?.Invoke(_fixed, t);
                break;
            case Drag.PendingMove when moved:
                _drag = Drag.Move;
                RangeMoved?.Invoke(_moveFrom + (t - _anchor));
                break;
            case Drag.Move:
                RangeMoved?.Invoke(_moveFrom + (t - _anchor));
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag is Drag.Pending or Drag.PendingMove) PositionDragged?.Invoke(_anchor); // ドラッグしなかった＝クリック
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Zoomed?.Invoke(e.Delta > 0 ? 0.8 : 1.25, T(e.GetPosition(this).X));
        e.Handled = true;
    }

    Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w <= 0) return;
        var text = Res("TextFillColorSecondaryBrush", Brushes.Gray);
        var stroke = Res("ControlStrokeColorDefaultBrush", Brushes.LightGray);
        var accent = Res("AccentFillColorDefaultBrush", Brushes.RoyalBlue);
        var accentColor = (accent as SolidColorBrush)?.Color ?? Colors.RoyalBlue;
        var fill = new SolidColorBrush(Color.FromArgb(0x60, accentColor.R, accentColor.G, accentColor.B));
        var track = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
        var head = Res("TextFillColorPrimaryBrush", Brushes.Black);

        dc.DrawRoundedRectangle(track, new Pen(stroke, 1), new Rect(0.5, 0.5, w - 1, Track - 1), 4, 4);

        // 拡大のバーで見ている範囲（「全体」のバーだけ）
        if (FrameEnd > FrameStart)
        {
            double fs = Math.Clamp(X(FrameStart), 0, w), fe = Math.Clamp(X(FrameEnd), 0, w);
            dc.DrawRectangle(null, new Pen(text, 1) { DashStyle = DashStyles.Dash }, new Rect(fs, 2, Math.Max(1, fe - fs), Track - 4));
        }

        // 選んだ範囲。狭くても見えるよう、最低 3 ピクセルにする
        if (HasRange)
        {
            double s = Math.Clamp(X(SelStart), 0, w), e = Math.Clamp(X(SelEnd), 0, w);
            if (e - s < 3) { double c = (s + e) / 2; s = c - 1.5; e = c + 1.5; }
            dc.DrawRectangle(fill, new Pen(accent, CanSelect ? 2 : 1), new Rect(s, 1, e - s, Track - 2));
            if (CanSelect)
                foreach (double g in new[] { X(SelStart), X(SelEnd) })
                    if (g >= 0 && g <= w) dc.DrawRoundedRectangle(accent, null, new Rect(g - 3, Track / 2 - 8, 6, 16), 2, 2);
        }

        // 目盛り。字と字のあいだが 90 ピクセル以上になる、きりのいい間隔にする
        double span = ViewEnd - ViewStart;
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];
        double step = steps.FirstOrDefault(s => s / span * w >= 90, steps[^1]);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = Math.Ceiling(ViewStart / step) * step; t <= ViewEnd + 1e-6; t += step)
        {
            double x = X(t);
            dc.DrawLine(new Pen(stroke, 1), new Point(x, Track), new Point(x, Track + 3));
            var label = new FormattedText(MainWindow.Fmt(t), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Yu Gothic UI"), 11, text, dpi);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, 0, Math.Max(0, w - label.Width)), Track + 3));
        }

        // 今の位置
        if (Position >= ViewStart && Position <= ViewEnd)
        {
            double x = X(Position);
            dc.DrawLine(new Pen(head, 2), new Point(x, 0), new Point(x, Track));
            dc.DrawEllipse(head, null, new Point(x, 3), 4.5, 4.5);
        }
    }
}
