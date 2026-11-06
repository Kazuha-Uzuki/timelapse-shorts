using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TimelapseShorts;

/// <summary>
/// 冒頭に持ってくる部分の選びかた（設定に覚える分と、⑤ のまとめに使う分）。秒数・フェードは打ったままの文字で持つ。
/// Start / End は選んだ範囲（元の動画の秒）。動画ごとなので覚えない（動画を入れたら「最後の ○ 秒」から始める）。
/// </summary>
public sealed record IntroChoice(bool Enabled, string Seconds, bool Copy, string FadeIn, string FadeOut, double Start = 0, double End = 0)
{
    public bool HasRange => End > Start;
}

/// <summary>
/// ② 冒頭。冒頭に持ってくる範囲（元の動画の秒）と冒頭の秒数は連動していて、冒頭は本編と同じ速さになる。
/// バーで範囲を変えると秒数が、秒数を変えるとバーが変わる（秒数は 0.1 秒刻み。秒数を変えたときは範囲の終わりを動かさない）。
/// 左に元の動画（再生できなければ、バーの位置の画を FFmpeg で描く）、右に設定のカード、下に「全体」と「拡大」のバー。
/// </summary>
public partial class IntroPanel : UserControl
{
    public const double MinSeconds = 1; // 冒頭の秒数の下限
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    IReadOnlyList<string> _files = [];
    IReadOnlyList<double> _durations = [];
    double _total, _target;          // 元の動画の長さ・総尺（どちらも 0 ならまだ無い）
    double _seconds = 5;             // 冒頭の秒数（0.1 秒刻み）
    double _start, _end;             // 冒頭に持ってくる範囲（元の動画の秒）。_end が _start 以下なら、まだ無い
    double _pos;                     // 今見ている位置
    double _viewStart, _viewEnd = 1; // 拡大のバーで見ている範囲
    bool _ready, _updating;          // _updating: 秒数の欄をこちらで書き換えている間（打った値として扱わない）
    bool _active, _sourceDirty = true;
    int _sourceVersion;              // 動画を入れ替えた回数（入れ替える前の画の読み込みを捨てる）
    bool? _canPlay;                  // ここで再生できるか（null はまだ分からない）
    bool _playing;
    int _playerFile = -1;            // 再生に開いているファイル
    double _pendingLocal;            // 開き終わったら移る、そのファイルの中の秒
    double? _wantFrame;              // 次に描く画の位置（再生できないとき）
    bool _rendering;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>選んだ内容が変わった（範囲・秒数・持っていきかた・フェード・オンオフ）。</summary>
    public event Action? Changed;

    /// <summary>「長さ・投稿先」のカード（MainWindow が入れる）。</summary>
    public object? LengthContent { get => LengthSlot.Content; set => LengthSlot.Content = value; }

    /// <summary>下の「戻る／次へ」（MainWindow が入れる）。</summary>
    public object? FooterContent { get => FooterSlot.Content; set => FooterSlot.Content = value; }

    public IntroPanel()
    {
        InitializeComponent();
        SetSecondsText(_seconds);

        OverallBar.PositionDragged += t =>
        {
            SeekTo(t);
            CenterView(_pos, _viewEnd - _viewStart); // 全体のバーで動かしたら、拡大のバーもそこを見る
            Refresh();
        };
        ZoomBar.PositionDragged += t =>
        {
            SeekTo(t);
            Refresh();
        };
        OverallBar.RangeEdited += (fix, moving) =>
        {
            EditRange(fix, moving);
            KeepInView();
        };
        ZoomBar.RangeEdited += EditRange;
        OverallBar.RangeMoved += start =>
        {
            MoveRange(start);
            KeepInView();
        };
        ZoomBar.RangeMoved += MoveRange;
        OverallBar.Zoomed += Zoom;
        ZoomBar.Zoomed += Zoom;
        _timer.Tick += (_, _) =>
        {
            if (_canPlay != true || !_playing || _playerFile < 0) return;
            _pos = _durations.Take(_playerFile).Sum() + Player.Position.TotalSeconds;
            if (_pos < _viewStart || _pos > _viewEnd) CenterView(_pos, _viewEnd - _viewStart);
            Refresh();
        };
        _ready = true;
        Refresh();
    }

