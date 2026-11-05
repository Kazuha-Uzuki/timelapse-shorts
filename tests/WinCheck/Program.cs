using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TimelapseShorts;

// Windows 上で、同梱の ffmpeg とフォントを使って実際に変換し、結果を確かめる（GitHub Actions 用）。
// exe と同じフォルダに ffmpeg.exe / ffprobe.exe / fonts フォルダを置いて実行する。
static class Program
{
    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    static int _failures;
    static MainWindow? _window;        // CheckWindow で作った画面（最後にプレビューの表示も確かめる）
    static List<Ffmpeg.PreviewFrame>? _previewFrames; // RunAsync で作ったプレビューの画
    // 手動実行で「スクリーンショット」を ON にしたときだけ、画面の画像をログに出す（見た目の確認用）
    static readonly bool Shots = Environment.GetEnvironmentVariable("WINCHECK_SHOTS") == "1";

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "OK" : "NG")}  {name}  {detail}");
        if (!ok) _failures++;
    }

    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // テーマ（ThemeMode）はアプリに付くので、本物と同じように Application を作っておく。
        // 窓を閉じても終わらないようにする（あとで設定の復元を確かめるため、もう一度窓を作る）
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 前回の設定は、このテスト専用の場所に書く（最初は無い＝初めての起動）
        AppSettings.FilePath = Path.Combine(Path.GetTempPath(), $"wincheck-settings-{Environment.ProcessId}.json");
        File.Delete(AppSettings.FilePath);
        CheckWindow(); // 画面は STA のこのスレッドで作る必要があるので、先に同期で確かめる
        // 変換のテストは別のスレッドで（画面を作ったスレッドで待つと止まることがあるため）
        Task.Run(() => RunAsync()).GetAwaiter().GetResult();
        CheckShownWindow(); // プレビューの画ができてから、画面を作ったこのスレッドで表示を確かめる
        Console.WriteLine(_failures == 0 ? "すべて OK" : $"NG が {_failures} 件");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>画面を実際に作り（表示はしない）、初期状態と入力チェックを確かめる。</summary>
    static void CheckWindow()
    {
        MainWindow w;
        try { w = new MainWindow(); }
        catch (Exception ex) { Check("画面を作れる", false, ex.ToString()); return; }
        Check("画面を作れる", true);
        _window = w;

        T Get<T>(string field) => (T)typeof(MainWindow).GetField(field, F)!.GetValue(w)!;
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, F)!.SetValue(w, value);
        void Refresh() => typeof(MainWindow).GetMethod("RefreshInfo", F)!.Invoke(w, null);
        void Call(string method) => typeof(MainWindow).GetMethod(method, F)!.Invoke(w, null);

        Check("フォント一覧（★が先頭）", w.FontBox.Items.Count > 9 && w.FontBox.Items[0]!.ToString()!.StartsWith('★'),
            $"{w.FontBox.Items.Count} 個 / 先頭 {w.FontBox.Items[0]}");
        Check("色と位置の選択肢", w.ColorBox.Items.Count == 4 && w.PositionBox.Items.Count == 3);
        Check("総尺の初期値", w.LengthBox.Text == "2:59", w.LengthBox.Text);

        // 1時間の動画（スマホが2つに分けたもの）を入れた状態にする
        Get<List<string>>("_files").AddRange([@"C:\動画\作業 2026-11-01 (1).mp4", @"C:\動画\作業 2026-11-01 (2).mp4"]);
        Set("_totalSeconds", 3600.0);
        Set("_encoder", "h264_mf");
        Call("RefreshList");
        Check("基本の表示", w.InfoText.Text == "合計 1:00:00 → 2:59（20.1倍速）" && w.StartButton.IsEnabled
                            && w.PreviewButton.IsEnabled, w.InfoText.Text);
        Set("_encoder", null);
        Refresh();
        Check("エンコーダーが無くてもプレビューはできる", w.PreviewButton.IsEnabled && !w.StartButton.IsEnabled);
        Set("_encoder", "h264_mf");

        w.TextCheck.IsChecked = true;
        Check("テキストが空なら作れない", w.InfoText.Text == "入れるテキストを入力してください" && !w.StartButton.IsEnabled
                                      && !w.PreviewButton.IsEnabled, w.InfoText.Text);
        w.CaptionBox.Text = "完成まで3時間";
        Check("テキストあり", w.InfoText.Text.EndsWith("・テキストあり") && w.SampleText.Text == "完成まで3時間", w.InfoText.Text);
        w.SizeBox.Text = "10";
        Check("文字の大きさの範囲", w.InfoText.Text.StartsWith("文字の大きさ") && !w.StartButton.IsEnabled, w.InfoText.Text);
        w.SizeBox.Text = "８０"; // 全角でも読める
        Check("全角の数字", w.StartButton.IsEnabled, w.InfoText.Text);

        Set("_bgmFile", @"C:\dummy\曲.mp3");
        Set("_bgmSeconds", 120.0);
        Call("ShowBgm");
        Refresh();
        Check("BGMあり", w.InfoText.Text.Contains("・BGMあり") && w.FadeInBox.IsEnabled, w.InfoText.Text);
        w.FadeInBox.Text = "100";
        w.FadeOutBox.Text = "100";
        Check("フェードが長すぎると作れない", w.InfoText.Text.StartsWith("フェードの秒数") && !w.StartButton.IsEnabled, w.InfoText.Text);
        w.FadeInBox.Text = "1.5";
        w.FadeOutBox.Text = "３";
        Check("小数・全角のフェード", w.StartButton.IsEnabled, w.InfoText.Text);

        // 音量バー: 曲の平均 -20 dB・最大 -3 dB として、音量を変える
        Set("_bgmMean", -20.0);
        Set("_bgmPeak", -3.0);
        w.VolumeSlider.Value = 6;
        Check("音量バー（+6 dB で 0 dB 超え）",
            w.VolumeText.Text == "+6 dB" && w.MeanBar.Value == -14 && w.PeakBar.Value == 0 && w.PeakText.Text == "+3.0 dB"
            && w.LevelNote.Text.Contains("自動で少し抑えます") && w.InfoText.Text.Contains("BGMあり（+6 dB）"),
            $"{w.VolumeText.Text} / 平均 {w.MeanText.Text} / 最大 {w.PeakText.Text} / {w.LevelNote.Text} / {w.InfoText.Text}");
        w.VolumeSlider.Value = -6;
        Check("音量バー（-6 dB）", w.PeakBar.Value == -9 && w.PeakText.Text == "-9.0 dB" && !w.LevelNote.Text.Contains("超えています"),
            $"{w.VolumeText.Text} / 最大 {w.PeakText.Text} / {w.LevelNote.Text}");
    }

    static async Task RunAsync()
    {
        string work = Path.Combine(Path.GetTempPath(), "wincheck");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        // テスト素材（日本語・空白入りのファイル名で、パスの扱いも確かめる）。2本目は複数ファイルのプレビュー用
        string video = Path.Combine(work, "テスト 動画.mp4");
        string video2 = Path.Combine(work, "テスト 動画 2.mp4");
        string song = Path.Combine(work, "テスト曲.m4a");
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=s=540x960:r=30:d=30", "-c:v", "mpeg4", "-q:v", "5", video);
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc=s=540x960:r=30:d=30", "-c:v", "mpeg4", "-q:v", "5", video2);
        await FfmpegAsync("-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "aac", song);

        string? encoder = await Ffmpeg.DetectEncoderAsync();
        Check("エンコーダー", encoder != null, encoder ?? "");
        if (encoder == null) return;

        var fonts = FontCatalog.Load();
        var presets = fonts.Where(f => f.File != null).ToList();
        Check("同梱フォントの一覧", presets.Count == 9, string.Join(" / ", presets.Select(p => p.Display)));
        var arial = fonts.FirstOrDefault(f => f.File == null && f.Display == "Arial");
        Check("PC のフォントの一覧", arial != null, $"全 {fonts.Count} 個");

        double total = await Ffmpeg.GetDurationAsync(video);
        double songSeconds = await Ffmpeg.GetAudioDurationAsync(song);
        const string text = "完成まで3時間\nイラスト";

        // 文字なし（比べる基準）
        string plain = await ConvertAsync(video, total, encoder, "plain", null, null);
        string plainHash = await FrameHashAsync(plain);

        // 同梱フォントそれぞれで文字を入れる。フォントが正しく選ばれていれば、どの2つも見た目が違うはず
        var hashes = new Dictionary<string, string>();
        foreach (var font in presets)
        {
            var (file, family, missing) = FontCatalog.Resolve(font, text);
            Check($"足りない文字なし: {font.Display}", missing == "", $"family={family} {missing}");
            var caption = new Ffmpeg.Caption(text, file, family, 120, "FFFFFF", "000000", Ffmpeg.TextPosition.Middle);
            string output = await ConvertAsync(video, total, encoder, $"font{hashes.Count}", null, caption);
            hashes[font.Display] = await FrameHashAsync(output);
            Check($"文字が描かれた: {font.Display}", hashes[font.Display] != plainHash);
        }
        Check("同梱フォントがそれぞれ別の見た目", hashes.Values.Distinct().Count() == hashes.Count,
            $"{hashes.Values.Distinct().Count()} 種類 / {hashes.Count} 個");

        // PC のフォント（Arial）。日本語は入っていないので、足りない文字として見つかるはず
        if (arial != null)
        {
            var (file, family, missing) = FontCatalog.Resolve(arial, "Test テスト");
            Check("PC のフォントの場所と名前", File.Exists(file) && family == "Arial", $"{file} / {family}");
            Check("足りない文字を見つける", missing == "テスト", missing);
            var caption = new Ffmpeg.Caption("Test", file, family, 120, "FFE600", "000000", Ffmpeg.TextPosition.Bottom);
            string output = await ConvertAsync(video, total, encoder, "arial", null, caption);
            Check("PC のフォントで文字が描かれた", await FrameHashAsync(output) != plainHash);
        }

        // 冒頭移動＋BGM（2秒の曲を繰り返し、フェードあり）＋文字
        var bgm = new Ffmpeg.Bgm(song, songSeconds, 1, 1);
        var cap = presets.Count > 0 ? FontCatalog.Resolve(presets[0], text) : default;
        string full = await ConvertAsync(video, total, encoder, "full", bgm,
            presets.Count > 0 ? new Ffmpeg.Caption(text, cap.File, cap.Family, 90, "FFFFFF", "000000", Ffmpeg.TextPosition.Top) : null,
            intro: 1);
        string info = await FfprobeAsync("-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,duration",
            "-of", "csv=p=0", full);
        Check("BGM 入りの動画", info.Contains("video") && info.Contains("1080,1920") && info.Contains("aac"), info.Replace("\r\n", " | "));
        string audioLength = (await FfprobeAsync("-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration",
            "-of", "csv=p=0", full)).Trim();
        Check("BGM の長さが動画と同じ（5秒）", double.TryParse(audioLength, System.Globalization.CultureInfo.InvariantCulture, out var a)
                                            && Math.Abs(a - 5) < 0.05, audioLength);
        // 音量: 測定と、下げる・大きく上げる（音割れしないこと）
        var (songMean, songPeak) = await Ffmpeg.GetVolumeAsync(song);
        Check("曲の音量を測れる", !double.IsNaN(songMean) && !double.IsNaN(songPeak), $"平均 {songMean} / 最大 {songPeak} dB");
        var plainBgm = bgm with { FadeIn = 0, FadeOut = 0 };
        var (_, quieterPeak) = await Ffmpeg.GetVolumeAsync(await ConvertAsync(video, total, encoder, "minus6", plainBgm with { GainDb = -6 }, null));
        Check("音量 -6 dB", Math.Abs(quieterPeak - (songPeak - 6)) < 1, $"最大 {songPeak} → {quieterPeak} dB");
        var (_, louderPeak) = await Ffmpeg.GetVolumeAsync(await ConvertAsync(video, total, encoder, "plus24", plainBgm with { GainDb = 24 }, null));
        Check("音量 +24 dB でも音割れしない", louderPeak <= 0, $"最大 {songPeak} + 24 → {louderPeak} dB");

        await CheckPreviewAsync(video, video2, total, text, cap);

        Check("作業用フォルダが残っていない", Directory.GetDirectories(Path.GetTempPath(), "timelapse_*").Length == 0);
    }

    /// <summary>プレビュー（最初・真ん中・最後の3枚）。大きさ、文字、帯、複数ファイルのときに正しい場所を描くかを確かめる。</summary>
    static async Task CheckPreviewAsync(string video, string video2, double total, string text, (string File, string Family, string Missing) font)
    {
        Task<List<Ffmpeg.PreviewFrame>> Render(List<string> inputs, double seconds, double intro, Ffmpeg.Caption? caption) =>
            Ffmpeg.RenderPreviewAsync(inputs, seconds, 5, intro, Ffmpeg.Layout.Crop, caption, CancellationToken.None);
        static (int W, int H) PngSize(byte[] png) =>
            (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        static bool Same(Ffmpeg.PreviewFrame a, Ffmpeg.PreviewFrame b) => a.Png.AsSpan().SequenceEqual(b.Png);

        var sw = Stopwatch.StartNew();
        var plain = await Render([video], total, 1, null);
        Check("プレビュー: 3枚・360×640", plain.Count == 3 && plain.All(f => PngSize(f.Png) == (360, 640)),
            string.Join(" / ", plain.Select(f => $"{f.Seconds:0.###}秒 {PngSize(f.Png).W}×{PngSize(f.Png).H}"))
            + $"（{sw.Elapsed.TotalSeconds:0.00}秒）");
        Check("プレビュー: 最初・真ん中・最後の時刻", plain.Count == 3 && plain[0].Seconds == 0 && plain[1].Seconds == 2.5
                                                && Math.Abs(plain[2].Seconds - (5 - 1.0 / 30)) < 1e-9);
        Check("プレビュー: 3枚とも違う画", plain.Select(f => Convert.ToBase64String(f.Png)).Distinct().Count() == 3);
        List<Ffmpeg.PreviewFrame>? withText = null;
        if (font.File != null)
        {
            var caption = new Ffmpeg.Caption(text, font.File, font.Family, 90, "FFFFFF", "000000", Ffmpeg.TextPosition.Top);
            withText = await Render([video], total, 1, caption);
            Check("プレビュー: 文字が入る", withText.Count == 3 && !Same(withText[0], plain[0]));
            // 背景あり（帯）: 字幕の書き方が「帯」になり、画もフチ取りのときと変わる
            var boxed = caption with { Box = true };
            var withBox = await Render([video], total, 1, boxed);
            Check("テキストの背景（帯）", Ffmpeg.BuildAss(boxed, 5).Contains(",100,100,0,0,3,")
                                        && Ffmpeg.BuildAss(caption, 5).Contains(",100,100,0,0,1,")
                                        && !Same(withBox[0], withText[0]) && !Same(withBox[0], plain[0]));
        }

        // 2本（各30秒）をつないで5秒・最後の1秒を冒頭へ → 12倍速。
        // 最初の画は「2本目の18秒目」、真ん中の画は「1本目の18秒目」になるはず。
        // それぞれ1本だけで「最後の2秒を冒頭へ」（6倍速で、最初の画が18秒目）にしたときの最初の画と同じになるか比べる
        double total2 = await Ffmpeg.GetDurationAsync(video2);
        sw.Restart();
        var multi = await Render([video, video2], total + total2, 1, null);
        double multiSeconds = sw.Elapsed.TotalSeconds;
        var only2 = await Render([video2], total2, 2, null);
        var only1 = await Render([video], total, 2, null);
        Check("プレビュー（2本）: 最初は2本目の完成品", Same(multi[0], only2[0]), $"{multiSeconds:0.00}秒");
        Check("プレビュー（2本）: 真ん中は1本目", Same(multi[1], only1[0]));
        _previewFrames = withText ?? plain;
    }

    /// <summary>
    /// 画面を実際に表示して、プレビュー欄（画・切り替え・設定が変わったときの表示）と、
    /// 標準・最小の大きさでの配置（切れている所がないか）を確かめる。
    /// </summary>
    static void CheckShownWindow()
    {
        if (_window == null || _previewFrames == null)
        {
            Check("プレビュー欄", false, "前のテストが通っていないので確かめられない");
            return;
        }
        var w = _window;
        try
        {
            w.Show();
            AllowLargeWindow(w);
            var setPreview = typeof(MainWindow).GetMethod("SetPreview", F)!;
            Check("プレビュー欄: 最初は案内だけ", w.PreviewImage.Source == null && w.PreviewPlaceholder.IsVisible);
            setPreview.Invoke(w, [_previewFrames, true]);
            var first = w.PreviewImage.Source;
            Check("プレビュー欄に画が出る",
                first is BitmapSource { PixelWidth: 360, PixelHeight: 640 } && !w.PreviewPlaceholder.IsVisible
                && w.PreviewCaption.Text == "最初（完成品）・0:00 の画", w.PreviewCaption.Text);
            w.PreviewLastRadio.IsChecked = true;
            Check("プレビュー欄: 最後に切り替えられる", !ReferenceEquals(first, w.PreviewImage.Source)
                                                    && w.PreviewCaption.Text == "最後・0:05 の画", w.PreviewCaption.Text);
            w.PreviewFirstRadio.IsChecked = true;

            Check("プレビュー欄: 作ったあと設定を変えなければ最新", !w.PreviewStale.IsVisible);
            string size = w.SizeBox.Text;
            w.SizeBox.Text = "120";
            bool staleAfterChange = w.PreviewStale.IsVisible;
            w.BackBoxRadio.IsChecked = true;
            w.BackNoneRadio.IsChecked = true;
            w.SizeBox.Text = size;
            Check("プレビュー欄: 設定を変えると古いと出て、戻すと消える", staleAfterChange && !w.PreviewStale.IsVisible);
            w.VolumeSlider.Value = -3;
            Check("プレビュー欄: BGM の音量では古くならない", !w.PreviewStale.IsVisible);
            w.VolumeSlider.Value = -6;

            SettingsWindow.ApplyTheme("Light"); // CI の Windows の色に左右されないよう、まずライトで確かめる
            Check("ライト: 背景が明るい", Luminance(w.Background) > 0.8, $"{w.Background}");
            CheckLayout(w, 1480, 720, "標準"); // MainWindow.xaml の Width / Height
            CheckLayout(w, 1240, 650, "最小"); // MainWindow.xaml の MinWidth / MinHeight
            CheckLayout(w, 1920, 1030, "最大化"); // フルHDの画面いっぱい（プレビューが大きくなる）

            // 設定画面からダークにする（実際の操作と同じ道すじで、設定にも「Dark」が入る）
            var settings = new SettingsWindow((AppSettings)typeof(MainWindow).GetField("_settings", F)!.GetValue(w)!) { Owner = w };
            settings.Show();
            Check("設定画面: 最初は「Windows に合わせる」", settings.SystemRadio.IsChecked == true);
            settings.DarkRadio.IsChecked = true;
            settings.UpdateLayout();
            w.UpdateLayout();
            Check("ダーク: 背景が暗く、文字が明るい", Luminance(w.Background) < 0.2 && Luminance(w.Foreground) > 0.8
                                                && Luminance(w.LengthHint.Foreground) > 0.5,
                $"背景 {w.Background} / 文字 {w.Foreground} / 補足 {w.LengthHint.Foreground}");
            Check("ダーク: ボタンや入力欄も暗い", Luminance(w.LengthBox.Background) < 0.3 && Luminance(w.StartButton.Background) < 0.3,
                $"入力欄 {w.LengthBox.Background} / ボタン {w.StartButton.Background}");
            var settingsClient = (FrameworkElement)VisualTreeHelper.GetChild(settings, 0);
            Shot("settings-dark", settingsClient, settingsClient.RenderSize);
            settings.Close();
            CheckLayout(w, 1480, 720, "ダーク標準");
            CheckLayout(w, 1240, 650, "ダーク最小");

            // 次の起動で戻るか確かめるため、最初とは違う値にしておく
            ((RadioButton)w.PlatformPanel.Children[2]).IsChecked = true; // TikTok
            w.LengthBox.Text = "4:30";
            w.IntroBox.Text = "7";
            w.BlurRadio.IsChecked = true;
            w.FadeInBox.Text = "2";
            w.VolumeSlider.Value = -4;
            w.FontBox.SelectedIndex = 1;
            w.ColorBox.SelectedIndex = 2;
            w.PositionBox.SelectedIndex = 2;
            w.BackBoxRadio.IsChecked = true;
            w.SizeBox.Text = "100";

            typeof(MainWindow).GetMethod("ClearButton_Click", F)!.Invoke(w, [w.ClearButton, new RoutedEventArgs()]);
            Check("動画をクリアするとプレビューも消える", w.PreviewImage.Source == null && w.PreviewPlaceholder.IsVisible
                                                    && !w.PreviewStale.IsVisible);
            string font = w.FontBox.SelectedItem!.ToString()!;
            w.Close(); // 閉じるときに設定を書く
            CheckRestore(font);
        }
        catch (Exception ex)
        {
            Check("プレビュー欄", false, ex.ToString());
        }
    }

    /// <summary>閉じたときの設定が、次に開いた画面に戻るか。設定のファイルが壊れていても開けるか。</summary>
    static void CheckRestore(string font)
    {
        Check("設定を書いた", File.Exists(AppSettings.FilePath));
        var w = new MainWindow();
        Check("前回の設定に戻る",
            w.PlatformPanel.Children.OfType<RadioButton>().ElementAt(2).IsChecked == true && w.LengthBox.Text == "4:30"
            && w.IntroCheck.IsChecked == true && w.IntroBox.Text == "7" && w.BlurRadio.IsChecked == true
            && w.FadeInBox.Text == "2" && w.FadeOutBox.Text == "３" && w.VolumeSlider.Value == -4
            && w.FontBox.SelectedItem?.ToString() == font && w.ColorBox.SelectedIndex == 2 && w.PositionBox.SelectedIndex == 2
            && w.BackBoxRadio.IsChecked == true && w.SizeBox.Text == "100",
            File.ReadAllText(AppSettings.FilePath).ReplaceLineEndings(" "));
        Check("前回の色（ダーク）で開く", Application.Current.ThemeMode == ThemeMode.Dark && Luminance(w.Background) < 0.2,
            $"{Application.Current.ThemeMode}");
        w.Close();

        File.WriteAllText(AppSettings.FilePath, "{ こわれた");
        try
        {
            w = new MainWindow();
            Check("設定のファイルが壊れていても開ける（最初の設定になる）", w.LengthBox.Text == "2:59" && w.CropRadio.IsChecked == true,
                w.LengthBox.Text);
            w.Close();
        }
        catch (Exception ex)
        {
            Check("設定のファイルが壊れていても開ける（最初の設定になる）", false, ex.ToString());
        }
        File.Delete(AppSettings.FilePath);
    }

    /// <summary>
    /// 単色のブラシの明るさ（0〜1）。Fluent の色は半透明が多いので、透けている分は黒の上に重ねたとして数える。
    /// 単色でなければ NaN（比べると必ず NG になる）。
    /// </summary>
    static double Luminance(Brush? brush) => brush is SolidColorBrush { Color: var c }
        ? (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255 * c.A / 255 * brush.Opacity
        : double.NaN;

    /// <summary>
    /// テスト用の PC（GitHub の Windows）は画面が 1024×768 で、そのままでは窓をそれより大きくできない。
    /// 窓の大きさの上限（WM_GETMINMAXINFO の ptMaxTrackSize）を広げて、本来の大きさで確かめられるようにする。
    /// </summary>
    static void AllowLargeWindow(Window w)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WmGetMinMaxInfo = 0x0024;
            if (msg == WmGetMinMaxInfo)
            {
                Marshal.WriteInt32(lParam, 32, 4000); // MINMAXINFO.ptMaxTrackSize.x
                Marshal.WriteInt32(lParam, 36, 4000); // MINMAXINFO.ptMaxTrackSize.y
            }
            return IntPtr.Zero; // WPF 側の処理も続けて行わせる
        });
        // WPF は窓を作ったときの上限を覚えていて、大きさを変える前にそれで切り詰めるので、そちらも広げる
        foreach (var name in new[] { "_trackMaxWidthDeviceUnits", "_trackMaxHeightDeviceUnits" })
            if (typeof(Window).GetField(name, F) is { } field)
                field.SetValue(w, Convert.ChangeType(4000, field.FieldType));
    }

    /// <summary>窓を width×height にして、切れている部品がないか・ボタンが窓に収まるかを確かめる。</summary>
    static void CheckLayout(MainWindow w, double width, double height, string label)
    {
        w.Width = width;
        w.Height = height;
        w.UpdateLayout();
        bool sized = Math.Abs(w.ActualWidth - width) < 1 && Math.Abs(w.ActualHeight - height) < 1;
        var root = (FrameworkElement)w.Content;
        var clipped = Descendants(root).Prepend(root)
            .Where(e => e.IsVisible && LayoutInformation.GetLayoutClip(e) != null)
            .Select(e => !string.IsNullOrEmpty(e.Name) ? e.Name : e is TextBlock t ? $"「{t.Text}」" : e.GetType().Name)
            .ToList();
        var corner = w.StartButton.TranslatePoint(new Point(w.StartButton.ActualWidth, w.StartButton.ActualHeight), root);
        bool inside = corner.X <= root.ActualWidth + 0.5 && corner.Y <= root.ActualHeight + 0.5;
        Check($"画面の配置（{label} {width}×{height}）: 切れている所がない", sized && clipped.Count == 0 && inside,
            (sized ? "" : $"窓が {w.ActualWidth}×{w.ActualHeight} にしかならない / ") + string.Join(" / ", clipped)
            + (inside ? "" : $" / 作成ボタンが窓の外（{corner.X:0}, {corner.Y:0}）"));
        // プレビューの枠は出来上がりと同じ 9:16 で、画が枠いっぱいに出る（黒い余白がない）
        var frame = w.PreviewFrame;
        var bottom = w.PreviewButton.TranslatePoint(new Point(0, w.PreviewButton.ActualHeight), root);
        Check($"プレビューの枠が 9:16（{label}）",
            Math.Abs(frame.ActualWidth / frame.ActualHeight - 9.0 / 16) < 0.005
            && Math.Abs(w.PreviewImage.ActualWidth - frame.ActualWidth) < 1 && Math.Abs(w.PreviewImage.ActualHeight - frame.ActualHeight) < 1
            && bottom.Y <= root.ActualHeight + 0.5,
            $"枠 {frame.ActualWidth:0.#}×{frame.ActualHeight:0.#} / 画 {w.PreviewImage.ActualWidth:0.#}×{w.PreviewImage.ActualHeight:0.#}"
            + $" / 欄の幅 {w.PreviewSection.ActualWidth:0}");
        var client = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        Shot($"main-{label}", client, client.RenderSize);
    }

    static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<FrameworkElement>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>Shots のときだけ、element を size の大きさで PNG にしてログに出す（Base64 を 4000 文字ずつ「SHOT 名前 …」の行で）。</summary>
    static void Shot(string name, FrameworkElement element, Size size)
    {
        if (!Shots) return;
        const double scale = 2; // 高解像度の画面で見ても粗くならないよう 2 倍で描く
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen()) dc.DrawRectangle(SystemColors.WindowBrush, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        string b64 = Convert.ToBase64String(stream.ToArray());
        for (int i = 0; i < b64.Length; i += 4000)
            Console.WriteLine($"SHOT {name} {b64.Substring(i, Math.Min(4000, b64.Length - i))}");
    }

    /// <summary>30秒の動画を5秒のタイムラプスにする（アプリと同じ処理）。</summary>
    static Task<string> ConvertAsync(string video, double total, string encoder, string suffix,
        Ffmpeg.Bgm? bgm, Ffmpeg.Caption? caption, double intro = 0) =>
        Ffmpeg.ConvertAsync([video], total, 5, intro, Ffmpeg.Layout.Crop, bgm, caption, suffix, encoder,
            new Progress<Ffmpeg.Progress>(_ => { }), CancellationToken.None);

    /// <summary>真ん中あたり（75コマ目）の画像のハッシュ。</summary>
    static async Task<string> FrameHashAsync(string video) =>
        (await RunAsync(Ffmpeg.FfmpegPath, "-hide_banner", "-loglevel", "error", "-i", video,
            "-vf", "select=eq(n\\,75)", "-frames:v", "1", "-f", "md5", "-")).Trim();

    static Task FfmpegAsync(params string[] args) =>
        RunAsync(Ffmpeg.FfmpegPath, ["-hide_banner", "-loglevel", "error", "-y", .. args]);

    static Task<string> FfprobeAsync(params string[] args) => RunAsync(Ffmpeg.FfprobePath, args);

    static async Task<string> RunAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} が失敗: {await stderr}");
        return await stdout;
    }
}
