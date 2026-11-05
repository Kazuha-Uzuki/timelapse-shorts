using System.Diagnostics;
using System.IO;
using TimelapseShorts;

// Windows 上で、同梱の ffmpeg とフォントを使って実際に変換し、結果を確かめる（GitHub Actions 用）。
// exe と同じフォルダに ffmpeg.exe / ffprobe.exe / fonts フォルダを置いて実行する。
static class Program
{
    static int _failures;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "OK" : "NG")}  {name}  {detail}");
        if (!ok) _failures++;
    }

    [STAThread]
    static int Main() => RunAsync().GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string work = Path.Combine(Path.GetTempPath(), "wincheck");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        // テスト素材（日本語・空白入りのファイル名で、パスの扱いも確かめる）
        string video = Path.Combine(work, "テスト 動画.mp4");
        string song = Path.Combine(work, "テスト曲.m4a");
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=s=540x960:r=30:d=30", "-c:v", "mpeg4", "-q:v", "5", video);
        await FfmpegAsync("-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "aac", song);

        string? encoder = await Ffmpeg.DetectEncoderAsync();
        Check("エンコーダー", encoder != null, encoder ?? "");
        if (encoder == null) return 1;

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
        Check("作業用フォルダが残っていない", Directory.GetDirectories(Path.GetTempPath(), "timelapse_*").Length == 0);

        Console.WriteLine(_failures == 0 ? "すべて OK" : $"NG が {_failures} 件");
        return _failures == 0 ? 0 : 1;
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