    // ---- 秒数と範囲の連動（冒頭は本編と同じ速さ） ----

    /// <summary>冒頭と本編の速さ（倍速）。移すなら全体と同じ、コピーなら本編は元の動画を全部使う。</summary>
    public static double Speed(double seconds, double total, double target, bool copy) =>
        copy ? Math.Max(1, total / Math.Max(1e-9, target - seconds)) : Math.Max(1, total / target);

    /// <summary>冒頭 seconds 秒にあたる、元の動画の長さ。</summary>
    public static double SourceLength(double seconds, double total, double target, bool copy) =>
        seconds * Speed(seconds, total, target, copy);

    /// <summary>元の動画の長さ length を冒頭にしたときの秒数（SourceLength の逆）。</summary>
    public static double SecondsFor(double length, double total, double target, bool copy) =>
        !copy ? length / Math.Max(1, total / target)
        : length >= target - total ? length * target / (total + length) : length;

    /// <summary>冒頭にできる秒数の上限（0.1 秒刻み）。本編が 1 秒以上残り、範囲が元の動画に収まる。</summary>
    public static double MaxSeconds(double total, double target, bool copy) =>
        Math.Floor((copy ? Math.Min(target - 1, SecondsFor(total, total, target, true)) : Math.Min(target, total) - 1) * 10 + 1e-6) / 10;

    static double Snap(double seconds) => Math.Round(seconds * 10) / 10;

    bool IsCopy => CopyRadio.IsChecked == true;
    double Max => MaxSeconds(_total, _target, IsCopy);
    bool CanCompute => _total > 0 && _target > 0 && Max >= MinSeconds;
    double Length(double seconds) => SourceLength(seconds, _total, _target, IsCopy);
    double Clamp(double seconds) => Math.Clamp(Snap(seconds), MinSeconds, Math.Max(MinSeconds, Max));

    /// <summary>範囲の終わりを end にして、冒頭 seconds 秒にあたる長さの範囲にする（元の動画からはみ出すなら、はみ出さない所へ）。</summary>
    void RangeFromEnd(double end, double seconds)
    {
        double length = Math.Min(Length(seconds), _total);
        end = Math.Clamp(end, length, _total);
        (_start, _end) = (end - length, end);
    }

    /// <summary>範囲のはじめを start にして、冒頭 seconds 秒にあたる長さの範囲にする。</summary>
    void RangeFromStart(double start, double seconds)
    {
        double length = Math.Min(Length(seconds), _total);
        start = Math.Clamp(start, 0, _total - length);
        (_start, _end) = (start, start + length);
    }

    /// <summary>範囲の端を動かした（バー）。動かした所までの長さから秒数を決め、その秒数の長さにする。</summary>
    internal void EditRange(double fix, double moving)
    {
        if (!CanCompute || IntroCheck.IsChecked != true) return;
        _seconds = Clamp(SecondsFor(Math.Abs(moving - fix), _total, _target, IsCopy));
        if (moving >= fix) RangeFromStart(fix, _seconds);
        else RangeFromEnd(fix, _seconds);
        SetSecondsText(_seconds);
        Notify();
    }

    /// <summary>範囲ごと横にずらした（バー）。長さ（秒数）は変わらない。</summary>
    internal void MoveRange(double start)
    {
        if (!CanCompute || IntroCheck.IsChecked != true) return;
        RangeFromStart(start, _seconds);
        Notify();
    }

    void SetSecondsText(double seconds)
    {
        _updating = true;
        SecondsBox.Text = seconds.ToString("0.#", Inv);
        _updating = false;
    }

