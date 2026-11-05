using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using TimelapseShorts;

// Windows 上で、同梱の ffmpeg とフォントを使って実際に変換し、結果を確かめる（GitHub Actions 用）。
// exe と同じフォルダに ffmpeg.exe / ffprobe.exe / fonts フォルダを置いて実行する。
static class Program
{
    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    static int _failures;
    static MainWindow? _window;        // CheckWindow で作った画面（最後にプレビューの表示も確かめる）
    static List<byte[]>? _previewPngs; // RunAsync で作ったプレビューの画
    static List<byte[]>? _shotPngs;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "OK" : "NG")}  {name}  {detail}");
        if (!ok) _failures++;
    }

    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        CheckWindow(); // 画面は STA のこのスレッドで作る必要があるので、先に同期で確かめる
        // 変換のテストは別のスレッドで（画面を作ったスレッドで待つと止まることがあるため）
        Task.Run(() => RunAsync()).GetAwaiter().GetResult();
        CheckPreviewWindow(); // プレビューの画ができてから、画面を作ったこのスレッドで表示を確かめる
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

        Check("フォント一覧（★が先頭）", w.FontBox.Items.Count > 9 && w.FontBox.Items[0]!.ToString()!.StartsWith('★'),
            $"{w.FontBox.Items.Count} 個 / 先頭 {w.FontBox.Items[0]}");
        Check("色と位置の選択肢", w.ColorBox.Items.Count == 4 && w.PositionBox.Items.Count == 3);
        Check("総尺の初期値", w.LengthBox.Text == "2:59", w.LengthBox.Text);

        // 1時間の動画を入れた状態にする
        Get<List<string>>("_files").Add("dummy.mp4");
        Set("_totalSeconds", 3600.0);
        Set("_encoder", "h264_mf");
        Refresh();
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
        Check("テキストあり", w.InfoText.Text.EndsWith("・テキストあり") && w.PreviewText.Text == "完成まで3時間", w.InfoText.Text);
        w.SizeBox.Text = "10";
        Check("文字の大きさの範囲", w.InfoText.Text.StartsWith("文字の大きさ") && !w.StartButton.IsEnabled, w.InfoText.Text);
        w.SizeBox.Text = "８０"; // 全角でも読める
        Check("全角の数字", w.StartButton.IsEnabled, w.InfoText.Text);

        Set("_bgmFile", @"C:\dummy\曲.mp3");
        Set("_bgmSeconds", 120.0);
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

    /// <summary>プレビュー（最初・真ん中・最後の3枚）。大きさ、文字、複数ファイルのときに正しい場所を描くかを確かめる。</summary>
    static async Task CheckPreviewAsync(string video, string video2, double total, string text, (string File, string Family, string Missing) font)
    {
        Task<List<byte[]>> Render(List<string> inputs, double seconds, double intro, Ffmpeg.Caption? caption) =>
            Ffmpeg.RenderPreviewAsync(inputs, seconds, 5, intro, Ffmpeg.Layout.Crop, caption, CancellationToken.None);
        static (int W, int H) PngSize(byte[] png) =>
            (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        var sw = Stopwatch.StartNew();
        var plain = await Render([video], total, 1, null);
        Check("プレビュー: 3枚・360×640", plain.Count == 3 && plain.All(p => PngSize(p) == (360, 640)),
            string.Join(" / ", plain.Select(p => $"{PngSize(p).W}×{PngSize(p).H}")) + $"（{sw.Elapsed.TotalSeconds:0.00}秒）");
        Check("プレビュー: 3枚とも違う画", plain.Select(p => Convert.ToBase64String(p)).Distinct().Count() == 3);
        if (font.File != null)
        {
            var withText = await Render([video], total, 1,
                new Ffmpeg.Caption(text, font.File, font.Family, 90, "FFFFFF", "000000", Ffmpeg.TextPosition.Top));
            Check("プレビュー: 文字が入る", withText.Count == 3 && !withText[0].AsSpan().SequenceEqual(plain[0]));
            _shotPngs = withText;
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
        Check("プレビュー（2本）: 最初は2本目の完成品", multi[0].AsSpan().SequenceEqual(only2[0]), $"{multiSeconds:0.00}秒");
        Check("プレビュー（2本）: 真ん中は1本目", multi[1].AsSpan().SequenceEqual(only1[0]));
        _previewPngs = plain;
    }

    static void Shot(string name, Window window)
    {
        window.UpdateLayout();
        var client = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(window, 0);
        int width = (int)Math.Ceiling(client.ActualWidth), height = (int)Math.Ceiling(client.ActualHeight);
        var rtb = new RenderTargetBitmap(width * 2, height * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        string b64 = Convert.ToBase64String(ms.ToArray());
        Console.WriteLine($"SHOTSIZE {name} {width}x{height} {b64.Length}");
        for (int i = 0; i < b64.Length; i += 4000)
            Console.WriteLine($"SHOT {name} {b64.Substring(i, Math.Min(4000, b64.Length - i))}");
    }

    /// <summary>プレビューの画面を実際に開いて、画が入るか・2回目は同じ画面を使い回すか・閉じられるかを確かめる。</summary>
    static void CheckPreviewWindow()
    {
        if (_window == null || _previewPngs == null)
        {
            Check("プレビューの画面", false, "前のテストが通っていないので確かめられない");
            return;
        }
        var w = _window;
        var show = typeof(MainWindow).GetMethod("ShowPreview", F)!;
        Window? Current() => (Window?)typeof(MainWindow).GetField("_previewWindow", F)!.GetValue(w);
        try
        {
            w.Show(); // 持ち主の画面が表示されていないと、プレビューの画面を開けない
            show.Invoke(w, [_previewPngs, true]);
            var preview = Current();
            var images = (Image[])typeof(MainWindow).GetField("_previewImages", F)!.GetValue(w)!;
            var labels = (TextBlock[])typeof(MainWindow).GetField("_previewLabels", F)!.GetValue(w)!;
            Check("プレビューの画面が開く",
                preview is { IsVisible: true } && images.All(i => i.Source is BitmapSource { PixelWidth: 360, PixelHeight: 640 })
                && labels[0].Text == "最初（完成品）",
                string.Join(" / ", labels.Select(l => l.Text)));
            show.Invoke(w, [_shotPngs ?? _previewPngs, true]);
            Shot("preview", Current()!);
            Shot("main", w);
            show.Invoke(w, [_previewPngs, false]);
            Check("2回目は同じ画面で画を差し替える", ReferenceEquals(preview, Current()) && w.OwnedWindows.Count == 1
                                                    && labels[0].Text == "最初");
            preview?.Close();
            Check("プレビューの画面を閉じられる", Current() == null);
            w.Close();
        }
        catch (Exception ex)
        {
            Check("プレビューの画面", false, ex.ToString());
        }
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
