using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TimelapseShorts;

/// <summary>
/// 冒頭に持ってくる部分の選びかた（画面の入力のまま。秒数などは打ったままの文字で持つ）。
/// Pick なら元の動画の Start〜End 秒（見ながら選んだ範囲）、そうでなければ「最後の Seconds 秒」。
/// Start / End は、動画を入れ替えたら選び直す（End が Start 以下なら、まだ選んでいない）。
/// </summary>
public sealed record IntroChoice(bool Pick, double Start, double End, string Seconds, bool Copy, string FadeIn, string FadeOut)
{
    public bool HasRange => End > Start;

    /// <summary>確かめて、変換に渡す内容にする。問題があればその説明を返す。動画がまだ無ければ（total が 0）intro は null。</summary>
    public string? Build(double total, double target, out Ffmpeg.Intro? intro)
    {
        intro = null;
        if (!MainWindow.TryParseNumber(Seconds, out double n) || n < 1)
            return "冒頭の長さは 1 秒以上にしてください";
        n = Math.Round(n * Ffmpeg.OutputFps) / Ffmpeg.OutputFps; // 1 コマの長さに合わせる
        if (!MainWindow.TryParseNumber(FadeIn, out double fadeIn) || !MainWindow.TryParseNumber(FadeOut, out double fadeOut)
            || fadeIn < 0 || fadeOut < 0 || fadeIn + fadeOut > n)
            return "完成品のフェードの秒数は 0 以上で、イン・アウトの合計を冒頭の長さ以内にしてください";
        if (n >= target) return "冒頭の長さは、総尺より短くしてください";
        if (total <= 0) return null;
        if (Pick && !HasRange) return "「冒頭を選ぶ…」で、冒頭に持ってくる部分を選んでください";
        if (Pick && End - Start < 0.5) return "選んだ部分が短すぎます（0.5 秒以上にしてください）";
        var i = Pick ? new Ffmpeg.Intro(Start, End, n, Copy, fadeIn, fadeOut) : Ffmpeg.Intro.Last(total, target, n, Copy, fadeIn, fadeOut);
        if (Ffmpeg.MainSourceSeconds(i, total) < 1)
            return "冒頭に持ってくる部分が長すぎて、本編が残りません（短くするか、「コピー」にしてください）";
        intro = i;
        return null;
    }

    /// <summary>メイン画面に出す 1 行のまとめ。</summary>
    public string Describe()
    {
        string how = Copy ? "コピー" : "移す";
        string what = !Pick ? $"最後の {Seconds} 秒を{how}"
            : HasRange ? $"{MainWindow.Fmt(Start)}〜{MainWindow.Fmt(End)} を {Seconds} 秒にして{how}"
            : "見ながら選ぶ（まだ選んでいません）";
        return $"{what}（フェード {FadeIn}／{FadeOut} 秒）";
    }
}

/// <summary>
/// 冒頭に持ってくる部分を選ぶ画面。左に元の動画（再生できなければ、バーの位置の画を FFmpeg で描く）、
/// 右に選びかたなどのカード、下に「全体」と「拡大」の 2 本のバー。OK なら Result に選んだ内容が入る。
/// </summary>
public partial class IntroWindow : Window
{
    readonly IReadOnlyList<string> _files;
    readonly IReadOnlyList<double> _durations;
    readonly double _total, _target;
    double _start, _end;            // 見ながら選んだ範囲（元の動画の秒）
    double _pos;                    // 今の位置
    double _viewStart, _viewEnd;    // 拡大のバーで見ている範囲
    bool? _canPlay;                 // ここで再生できるか（null はまだ分からない）
    bool _playing;
    int _playerFile = -1;           // 再生に開いているファイル
    double _pendingLocal;           // 開き終わったら移る、そのファイルの中の秒
    double? _wantFrame;             // 次に描く画の位置（再生できないとき）
    bool _rendering, _ready;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public IntroChoice Result { get; private set; }

