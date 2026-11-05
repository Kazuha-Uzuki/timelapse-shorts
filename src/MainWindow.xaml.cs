using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
    sealed record Settings(int Target, int Intro, double FadeIn, double FadeOut, int TextSize);

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
    string? _lastOutputDir;
    CancellationTokenSource? _cts;
    bool _busy, _ready;
    Window? _previewWindow; // プレビューの画面（開いていなければ null）
    readonly Image[] _previewImages = new Image[3];
    readonly TextBlock[] _previewLabels = new TextBlock[3];

    // 変換中にPCがスリープして処理が止まるのを防ぐ（画面が消えるのは構わない）
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    public MainWindow()
    {
        InitializeComponent();
        // 画面の小さいノートPCでもはみ出さないよう、作業領域（タスクバーを除いた広さ）に収める
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        // 投稿先ボタン（押した状態が残り、1つだけ選べる）
        var toggleStyle = TryFindResource(typeof(ToggleButton)) as Style;
        foreach (var p in Platforms)
        {
            var button = new RadioButton
            {
                GroupName = "Platform",
                Tag = p,
                Margin = new Thickness(3),
                Padding = new Thickness(4, 6, 4, 6),
                Content = new TextBlock
                {
                    Text = $"{p.Name}\n最大 {Fmt(p.MaxSeconds)}",
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            if (toggleStyle != null) button.Style = toggleStyle;
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
        FilesList.ItemsSource = _files.Select(f => System.IO.Path.GetFileName(f)).ToList();
        RefreshInfo();
    }

    /// <summary>「2:59」や「179」を秒に直す。全角で入力されても読めるようにする。</summary>
    static bool TryParseSeconds(string text, out int seconds)
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
    static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Normalize(NormalizationForm.FormKC).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>入力欄を確かめて設定を返す。問題があればその説明を返す。</summary>
    string? Validate(out Settings settings)
    {
        settings = new Settings(0, 0, 0, 0, 0);
        if (!TryParseSeconds(LengthBox.Text, out int target) || target < 1 || target > _platform.MaxSeconds)
            return $"総尺は 0:01〜{Fmt(_platform.MaxSeconds)} の間で入力してください（例: {Fmt(_platform.DefaultSeconds)}）";
        double length = _totalSeconds > 0 ? Math.Min(_totalSeconds, target) : target; // 出来上がりの長さ
        int intro = 0;
        if (IntroCheck.IsChecked == true && (!TryParseSeconds(IntroBox.Text, out intro) || intro < 1 || intro >= length))
            return "冒頭に移動する秒数は、1秒以上で総尺より短くしてください";
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
        UpdatePreview();
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
            double factor = Math.Max(1, _totalSeconds / s.Target);
            InfoText.Text = $"合計 {Fmt(_totalSeconds)} → {Fmt(_totalSeconds / factor)}" +
                            (factor > 1 ? $"（{factor:0.#}倍速）" : "（元が短いので等速のまま縦長にします）") +
                            (_bgmFile == null ? "" : VolumeSlider.Value == 0 ? "・BGMあり" : $"・BGMあり（{VolumeText.Text}）") +
                            (TextCheck.IsChecked == true ? "・テキストあり" : "");
        }
        UpdateState();
    }

    /// <summary>テキストの見本。文字の色で、縁取りの色を背景にして表示する。</summary>
    void UpdatePreview()
    {
        if (FontBox.SelectedItem is FontCatalog.Item font) PreviewText.FontFamily = font.Family;
        PreviewText.Text = string.IsNullOrWhiteSpace(CaptionBox.Text) ? "ここに見本が出ます" : CaptionBox.Text.Trim();
        if (ColorBox.SelectedItem is ColorPreset c)
        {
            PreviewText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + c.Text));
            PreviewBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#" + c.Outline));
        }
        PreviewBorder.Opacity = TextCheck.IsChecked == true ? 1 : 0.4;
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
            (Ffmpeg.TextPosition)Math.Max(0, PositionBox.SelectedIndex));
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
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        try
        {
            string output = await Ffmpeg.ConvertAsync(_files, _totalSeconds, s.Target, s.Intro, layout, bgm, caption,
                _platform.Key, _encoder, progress, _cts.Token);
            _lastOutputDir = System.IO.Path.GetDirectoryName(output);
            StatusText.Text = $"完成しました（{Fmt(sw.Elapsed.TotalSeconds)}）: {System.IO.Path.GetFileName(output)}";
            OpenFolderButton.Visibility = Visibility.Visible;
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
    }

    /// <summary>出来上がりの「最初・真ん中・最後」の画を作って、別の小さな画面に並べる。</summary>
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
            var pngs = await Ffmpeg.RenderPreviewAsync(_files, _totalSeconds, s.Target, s.Intro, layout, caption,
                CancellationToken.None);
            StatusText.Text = status;
            ShowPreview(pngs, s.Intro > 0);
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

    /// <summary>プレビューの画面を開く（開いていれば画を差し替える）。</summary>
    void ShowPreview(IReadOnlyList<byte[]> pngs, bool introMoved)
    {
        if (_previewWindow == null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 3; i++)
            {
                _previewLabels[i] = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
                _previewImages[i] = new Image { Width = 216, Height = 384, Stretch = Stretch.Uniform };
                var cell = new StackPanel { Margin = new Thickness(6) };
                cell.Children.Add(_previewLabels[i]);
                cell.Children.Add(new Border { Background = Brushes.Black, Child = _previewImages[i] });
                row.Children.Add(cell);
            }
            var root = new StackPanel { Margin = new Thickness(10) };
            root.Children.Add(row);
            root.Children.Add(new TextBlock
            {
                Margin = new Thickness(6, 8, 6, 0),
                MaxWidth = 680,
                FontSize = 12,
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Text = "仕上がりと同じ処理で描いた画を、縮小して表示しています（音は入りません）。" +
                       "設定を変えたら、もう一度「プレビュー」を押してください。",
            });
            _previewWindow = new Window
            {
                Title = "プレビュー",
                Owner = this,
                Content = root,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                FontFamily = FontFamily,
                FontSize = 14,
            };
            _previewWindow.Closed += (_, _) => _previewWindow = null;
            _previewWindow.Show();
        }
        string[] names = introMoved ? ["最初（完成品）", "真ん中", "最後"] : ["最初", "真ん中", "最後"];
        for (int i = 0; i < 3; i++)
        {
            _previewLabels[i].Text = names[i];
            _previewImages[i].Source = LoadPng(pngs[i]);
        }
        _previewWindow.Activate();
    }

    static BitmapImage LoadPng(byte[] png)
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

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputDir != null)
            Process.Start(new ProcessStartInfo("explorer.exe", _lastOutputDir) { UseShellExecute = true });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cts?.Cancel(); // 変換中に閉じたら ffmpeg も止める
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
        IntroBox.IsEnabled = IntroCheck.IsChecked == true;
        BgmSelectButton.IsEnabled = Ffmpeg.IsAvailable;
        BgmClearButton.IsEnabled = FadeInBox.IsEnabled = FadeOutBox.IsEnabled = VolumeSlider.IsEnabled = _bgmFile != null;
        CaptionBox.IsEnabled = FontBox.IsEnabled = SizeBox.IsEnabled = ColorBox.IsEnabled = PositionBox.IsEnabled
            = TextCheck.IsChecked == true;
        bool valid = _files.Count > 0 && _totalSeconds > 0 && Validate(out _) == null;
        PreviewButton.IsEnabled = idle && valid; // プレビューは画像を作るだけなので、エンコーダーは要らない
        StartButton.IsEnabled = idle && _encoder != null && valid;
    }

    static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";
    }
}
