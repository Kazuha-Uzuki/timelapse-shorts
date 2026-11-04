using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace TimelapseShorts;

/// <summary>FFmpeg / FFprobe（exe と同じフォルダに同梱）の呼び出しをまとめたもの。</summary>
public static class Ffmpeg
{
    public const int OutputFps = 30;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Exe(string name) => Path.Combine(AppContext.BaseDirectory, name);
    public static string FfmpegPath => Exe("ffmpeg.exe");
    public static string FfprobePath => Exe("ffprobe.exe");
    public static bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    // GPU → OS標準 の順に試す。どれもLGPL版FFmpegで使えるもの。
    static readonly string[] EncoderCandidates = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libopenh264"];

    public static string EncoderLabel(string enc) => enc switch
    {
        "h264_nvenc" => "NVIDIA GPU (NVENC)",
        "h264_qsv" => "Intel GPU (Quick Sync)",
        "h264_amf" => "AMD GPU (AMF)",
        "h264_mf" => "Windows標準 (Media Foundation)",
        "libopenh264" => "CPU (OpenH264)",
        _ => enc,
    };

    static string[] EncoderArgs(string enc)
    {
        string[] rate = ["-b:v", "10M", "-maxrate", "14M", "-bufsize", "20M"];
        string[] specific = enc switch
        {
            "h264_nvenc" => ["-preset", "p5", "-pix_fmt", "nv12"],
            "h264_qsv" => ["-preset", "medium", "-pix_fmt", "nv12"],
            "h264_amf" => ["-quality", "quality", "-pix_fmt", "nv12"],
            "h264_mf" => ["-pix_fmt", "nv12"],
            _ => ["-pix_fmt", "yuv420p"],
        };
        return ["-c:v", enc, .. specific, .. rate];
    }