    public IntroWindow(IReadOnlyList<string> files, IReadOnlyList<double> durations, double target, IntroChoice choice)
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea; // 小さい画面でもはみ出さないよう、作業領域に収める
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        _files = files;
        _durations = durations;
        _total = durations.Sum();
        _target = target;
        Result = choice;
        LastBox.Text = SecondsBox.Text = choice.Seconds;
        FadeInBox.Text = choice.FadeIn;
        FadeOutBox.Text = choice.FadeOut;
        (choice.Copy ? CopyRadio : MoveRadio).IsChecked = true;
        (choice.Pick ? PickRadio : LastRadio).IsChecked = true;
        // まだ選んでいなければ、「最後の N 秒」と同じ範囲から始める
        (_start, _end) = choice.HasRange && choice.End <= _total ? (choice.Start, choice.End) : LastRange() ?? (Math.Max(0, _total - 60), _total);
        _pos = _start;
        FitView(_start, _end);

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
        ZoomBar.SelectionDragged += (s, e) =>
        {
            (_start, _end) = (s, e);
            Refresh();
        };
        ZoomBar.Zoomed += (factor, at) =>
        {
            double span = _viewEnd - _viewStart, next = Math.Clamp(span * factor, Math.Min(_total, 10), _total);
            double ratio = (at - _viewStart) / span; // マウスの位置の秒が動かないように
            _viewStart = Math.Clamp(at - ratio * next, 0, Math.Max(0, _total - next));
            _viewEnd = _viewStart + next;
            Refresh();
        };
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

    /// <summary>今の画面の入力。</summary>
    IntroChoice Current() => new(PickRadio.IsChecked == true, _start, _end, SecondsBox.Text, CopyRadio.IsChecked == true,
        FadeInBox.Text, FadeOutBox.Text);

    /// <summary>「数値で：最後の N 秒」の範囲。N が読めなければ null。</summary>
    (double, double)? LastRange()
    {
        if (!MainWindow.TryParseNumber(SecondsBox.Text, out double n) || n <= 0 || n >= _target) return null;
        var i = Ffmpeg.Intro.Last(_total, _target, n, CopyRadio.IsChecked == true);
        return (i.Start, i.End);
    }

    /// <summary>バーに出す範囲（見ながら選ぶなら選んだ範囲、数値でなら最後の N 秒）。</summary>
    (double Start, double End) Shown() => PickRadio.IsChecked == true ? (_start, _end) : LastRange() ?? (_start, _end);

    /// <summary>範囲がよく見えるよう、拡大のバーの範囲を決める（範囲の 5 倍くらい。1 分より狭くしない）。</summary>
    void FitView(double start, double end) =>
        CenterView((start + end) / 2, Math.Clamp(Math.Max(60, (end - start) * 5), Math.Min(_total, 10), _total));

    void CenterView(double center, double span)
    {
        _viewStart = Math.Clamp(center - span / 2, 0, Math.Max(0, _total - span));
        _viewEnd = _viewStart + span;
    }

    void Refresh()
    {
        if (!_ready) return;
        var c = Current();
        string? error = c.Build(_total, _target, out var intro);
        var (s, e) = Shown();
        bool pick = c.Pick;

        foreach (var bar in new[] { OverallBar, ZoomBar })
        {
            (bar.SelStart, bar.SelEnd, bar.Position) = (s, e, _pos);
            bar.Redraw();
        }
        (OverallBar.ViewStart, OverallBar.ViewEnd, OverallBar.FrameStart, OverallBar.FrameEnd) = (0, _total, _viewStart, _viewEnd);
        (ZoomBar.ViewStart, ZoomBar.ViewEnd, ZoomBar.CanSelect) = (_viewStart, _viewEnd, pick);

        TimeText.Text = $"{MainWindow.Fmt(_pos)} / {MainWindow.Fmt(_total)}";
        BadgeText.Text = $"元の動画 {MainWindow.Fmt(_pos)}";
        string range = $"選んだ所: {MainWindow.Fmt(s)}〜{MainWindow.Fmt(e)}（{Span(e - s)}）";
        HintText.Text = pick ? $"拡大のバーをドラッグして範囲を選ぶ（両端をつまんで調整）。{range}"
                             : $"数値で選んでいます（「見ながら選ぶ」にすると、バーで選べます）。{range}";
        FromButton.IsEnabled = ToButton.IsEnabled = pick;

        if (intro != null)
        {
            string speed = intro.Speed >= 1 ? $"{intro.Speed:0.#} 倍速" : $"{intro.Speed:0.##} 倍の速さ・ゆっくり";
            SpeedText.Text = pick ? $"選んだ {Span(e - s)} を {intro.Seconds:0.##} 秒にして入れます（{speed}）"
                                  : $"元の動画の最後の {Span(e - s)} を {intro.Seconds:0.##} 秒にして入れます（{speed}）";
            double all = Ffmpeg.OutputSeconds(intro, _total, _target), main = all - intro.Seconds;
            OrderIntroColumn.Width = new GridLength(intro.Seconds, GridUnitType.Star);
            OrderMainColumn.Width = new GridLength(Math.Max(main, 0.01), GridUnitType.Star);
            OrderIntroText.Text = $"冒頭 {intro.Seconds:0.#}秒";
            OrderMainText.Text = $"本編 {MainWindow.Fmt(main)}（{(c.Copy ? "選んだ所も入る" : "選んだ所は入らない")}・" +
                                 $"{Ffmpeg.MainFactor(intro, _total, _target):0.#}倍速）";
        }
        else
        {
            SpeedText.Text = "";
            OrderIntroText.Text = "冒頭";
            OrderMainText.Text = "本編";
        }
        ErrorText.Text = error ?? "";
        OkButton.IsEnabled = error == null;
    }

