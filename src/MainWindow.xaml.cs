using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace TimelapseShorts;

public partial class MainWindow : Window
{
    static readonly string[] VideoExtensions = [".mp4", ".mov", ".mkv", ".avi", ".m4v", ".mts", ".m2ts", ".wmv", ".webm"];

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
    Platform _platform = Platforms[0];
    string? _encoder;
    string? _lastOutputDir;
    CancellationTokenSource? _cts;
    bool _busy, _ready;

    // 変換中にPCがスリープして処理が止まるのを防ぐ（画面が消えるのは構わない）
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    public MainWindow()
    {
        InitializeComponent();
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
            ? $"（最大 {Fmt(_platform.MaxSeconds)}）"
            : $"（最大 {Fmt(_platform.MaxSeconds)}。{_platform.Note}）";
        LengthBox.Text = Fmt(_platform.DefaultSeconds);
        RefreshInfo();
    }

    void Settings_TextChanged(object sender, TextChangedEventArgs e) => RefreshInfo();

    void Settings_Changed(object sender, RoutedEventArgs e) => RefreshInfo();

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await AddFilesAsync(paths);
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

    /// <summary>入力された総尺と冒頭の秒数を確かめる。問題があればその説明を返す。</summary>
    string? Validate(out int target, out int intro)
    {
        intro = 0;
        if (!TryParseSeconds(LengthBox.Text, out target) || target < 1 || target > _platform.MaxSeconds)
            return $"総尺は 0:01〜{Fmt(_platform.MaxSeconds)} の間で入力してください（例: {Fmt(_platform.DefaultSeconds)}）";
        if (IntroCheck.IsChecked != true) return null;
        double length = _totalSeconds > 0 ? Math.Min(_totalSeconds, target) : target;
        if (!TryParseSeconds(IntroBox.Text, out intro) || intro < 1 || intro >= length)
            return "冒頭に移動する秒数は、1秒以上で総尺より短くしてください";
        return null;
    }

    void RefreshInfo()
    {
        if (!_ready) return;
        string? error = Validate(out int target, out _);
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
            double factor = Math.Max(1, _totalSeconds / target);
            InfoText.Text = $"合計 {Fmt(_totalSeconds)} → {Fmt(_totalSeconds / factor)}" +
                            (factor > 1 ? $"（{factor:0.#}倍速）" : "（元が短いので等速のまま縦長にします）");
        }
        UpdateState();
    }

    async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_encoder == null || _files.Count == 0 || Validate(out int target, out int intro) != null) return;
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
            string output = await Ffmpeg.ConvertAsync(_files, _totalSeconds, target, intro, layout,
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
        PlatformPanel.IsEnabled = LengthBox.IsEnabled = IntroCheck.IsEnabled = idle;
        IntroBox.IsEnabled = idle && IntroCheck.IsChecked == true;
        CropRadio.IsEnabled = BlurRadio.IsEnabled = idle;
        StartButton.IsEnabled = idle && _encoder != null && _files.Count > 0 && _totalSeconds > 0
                                && Validate(out _, out _) == null;
    }

    static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{t:m\\:ss}";
    }
}