    /// <summary>このPCで実際に動くエンコーダーを、1コマ試し書きして選ぶ。</summary>
    public static async Task<string?> DetectEncoderAsync()
    {
        foreach (var enc in EncoderCandidates)
        {
            var (code, _, _) = await RunAsync(FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=black:s=640x360:r=30:d=0.2",
                .. EncoderArgs(enc), "-f", "null", "-",
            ]);
            if (code == 0) return enc;
        }
        return null;
    }

    public static async Task<double> GetDurationAsync(string file)
    {
        var (code, stdout, stderr) = await RunAsync(FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]);
        if (code != 0 || !double.TryParse(stdout.Trim(), NumberStyles.Float, Inv, out var d))
            throw new InvalidOperationException($"動画として読み込めません: {Path.GetFileName(file)}\n{stderr.Trim()}");
        return d;
    }

    /// <summary>先頭60秒だけ見て、キーフレームの平均間隔（秒）を返す。測れなければ +∞。</summary>
    static async Task<double> GetKeyframeIntervalAsync(string file)
    {
        var (_, stdout, _) = await RunAsync(FfprobePath,
        [
            "-v", "error", "-skip_frame", "nokey", "-select_streams", "v:0",
            "-show_entries", "frame=pts_time", "-read_intervals", "%+60", "-of", "csv=p=0", file,
        ]);
        var times = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(',')[0].Trim())
            .Select(s => double.TryParse(s, NumberStyles.Float, Inv, out var t) ? t : double.NaN)
            .Where(t => !double.IsNaN(t)).ToList();
        if (times.Count < 2) return double.PositiveInfinity;
        return (times[^1] - times[0]) / (times.Count - 1);
    }

    /// <summary>HDR 動画（HLG / PQ）なら、その方式名を返す。普通の動画なら null。</summary>
    static async Task<string?> GetHdrTransferAsync(string file)
    {
        var (_, stdout, _) = await RunAsync(FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=color_transfer", "-of", "csv=p=0", file]);
        string trc = stdout.Split('\n')[0].Trim().TrimEnd(',');
        return trc is "arib-std-b67" or "smpte2084" ? trc : null;
    }

    public enum Layout { Crop, Blur }

    public record Progress(string Stage, double Fraction, TimeSpan? Remaining);

    /// <summary>
    /// 入力（複数なら名前順に連結）を targetSeconds 秒の縦長タイムラプスにする（元が短ければ等速のまま）。
    /// introSeconds が 0 より大きければ、タイムラプスの最後の introSeconds 秒（完成品）を冒頭に移動する。
    /// 作ったファイルのパスを返す。
    /// </summary>
    public static async Task<string> ConvertAsync(
        IReadOnlyList<string> inputs, double totalSeconds, double targetSeconds, double introSeconds,
        Layout layout, string suffix, string encoder, IProgress<Progress> progress, CancellationToken ct)
    {
        string first = inputs[0];
        double factor = Math.Max(1, totalSeconds / targetSeconds);       // 何倍速にするか
        int frames = (int)Math.Round(totalSeconds / factor * OutputFps);  // 出力のコマ数（総尺ぴったりで切る）
        double cut = totalSeconds - introSeconds * factor;                // 元動画で「完成品」が始まる位置
        if (introSeconds > 0 && cut <= 0)
            throw new ArgumentOutOfRangeException(nameof(introSeconds), "冒頭に移動する秒数が総尺以上です");

        // 拾う間隔がキーフレーム間隔以上なら、キーフレームだけ読めば足りる（大幅に速い）
        bool keyframesOnly = await GetKeyframeIntervalAsync(first) <= factor / OutputFps;
        string? hdr = await GetHdrTransferAsync(first);

        string? listFile = null;
        string[] source = ["-i", first];
        if (inputs.Count > 1)
        {
            listFile = Path.Combine(Path.GetTempPath(), $"timelapse_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var f in inputs)
                sb.Append("file '").Append(f.Replace('\\', '/').Replace("'", @"'\''")).Append("'\n");
            await File.WriteAllTextAsync(listFile, sb.ToString(), new UTF8Encoding(false), ct);
            source = ["-f", "concat", "-safe", "0", "-i", listFile];
        }
        string[] skip = keyframesOnly ? ["-skip_frame", "nokey"] : [];
        // 時刻を factor 分の1に縮めてから 30fps で拾う（細かい時間単位で縮めるのでずれず、最後のコマも捨てない）
        string pick = $"settb=AVTB,setpts=(PTS-STARTPTS)/{factor.ToString("0.######", Inv)},fps={OutputFps}:eof_action=pass";

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        string chain;
        if (introSeconds > 0)
        {
            // 最後（完成品）とそれ以外を別々に読み、完成品を先にしてつなぐ。元動画はどこも1回しか読まない
            args.AddRange([.. skip, "-ss", Sec(cut), .. source]);
            args.AddRange([.. skip, "-t", Sec(cut), .. source]);
            int introFrames = (int)Math.Round(introSeconds * OutputFps);
            chain = $"[0:v]{pick},trim=end_frame={introFrames}[end];[1:v]{pick}[main];[end][main]concat=n=2:v=1:a=0";
        }
        else
        {
            args.AddRange([.. skip, .. source]);
            chain = $"[0:v]{pick}";
        }
        // iPhone などの HDR 動画はそのままだと色が白っぽくなるので、普通の色（SDR）に直す
        if (hdr != null)
            chain += $",zscale=tin={hdr}:pin=bt2020:min=bt2020nc:t=bt709:p=bt709:m=bt709:npl=203:r=tv,format=yuv420p";

        string filter = layout == Layout.Crop
            ? $"{chain},crop='min(iw,ih*9/16)':'min(ih,iw*16/9)',scale=1080:1920,setsar=1[v]"
            : $"{chain},split[a][b];" +
              // 背景は1/4解像度でぼかしてから拡大する（見た目はほぼ同じで、等倍でぼかすより約4倍速い）
              "[a]scale=270:480:force_original_aspect_ratio=increase,crop=270:480,boxblur=5:2,scale=1080:1920[bg];" +
              "[b]scale=1080:1920:force_original_aspect_ratio=decrease[fg];" +
              "[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1[v]";

        string output = UniquePath(Path.Combine(Path.GetDirectoryName(first)!,
            $"{Path.GetFileNameWithoutExtension(first)}_{suffix}.mp4"));
        args.AddRange(["-filter_complex", filter, "-map", "[v]", "-an",
            "-r", OutputFps.ToString(Inv), "-frames:v", frames.ToString(Inv)]);
        args.AddRange(EncoderArgs(encoder));
        args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);

        try
        {
            string stage = keyframesOnly ? "変換中（高速モード）" : "変換中";
            await RunWithProgressAsync(args, (double)frames / OutputFps, stage, progress, output, ct);
        }
        finally
        {
            if (listFile != null) File.Delete(listFile);
        }
        return output;
    }

    static string Sec(double seconds) => seconds.ToString("0.###", Inv);

    static async Task RunWithProgressAsync(List<string> args, double expectedSeconds, string stage,
        IProgress<Progress> progress, string outputFile, CancellationToken ct)
    {
        var psi = NewStartInfo(FfmpegPath, args);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg を起動できません");
        var stderrTask = p.StandardError.ReadToEndAsync();
        var sw = Stopwatch.StartNew();
        progress.Report(new Progress(stage, 0, null));

        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("out_time_us=")) continue;
            if (!long.TryParse(line["out_time_us=".Length..], out var us) || us <= 0) continue;
            double frac = Math.Clamp(us / 1e6 / expectedSeconds, 0, 1);
            TimeSpan? remain = frac > 0.01
                ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - frac) / frac)
                : null;
            progress.Report(new Progress(stage, frac, remain));
        }
        await p.WaitForExitAsync(CancellationToken.None);
        string stderr = await stderrTask;

        if (ct.IsCancellationRequested || p.ExitCode != 0)
        {
            try { File.Delete(outputFile); } catch { }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("変換に失敗しました。\n" + stderr.Trim());
        }
        progress.Report(new Progress(stage, 1, TimeSpan.Zero));
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    static ProcessStartInfo NewStartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    static async Task<(int code, string stdout, string stderr)> RunAsync(string exe, IEnumerable<string> args)
    {
        using var p = Process.Start(NewStartInfo(exe, args)) ?? throw new InvalidOperationException($"{exe} を起動できません");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o, await e);
    }
}