    void SecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        // 読める値なら、範囲の終わりはそのままで、その秒数の長さにする（上限を超えるなどは、入力チェックが知らせる）
        if (MainWindow.TryParseNumber(SecondsBox.Text, out double n) && n >= MinSeconds && (!CanCompute || n <= Max + 1e-9))
        {
            _seconds = Snap(n);
            if (CanCompute) RangeFromEnd(_end > _start ? _end : _total, _seconds);
        }
        Notify();
    }

    /// <summary>入れ終わったら、使う秒数（0.1 秒刻み）の書き方にそろえる。</summary>
    void SecondsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (MainWindow.TryParseNumber(SecondsBox.Text, out double n) && Snap(n) == _seconds) SetSecondsText(_seconds);
    }

    /// <summary>倍速が変わったとき（移す／コピー・総尺・元の動画）。秒数はそのまま（上限を超えたら上限に）、範囲の終わりもそのままで、長さを合わせる。</summary>
    void Recompute()
    {
        if (!CanCompute) return;
        double seconds = Clamp(_seconds);
        if (seconds != _seconds) SetSecondsText(seconds); // 上限を超えたら上限に（欄も直す）
        _seconds = seconds;
        RangeFromEnd(_end > _start ? _end : _total, _seconds);
    }

    void Carry_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Recompute();
        Notify();
    }

    void IntroCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        Notify();
    }

    void Fade_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        Notify();
    }

    void LastButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCompute) return;
        RangeFromEnd(_total, _seconds);
        SeekTo(_end);
        FitView(_start, _end);
        Notify();
    }

    void FromButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCompute || IntroCheck.IsChecked != true) return;
        if (_pos <= _end - Length(MinSeconds)) EditRange(_end, _pos); // 終わりはそのままで、はじめを今の位置に
        else
        {
            RangeFromStart(_pos, _seconds); // 終わりより後ろなら、範囲ごと今の位置から
            Notify();
        }
    }

    void ToButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCompute || IntroCheck.IsChecked != true) return;
        if (_pos >= _start + Length(MinSeconds)) EditRange(_start, _pos); // はじめはそのままで、終わりを今の位置に
        else
        {
            RangeFromEnd(_pos, _seconds); // はじめより前なら、範囲ごと今の位置まで
            Notify();
        }
    }

    void Notify()
    {
        Refresh();
        Changed?.Invoke();
    }

    // ---- MainWindow から ----

    /// <summary>元の動画を入れ替えた。範囲は「最後の ○ 秒」から選び直す。</summary>
    public void SetSource(IReadOnlyList<string> files, IReadOnlyList<double> durations)
    {
        _files = files.ToList();
        _durations = durations.ToList();
        _total = _durations.Sum();
        _sourceVersion++;
        (_start, _end) = (0, 0);
        Recompute(); // 最後の ○ 秒（範囲がまだ無いので、元の動画の終わりから）
        _pos = _end > _start ? _end : 0; // 完成品（範囲の終わり）を見せる
        FitView(_start, _end);
        _sourceDirty = true;
        if (_active) LoadSource();
        else StopPlayer();
        Refresh();
    }

    /// <summary>総尺（秒。読めなければ 0）。変わったら、秒数はそのままで範囲の長さを合わせる（Changed は出さない）。</summary>
    public void SetTarget(double target)
    {
        if (target == _target) return;
        _target = target;
        Recompute();
        Refresh();
    }

    /// <summary>前回の設定に戻す（範囲は動画を入れたときに決める）。</summary>
    public void Restore(IntroChoice c)
    {
        _ready = false;
        IntroCheck.IsChecked = c.Enabled;
        (c.Copy ? CopyRadio : MoveRadio).IsChecked = true;
        FadeInBox.Text = c.FadeIn;
        FadeOutBox.Text = c.FadeOut;
        if (MainWindow.TryParseNumber(c.Seconds, out double n) && n >= MinSeconds) _seconds = Snap(n);
        _updating = true;
        SecondsBox.Text = c.Seconds;
        _updating = false;
        _ready = true;
        Recompute();
        Refresh();
    }

    public IntroChoice Choice => new(IntroCheck.IsChecked == true, SecondsBox.Text, IsCopy, FadeInBox.Text, FadeOutBox.Text, _start, _end);

    /// <summary>確かめて、変換に渡す内容にする。問題があればその説明を返す。冒頭に持ってこない・動画がまだ無いなら intro は null。</summary>
    public string? Build(out Ffmpeg.Intro? intro)
    {
        intro = null;
        if (IntroCheck.IsChecked != true) return null;
        if (!MainWindow.TryParseNumber(SecondsBox.Text, out double typed) || typed < MinSeconds)
            return "冒頭の長さは 1 秒以上にしてください";
        if (!MainWindow.TryParseNumber(FadeInBox.Text, out double fadeIn) || !MainWindow.TryParseNumber(FadeOutBox.Text, out double fadeOut)
            || fadeIn < 0 || fadeOut < 0 || fadeIn + fadeOut > Snap(typed) + 1e-9)
            return "完成品のフェードの秒数は 0 以上で、イン・アウトの合計を冒頭の長さ以内にしてください";
        if (_total <= 0 || _target <= 0) return null; // 動画・総尺がまだ（そちらは MainWindow の入力チェックが知らせる）
        double max = Max;
        if (max < MinSeconds) return "動画が短すぎて、冒頭に持ってこられません（「完成品などを冒頭に」を外してください）";
        if (typed > max + 1e-9) return $"冒頭の長さは 1〜{max.ToString("0.#", Inv)} 秒にしてください（{(IsCopy ? "コピーのとき" : "移すとき")}）";
        if (_end <= _start) return "冒頭に持ってくる範囲を選んでください";
        intro = new Ffmpeg.Intro(_start, _end, _seconds, IsCopy, fadeIn, fadeOut);
        return null;
    }

    // ---- 表示 ----

    /// <summary>範囲がよく見えるよう、拡大のバーの範囲を決める（範囲の 5 倍くらい。1 分より狭くしない）。</summary>
    void FitView(double start, double end)
    {
        double span = Math.Clamp(Math.Max(60, (end - start) * 5), Math.Min(Math.Max(_total, 1), 10), Math.Max(_total, 1));
        CenterView(end > start ? (start + end) / 2 : _pos, span);
    }

    void CenterView(double center, double span)
    {
        double total = Math.Max(_total, 1);
        span = Math.Min(span, total);
        _viewStart = Math.Clamp(center - span / 2, 0, total - span);
        _viewEnd = _viewStart + span;
    }

    /// <summary>範囲が拡大のバーの外に出たら、拡大のバーをそこへ動かす。</summary>
    void KeepInView()
    {
        if (_end <= _start || (_start >= _viewStart && _end <= _viewEnd)) return;
        CenterView((_start + _end) / 2, Math.Max(_viewEnd - _viewStart, (_end - _start) * 1.5));
        Refresh();
    }

    void Zoom(double factor, double at)
    {
        double span = _viewEnd - _viewStart, total = Math.Max(_total, 1);
        double next = Math.Clamp(span * factor, Math.Min(total, 10), total);
        double ratio = (at - _viewStart) / span; // マウスの位置の秒が動かないように
        _viewStart = Math.Clamp(at - ratio * next, 0, total - next);
        _viewEnd = _viewStart + next;
        Refresh();
    }

    /// <summary>「2分05秒」のような長さ。</summary>
    internal static string Span(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalMinutes < 1 ? $"{t.Seconds}秒"
            : t.TotalHours < 1 ? $"{t.Minutes}分{t.Seconds:00}秒"
            : $"{(int)t.TotalHours}時間{t.Minutes:00}分{t.Seconds:00}秒";
    }

    void Refresh()
    {
        if (!_ready) return;
        bool on = IntroCheck.IsChecked == true, can = CanCompute;
        SecondsBox.IsEnabled = MoveRadio.IsEnabled = CopyRadio.IsEnabled = FadeInBox.IsEnabled = FadeOutBox.IsEnabled = on;
        LastButton.IsEnabled = FromButton.IsEnabled = ToButton.IsEnabled = on && can;
        bool range = on && can && _end > _start;
        double total = Math.Max(_total, 1);
        foreach (var bar in new[] { OverallBar, ZoomBar })
        {
            (bar.SelStart, bar.SelEnd, bar.Position, bar.CanSelect) = (range ? _start : 0, range ? _end : 0, _pos, on && can);
        }
        (OverallBar.ViewStart, OverallBar.ViewEnd, OverallBar.FrameStart, OverallBar.FrameEnd) = (0, total, _viewStart, _viewEnd);
        (ZoomBar.ViewStart, ZoomBar.ViewEnd) = (_viewStart, Math.Max(_viewEnd, _viewStart + 1e-3));
        OverallBar.Redraw();
        ZoomBar.Redraw();

        TimeText.Text = $"{MainWindow.Fmt(_pos)} / {MainWindow.Fmt(_total)}";
        BadgeText.Text = $"元の動画 {MainWindow.Fmt(_pos)}";
        RangeText.Text = range
            ? $"選んだ所 {MainWindow.Fmt(_start)}〜{MainWindow.Fmt(_end)}（{Span(_end - _start)}・{Speed(_seconds, _total, _target, IsCopy):0.#} 倍速）"
            : "";

        string? error = Build(out var intro);
        ErrorText.Text = _total > 0 ? error ?? "" : "";
        if (_total > 0 && _target > 0)
        {
            var i = error == null ? intro : null;
            double all = Ffmpeg.OutputSeconds(i, _total, _target), main = all - (i?.Seconds ?? 0);
            double factor = Ffmpeg.MainFactor(i, _total, _target);
            OrderTitle.Text = $"出来上がりの並び（{MainWindow.Fmt(all)}）";
            bool show = i != null;
            OrderIntroColumn.Width = new GridLength(show ? i!.Seconds : 0, GridUnitType.Star);
            OrderIntroColumn.MinWidth = show ? 76 : 0;
            OrderIntroBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            OrderMainBorder.CornerRadius = show ? new CornerRadius(0, 4, 4, 0) : new CornerRadius(4);
            OrderMainColumn.Width = new GridLength(Math.Max(main, 0.01), GridUnitType.Star);
            OrderIntroText.Text = show ? $"冒頭 {i!.Seconds.ToString("0.#", Inv)}秒" : "";
            string speed = factor > 1 ? $"{factor:0.#}倍速" : "等速";
            OrderMainText.Text = show ? $"本編 {MainWindow.Fmt(main)}（{(i!.Copy ? "選んだ所も入る" : "選んだ所は入らない")}・{speed}）"
                                      : $"本編 {MainWindow.Fmt(main)}（{speed}）";
        }
        else
        {
            OrderTitle.Text = "出来上がりの並び";
            OrderIntroText.Text = "冒頭";
            OrderMainText.Text = "本編";
        }
        UpdatePlayButton();
    }

    /// <summary>動画の枠を 9:16 にして、使える高さいっぱいに大きくする（横は全体の 4 割まで）。</summary>
    void VideoHolder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = VideoHolder.ActualHeight;
        if (height <= 0) return;
        double width = Math.Min(height * 9 / 16, Math.Max(150, TopArea.ActualWidth * 0.4));
        if (Math.Abs(VideoColumn.Width.Value - width) > 0.5) VideoColumn.Width = new GridLength(width);
        VideoFrame.Width = width;
        VideoFrame.Height = width * 16 / 9;
    }

    // ---- 動画を見る（再生、またはバーの位置の画） ----

    /// <summary>② を出した。動画を読み込む（入れ替えていたら読み直す）。</summary>
    public void Activate()
    {
        _active = true;
        if (_sourceDirty) LoadSource();
        _timer.Start();
    }

    /// <summary>② から離れた。再生していたら止める。</summary>
    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        if (_playing)
        {
            _playing = false;
            Player.Pause();
            UpdatePlayButton();
        }
    }

    void StopPlayer()
    {
        _playing = false;
        _playerFile = -1;
        _canPlay = null;
        Player.Close();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        FrameImage.Source = null;
        FrameImage.Visibility = Visibility.Visible;
        VideoPlaceholder.Text = _files.Count == 0 ? "① で動画を入れると、ここに元の動画が出ます" : "動画を読み込んでいます…";
        VideoPlaceholder.Visibility = Visibility.Visible;
        VideoNote.Text = "";
        UpdatePlayButton();
    }

    async void LoadSource()
    {
        _sourceDirty = false;
        int version = _sourceVersion;
        StopPlayer();
        if (_files.Count == 0) return;
        RequestFrame(_pos); // 再生できるか分かるまでのあいだも、画を出しておく
        bool hdr;
        try { hdr = await Ffmpeg.IsHdrAsync(_files[0]); }
        catch { hdr = false; }
        if (version != _sourceVersion) return;
        if (!_active)
        {
            _sourceDirty = true; // 調べている間に ② から離れた。次に ② を出したときに読み直す
            return;
        }
        if (hdr)
        {
            UseFrames("HDR の動画なので、色が正しく見えるよう、バーを動かした位置の画を出します（ここでは再生しません）。");
            return;
        }
        Open(_pos);
    }

    /// <summary>つないだ動画の t 秒目を含むファイルを開いて、その位置に移る。</summary>
    void Open(double t)
    {
        var (k, local) = Locate(t);
        _pendingLocal = local;
        if (k == _playerFile && _canPlay == true)
        {
            Player.Position = TimeSpan.FromSeconds(local);
            return;
        }
        _playerFile = k;
        Player.Source = new Uri(_files[k]);
        Player.Pause(); // 開くだけ（開けたら Player_MediaOpened）
    }

    (int, double) Locate(double t)
    {
        int i = 0;
        while (i < _durations.Count - 1 && t >= _durations[i]) t -= _durations[i++];
        return (i, t);
    }

    void SeekTo(double t)
    {
        _pos = Math.Clamp(t, 0, _total);
        if (_files.Count == 0) return;
        if (_canPlay != true) RequestFrame(_pos);
        if (_canPlay != false && _active) Open(_pos);
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_canPlay == false) return;
        if (_canPlay == null)
        {
            _canPlay = true;
            Player.Visibility = Visibility.Visible;
            FrameImage.Visibility = VideoPlaceholder.Visibility = Visibility.Collapsed;
            VideoNote.Text = "▶ か動画のクリックで再生・一時停止。バーを動かすと、その位置を見られます。";
        }
        Player.Position = TimeSpan.FromSeconds(_pendingLocal);
        if (_playing) Player.Play();
        else Player.Pause();
        UpdatePlayButton();
    }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
        UseFrames("この PC では、ここで再生できない形式でした（HEVC など）。バーを動かした位置の画を出します。");

    void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (_playerFile < _files.Count - 1) // 次のファイルへ続ける
        {
            _pos = _durations.Take(_playerFile + 1).Sum();
            Open(_pos + 0.001);
        }
        else
        {
            _playing = false;
            UpdatePlayButton();
        }
    }

    /// <summary>再生をやめて、バーの位置の画を出すようにする。</summary>
    void UseFrames(string note)
    {
        _canPlay = false;
        _playing = false;
        Player.Close();
        Player.Visibility = Visibility.Collapsed;
        FrameImage.Visibility = Visibility.Visible;
        VideoNote.Text = note;
        UpdatePlayButton();
        RequestFrame(_pos);
    }

    void UpdatePlayButton()
    {
        PlayButton.IsEnabled = _canPlay == true;
        PlayButton.Content = _playing ? "❚❚" : "▶";
    }

    void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_canPlay != true) return;
        _playing = !_playing;
        if (_playing) Player.Play();
        else Player.Pause();
        UpdatePlayButton();
    }

    void VideoFrame_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => PlayButton_Click(sender, e);

    /// <summary>t 秒目の画を描いて出す。描いている途中に頼まれたら、終わってから最後に頼まれた位置だけを描く。</summary>
    async void RequestFrame(double t)
    {
        _wantFrame = t;
        if (_rendering) return;
        _rendering = true;
        try
        {
            while (_wantFrame is double at)
            {
                _wantFrame = null;
                int version = _sourceVersion;
                var files = _files;
                var durations = _durations;
                if (files.Count == 0) break;
                try
                {
                    byte[] png = await Ffmpeg.RenderSourceFrameAsync(files, durations, at, 720, CancellationToken.None);
                    if (version != _sourceVersion || _canPlay == true) continue; // そのあいだに入れ替えた・再生できるようになった
                    FrameImage.Source = MainWindow.LoadPng(png);
                    VideoPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    if (version != _sourceVersion) continue;
                    VideoPlaceholder.Text = "画を出せませんでした。\n" + ex.Message;
                    VideoPlaceholder.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>テスト用: 元の動画が出ているか（再生できる、または画が出た）。</summary>
    internal bool ShowsVideo => _canPlay == true || FrameImage.Source != null;
}
