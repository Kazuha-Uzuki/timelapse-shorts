using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    // 色の欄の最後の「自由に選ぶ（RGB）」。選ぶと、その下のパレット（ColorPanel）で文字とフチ・帯の色を選べる
    static readonly ColorPreset CustomColor = new("自由に選ぶ（RGB）", "", "");

    static readonly string[] Positions = ["上", "中央", "下", "自由（ドラッグ）"]; // Ffmpeg.TextPosition と同じ並び

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
    int _step;                                  // 今の手順（0 ① 動画 … 4 ⑤ 作成）
    bool _customColorsSet;                      // パレット（自由に選ぶ）に色が入っている（選んだ・覚えていた・セットから写した）
    bool _restoring;                            // 前回の設定を戻している最中（位置を「自由」にしても、X/Y を今の位置で上書きしない）
    bool _settingXY;                            // 字幕の X/Y の欄を、ドラッグなどでこちらから書き換えている最中

    // ---- 字幕の重ね表示とドラッグ（プレビュー欄） ----
    // プレビューの画は字幕なしで描き、字幕だけの透明な画（変換と同じ libass で描く）を重ねる。字幕の設定を変えると
    // 少し待ってから描き直す。位置だけの変更（自由な位置）は描き直さず、描いたときの位置からずらして見せる
    readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    int _overlayVersion;                        // 字幕の画を頼んだ回数（古い描画の結果を捨てる）
    string? _overlayKey;                        // 今の字幕の画を描いたときの見た目（文字・フォント・色・大きさ・帯・位置の決め方）
    Point _overlayAnchor;                       // その画を描いたときの字幕の位置（自由なら X/Y、決まった位置なら字幕の枠の真ん中）
    Rect _overlayBox = Rect.Empty;              // その画の中で字幕が描かれている枠。どれも出来上がりの 1080×1920 の中の位置
    Point? _dragStart;                          // 字幕のドラッグを始めた所（プレビュー欄の中）。ドラッグしていなければ null
    Point _dragFrom;                            // ドラッグを始めたときの字幕の位置（1080×1920 の中）
    const int OverlayWidth = 720, OverlayHeight = 1280; // 字幕の画の大きさ（出来上がりの 2/3。枠は 300 くらいなので十分）
    const double SnapDistance = 16;             // 真ん中にこれだけ（1080×1920 の中で）近づけたら、ぴったり真ん中にする
    bool _clipRendering;                        // 動画のプレビューを作っている最中（「動画で見る」が「やめる」になる）
    int _sourceVersion;                         // 動画を入れ替えた回数（① の最初の画の読み込みが古くないか）

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

        // ② の「長さ・投稿先」と「戻る／次へ」を IntroPanel の中へ移す（MainWindow.xaml の IntroParts を参照）
        IntroParts.Children.Clear();
        StepArea.Children.Remove(IntroParts);
        Intro.LengthContent = LengthCard;
        Intro.FooterContent = IntroFooter;

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
        Intro.Changed += RefreshInfo;
        FontBox.SelectedIndex = 0;
        ColorBox.ItemsSource = ColorPresets.Append(CustomColor).ToList();
        ColorBox.SelectedIndex = 0;
        CustomColors.Changed += () =>
        {
            _customColorsSet = true;
            RefreshInfo();
        };
        PositionBox.ItemsSource = Positions;
        PositionBox.SelectedIndex = 0;
        TextXBox.Text = "540";
        TextYBox.Text = "960";
        _overlayTimer.Tick += (_, _) => RenderOverlay();
        ShowBgm();

        _ready = true;
        ((RadioButton)PlatformPanel.Children[0]).IsChecked = true;
        RestoreSettings();
        RefreshList(); // 一覧・合計などの最初の表示

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
        // 冒頭の範囲は動画ごとなので覚えない（秒数・持っていきかた・フェードは覚える）
        Intro.Restore(new IntroChoice(a.Intro, a.IntroSeconds, a.IntroCopy, a.IntroFadeIn, a.IntroFadeOut));
        (a.Blur ? BlurRadio : CropRadio).IsChecked = true;
        FadeInBox.Text = a.FadeIn;
        FadeOutBox.Text = a.FadeOut;
        VolumeSlider.Value = Math.Clamp(a.Volume, VolumeSlider.Minimum, VolumeSlider.Maximum);
        if (FontBox.Items.OfType<FontCatalog.Item>().FirstOrDefault(f => f.Display == a.Font) is { } font) FontBox.SelectedItem = font;
        if (ColorPanel.Parse(a.CustomTextColor) != null && ColorPanel.Parse(a.CustomOutlineColor) != null)
        {
            CustomColors.SetColors(a.CustomTextColor, a.CustomOutlineColor);
            _customColorsSet = true;
        }
        if (a.Color == CustomColor.Name) ColorBox.SelectedItem = CustomColor;
        else if (ColorPresets.FirstOrDefault(c => c.Name == a.Color) is { } color) ColorBox.SelectedItem = color;
        TextXBox.Text = a.TextX;
        TextYBox.Text = a.TextY;
        _restoring = true;
        PositionBox.SelectedIndex = Math.Clamp(a.Position, 0, Positions.Length - 1);
        _restoring = false;
        (a.TextBack ? BackBoxRadio : BackNoneRadio).IsChecked = true;
        SizeBox.Text = a.TextSize;
    }

    /// <summary>今の設定を、次の起動のために書いておく。</summary>
    void SaveSettings()
    {
        var a = _settings;
        a.Platform = _platform.Key;
        a.Length = LengthBox.Text;
        var intro = Intro.Choice;
        a.Intro = intro.Enabled;
        a.IntroSeconds = intro.Seconds;
        a.IntroCopy = intro.Copy;
        a.IntroFadeIn = intro.FadeIn;
        a.IntroFadeOut = intro.FadeOut;
        a.Blur = BlurRadio.IsChecked == true;
        a.FadeIn = FadeInBox.Text;
        a.FadeOut = FadeOutBox.Text;
        a.Volume = VolumeSlider.Value;
        a.Font = (FontBox.SelectedItem as FontCatalog.Item)?.Display;
        a.Color = (ColorBox.SelectedItem as ColorPreset)?.Name;
        if (_customColorsSet) (a.CustomTextColor, a.CustomOutlineColor) = (CustomColors.TextColor, CustomColors.OutlineColor);
        a.Position = PositionBox.SelectedIndex;
        a.TextX = TextXBox.Text;
        a.TextY = TextYBox.Text;
        a.TextBack = BackBoxRadio.IsChecked == true;
        a.TextSize = SizeBox.Text;
        a.Save();
    }

    void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings) { Owner = this }.ShowDialog();
        _settings.Save(); // 色を変えたらすぐ覚える（このあと落ちても次は同じ色で開く）
        RefreshInfo();    // 保存先が変わったかもしれない
    }

    // ---- 手順（① 動画 ▶ ② 冒頭 ▶ ③ BGM ▶ ④ テキスト ▶ ⑤ 作成） ----

    RadioButton[] StepButtons => [StepVideo, StepIntro, StepBgm, StepText, StepMake];
    // 済んだ手順の ✓（ボタンの右の余白に重ねてある。ボタンの文字を変えると幅が最初のままで文字が切れたため、文字は変えない）
    TextBlock[] StepMarks => [StepVideoMark, StepIntroMark, StepBgmMark, StepTextMark, StepMakeMark];

    void Step_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ShowStep(int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture));
    }

    /// <summary>「次へ」「戻る」「変える」。Tag の手順へ。</summary>
    void GoStep_Click(object sender, RoutedEventArgs e) =>
        ShowStep(int.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture));

    /// <summary>手順の画面を切り替える。左の画は、① は元の動画の最初、③④⑤ は出来上がりのプレビュー（② は IntroPanel の中）。</summary>
    internal void ShowStep(int step)
    {
        _step = step;
        if (StepButtons[step].IsChecked != true) StepButtons[step].IsChecked = true; // Step_Checked からもう一度呼ばれる
        var show = (bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        SourcePicture.Visibility = show(step == 0);
        PreviewArea.Visibility = show(step >= 2);
        VideoStep.Visibility = show(step == 0);
        Intro.Visibility = show(step == 1);
        BgmStep.Visibility = show(step == 2);
        TextStep.Visibility = show(step == 3);
        MakeStep.Visibility = show(step == 4);
        if (step == 1) Intro.Activate();
        else Intro.Deactivate();
        // プレビュー欄が見えなくなるなら、動画のプレビューは止める（音だけ鳴り続けないように）
        if (step < 2 && PreviewVideo.Visibility == Visibility.Visible && !_clipPaused && !_clipEnded)
        {
            PreviewVideo.Pause();
            _clipPaused = true;
            PreviewReplayText.Text = "▶ クリックで続き";
            PreviewReplay.Visibility = Visibility.Visible;
        }
        UpdateStepMarks();
    }

    /// <summary>済んだ手順に ✓ を付ける（BGM・テキストは入れたとき）。</summary>
    void UpdateStepMarks()
    {
        bool hasVideo = _files.Count > 0 && _totalSeconds > 0;
        bool[] done =
        [
            hasVideo,
            hasVideo && TryParseSeconds(LengthBox.Text, out int t) && t >= 1 && t <= _platform.MaxSeconds && Intro.Build(out _) == null,
            _bgmFile != null,
            TextCheck.IsChecked == true && !string.IsNullOrWhiteSpace(CaptionBox.Text),
            false,
        ];
        for (int i = 0; i < StepButtons.Length; i++)
        {
            bool mark = done[i] && i != _step;
            StepMarks[i].Visibility = mark ? Visibility.Visible : Visibility.Hidden;
            // 済んだ手順は緑の字（テーマを変えたら、その色に変わる）
            if (mark) StepButtons[i].SetResourceReference(ForegroundProperty, "SystemFillColorSuccessBrush");
            else StepButtons[i].ClearValue(ForegroundProperty);
        }
    }

    /// <summary>テスト用: 手順のボタンの文字（✓ が出ていれば「② 冒頭 ✓」）。</summary>
    internal string StepLabel(int step) => (string)StepButtons[step].Content + (StepMarks[step].Visibility == Visibility.Visible ? " ✓" : "");

    /// <summary>
    /// 左の画（① の最初の画、③④⑤ のプレビュー）の枠を出来上がりと同じ 9:16 にして、使える高さいっぱいに大きくする
    /// （横は全体の 4 割まで）。
    /// </summary>
    void PictureHolder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var holder = (FrameworkElement)sender;
        if (!holder.IsVisible || holder.ActualHeight <= 0) return;
        var frame = holder == PreviewHolder ? PreviewFrame : SourceFrame;
        double width = Math.Min(holder.ActualHeight * 9 / 16, Math.Max(160, StepArea.ActualWidth * 0.4));
        if (Math.Abs(PictureColumn.Width.Value - width) > 0.5) PictureColumn.Width = new GridLength(width);
        frame.Width = width;
        frame.Height = width * 16 / 9;
        if (frame == PreviewFrame) UpdateOverlayPlacement(); // 字幕のずらしかたは、枠の大きさで変わる
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!Ffmpeg.IsAvailable)
        {
            StatusText.Text = FilesStatusText.Text = "ffmpeg.exe / ffprobe.exe が見つかりません。このアプリと同じフォルダに置いてください。";
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

    /// <summary>
    /// 色のセットを変えた。「自由に選ぶ（RGB）」ならパレットを出す（パレットにまだ色が無ければ、それまでのセットの色から始める）。
    /// </summary>
    void ColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool custom = ReferenceEquals(ColorBox.SelectedItem, CustomColor);
        if (custom && !_customColorsSet && e.RemovedItems.OfType<ColorPreset>().FirstOrDefault() is { } from && from != CustomColor)
        {
            CustomColors.SetColors(from.Text, from.Outline);
            _customColorsSet = true;
        }
        CustomColorBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        RefreshInfo();
    }

    /// <summary>文字の色とフチ（背景ありなら帯）の色（"RRGGBB"）。「自由に選ぶ（RGB）」ならパレットの色。</summary>
    (string Text, string Outline) CaptionColors()
    {
        if (ReferenceEquals(ColorBox.SelectedItem, CustomColor)) return (CustomColors.TextColor, CustomColors.OutlineColor);
        var preset = ColorBox.SelectedItem as ColorPreset ?? ColorPresets[0];
        return (preset.Text, preset.Outline);
    }

    // ---- 字幕の位置（上・中央・下・自由）と、プレビュー欄での重ね表示・ドラッグ ----

    bool CustomPosition => PositionBox.SelectedIndex == (int)Ffmpeg.TextPosition.Custom;

    /// <summary>自由な位置（X/Y の欄。字幕の真ん中の位置）。0〜1080・0〜1920 の数で読めなければ false。全角でも読める。</summary>
    bool TryGetTextXY(out double x, out double y)
    {
        y = 0;
        return TryParseNumber(TextXBox.Text, out x) && TryParseNumber(TextYBox.Text, out y)
               && x is >= 0 and <= 1080 && y is >= 0 and <= 1920;
    }

    /// <summary>⑤ のまとめに出す位置（「上」や「自由（X 540・Y 1500）」）。</summary>
    string PositionLabel() => CustomPosition ? $"自由（X {TextXBox.Text.Trim()}・Y {TextYBox.Text.Trim()}）" : PositionBox.SelectedItem?.ToString() ?? "";

    /// <summary>
    /// 位置を変えた。「自由」なら X/Y の欄を出す。決まった位置から「自由」にしたときは、X/Y を今の字幕の位置から始める
    /// （字幕の画があればその真ん中、無ければ決まった位置のおおよその所）。
    /// </summary>
    void PositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PositionXY.Visibility = CustomPosition ? Visibility.Visible : Visibility.Collapsed;
        if (_ready && !_restoring && CustomPosition && e.RemovedItems.Count > 0 && _dragStart == null)
        {
            int from = Array.IndexOf(Positions, e.RemovedItems[0] as string);
            if (from is >= 0 and < 3) SetTextXY(PresetCaptionCenter(from), refresh: false);
        }
        RefreshInfo();
    }

    /// <summary>決まった位置（0 上・1 中央・2 下）の字幕の真ん中。描いた字幕の画があればそれから、無ければおおよそ。</summary>
    Point PresetCaptionCenter(int preset)
    {
        if (!_overlayBox.IsEmpty && _overlayKey != null && !_overlayKey.EndsWith("|Custom")) return _overlayAnchor;
        double half = TryParseSeconds(SizeBox.Text, out int size) ? size * 0.6 : 48;
        return new Point(540, preset switch { 0 => 280 + half, 1 => 960, _ => 1320 - half }); // Ffmpeg.BuildAss の余白から
    }

    void PositionXY_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _settingXY) return;
        RefreshInfo();
    }

    /// <summary>X/Y の欄に位置を書く（ドラッグなど。打った値と同じように扱う）。</summary>
    void SetTextXY(Point p, bool refresh = true)
    {
        _settingXY = true;
        TextXBox.Text = Math.Round(p.X).ToString(CultureInfo.InvariantCulture);
        TextYBox.Text = Math.Round(p.Y).ToString(CultureInfo.InvariantCulture);
        _settingXY = false;
        if (refresh) RefreshInfo();
    }

    /// <summary>重ねて出す字幕（今の設定）。入れない・まだ読めない設定なら null（フォントに無い文字があっても聞かない）。</summary>
    Ffmpeg.Caption? OverlayCaption()
    {
        if (TextCheck.IsChecked != true || string.IsNullOrWhiteSpace(CaptionBox.Text) || FontBox.SelectedItem is not FontCatalog.Item font
            || !TryParseSeconds(SizeBox.Text, out int size) || size < 20 || size > 400) return null;
        double x = 540, y = 960;
        if (CustomPosition && !TryGetTextXY(out x, out y)) return null;
        try
        {
            string text = CaptionBox.Text.Trim();
            var (file, family, _) = FontCatalog.Resolve(font, text);
            var colors = CaptionColors();
            return new Ffmpeg.Caption(text, file, family, size, colors.Text, colors.Outline,
                (Ffmpeg.TextPosition)Math.Max(0, PositionBox.SelectedIndex), BackBoxRadio.IsChecked == true, x, y);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>字幕の画の見た目（自由な位置の X/Y は含めない。位置だけの変更は、描き直さずにずらすため）。</summary>
    static string OverlayKey(Ffmpeg.Caption c) =>
        string.Join("|", c.Text, c.FontFile, c.FontFamily, c.Size, c.TextColor, c.OutlineColor, c.Box, c.Position);

    /// <summary>字幕の設定が変わったかもしれない。位置はすぐ合わせ、画は少し待ってから（打っている途中に何度も描かないよう）描き直す。</summary>
    void ScheduleOverlay()
    {
        if (!_ready) return;
        UpdateOverlayPlacement();
        _overlayTimer.Stop();
        _overlayTimer.Start();
    }

    /// <summary>字幕だけの画を描く（見た目が変わっていなければ描かない）。ドラッグ中は、終わってから描く。</summary>
    async void RenderOverlay()
    {
        _overlayTimer.Stop();
        if (_dragStart != null) return;
        var caption = OverlayCaption();
        int version = ++_overlayVersion;
        if (caption == null || !Ffmpeg.IsAvailable)
        {
            ClearOverlay();
            return;
        }
        string key = OverlayKey(caption);
        if (key == _overlayKey && CaptionOverlay.Source != null)
        {
            UpdateOverlayPlacement();
            return;
        }
        try
        {
            byte[] png = await Ffmpeg.RenderCaptionAsync(caption, OverlayWidth, OverlayHeight, CancellationToken.None);
            if (version != _overlayVersion || _dragStart != null) return;
            var image = LoadPng(png);
            var box = AlphaBounds(image);
            double k = 1080.0 / OverlayWidth;
            _overlayBox = box.IsEmpty ? Rect.Empty : new Rect(box.X * k, box.Y * k, box.Width * k, box.Height * k);
            // 決まった位置（上・中央・下）は左右の真ん中ぞろえなので、横は 540 ちょうど（字の形で枠の真ん中は少しずれる）。
            // 縦は字幕の枠の真ん中
            _overlayAnchor = caption.Position == Ffmpeg.TextPosition.Custom ? new Point(caption.X, caption.Y)
                : _overlayBox.IsEmpty ? new Point(540, 960)
                : new Point(540, Math.Round(_overlayBox.Y + _overlayBox.Height / 2));
            _overlayKey = key;
            CaptionOverlay.Source = image;
            UpdateOverlayPlacement();
        }
        catch
        {
            if (version == _overlayVersion) ClearOverlay();
        }
    }

    void ClearOverlay()
    {
        CaptionOverlay.Source = null;
        _overlayKey = null;
        _overlayBox = Rect.Empty;
        UpdateOverlayPlacement();
    }

    /// <summary>
    /// 字幕の画を出すか決めて、今の位置に置く（自由な位置なら、描いたときの位置からずらす）。
    /// 静止画のプレビュー（まだ作っていなければ暗い枠の上）に重ねる。動画のプレビューは字幕も焼き込んであるので重ねない。
    /// </summary>
    void UpdateOverlayPlacement()
    {
        bool show = CaptionOverlay.Source != null && TextCheck.IsChecked == true && PreviewImage.Visibility == Visibility.Visible;
        CaptionOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        var pos = CurrentCaptionPosition();
        double k = PreviewScale();
        CaptionShift.X = (pos.X - _overlayAnchor.X) * k;
        CaptionShift.Y = (pos.Y - _overlayAnchor.Y) * k;
        if (!show) HideDragGuides();
    }

    /// <summary>字幕の今の位置（1080×1920 の中）。自由なら X/Y の欄、決まった位置なら描いた画の字幕の真ん中。</summary>
    Point CurrentCaptionPosition() =>
        CustomPosition && TryGetTextXY(out double x, out double y) ? new Point(x, y) : _overlayAnchor;

    /// <summary>出来上がりの 1 ピクセルが、プレビュー欄では何ピクセルか。</summary>
    double PreviewScale() => (PreviewFrame.ActualWidth > 0 ? PreviewFrame.ActualWidth : PreviewFrame.Width) / 1080;

    /// <summary>字幕の枠（プレビュー欄の中の位置）。字幕の画が無ければ Empty。</summary>
    internal Rect CaptionScreenBox()
    {
        if (_overlayBox.IsEmpty) return Rect.Empty;
        var pos = CurrentCaptionPosition();
        var box = _overlayBox;
        box.Offset(pos.X - _overlayAnchor.X, pos.Y - _overlayAnchor.Y);
        double k = PreviewScale();
        return new Rect(box.X * k, box.Y * k, box.Width * k, box.Height * k);
    }

    /// <summary>透明な画の中で、何か描かれている所（少しでも不透明な所）を囲む枠（画の画素で）。何も無ければ Empty。</summary>
    internal static Rect AlphaBounds(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x * 4 + 3] <= 24) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>プレビュー欄の中の点 p が、ドラッグできる字幕の上か（④ で、字幕を重ねているときだけ）。</summary>
    bool OverCaption(Point p)
    {
        if (_step != 3 || CaptionOverlay.Visibility != Visibility.Visible || _busy) return false;
        var box = CaptionScreenBox();
        if (box.IsEmpty) return false;
        box.Inflate(8, 8); // 細い字でもつかみやすいよう、少し広めに
        return box.Contains(p);
    }

    /// <summary>字幕のドラッグを始める（p はプレビュー欄の中の位置）。字幕の上でなければ false。決まった位置からなら「自由」にする。</summary>
    internal bool BeginCaptionDrag(Point p)
    {
        if (!OverCaption(p)) return false;
        _dragFrom = CurrentCaptionPosition();
        _dragStart = p;
        if (!CustomPosition)
        {
            SetTextXY(_dragFrom, refresh: false);
            PositionBox.SelectedIndex = (int)Ffmpeg.TextPosition.Custom;
        }
        ShowDragGuides(false, false);
        return true;
    }

    /// <summary>字幕をドラッグして p まで動かした。真ん中の近くならぴったり真ん中にし、画面からはみ出さないようにする。</summary>
    internal void MoveCaptionDrag(Point p)
    {
        if (_dragStart is not { } start) return;
        double k = PreviewScale();
        var (pos, snapX, snapY) = SnapAndClamp(new Point(_dragFrom.X + (p.X - start.X) / k, _dragFrom.Y + (p.Y - start.Y) / k));
        SetTextXY(pos);
        ShowDragGuides(snapX, snapY);
    }

    /// <summary>字幕のドラッグを終える（その位置で字幕を描き直す）。</summary>
    internal void EndCaptionDrag()
    {
        if (_dragStart == null) return;
        _dragStart = null;
        HideDragGuides();
        ScheduleOverlay();
    }

    /// <summary>
    /// 字幕の位置 p（1080×1920 の中）を、真ん中の近くならぴったり真ん中にして（SnapX / SnapY）、字幕の枠が
    /// 画面からはみ出さない所に収める（字幕が画面より大きければ真ん中）。
    /// </summary>
    internal (Point Pos, bool SnapX, bool SnapY) SnapAndClamp(Point p)
    {
        double x = Math.Abs(p.X - 540) <= SnapDistance ? 540 : p.X;
        double y = Math.Abs(p.Y - 960) <= SnapDistance ? 960 : p.Y;
        if (!_overlayBox.IsEmpty)
        {
            // 字幕の枠は、位置からこれだけずれた所にある（描いた画から）
            x = Inside(x, _overlayAnchor.X - _overlayBox.Left, 1080 - (_overlayBox.Right - _overlayAnchor.X));
            y = Inside(y, _overlayAnchor.Y - _overlayBox.Top, 1920 - (_overlayBox.Bottom - _overlayAnchor.Y));
        }
        x = Math.Round(x);
        y = Math.Round(y);
        return (new Point(x, y), x == 540, y == 960);

        static double Inside(double v, double min, double max) => min <= max ? Math.Clamp(v, min, max) : (min + max) / 2;
    }

    /// <summary>ドラッグ中の目安を出す（字幕の枠と位置。真ん中にぴったり合ったら、その線）。</summary>
    void ShowDragGuides(bool snapX, bool snapY)
    {
        double width = PreviewFrame.ActualWidth, height = PreviewFrame.ActualHeight;
        var box = CaptionScreenBox();
        CaptionGuides.Visibility = Visibility.Visible;
        (GuideV.X1, GuideV.X2, GuideV.Y1, GuideV.Y2) = (width / 2, width / 2, 0, height);
        (GuideH.X1, GuideH.X2, GuideH.Y1, GuideH.Y2) = (0, width, height / 2, height / 2);
        GuideV.Visibility = snapX ? Visibility.Visible : Visibility.Collapsed;
        GuideH.Visibility = snapY ? Visibility.Visible : Visibility.Collapsed;
        if (!box.IsEmpty)
        {
            Canvas.SetLeft(CaptionFrame, box.X - 3);
            Canvas.SetTop(CaptionFrame, box.Y - 3);
            CaptionFrame.Width = box.Width + 6;
            CaptionFrame.Height = box.Height + 6;
        }
        var pos = CurrentCaptionPosition();
        CaptionPosText.Text = $"X {pos.X:0}・Y {pos.Y:0}";
        CaptionPosLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var label = CaptionPosLabel.DesiredSize;
        // 字幕の枠の上に出す（上に場所が無ければ下）
        Canvas.SetLeft(CaptionPosLabel, Math.Clamp(box.IsEmpty ? 4 : box.X, 0, Math.Max(0, width - label.Width)));
        Canvas.SetTop(CaptionPosLabel, box.IsEmpty ? 4 : box.Y - label.Height - 6 >= 0 ? box.Y - label.Height - 6 : box.Bottom + 6);
    }

    void HideDragGuides() => CaptionGuides.Visibility = Visibility.Collapsed;

    void PreviewFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!BeginCaptionDrag(e.GetPosition(PreviewFrame))) return;
        PreviewFrame.CaptureMouse();
        e.Handled = true;
    }

    void PreviewFrame_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(PreviewFrame);
        if (_dragStart != null)
        {
            MoveCaptionDrag(p);
            return;
        }
        // ④ で字幕の上に来たら、動かせることがわかる形のカーソルにする（動画のときは、クリックで再生・一時停止の手の形）
        if (PreviewVideoRadio.IsChecked != true) PreviewFrame.Cursor = OverCaption(p) ? Cursors.SizeAll : null;
    }

    void PreviewFrame_LostMouseCapture(object sender, MouseEventArgs e) => EndCaptionDrag();

    /// <summary>⑤ のまとめに出す色（セットの名前か、自由に選んだ 2 色）。</summary>
    string ColorLabel()
    {
        if (!ReferenceEquals(ColorBox.SelectedItem, CustomColor)) return ColorBox.SelectedItem?.ToString() ?? "";
        var (text, outline) = CaptionColors();
        return $"文字 #{text}／{(BackBoxRadio.IsChecked == true ? "帯" : "フチ")} #{outline}";
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    static bool IsAudio(string path) => AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        // 音楽ファイルは BGM に、それ以外は動画として受け取る。入れたものの手順を見せる
        if (paths.FirstOrDefault(IsAudio) is { } audio)
        {
            ShowStep(2);
            await SetBgmAsync(audio);
        }
        if (paths.Where(p => !IsAudio(p)).ToArray() is { Length: > 0 } videos)
        {
            ShowStep(0);
            await AddFilesAsync(videos);
        }
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
        BgmStatusText.Text = "";
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
            BgmStatusText.Text = ex.Message;
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

        FilesStatusText.Text = "動画の長さを読み込み中…";
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
        FilesStatusText.Text = skipped.Count == 0 ? "" : "動画として読み込めなかったので除外: " + string.Join(", ", skipped);
        SetBusy(false);
        RefreshList();
    }

    /// <summary>① の一覧の 1 行。</summary>
    internal sealed record FileRow(string Name, string Length);

    /// <summary>動画を入れ替えた。一覧・合計・② の範囲（最後の ○ 秒から選び直す）・① の最初の画を新しくする。</summary>
    void RefreshList()
    {
        var durations = _files.Select(f => _durations.TryGetValue(f, out double d) ? d : 0).ToList();
        _totalSeconds = durations.Sum();
        FilesList.ItemsSource = _files.Select((f, i) => new FileRow(System.IO.Path.GetFileName(f), Fmt(durations[i]))).ToList();
        FilesTotalText.Text = _files.Count == 0 ? "（まだ入れていません）"
            : $"{_files.Count}本・合計 {Fmt(_totalSeconds)}" + (_files.Count > 1 ? "（名前順につなげて1本にします）" : "");
        Intro.SetSource(_files, durations);
        ShowSourceFrame(durations);
        RefreshInfo();
    }

    /// <summary>① の左に、入れた動画の最初の画を出す（作れなくても、ほかには困らないので何も言わない）。</summary>
    async void ShowSourceFrame(List<double> durations)
    {
        int version = ++_sourceVersion;
        SourceImage.Source = null;
        SourcePlaceholder.Text = _files.Count == 0 ? "動画を入れると、ここに最初の画が出ます" : "最初の画を作っています…";
        SourcePlaceholder.Visibility = Visibility.Visible;
        if (_files.Count == 0 || !Ffmpeg.IsAvailable) return;
        try
        {
            byte[] png = await Ffmpeg.RenderSourceFrameAsync(_files.ToList(), durations, 0, 720, CancellationToken.None);
            if (version != _sourceVersion) return;
            SourceImage.Source = LoadPng(png);
            SourcePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (version == _sourceVersion) SourcePlaceholder.Text = "最初の画を作れませんでした";
        }
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
        if (Intro.Build(out var intro) is { } introError) return introError;
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
            if (CustomPosition && !TryGetTextXY(out _, out _))
                return "字幕の位置は、X を 0〜1080・Y を 0〜1920 の数で入れてください";
        }
        settings = new Settings(target, intro, fadeIn, fadeOut, size);
        return null;
    }

    void RefreshInfo()
    {
        if (!_ready) return;
        Intro.SetTarget(TryParseSeconds(LengthBox.Text, out int target) && target >= 1 && target <= _platform.MaxSeconds ? target : 0);
        UpdateSample();
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
        if (_files.Count == 0) InfoText.Text = "① で動画を入れてください";
        UpdateSummary(error == null ? s : null);
        UpdateStepMarks();
        UpdateState();
        UpdatePreviewStale();
        ScheduleOverlay();
    }

    /// <summary>⑤ の「設定のまとめ」と「保存先」。s は入力チェックを通ったときの設定（通らなければ null）。</summary>
    void UpdateSummary(Settings? s)
    {
        SumVideoText.Text = (_files.Count == 0 ? "まだ入れていません" : $"{_files.Count}本・合計 {Fmt(_totalSeconds)}")
                            + (BlurRadio.IsChecked == true ? "・横向きは全体を残す（ぼかした背景）" : "・横向きは中央を切り抜く");
        var intro = Intro.Choice;
        // 総尺は「179」と打っても「2:59」の書き方で出す（読めないときは打ったまま）
        string length = $"{_platform.Name} {(s != null ? Fmt(s.Target) : LengthBox.Text)}"
                        + (s != null && _totalSeconds > 0 ? $"（{Ffmpeg.MainFactor(s.Intro, _totalSeconds, s.Target):0.#}倍速）" : "");
        SumIntroText.Text = length + (!intro.Enabled ? "・冒頭なし"
            : s?.Intro is { } i ? $"・{Fmt(i.Start)}〜{Fmt(i.End)} を冒頭 {i.Seconds:0.#} 秒に・{(i.Copy ? "コピー" : "移す")}"
                                  + $"・フェード {intro.FadeIn}／{intro.FadeOut} 秒"
            : $"・冒頭 {intro.Seconds} 秒（{(intro.Copy ? "コピー" : "移す")}）");
        SumBgmText.Text = _bgmFile == null ? "なし"
            : $"♪ {System.IO.Path.GetFileName(_bgmFile)}（{Fmt(_bgmSeconds)}）・{VolumeText.Text}・フェード {FadeInBox.Text}／{FadeOutBox.Text} 秒";
        string firstLine = CaptionBox.Text.Trim().Split('\n')[0].Trim();
        SumTextText.Text = TextCheck.IsChecked != true ? "なし"
            : $"{(firstLine == "" ? "（まだ入れていません）" : firstLine)}・{FontBox.SelectedItem}・{ColorLabel()}・{SizeBox.Text}"
              + $"・{PositionLabel()}" + (BackBoxRadio.IsChecked == true ? "・帯あり" : "");
        string name = _files.Count == 0 ? "（元の動画の名前）" : System.IO.Path.GetFileNameWithoutExtension(_files[0]);
        string file = $"「{name}_{_platform.Key}.mp4」";
        SaveText.Text = _settings.UseOutputFolder && _settings.OutputFolder is { } folder
            ? $"決まったフォルダ（{folder}）に{file}を作ります（「設定」で変えられます）"
            : $"元の動画と同じフォルダに{file}を作ります（「設定」で、決まったフォルダにもできます）";
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
        var colors = CaptionColors();
        var text = (Color)ColorConverter.ConvertFromString("#" + colors.Text);
        var outline = (Color)ColorConverter.ConvertFromString("#" + colors.Outline);
        SampleText.Foreground = new SolidColorBrush(text);
        bool box = BackBoxRadio.IsChecked == true;
        SampleBorder.Background = box ? new SolidColorBrush(outline) : SampleVideo;
        SampleText.Effect = box ? null : new DropShadowEffect { Color = outline, ShadowDepth = 0, BlurRadius = 5, Opacity = 1 };
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
        var color = CaptionColors();
        double x = 540, y = 960;
        if (CustomPosition) TryGetTextXY(out x, out y); // 読めることは Validate で確かめてある
        caption = new Ffmpeg.Caption(text, file, family, s.TextSize, color.Text, color.Outline,
            (Ffmpeg.TextPosition)Math.Max(0, PositionBox.SelectedIndex), BackBoxRadio.IsChecked == true, x, y);
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
        var layout = BlurRadio.IsChecked == true ? Ffmpeg.Layout.Blur : Ffmpeg.Layout.Crop;
        PreviewCaption.Text = "プレビューを作成中…";
        SetBusy(true);
        try
        {
            // 字幕は描かない（字幕だけの画を重ねて出すので、字幕を変えても描き直さなくてよい）
            var frames = await Ffmpeg.RenderPreviewAsync(_files, _totalSeconds, s.Target, s.Intro, layout, null,
                CancellationToken.None);
            SetPreview(frames, s.Intro != null);
        }
        catch (Exception ex)
        {
            ShowPreviewFrame();
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
        if (_clipRendering)
        {
            _cts?.Cancel(); // 作っている最中は「やめる」
            return;
        }
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
        _cts = new CancellationTokenSource();
        _clipRendering = true;
        PreviewVideoButton.Content = "やめる";
        PreviewBar.Value = 0;
        PreviewBar.Visibility = Visibility.Visible;
        PreviewCaption.Text = "動画のプレビューを作成中…";
        SetBusy(true, cancellable: true);
        var progress = new Progress<Ffmpeg.Progress>(p =>
        {
            if (!_clipRendering) return;
            PreviewBar.Value = p.Fraction;
            PreviewCaption.Text = $"{p.Stage}… {p.Fraction:P0}";
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
            ShowPreviewFrame();
        }
        catch (Exception ex)
        {
            TryDelete(file);
            ShowPreviewFrame();
            MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _clipRendering = false;
            PreviewVideoButton.Content = "動画で見る";
            PreviewBar.Visibility = Visibility.Collapsed;
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
    void PreviewFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart != null)
        {
            EndCaptionDrag();
            PreviewFrame.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
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
                UpdateOverlayPlacement();
                return;
            }
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewVideo.Visibility = Visibility.Visible;
            PreviewVideo.Source = new Uri(_clipFile);
            _clipPaused = _clipEnded = false;
            PreviewReplay.Visibility = Visibility.Collapsed;
            PreviewVideo.Play();
            UpdateOverlayPlacement();
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
        UpdateOverlayPlacement();
    }

    /// <summary>
    /// 画の見た目に関わる設定をまとめた文字列。字幕は画に重ねてすぐ描き直すので含めない（BGM と完成品のフェードも
    /// 静止画に出ないので含めない）。
    /// </summary>
    string PreviewKey() => string.Join("|", string.Join(";", _files), LengthBox.Text,
        Intro.Choice with { FadeIn = "", FadeOut = "" }, BlurRadio.IsChecked);

    /// <summary>字幕に関わる設定（動画のプレビューは字幕も焼き込むので、こちらも含める）。</summary>
    string CaptionKey() => string.Join("|", TextCheck.IsChecked, CaptionBox.Text, FontBox.SelectedIndex, CaptionColors(),
        BackBoxRadio.IsChecked, SizeBox.Text, PositionBox.SelectedIndex, TextXBox.Text, TextYBox.Text);

    /// <summary>
    /// 動画のプレビューに関わる設定。画の設定に、BGM（曲・音量・フェードイン）と完成品のフェードを足したもの。
    /// BGM のフェードアウトは動画の終わりにかかるので、最初の数秒の動画には出ない（含めない）。
    /// </summary>
    string ClipKey() => string.Join("|", PreviewKey(), CaptionKey(), _bgmFile, VolumeSlider.Value, FadeInBox.Text,
        Intro.Choice.FadeIn, Intro.Choice.FadeOut);

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

    /// <summary>⑤ の「フィードバックを送る」で開く Google フォーム。</summary>
    internal const string FeedbackUrl =
        "https://docs.google.com/forms/d/e/1FAIpQLScQwEMFGRVx0QjsAopug_X9hvf_11NmBE9o8TivSNqL1xdMyQ/viewform?usp=publish-editor";

    /// <summary>フィードバックの Google フォームを、ふだん使うブラウザーで開く。開けなければ、アドレスをコピーして知らせる。</summary>
    void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(FeedbackUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            try { Clipboard.SetText(FeedbackUrl); } catch { /* コピーできなくても、下でアドレスを見せる */ }
            MessageBox.Show(this, "ブラウザーを開けませんでした。フォームのアドレスをコピーしたので、ブラウザーに貼り付けてください。\n\n"
                                  + FeedbackUrl + "\n\n（" + ex.Message + "）", "フィードバック", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

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
        // 変換中は設定を変えられないようにする（手順の画面の行き来と、プレビュー欄はそのまま）
        VideoStep.IsEnabled = Intro.IsEnabled = BgmStep.IsEnabled = TextStep.IsEnabled = idle;
        BgmSelectButton.IsEnabled = Ffmpeg.IsAvailable;
        BgmClearButton.IsEnabled = FadeInBox.IsEnabled = FadeOutBox.IsEnabled = VolumeSlider.IsEnabled = _bgmFile != null;
        CaptionBox.IsEnabled = FontBox.IsEnabled = SizeBox.IsEnabled = ColorBox.IsEnabled = CustomColors.IsEnabled = PositionBox.IsEnabled
            = TextXBox.IsEnabled = TextYBox.IsEnabled
            = BackNoneRadio.IsEnabled = BackBoxRadio.IsEnabled = TextCheck.IsChecked == true;
        bool valid = _files.Count > 0 && _totalSeconds > 0 && Validate(out _) == null;
        PreviewButton.IsEnabled = idle && valid; // プレビューは画像を作るだけなので、エンコーダーは要らない
        // 動画のプレビューは動画を作るので要る。作っている最中は「やめる」として押せる
        PreviewVideoButton.IsEnabled = _clipRendering || (idle && _encoder != null && valid);
        StartButton.IsEnabled = idle && _encoder != null && valid;
    }

    internal static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";
    }
}