    /// <summary>「2分05秒」のような長さ。</summary>
    static string Span(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds, 1));
        return t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.#}秒"
            : t.TotalHours < 1 ? $"{t.Minutes}分{t.Seconds:00}秒"
            : $"{(int)t.TotalHours}時間{t.Minutes:00}分{t.Seconds:00}秒";
    }

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender == PickRadio || sender == LastRadio)
        {
            var (s, end) = Shown();
            FitView(s, end); // 選びかたを変えたら、その範囲を見せる
        }
        Refresh();
    }

    // 「最後の N 秒」と「冒頭の長さ」は同じ数（片方を変えたら、もう片方も変わる）
    void LastBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SecondsBox != null && SecondsBox.Text != LastBox.Text) SecondsBox.Text = LastBox.Text;
    }

    void SecondsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LastBox != null && LastBox.Text != SecondsBox.Text) LastBox.Text = SecondsBox.Text;
        Refresh();
    }

    void Box_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    void FromButton_Click(object sender, RoutedEventArgs e)
    {
        double length = _end - _start;
        _start = _pos;
        if (_end <= _start + 0.5) _end = Math.Min(_total, _start + Math.Max(1, length));
        Refresh();
    }

    void ToButton_Click(object sender, RoutedEventArgs e)
    {
        double length = _end - _start;
        _end = _pos;
        if (_start >= _end - 0.5) _start = Math.Max(0, _end - Math.Max(1, length));
        Refresh();
    }

    void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Result = Current();
        DialogResult = true;
    }

    /// <summary>動画の枠を 9:16 にして、高さいっぱいに大きくする。</summary>
    void TopArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = TopArea.ActualHeight - VideoNote.ActualHeight - 6;
        if (height > 0) VideoColumn.Width = new GridLength(Math.Clamp(height * 9 / 16, 200, TopArea.ActualWidth * 0.45));
    }

    // ---- 動画を見る（再生、またはバーの位置の画） ----

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RequestFrame(_pos); // 再生できるか分かるまでのあいだも、画を出しておく
        bool hdr;
        try { hdr = await Ffmpeg.IsHdrAsync(_files[0]); }
        catch { hdr = false; }
        if (hdr)
        {
            UseFrames("HDR の動画なので、色が正しく見えるよう、バーを動かした位置の画を出します（ここでは再生しません）。");
            return;
        }
        _timer.Start();
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
        if (_canPlay == false) RequestFrame(_pos);
        else
        {
            if (_canPlay == null) RequestFrame(_pos);
            Open(_pos);
        }
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
        _timer.Stop();
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
                try
                {
                    byte[] png = await Ffmpeg.RenderSourceFrameAsync(_files, _durations, at, 720, CancellationToken.None);
                    if (_canPlay == true) continue; // そのあいだに再生できるようになった
                    FrameImage.Source = MainWindow.LoadPng(png);
                    VideoPlaceholder.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
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

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        Player.Close();
        base.OnClosed(e);
    }

    /// <summary>テスト用: 画が出るか、再生できると分かるまで待つあいだの状態。</summary>
    internal bool ShowsVideo => _canPlay == true || FrameImage.Source != null;
}
