using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TimelapseShorts;

public partial class MainWindow : Window
{
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".m4v", ".mts", ".m2ts", ".wmv", ".webm"];
    static readonly string[] AudioExtensions = [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma"];

    /// <summary>文字の色と縁取りの色（どちらも "RRGGBB"）。</summary>
    sealed record ColorPreset(string Name, string Text, string Outline)
    {
        public override string ToString() => Name;
    }

    static readonly ColorPreset[] ColorPresets =
    [
        new("白（黒フチ）", "FFFFFF", "000000"),
        new("黒（白フチ）", "000000", "FFFFFF"),
        new("黄（黒フチ）", "FFE600", "000000"),
        new("ピンク（白フチ）", "FF5FA2", "FFFFFF"),
    ];

    static readonly string[] Positions = ["上", "中央", "下"]; // Ffmpeg.TextPosition と同じ並び

    /// <summary>入力欄から読み取った設定。</summary>
    sealed record Settings(int Target, Ffmpeg.Intro? Intro, double FadeIn, double FadeOut, int TextSize);

    /// <summary>投稿先。MaxSeconds は各サービスの上限（2026年10月時点）。</summary>
    sealed record Platform(string Key, string Name, int MaxSeconds, string Note)
    {
        /// <summary>最初に入れておく総尺。上限と3分の短い方から、念のため1秒引く。</summary>
        public int DefaultSeconds => Math.Min(MaxSeconds, 180) - 1;
    }

    static readonly Platform[] Platforms =
    [
        new("youtube", "YouTube ショート", 180, "3分を超えると通常の動画になります"),
        new("instagram", "Instagram リール", 1200, "3分を超えるとおすすめに出にくくなります"),
        new("tiktok", "TikTok", 600, ""),
        new("x", "X", 140, "無料アカウントの上限です"),
    ];

    readonly List<string> _files = [];
    readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
    double _totalSeconds;
    string? _bgmFile;
    double _bgmSeconds;
    double _bgmMean = double.NaN, _bgmPeak = double.NaN; // 曲の平均・最大音量（dB）。測っていなければ NaN
    string? _measuringFile; // 音量を測っている最中の曲
    Platform _platform = Platforms[0];
    string? _encoder;
    string? _lastOutput; // 最後に作った動画
    CancellationTokenSource? _cts;
    bool _busy, _ready;
    List<Ffmpeg.PreviewFrame>? _previewFrames; // プレビュー欄の画（最初・真ん中・最後）。作っていなければ null
    bool _previewIntro;                         // その画を作ったとき、完成品を冒頭に移していたか
    string? _previewKey;                        // その画を作ったときの設定（変わったら「古い」と知らせる）
    string? _clipFile;                          // 動画のプレビュー（一時ファイル）。作っていなければ null
    string? _clipKey;                           // それを作ったときの設定
    string _clipCaption = "";                   // 「最初の 7 秒・音つき・12 秒で作成」
    bool _clipPaused, _clipEnded;               // 一時停止中か・最後まで見たか
    readonly AppSettings _settings = AppSettings.Load(); // 前回の設定（閉じるときに今の設定を書く）
    IntroChoice _intro = new(false, 0, 0, "5", false, "0.3", "0.3"); // 冒頭に持ってくる部分（「冒頭を選ぶ…」の画面で選ぶ）

    // 変換中にPCがスリープして処理が止まるのを防ぐ（画面が消えるのは構わない）
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    // 作成が終わったときの音（Windows の「一般の警告音」）と、PC のスリープ
    [DllImport("user32.dll")]
    static extern bool MessageBeep(uint type);
    const uint MbIconAsterisk = 0x40;

    [DllImport("powrprof.dll", SetLastError = true)]
    static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public MainWindow()
    {
        SettingsWindow.ApplyTheme(_settings.Theme); // 部品を作る前に色を決めておく
        InitializeComponent();
        // 画面の小さいノートPCでもはみ出さないよう、作業領域（タスクバーを除いた広さ）に収める
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        // 投稿先ボタン（押した状態が残り、1つだけ選べる）
        foreach (var p in Platforms)
        {
            var button = new RadioButton
            {
                GroupName = "Platform",
                Tag = p,
                Margin = new Thickness(3),
                Padding = new Thickness(4, 3, 4, 3),
                Content = new TextBlock
                {
                    Text = $"{p.Name}\n最大 {Fmt(p.MaxSeconds)}",
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            button.SetResourceReference(StyleProperty, typeof(ToggleButton)); // テーマを変えたら見た目も変わる
            button.Checked += Platform_Checked;
            PlatformPanel.Children.Add(button);
        }

        try { FontBox.ItemsSource = FontCatalog.Load(); }
        catch (Exception ex) { StatusText.Text = "フォントの一覧を読み込めませんでした: " + ex.Message; }
        FontBox.SelectedIndex = 0;
        ColorBox.ItemsSource = ColorPresets;
        ColorBox.SelectedIndex = 0;
        PositionBox.ItemsSource = Positions;
        PositionBox.SelectedIndex = 0;
        ShowBgm();

        _ready = true;
        ((RadioButton)PlatformPanel.Children[0]).IsChecked = true;
        RestoreSettings();

        // 前に落ちたときなどに残った、動画のプレビューの一時ファイルを片付ける
        // （1 時間より前のものだけ。ほかに開いているこのアプリが再生中のものは、使用中なので消えない）
        try
        {
            foreach (var f in Directory.EnumerateFiles(System.IO.Path.GetTempPath(), "timelapse-preview-*.mp4"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-1)) TryDelete(f);
        }
        catch
        {
        }
    }

    /// <summary>前回閉じたときの設定に戻す。見つからない値（消したフォントなど）は最初の値のまま。</summary>
    void RestoreSettings()
    {
        var a = _settings;
        if (PlatformPanel.Children.OfType<RadioButton>().FirstOrDefault(b => ((Platform)b.Tag).Key == a.Platform) is { } button)
        {
            button.IsChecked = true;
            if (a.Length != null) LengthBox.Text = a.Length; // 投稿先を選ぶと総尺が初期値になるので、そのあとで戻す
        }
        IntroCheck.IsChecked = a.Intro;
        // 見ながら選んだ範囲は動画ごとなので覚えない（選びかたは覚える）
        _intro = new IntroChoice(a.IntroPick, 0, 0, a.IntroSeconds, a.IntroCopy, a.IntroFadeIn, a.IntroFadeOut);
        (a.Blur ? BlurRadio : CropRadio).IsChecked = true;
        FadeInBox.Text = a.FadeIn;
        FadeOutBox.Text = a.FadeOut;
        VolumeSlider.Value = Math.Clamp(a.Volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
        if (FontBox.Items.OfType<FontCatalog.Item>().FirstOrDefault(f => f.Display == a.Font) is { } font) FontBox.SelectedItem = font;
        if (ColorPresets.FirstOrDefault(c => c.Name == a.Color) is { } color) ColorBox.SelectedItem = color;
        PositionBox.SelectedIndex = Math.Clamp(a.Position, 0, Positions.Length - 1);
        (a.TextBack ? BackBoxRadio : BackNoneRadio).IsChecked = true;
        SizeBox.Text = a.TextSize;
    }

    /// <summary>今の設定を、次の起動のために書いておく。</summary>
    void SaveSettings()
    {
        var a = _settings;
        a.Platform = _platform.Key;
        a.Length = LengthBox.Text;
        a.Intro = IntroCheck.IsChecked == true;
        a.IntroPick = _intro.Pick;
        a.IntroSeconds = _intro.Seconds;
        a.IntroCopy = _intro.Copy;
        a.IntroFadeIn = _intro.FadeIn;
        a.IntroFadeOut = _intro.FadeOut;
        a.Blur = BlurRadio.IsChecked == true;
        a.FadeIn = FadeInBox.Text;
        a.FadeOut = FadeOutBox.Text;
        a.Volume = VolumeSlider.Value;
        a.Font = (FontBox.SelectedItem as FontCatalog.Item)?.Display;
        a.Color = (ColorBox.SelectedItem as ColorPreset)?.Name;
        a.Position = PositionBox.SelectedIndex;
        a.TextBack = BackBoxRadio.IsChecked == true;
        a.TextSize = SizeBox.Text;
        a.Save();
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
        _settings.Save(); // 色を変えたらすぐ覚える（このあと落ちても次は同じ色で開く）
    }

    void IntroButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseSeconds(LengthBox.Text, out int target)) return;
        var w = new IntroWindow(_files, _files.Select(f => _durations[f]).ToList(), target, _intro) { Owner = this };
        if (w.ShowDialog() != true) return;
        _intro = w.Result;
        RefreshInfo();
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.IsAvailable)
        {
            StatusText.Text = "ffmpeg.exe / ffprobe.exe が見つかりません。このアプリと同じフォルダに置いてください。";
            AddButton.IsEnabled = false;
            return;
        }
        EncoderText.Text = "このPCで使えるエンコーダーを確認中…";
        _encoder = await Ffmpeg.DetectEncoderAsync();
        EncoderText.Text = _encoder == null
            ? "使えるH.264エンコーダーが見つかりませんでした（WindowsのN/KNエディションでは「メディア機能パック」が必要です）"
            : "エンコーダー: " + Ffmpeg.EncoderLabel(_encoder);
        UpdateState();
    }

    void Platform_Checked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Platform p) return;
        _platform = p;
        LengthHint.Text = _platform.Note == ""
            ? $"最大 {Fmt(_platform.MaxSeconds)}"
            : $"最大 {Fmt(_platform.MaxSeconds)}。{_platform.Note}";
        LengthBox.Text = Fmt(_platform.DefaultSeconds);
        RefreshInfo();
    }

    void Settings_TextChanged(object sender, TextChangedEventArgs e) => RefreshInfo();

    void Settings_Changed(object sender, RoutedEventArgs e) => RefreshInfo();

    void Settings_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshInfo();

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    static bool IsAudio(string path) => AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // 音楽ファイルは BGM に、それ以外は動画として受け取る
        if (paths.FirstOrDefault(IsAudio) is { } audio) await SetBgmAsync(audio);
        if (paths.Where(p => !IsAudio(p)).ToArray() is { Length: > 0 } videos) await AddFilesAsync(videos);
    }

    async void BgmSelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "音楽ファイル|" + string.Join(";", AudioExtensions.Select(x => "*" + x)) + "|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true) await SetBgmAsync(dlg.FileName);
    }

    void BgmClearButton_Click(object sender, RoutedEventArgs e)
    {
        _bgmFile = null;
        _bgmMean = _bgmPeak = double.NaN;
        ShowBgm();
        RefreshInfo();
    }

    async Task SetBgmAsync(string file)
    {
        try
        {
            _bgmSeconds = await Ffmpeg.GetAudioDurationAsync(file);
            _bgmFile = _measuringFile = file;
            _bgmMean = _bgmPeak = double.NaN;
            ShowBgm();
            var (mean, peak) = await Ffmpeg.GetVolumeAsync(file);
            if (_bgmFile == file) (_bgmMean, _bgmPeak) = (mean, peak); // 調べている間に曲が変わっていなければ反映
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        if (_measuringFile == file) _measuringFile = null;
        ShowBgm();
        RefreshInfo();
    }

    void ShowBgm()
    {
        BgmText.Text = _bgmFile == null
            ? "（なし）音楽ファイルをこの画面にドラッグ＆ドロップするか、「曲を選ぶ」から選んでください"
            : $"♪ {System.IO.Path.GetFileName(_bgmFile)}（{Fmt(_bgmSeconds)}）";
        ShowLevels();
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        ShowLevels();
        RefreshInfo();
    }

    static readonly Brush LevelOk = Frozen(0x3a, 0x9d, 0x5d), LevelOver = Frozen(0xd9, 0x3b, 0x3b);

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static string Db(double db) => db > 0 ? $"+{db:0.0} dB" : $"{db:0.0} dB";

    /// <summary>音量の数字と、調整後の平均・最大音量のバーを表示する。</summary>
    void ShowLevels()
    {
        double gain = VolumeSlider.Value;
        VolumeText.Text = gain == 0 ? "±0 dB" : $"{gain:+0;-0} dB";
        if (double.IsNaN(_bgmMean) || double.IsNaN(_bgmPeak))
        {
            MeanBar.Value = PeakBar.Value = MeanBar.Minimum;
            MeanText.Text = PeakText.Text = "—";
            LevelNote.Text = _bgmFile == null ? ""
                : _measuringFile == _bgmFile ? "曲の音量を調べています…"
                : "曲の音量を測れませんでした（音量の調整はできます）";
            return;
        }
        double mean = _bgmMean + gain, peak = _bgmPeak + gain;
        MeanBar.Value = Math.Clamp(mean, MeanBar.Minimum, MeanBar.Maximum);
        PeakBar.Value = Math.Clamp(peak, PeakBar.Minimum, PeakBar.Maximum);
        MeanText.Text = Db(mean);
        PeakText.Text = Db(peak);
        bool over = peak > 0;
        PeakBar.Foreground = PeakText.Foreground = over ? LevelOver : LevelOk;
        LevelNote.Text = over
            ? "最大が 0 dB を超えています。音割れしないよう、大きい部分は自動で少し抑えます。"
            : "最大が 0 dB を超えると音割れします（超えた分は自動で抑えます）。";
    }

    async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "動画ファイル|" + string.Join(";", VideoExtensions.Select(x => "*" + x)) + "|すべてのファイル|*.*",
        };
        if (dlg.ShowDialog(this) == true) await AddFilesAsync(dlg.FileNames);
    }

    void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        _totalSeconds = 0;
        SetClip(null, "");
        SetPreview(null, false);
        RefreshList();
    }

    async Task AddFilesAsync(IEnumerable<string> paths)
    {
        // フォルダが落とされたら中の動画を拾う
        var files = paths.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p).Where(f => VideoExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                : new[] { p })
            .Where(f => !_files.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0) return;

        StatusText.Text = "動画の長さを読み込み中…";
        SetBusy(true, cancellable: false);
        var skipped = new List<string>();
        foreach (var f in files)
        {
            try
            {
                _durations[f] = await Ffmpeg.GetDurationAsync(f);
                _files.Add(f);
            }
            catch
            {
                skipped.Add(System.IO.Path.GetFileName(f));
            }
        }
        _files.Sort(StringComparer.CurrentCultureIgnoreCase);
        _totalSeconds = _files.Sum(f => _durations[f]);
        StatusText.Text = skipped.Count == 0 ? "" : "動画として読み込めなかったので除外: " + string.Join(", ", skipped);
        SetBusy(false);
        RefreshList();
    }

    void RefreshList()
    {
        _intro = _intro with { Start = 0, End = 0 }; // 動画が変わったら、見ながら選んだ範囲は選び直す
        FilesList.ItemsSource = _files.Select(f => System.IO.Path.GetFileName(f)).ToList();
        RefreshInfo();
    }

    /// <summary>「2:59」や「179」を秒に直す。全角で入力されても読めるようにする。</summary>
    internal static bool TryParseSeconds(string text, out int seconds)
    {
        seconds = 0;
        var parts = text.Normalize(NormalizationForm.FormKC).Trim().Split(':');
        if (parts.Length == 1) return int.TryParse(parts[0], out seconds);
        if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s) && s is >= 0 and < 60)
        {
            seconds = m * 60 + s;
            return true;
        }
        return false;
    }

    /// <summary>「1.5」のような秒数を読む。全角で入力されても読めるようにする。</summary>
    internal static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Normalize(NormalizationForm.FormKC).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>入力欄を確かめて設定を返す。問題があればその説明を返す。</summary>
    string? Validate(out Settings settings)
    {
        settings = new Settings(0, null, 0, 0, 0);
        if (!TryParseSeconds(LengthBox.Text, out int target) || target < 1 || target > _platform.MaxSeconds)
            return $"総尺は 0:01〜{Fmt(_platform.MaxSeconds)} の間で入力してください（例: {Fmt(_platform.DefaultSeconds)}）";
        double length = _totalSeconds > 0 ? Math.Min(_totalSeconds, target) : target; // 出来上がりの長さ
        Ffmpeg.Intro? intro = null;
        if (IntroCheck.IsChecked == true && _intro.Build(_totalSeconds, target, out intro) is { } introError) return introError;
        double fadeIn = 0, fadeOut = 0;
        if (_bgmFile != null && (!TryParseNumber(FadeInBox.Text, out fadeIn) || !TryParseNumber(FadeOutBox.Text, out fadeOut)
                                 || fadeIn < 0 || fadeOut < 0 || fadeIn + fadeOut > length))
            return "フェードの秒数は 0 以上で、フェードインとフェードアウトの合計を総尺以内にしてください";
        int size = 0;
        if (TextCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(CaptionBox.Text)) return "入れるテキストを入力してください";
            if (!TryParseSeconds(SizeBox.Text, out size) || size < 20 || size > 400) return "文字の大きさは 20〜400 にしてください";
            if (FontBox.SelectedItem == null) return "フォントを選んでください";
        }
        settings = new Settings(target, intro, fadeIn, fadeOut, size);
        return null;
    }

    void RefreshInfo()
    {
        if (!_ready) return;
        UpdateSample();
        IntroSummary.Text = _intro.Describe();
        string? error = Validate(out var s);
        if (error != null)
        {
            InfoText.Text = error;
        }
        else if (_files.Count == 0)
        {
            InfoText.Text = "";
        }
        else
        {
            double factor = Ffmpeg.MainFactor(s.Intro, _totalSeconds, s.Target);
            InfoText.Text = $"合計 {Fmt(_totalSeconds)} → {Fmt(Ffmpeg.OutputSeconds(s.Intro, _totalSeconds, s.Target))}" +
                            (factor > 1 ? $"（{factor:0.#}倍速）" : "（元が短いので等速のまま縦長にします）") +
                            (_bgmFile == null ? "" : VolumeSlider.Value == 0 ? "・BGMあり" : $"・BGMあり（{VolumeText.Text}）") +
                            (TextCheck.IsChecked == true ? "・テキストあり" : "");
        }
        UpdateState();
        UpdatePreviewStale();
    }

    // 背景なしの見本で、動画の代わりにする灰色
    static readonly Brush SampleVideo = FrozenGradient(Color.FromRgb(0x5d, 0x6d, 0x7e), Color.FromRgb(0xa8, 0x9f, 0x91));

    static Brush FrozenGradient(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, 0);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// テキストの見本。背景ありは「フチの色の帯に文字」、背景なしは「灰色（動画の代わり）にフチ取りの文字」で、
    /// 出来上がりに近い見た目にする（正確な見た目はプレビューで確かめる）。
    /// </summary>
    void UpdateSample()
    {
        if (FontBox.SelectedItem is FontCatalog.Item font) SampleText.FontFamily = font.Family;
        SampleText.Text = string.IsNullOrWhiteSpace(CaptionBox.Text) ? "ここに見本が出ます" : CaptionBox.Text.Trim();
        if (ColorBox.SelectedItem is ColorPreset c)
        {
            var text = (Color)ColorConverter.ConvertFromString("#" + c.Text);
            var outline = (Color)ColorConverter.ConvertFromString("#" + c.Outline);
            SampleText.Foreground = new SolidColorBrush(text);
            bool box = BackBoxRadio.IsChecked == true;
            SampleBorder.Background = box ? new SolidColorBrush(outline) : SampleVideo;
            SampleText.Effect = box ? null : new DropShadowEffect { Color = outline, ShadowDepth = 0, BlurRadius = 5, Opacity = 1 };
        }
        SampleBorder.Opacity = TextCheck.IsChecked == true ? 1 : 0.4;
    }

    /// <summary>テキストの設定から、変換に渡す内容を作る。やめる場合は false。action は確認の文に使う（「作成」など）。</summary>
    bool TryMakeCaption(Settings s, string action, out Ffmpeg.Caption? caption)
    {
        caption = null;
        if (TextCheck.IsChecked != true || FontBox.SelectedItem is not FontCatalog.Item font) return true;
        string text = CaptionBox.Text.Trim();
        var (file, family, missing) = FontCatalog.Resolve(font, text);
        if (missing != "" && MessageBox.Show(this,
                $"フォント「{font.Display}」には次の文字がありません。四角や空白で表示されます。\n\n{missing}\n\nこのまま{action}しますか？",
                "フォントの確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        var color = ColorPresets[Math.Max(0, ColorBox.SelectedIndex)];
        caption = new Ffmpeg.Caption(text, file, family, s.TextSize, color.Text, color.Outline,
            (Ffmpeg.TextPosition)Math.Max(0, PositionBox.SelectedIndex), BackBoxRadio.IsChecked == true);
        return true;
    }

    async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_encoder == null || _files.Count == 0 || Validate(out var s) != null) return;
        Ffmpeg.Caption? caption;
        try
        {
            if (!TryMakeCaption(s, "作成", out caption)) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (!TryGetOutputDir(out string? outputDir)) return;
        Ffmpeg.Bgm? bgm = _bgmFile == null ? null : new Ffmpeg.Bgm(_bgmFile, _bgmSeconds, s.FadeIn, s.FadeOut, VolumeSlider.Value);
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        OpenFolderButton.Visibility = Visibility.Collapsed;
        var progress = new Progress<Ffmpeg.Progress>(p =>
        {
            Bar.Value = p.Fraction;
            StatusText.Text = $"{p.Stage}… {p.Fraction:P0}" +
                              (p.Remaining is { } r ? $"　残り 約{Fmt(r.TotalSeconds)}" : "");
        });
        var layout = BlurRadio.IsChecked == true ? Ffmpeg.Layout.Blur : Ffmpeg.Layout.Crop;
        var sw = Stopwatch.StartNew();
        bool done = false;
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        try
        {
            string output = await Ffmpeg.ConvertAsync(_files, _totalSeconds, s.Target, s.Intro, layout, bgm, caption,
                _platform.Key, _encoder, progress, _cts.Token, outputDir);
            _lastOutput = output;
            StatusText.Text = $"完成しました（{Fmt(sw.Elapsed.TotalSeconds)}）: {System.IO.Path.GetFileName(output)}";
            OpenFolderButton.Visibility = Visibility.Visible;
            done = true;
        }
        catch (OperationCanceledException)
        {
            Bar.Value = 0;
            StatusText.Text = "中止しました。";
        }
        catch (Exception ex)
        {
            Bar.Value = 0;
            StatusText.Text = "";
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
        if (done) AfterDone();
    }

    /// <summary>
    /// 保存先のフォルダ（null なら元の動画と同じフォルダ）。決まったフォルダが見つからなければ（外付けのドライブを
    /// 外したなど）、変換を始める前に確かめる（何時間も変換したあとで保存できないのを防ぐ）。やめるなら false。
    /// </summary>
    bool TryGetOutputDir(out string? dir)
    {
        dir = null;
        if (!_settings.UseOutputFolder || _settings.OutputFolder is not { } folder) return true;
        if (Directory.Exists(folder))
        {
            dir = folder;
            return true;
        }
        return MessageBox.Show(this,
            $"保存先のフォルダが見つかりません。\n{folder}\n\n今回は、元の動画と同じフォルダに保存しますか？\n" +
            "（「いいえ」で作成をやめます。保存先は「設定」で変えられます）",
            "保存先の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>作成が終わったあとの動き（設定画面の「作成が終わったとき」のとおり。作成中に変えても、ここで読むので効く）。</summary>
    void AfterDone()
    {
        if (_settings.DoneSound) MessageBeep(MbIconAsterisk);
        if (_settings.DoneOpenFolder) OpenOutputFolder();
        if (!_settings.DoneSleep) return;
        // PC を使っている人が気づけるよう、お知らせを見えるところに出して音も鳴らす
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!_settings.DoneSound) MessageBeep(MbIconAsterisk);
        if (new SleepWindow { Owner = this }.ShowDialog() != true) return;
        if (!SetSuspendState(false, false, false))
            StatusText.Text += "\nPC をスリープできませんでした（この PC では使えないことがあります）。";
    }

    /// <summary>できた動画を選んだ状態で、保存先のフォルダを開く。</summary>
    void OpenOutputFolder()
    {
        if (_lastOutput != null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutput}\"") { UseShellExecute = true });
    }

    /// <summary>出来上がりの「最初・真ん中・最後」の画を作って、右のプレビュー欄に出す。</summary>
    async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || Validate(out var s) != null) return;
        Ffmpeg.Caption? caption;
        try
        {
            if (!TryMakeCaption(s, "プレビュー", out caption)) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var layout = BlurRadio.IsChecked == true ? Ffmpeg.Layout.Blur : Ffmpeg.Layout.Crop;
        string status = StatusText.Text;
        StatusText.Text = "プレビューを作成中…";
        SetBusy(true);
        try
        {
            var frames = await Ffmpeg.RenderPreviewAsync(_files, _totalSeconds, s.Target, s.Intro, layout, caption,
                CancellationToken.None);
            StatusText.Text = status;
            SetPreview(frames, s.Intro != null);
        }
        catch (Exception ex)
        {
            StatusText.Text = status;
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>プレビュー欄の画を入れ替える（null で空にする）。今の設定を、その画を作ったときの設定として覚える。</summary>
    void SetPreview(List<Ffmpeg.PreviewFrame>? frames, bool introMoved)
    {
        _previewFrames = frames;
        _previewIntro = introMoved;
        _previewKey = PreviewKey();
        if (frames != null && PreviewVideoRadio.IsChecked == true) PreviewFirstRadio.IsChecked = true; // 作った画を見せる
        else ShowPreviewFrame();
        UpdatePreviewStale();
    }

    const double PreviewMinColumn = 290; // プレビュー欄の最小の幅（MainWindow.xaml の初期値）
    const double SettingsMinWidth = 890; // 左（動画・設定）に残す最小の幅（最小サイズの窓のときの幅）

    /// <summary>
    /// プレビューの枠を出来上がりと同じ 9:16 にして、欄の高さいっぱいに大きくする。
    /// 窓を縦に大きくしたら欄の幅も広げる（左の設定が最小サイズのときより狭くならない範囲で）。
    /// </summary>
    void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = PreviewArea.ActualHeight - PreviewControls.ActualHeight; // 枠に使える高さ
        if (height <= 0 || Content is not FrameworkElement root) return;
        double chrome = PreviewColumn.ActualWidth - PreviewArea.ActualWidth;      // 欄の線と余白
        double maxColumn = Math.Max(PreviewMinColumn, root.ActualWidth - 12 - SettingsMinWidth);
        double column = Math.Clamp(height * 9 / 16 + chrome, PreviewMinColumn, maxColumn);
        if (Math.Abs(PreviewColumn.ActualWidth - column) > 1) PreviewColumn.Width = new GridLength(column);
        double width = Math.Min(column - chrome, height * 9 / 16);
        PreviewFrame.Width = width;
        PreviewFrame.Height = width * 16 / 9;
    }

    void PreviewFrame_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ShowPreviewFrame();
        UpdatePreviewStale(); // 画と動画で「古い」かどうかが違う
    }

    /// <summary>
    /// 動画のプレビュー（最初の数秒・BGM 入り）を作って、プレビュー欄で再生する。作り終わるまでの時間は、
    /// 元の動画のうち読む部分の長さと PC の速さで決まる（静止画のプレビューより時間がかかる）。
    /// </summary>
    async void PreviewVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_encoder == null || _files.Count == 0 || Validate(out var s) != null) return;
        Ffmpeg.Caption? caption;
        try
        {
            if (!TryMakeCaption(s, "プレビュー", out caption)) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Ffmpeg.Bgm? bgm = _bgmFile == null ? null : new Ffmpeg.Bgm(_bgmFile, _bgmSeconds, s.FadeIn, s.FadeOut, VolumeSlider.Value);
        var layout = BlurRadio.IsChecked == true ? Ffmpeg.Layout.Blur : Ffmpeg.Layout.Crop;
        // 完成品の部分と、本編に切り替わって 2 秒まで（冒頭に移さないなら最初の 5 秒）。出来上がりより長くはしない
        double length = Ffmpeg.OutputSeconds(s.Intro, _totalSeconds, s.Target);
        double clipSeconds = Math.Min(length, s.Intro != null ? s.Intro.Seconds + 2 : 5);
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"timelapse-preview-{Guid.NewGuid():N}.mp4");
        string status = StatusText.Text;
        double bar = Bar.Value;
        _cts = new CancellationTokenSource();
        SetBusy(true, cancellable: true);
        var progress = new Progress<Ffmpeg.Progress>(p =>
        {
            Bar.Value = p.Fraction;
            StatusText.Text = $"{p.Stage}… {p.Fraction:P0}";
        });
        var sw = Stopwatch.StartNew();
        try
        {
            await Ffmpeg.RenderPreviewClipAsync(_files, _totalSeconds, s.Target, s.Intro,
                layout, bgm, caption, _encoder, clipSeconds, file, progress, _cts.Token);
            double took = sw.Elapsed.TotalSeconds;
            SetClip(file, $"最初の {clipSeconds:0.#} 秒・{(bgm != null ? "音つき" : "音なし")}・"
                          + (took < 60 ? $"{took:0} 秒" : Fmt(took)) + "で作成");
        }
        catch (OperationCanceledException)
        {
            TryDelete(file);
        }
        catch (Exception ex)
        {
            TryDelete(file);
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StatusText.Text = status;
            Bar.Value = bar;
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    /// <summary>動画のプレビューを入れ替える（null で消す）。前のファイルは消す。今の設定を、それを作ったときの設定として覚える。</summary>
    void SetClip(string? file, string caption)
    {
        CloseClip();
        if (_clipFile != null) TryDelete(_clipFile);
        _clipFile = file;
        _clipCaption = caption;
        _clipKey = ClipKey();
        if (file != null && PreviewVideoRadio.IsChecked != true) PreviewVideoRadio.IsChecked = true; // 切り替えると再生が始まる
        else ShowPreviewFrame();
        UpdatePreviewStale();
    }

    /// <summary>再生をやめて、ファイルを手放す（消せるように）。</summary>
    void CloseClip()
    {
        PreviewVideo.Stop();
        PreviewVideo.Close();
        PreviewVideo.Source = null;
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewReplay.Visibility = Visibility.Collapsed;
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }

    /// <summary>動画を出しているときは、クリックで一時停止・続き・もう一度。</summary>
    void PreviewFrame_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PreviewVideoRadio.IsChecked != true || _clipFile == null || PreviewVideo.Visibility != Visibility.Visible) return;
        if (_clipEnded || _clipPaused)
        {
            if (_clipEnded) PreviewVideo.Position = TimeSpan.Zero;
            _clipEnded = _clipPaused = false;
            PreviewVideo.Play();
            PreviewReplay.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewVideo.Pause();
            _clipPaused = true;
            PreviewReplayText.Text = "▶ クリックで続き";
            PreviewReplay.Visibility = Visibility.Visible;
        }
    }

    void PreviewVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        _clipEnded = true;
        PreviewReplayText.Text = "▶ クリックでもう一度";
        PreviewReplay.Visibility = Visibility.Visible;
    }

    /// <summary>この PC ではここで再生できなかった（動画の再生に使う Windows の機能が無いなど）。ほかのアプリで開けるようにする。</summary>
    void PreviewVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        CloseClip();
        PreviewPlaceholder.Text = "この PC では、ここで動画を再生できませんでした。";
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewOpenButton.Visibility = Visibility.Visible;
    }

    void PreviewOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clipFile != null) Process.Start(new ProcessStartInfo(_clipFile) { UseShellExecute = true });
    }

    /// <summary>「最初・真ん中・最後」の画か、動画のプレビューのうち、選ばれているものを表示する。</summary>
    void ShowPreviewFrame()
    {
        PreviewOpenButton.Visibility = Visibility.Collapsed;
        if (PreviewVideoRadio.IsChecked == true)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewCaption.Text = _clipFile == null ? "" : _clipCaption;
            PreviewFrame.Cursor = _clipFile == null ? null : System.Windows.Input.Cursors.Hand;
            if (_clipFile == null)
            {
                CloseClip();
                PreviewPlaceholder.Text = "動画を入れて、下の「動画で見る」を押すと、出来上がりの最初の数秒（完成品と、本編のはじめ）を"
                                          + "BGM つきの動画で作って、ここで再生します";
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewVideo.Visibility = Visibility.Visible;
            PreviewVideo.Source = new Uri(_clipFile);
            _clipPaused = _clipEnded = false;
            PreviewReplay.Visibility = Visibility.Collapsed;
            PreviewVideo.Play();
            return;
        }
        CloseClip();
        PreviewFrame.Cursor = null;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Text = "動画を入れて、下の「プレビュー」を押すと、ここに出来上がりの画が出ます";
        int i = PreviewMiddleRadio.IsChecked == true ? 1 : PreviewLastRadio.IsChecked == true ? 2 : 0;
        string[] names = _previewIntro ? ["最初（完成品）", "真ん中", "最後"] : ["最初", "真ん中", "最後"];
        if (_previewFrames is { Count: 3 } frames)
        {
            PreviewImage.Source = LoadPng(frames[i].Png);
            PreviewCaption.Text = $"{names[i]}・{Fmt(frames[i].Seconds)} の画";
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewImage.Source = null;
            PreviewCaption.Text = "";
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>画の見た目に関わる設定をまとめた文字列（BGM と完成品のフェードは静止画に出ないので含めない）。</summary>
    string PreviewKey() => string.Join("|", string.Join(";", _files), LengthBox.Text, IntroCheck.IsChecked,
        _intro with { FadeIn = "", FadeOut = "" },
        BlurRadio.IsChecked, TextCheck.IsChecked, CaptionBox.Text, FontBox.SelectedIndex, ColorBox.SelectedIndex,
        BackBoxRadio.IsChecked, SizeBox.Text, PositionBox.SelectedIndex);

    /// <summary>
    /// 動画のプレビューに関わる設定。画の設定に、BGM（曲・音量・フェードイン）と完成品のフェードを足したもの。
    /// BGM のフェードアウトは動画の終わりにかかるので、最初の数秒の動画には出ない（含めない）。
    /// </summary>
    string ClipKey() => string.Join("|", PreviewKey(), _bgmFile, VolumeSlider.Value, FadeInBox.Text,
        _intro.FadeIn, _intro.FadeOut);

    /// <summary>表示中のプレビュー（画か動画）を作ったあとに設定が変わっていたら、そのことを画の上に出す。</summary>
    void UpdatePreviewStale()
    {
        bool video = PreviewVideoRadio.IsChecked == true;
        bool stale = video ? _clipFile != null && ClipKey() != _clipKey : _previewFrames != null && PreviewKey() != _previewKey;
        PreviewStaleText.Text = $"設定が変わりました。「{(video ? "動画で見る" : "プレビュー")}」を押すと反映されます";
        PreviewStale.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static BitmapImage LoadPng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // 読み込みを済ませてから stream を閉じる
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OpenFolderButton_Click(object sender, RoutedEventArgs e) => OpenOutputFolder();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel(); // 変換中に閉じたら ffmpeg も止める
        SaveSettings();
        SetClip(null, ""); // 動画のプレビューの一時ファイルを消す
        base.OnClosing(e);
    }

    void SetBusy(bool busy, bool cancellable = false)
    {
        _busy = busy;
        CancelButton.IsEnabled = busy && cancellable;
        UpdateState();
    }

    void UpdateState()
    {
        bool idle = !_busy;
        AddButton.IsEnabled = idle && Ffmpeg.IsAvailable;
        ClearButton.IsEnabled = idle && _files.Count > 0;
        SettingsPanel.IsEnabled = idle; // 変換中は設定を変えられないようにする
        // 冒頭を選ぶ画面は、動画と総尺が決まってから開ける（選んだ範囲を何秒にするかは総尺で変わる）
        IntroButton.IsEnabled = IntroCheck.IsChecked == true && _files.Count > 0 && _totalSeconds > 0
                                && TryParseSeconds(LengthBox.Text, out int t) && t >= 1 && t <= _platform.MaxSeconds;
        BgmSelectButton.IsEnabled = Ffmpeg.IsAvailable;
        BgmClearButton.IsEnabled = FadeInBox.IsEnabled = FadeOutBox.IsEnabled = VolumeSlider.IsEnabled = _bgmFile != null;
        CaptionBox.IsEnabled = FontBox.IsEnabled = SizeBox.IsEnabled = ColorBox.IsEnabled = PositionBox.IsEnabled
            = BackNoneRadio.IsEnabled = BackBoxRadio.IsEnabled = TextCheck.IsChecked == true;
        bool valid = _files.Count > 0 && _totalSeconds > 0 && Validate(out _) == null;
        PreviewButton.IsEnabled = idle && valid; // プレビューは画像を作るだけなので、エンコーダーは要らない
        PreviewVideoButton.IsEnabled = idle && _encoder != null && valid; // 動画のプレビューは動画を作るので要る
        StartButton.IsEnabled = idle && _encoder != null && valid;
    }

    internal static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";
    }
}
