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

    /// <summary>BGM 用。音声の長さ（秒）を返す。音声が入っていなければ例外。</summary>
    public static async Task<double> GetAudioDurationAsync(string file)
    {
        var (_, stdout, _) = await RunAsync(FfprobePath,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type:format=duration", "-of", "default=nw=1", file]);
        var lines = stdout.Split('\n', StringSplitOptions.TrimEntries);
        string? duration = lines.FirstOrDefault(l => l.StartsWith("duration="))?["duration=".Length..];
        if (!lines.Contains("codec_type=audio") || !double.TryParse(duration, NumberStyles.Float, Inv, out var d) || d <= 0)
            throw new InvalidOperationException($"音声として読み込めません: {Path.GetFileName(file)}");
        return d;
    }

    /// <summary>BGM 用。曲全体の平均音量と最大音量（dB。0 dB が音割れの境目）を測る。測れなければ NaN。</summary>
    public static async Task<(double Mean, double Peak)> GetVolumeAsync(string file)
    {
        var (_, _, stderr) = await RunAsync(FfmpegPath,
            ["-hide_banner", "-nostats", "-i", file, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-"]);
        double Find(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(stderr, key + @":\s*(-?[\d.]+) dB");
            return m.Success ? double.Parse(m.Groups[1].Value, Inv) : double.NaN;
        }
        return (Find("mean_volume"), Find("max_volume"));
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

    /// <summary>BGM。Seconds は曲の長さ（動画より短いときに繰り返す回数の計算に使う）。GainDb は音量の上げ下げ（dB）。</summary>
    public record Bgm(string File, double Seconds, double FadeIn, double FadeOut, double GainDb = 0);

    public enum TextPosition { Top, Middle, Bottom }

    /// <summary>
    /// 動画に重ねる文字。色は "RRGGBB"。
    /// FontFamily はフォントの中に書かれた Windows 用のファミリー名（libass はこの名前でフォントを探す）。
    /// Box が true なら、フチ取りの代わりに OutlineColor の帯（背景）の上に文字を描く。
    /// </summary>
    public record Caption(string Text, string FontFile, string FontFamily, int Size,
        string TextColor, string OutlineColor, TextPosition Position, bool Box = false);

    /// <summary>プレビューの1枚。Seconds は出来上がりの何秒目か。</summary>
    public record PreviewFrame(double Seconds, byte[] Png);

    /// <summary>
    /// 冒頭に持ってくる部分。元の動画（つないだもの）の Start〜End 秒を、出来上がりの最初の Seconds 秒にする
    /// （倍速は (End − Start) / Seconds。1 より小さければゆっくりになる）。
    /// Copy が false なら本編からはなくし（移す）、true なら本編にも残す（コピー）。
    /// FadeIn / FadeOut は、この部分だけのフェード（秒。0 でなし）。
    /// </summary>
    public record Intro(double Start, double End, double Seconds, bool Copy = false, double FadeIn = 0, double FadeOut = 0)
    {
        public double Speed => (End - Start) / Seconds;

        /// <summary>
        /// 「数値で：最後の N 秒」。出来上がりの本編の最後の seconds 秒にあたる部分（元の動画の終わり）を冒頭に持ってくる。
        /// 移すなら今までと同じ結果になる（完成品も本編も、全体と同じ倍速）。
        /// </summary>
        public static Intro Last(double total, double target, double seconds, bool copy = false, double fadeIn = 0, double fadeOut = 0)
        {
            double factor = copy ? Math.Max(1, total / Math.Max(1e-9, target - seconds)) : Math.Max(1, total / target);
            return new Intro(Math.Max(0, total - seconds * factor), total, seconds, copy, fadeIn, fadeOut);
        }
    }

    /// <summary>本編に使う元の動画の長さ（秒）。移すなら、冒頭に持っていった部分を除く。</summary>
    public static double MainSourceSeconds(Intro? intro, double total) =>
        intro == null || intro.Copy ? total : intro.Start + (total - intro.End);

    /// <summary>本編の倍速。本編に使う元の長さを、総尺から冒頭を引いた長さにする（元が短ければ等速）。</summary>
    public static double MainFactor(Intro? intro, double total, double target) =>
        Math.Max(1, MainSourceSeconds(intro, total) / Math.Max(1e-9, target - (intro?.Seconds ?? 0)));

    /// <summary>出来上がりの冒頭と本編のコマ数。</summary>
    static (int Intro, int Main) FrameCounts(Intro? intro, double total, double target) =>
        (intro == null ? 0 : (int)Math.Round(intro.Seconds * OutputFps),
         (int)Math.Round(MainSourceSeconds(intro, total) / MainFactor(intro, total, target) * OutputFps));

    /// <summary>出来上がりの長さ（秒）。</summary>
    public static double OutputSeconds(Intro? intro, double total, double target)
    {
        var (i, m) = FrameCounts(intro, total, target);
        return (double)(i + m) / OutputFps;
    }

    /// <summary>出来上がりの t 秒目が、元の動画（つないだもの）の何秒目か。</summary>
    public static double SourceTime(Intro? intro, double total, double target, double t)
    {
        double introOut = intro == null ? 0 : FrameCounts(intro, total, target).Intro / (double)OutputFps;
        if (intro != null && t < introOut) return intro.Start + t * intro.Speed;
        double src = (t - introOut) * MainFactor(intro, total, target);
        // 移したなら、本編は冒頭に持っていった部分を飛ばしてつながる
        return intro is { Copy: false } && src >= intro.Start ? src + (intro.End - intro.Start) : src;
    }

    /// <summary>
    /// 入力（複数なら名前順に連結）を targetSeconds 秒の縦長タイムラプスにする（元が短ければ等速のまま）。
    /// intro があれば、その部分（完成品など）を冒頭に持ってくる。
    /// bgm / caption があれば、BGM と文字を入れる。作ったファイルのパスを返す。
    /// outputDir が null なら、1本目の動画と同じフォルダに作る。
    /// </summary>
    public static async Task<string> ConvertAsync(
        IReadOnlyList<string> inputs, double totalSeconds, double targetSeconds, Intro? intro,
        Layout layout, Bgm? bgm, Caption? caption, string suffix, string encoder,
        IProgress<Progress> progress, CancellationToken ct, string? outputDir = null)
    {
        CheckIntro(intro, totalSeconds, targetSeconds);

        // 作業用フォルダ。ffmpeg をここで動かし、中のファイルは相対パスで渡す（パスの書き方の問題を避ける）
        string work = Path.Combine(Path.GetTempPath(), $"timelapse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            return await ConvertInAsync(work, inputs, totalSeconds, targetSeconds, intro, layout, bgm, caption,
                suffix, encoder, progress, ct, outputDir);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    static void CheckIntro(Intro? intro, double total, double target)
    {
        if (intro == null) return;
        if (intro.Start < 0 || intro.End > total + 0.001 || intro.End - intro.Start <= 0 || intro.Seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intro), "冒頭に持ってくる部分が、元の動画の外です");
        if (MainSourceSeconds(intro, total) <= 0 || target - intro.Seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(intro), "冒頭に持ってくると、本編がなくなります");
    }

    /// <summary>動画のプレビューにするときの指定。出来上がりの最初の Seconds 秒だけを、360×640 で Output に作る。</summary>
    sealed record Clip(double Seconds, string Output);

    /// <summary>
    /// 動画のプレビュー: 出来上がりの最初の clipSeconds 秒（完成品と、本編のはじめ）だけを、変換と同じ処理で
    /// 360×640・BGM 入りの mp4 にして output に作る。元の動画はその部分しか読まないので、全部を作成するよりずっと速い
    /// （それでも、1コマずつしか読まない静止画のプレビューよりは、PC の速さに左右される）。
    /// </summary>
    public static async Task RenderPreviewClipAsync(IReadOnlyList<string> inputs, double totalSeconds, double targetSeconds,
        Intro? intro, Layout layout, Bgm? bgm, Caption? caption,
        string encoder, double clipSeconds, string output, IProgress<Progress> progress, CancellationToken ct)
    {
        CheckIntro(intro, totalSeconds, targetSeconds);
        string work = Path.Combine(Path.GetTempPath(), $"timelapse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            await ConvertInAsync(work, inputs, totalSeconds, targetSeconds, intro, layout, bgm, caption, "", encoder,
                progress, ct, null, new Clip(clipSeconds, output));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    static async Task<string> ConvertInAsync(string work,
        IReadOnlyList<string> inputs, double totalSeconds, double targetSeconds, Intro? intro,
        Layout layout, Bgm? bgm, Caption? caption, string suffix, string encoder,
        IProgress<Progress> progress, CancellationToken ct, string? outputDir, Clip? clip = null)
    {
        string first = inputs[0];
        double factor = MainFactor(intro, totalSeconds, targetSeconds);   // 本編を何倍速にするか
        var (introFrames, mainFrames) = FrameCounts(intro, totalSeconds, targetSeconds);
        int frames = introFrames + mainFrames;                            // 出力のコマ数（総尺ぴったりで切る）
        double seconds = (double)frames / OutputFps;                      // 出力の長さ
        // 動画のプレビューなら、作るのは最初の clip.Seconds 秒だけ（BGM もその長さで切る。フェードアウトの位置は本物のまま）
        if (clip != null) frames = Math.Min(frames, (int)Math.Round(clip.Seconds * OutputFps));
        double made = (double)frames / OutputFps;                         // 実際に作る長さ
        // 本編で読む元の長さ。動画のプレビューなら、作る長さのぶん（と少しの余り）だけ
        double mainSource = MainSourceSeconds(intro, totalSeconds);
        double mainRead = clip == null ? mainSource
            : Math.Min(mainSource, (Math.Max(0, made - (double)introFrames / OutputFps) + 0.2) * factor);

        double keyInterval = await GetKeyframeIntervalAsync(first);
        string? hdr = await GetHdrTransferAsync(first);
        var durations = await DurationsAsync(inputs);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var parts = new List<string>(); // つなぐ部分（フィルター）。並べた順に出来上がりになる
        bool keyframesOnly = false;

        // 元の動画の from 秒目から length 秒（null なら終わりまで）を、speed 倍速の 30fps にする部分を足す。
        // その時刻を含むファイルから読む（つないだリストの途中へ飛ぶと、音の入っていない動画では ffmpeg の時刻の丸めの
        // せいで飛べず、先頭から読み直して遅くなることがあるため）
        async Task AddPart(double from, double? length, double speed, string extra = "")
        {
            int n = parts.Count;
            var (k, local) = Locate(durations, from);
            string[] source = await SourceArgsAsync(work, $"list{n}.txt", inputs.Skip(k).ToList(), durations.Skip(k).ToList(), ct);
            // 拾う間隔がキーフレーム間隔以上なら、キーフレームだけ読めば足りる（大幅に速い）
            bool keyOnly = keyInterval <= speed / OutputFps;
            keyframesOnly |= keyOnly && speed == factor;
            if (keyOnly) args.AddRange(["-skip_frame", "nokey"]);
            if (local > 0) args.AddRange(["-ss", Sec(local)]);
            if (length is { } t) args.AddRange(["-t", Sec(t)]);
            args.AddRange(source);
            // 時刻を speed 分の1に縮めてから 30fps で拾う（細かい時間単位で縮めるのでずれず、最後のコマも捨てない）
            parts.Add($"[{n}:v]settb=AVTB,setpts=(PTS-STARTPTS)/{speed.ToString("0.######", Inv)}," +
                      $"fps={OutputFps}:eof_action=pass{extra}");
        }

        if (intro != null)
        {
            // 冒頭の部分。コマ数ちょうどで切るので、少し長めに読む（元の動画の終わりまでなら、それ以上は読まれない）
            await AddPart(intro.Start, intro.End - intro.Start + 2 * intro.Speed / OutputFps, intro.Speed,
                $",trim=end_frame={introFrames}{IntroFades(introFrames, intro.FadeIn, intro.FadeOut)}");
        }
        if (intro is { Copy: false })
        {
            // 移したなら、本編は冒頭に持っていった部分の前と後ろをつなぐ。元の動画はどこも1回しか読まない
            if (intro.Start > 0 && mainRead > 0) await AddPart(0, Math.Min(intro.Start, mainRead), factor);
            double after = mainRead - intro.Start;
            if (intro.End < totalSeconds && after > 0) await AddPart(intro.End, clip != null ? after : null, factor);
        }
        else
        {
            await AddPart(0, clip != null ? mainRead : null, factor);
        }
        string chain = parts.Count == 1 ? parts[0]
            : string.Concat(parts.Select((p, i) => $"{p}[p{i}];")) +
              string.Concat(parts.Select((_, i) => $"[p{i}]")) + $"concat=n={parts.Count}:v=1:a=0";
        string filter = await FinishVideoAsync(work, chain, hdr, layout, caption, seconds, ct)
                        + (clip != null ? ",scale=360:640" : "") + "[v]";

        string[] audio = ["-an"];
        if (bgm != null)
        {
            // 曲が短ければ足りる回数だけ繰り返し、動画の長さで切ってからフェードをかける
            int loops = bgm.Seconds > 0 ? Math.Max(0, (int)Math.Ceiling(made / bgm.Seconds) - 1) : 0;
            args.AddRange(["-stream_loop", loops.ToString(Inv), "-i", bgm.File]);
            filter += $";[{parts.Count}:a]atrim=duration={Sec(made)},asetpts=PTS-STARTPTS";
            if (bgm.GainDb != 0) filter += $",volume={Sec(bgm.GainDb)}dB";
            // 上げたときに 0 dB を超える部分は、音割れしないよう少し手前（-0.3 dB）で抑える
            if (bgm.GainDb > 0) filter += ",alimiter=limit=0.966:level=false";
            if (bgm.FadeIn > 0) filter += $",afade=t=in:st=0:d={Sec(bgm.FadeIn)}";
            if (bgm.FadeOut > 0) filter += $",afade=t=out:st={Sec(seconds - bgm.FadeOut)}:d={Sec(bgm.FadeOut)}";
            filter += "[a]";
            audio = ["-map", "[a]", "-c:a", "aac", "-b:a", "192k"];
        }

        string output = clip?.Output ?? UniquePath(Path.Combine(outputDir ?? Path.GetDirectoryName(first)!,
            $"{Path.GetFileNameWithoutExtension(first)}_{suffix}.mp4"));
        args.AddRange(["-filter_complex", filter, "-map", "[v]", .. audio,
            "-r", OutputFps.ToString(Inv), "-frames:v", frames.ToString(Inv)]);
        args.AddRange(EncoderArgs(encoder));
        args.AddRange(["-movflags", "+faststart", "-progress", "pipe:1", "-nostats", output]);

        string stage = clip != null ? "動画のプレビューを作成中" : keyframesOnly ? "変換中（高速モード）" : "変換中";
        await RunWithProgressAsync(args, made, stage, progress, output, work, ct);
        return output;
    }

    /// <summary>
    /// 完成品の部分（frames コマ）のフェード。黒から出てきて、黒に消えてから本編につなぐ。コマ数で指定するので
    /// 本編には かからない（fade の out は、そのあとのコマも全部黒にするため、つなぐ前のこの部分だけにかける）。
    /// 文字はこのあとで動画全体に描くので、フェード中も消えない。
    /// </summary>
    static string IntroFades(int frames, double fadeIn, double fadeOut)
    {
        int fin = Math.Min(frames, (int)Math.Round(fadeIn * OutputFps));
        int fout = Math.Min(frames, (int)Math.Round(fadeOut * OutputFps));
        return (fin > 0 ? $",fade=t=in:s=0:n={fin}" : "") + (fout > 0 ? $",fade=t=out:s={frames - fout}:n={fout}" : "");
    }

    static async Task<List<double>> DurationsAsync(IReadOnlyList<string> inputs)
    {
        var list = new List<double>();
        foreach (var f in inputs) list.Add(await GetDurationAsync(f));
        return list;
    }

    /// <summary>つないだときの t 秒目が、何番目のファイルの何秒目か。</summary>
    static (int Index, double Local) Locate(IReadOnlyList<double> durations, double t)
    {
        int i = 0;
        while (i < durations.Count - 1 && t >= durations[i]) t -= durations[i++];
        return (i, t);
    }

    /// <summary>
    /// ffmpeg に渡す入力の引数を返す。ファイルが複数なら連結リスト（listName）を作業用フォルダに書く。
    /// 各ファイルの長さも書いておくと、途中の時刻へすぐ移動できる（書かないと先頭から読み進めるので遅い）。
    /// </summary>
    static async Task<string[]> SourceArgsAsync(string work, string listName, IReadOnlyList<string> files,
        IReadOnlyList<double> durations, CancellationToken ct)
    {
        if (files.Count == 1) return ["-i", files[0]];
        var sb = new StringBuilder();
        for (int i = 0; i < files.Count; i++)
            sb.Append("file '").Append(files[i].Replace('\\', '/').Replace("'", @"'\''")).Append("'\n")
              .Append("duration ").Append(durations[i].ToString("0.######", Inv)).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(work, listName), sb.ToString(), new UTF8Encoding(false), ct);
        return ["-f", "concat", "-safe", "0", "-i", listName];
    }

    /// <summary>
    /// 映像の仕上げ（HDR の色直し・縦長化・文字入れ）を chain の後ろにつなげる。変換とプレビューで同じものを使う。
    /// 文字があれば、字幕ファイルとフォントを作業用フォルダ work に用意する。
    /// </summary>
    static async Task<string> FinishVideoAsync(string work, string chain, string? hdr, Layout layout,
        Caption? caption, double seconds, CancellationToken ct)
    {
        // iPhone などの HDR 動画はそのままだと色が白っぽくなるので、普通の色（SDR）に直す
        if (hdr != null)
            chain += $",zscale=tin={hdr}:pin=bt2020:min=bt2020nc:t=bt709:p=bt709:m=bt709:npl=203:r=tv,format=yuv420p";

        string filter = layout == Layout.Crop
            ? $"{chain},crop='min(iw,ih*9/16)':'min(ih,iw*16/9)',scale=1080:1920,setsar=1"
            : $"{chain},split[a][b];" +
              // 背景は1/4解像度でぼかしてから拡大する（見た目はほぼ同じで、等倍でぼかすより約4倍速い）。
              // ぼかしは gblur（boxblur は GPL のフィルターで、同梱の LGPL 版 FFmpeg には入っていない。
              // sigma 5.5 が、前の boxblur=5:2 といちばん近い見た目）
              "[a]scale=270:480:force_original_aspect_ratio=increase,crop=270:480,gblur=sigma=5.5,scale=1080:1920[bg];" +
              "[b]scale=1080:1920:force_original_aspect_ratio=decrease[fg];" +
              "[bg][fg]overlay=(W-w)/2:(H-h)/2,setsar=1";

        // 文字は字幕ファイル（ASS）にして libass で描く。フォントは作業用フォルダにコピーして読ませる
        if (caption != null)
        {
            Directory.CreateDirectory(Path.Combine(work, "fonts"));
            File.Copy(caption.FontFile, Path.Combine(work, "fonts", Path.GetFileName(caption.FontFile)));
            await File.WriteAllTextAsync(Path.Combine(work, "caption.ass"), BuildAss(caption, seconds), new UTF8Encoding(false), ct);
            filter += ",ass=caption.ass:fontsdir=fonts";
        }
        return filter;
    }

    /// <summary>
    /// 仕上がりの「最初・真ん中・最後」の画を、変換と同じ処理で描いて PNG（360×640）で返す。
    /// 元の動画はその3か所しか読まないので、PC の性能にほとんど左右されない。
    /// </summary>
    public static async Task<List<PreviewFrame>> RenderPreviewAsync(IReadOnlyList<string> inputs, double totalSeconds,
        double targetSeconds, Intro? intro, Layout layout, Caption? caption, CancellationToken ct)
    {
        CheckIntro(intro, totalSeconds, targetSeconds);
        string work = Path.Combine(Path.GetTempPath(), $"timelapse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            double seconds = OutputSeconds(intro, totalSeconds, targetSeconds); // 出来上がりの長さ
            var durations = await DurationsAsync(inputs);
            string filter = await FinishVideoAsync(work, "[0:v]null", await GetHdrTransferAsync(inputs[0]), layout,
                caption, seconds, ct) + ",scale=360:640[v]";

            var frames = new List<PreviewFrame>();
            foreach (double t in new[] { 0, seconds / 2, seconds - 1.0 / OutputFps })
            {
                // 出来上がりの t 秒目が、元の動画の何秒目か（冒頭に持ってきた部分も考える）
                double src = SourceTime(intro, totalSeconds, targetSeconds, t);
                var (k, local) = Locate(durations, Math.Clamp(src, 0, Math.Max(0, totalSeconds - 0.1)));
                string png = Path.Combine(work, $"preview{frames.Count}.png");
                string stderr = "";
                // その時刻を含むファイルだけを開いて1コマ描く。動画の終わりぎわは映像が音より少し短くて
                // 画が無いことがあるので、そのときは2秒前を描く
                foreach (double at in new[] { local, Math.Max(0, local - 2) })
                {
                    (_, _, stderr) = await RunAsync(FfmpegPath,
                        ["-hide_banner", "-loglevel", "error", "-y", "-ss", Sec(at), "-i", inputs[k],
                         "-filter_complex", filter, "-map", "[v]", "-frames:v", "1", png], work);
                    if (File.Exists(png)) break;
                }
                if (!File.Exists(png))
                    throw new InvalidOperationException("プレビューを作れませんでした。\n" + stderr.Trim());
                frames.Add(new PreviewFrame(t, await File.ReadAllBytesAsync(png, ct)));
            }
            return frames;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>HDR 動画（iPhone などの HLG / PQ）か。そのまま再生すると色が白っぽくなる。</summary>
    public static async Task<bool> IsHdrAsync(string file) => await GetHdrTransferAsync(file) != null;

    /// <summary>
    /// 冒頭を選ぶ画面用。元の動画（つないだもの）の t 秒目の画を、高さ height の PNG で返す（HDR は普通の色に直す）。
    /// その画面で動画を再生できないとき（HEVC の拡張機能が無い・HDR など）に、バーを動かした位置の画として出す。
    /// </summary>
    public static async Task<byte[]> RenderSourceFrameAsync(IReadOnlyList<string> inputs, IReadOnlyList<double> durations,
        double t, int height, CancellationToken ct)
    {
        var (k, local) = Locate(durations, Math.Clamp(t, 0, Math.Max(0, durations.Sum() - 0.1)));
        string? hdr = await GetHdrTransferAsync(inputs[k]);
        string vf = (hdr != null
            ? $"zscale=tin={hdr}:pin=bt2020:min=bt2020nc:t=bt709:p=bt709:m=bt709:npl=203:r=tv,format=yuv420p,"
            : "") + $"scale=-2:{height}";
        string png = Path.Combine(Path.GetTempPath(), $"timelapse-frame-{Guid.NewGuid():N}.png");
        try
        {
            string stderr = "";
            // 終わりぎわは画が無いことがあるので、そのときは 2 秒前を描く（RenderPreviewAsync と同じ）
            foreach (double at in new[] { local, Math.Max(0, local - 2) })
            {
                (_, _, stderr) = await RunAsync(FfmpegPath,
                    ["-hide_banner", "-loglevel", "error", "-y", "-ss", Sec(at), "-i", inputs[k], "-vf", vf, "-frames:v", "1", png]);
                if (File.Exists(png)) return await File.ReadAllBytesAsync(png, ct);
            }
            throw new InvalidOperationException("画を作れませんでした。\n" + stderr.Trim());
        }
        finally
        {
            try { File.Delete(png); } catch { }
        }
    }

    /// <summary>文字入れ用の字幕ファイル（ASS）を作る。画面は 1080×1920、動画の最初から最後まで表示する。</summary>
    public static string BuildAss(Caption c, double seconds)
    {
        static string Bgr(string rgb) => $"&H00{rgb[4..6]}{rgb[2..4]}{rgb[0..2]}"; // ASS の色は 青緑赤 の順
        // 上下は、SNS の画面上部のタブや下部のボタン・説明文に重なりにくい位置にする
        (int align, int marginV) = c.Position switch
        {
            TextPosition.Top => (8, 280),
            TextPosition.Middle => (5, 0),
            _ => (2, 600),
        };
        // 背景なし: フチ取り＋影。背景あり: OutlineColor の帯（ASS の BorderStyle 3。このとき Outline は帯の余白）
        (int border, int outline, int shadow) = c.Box
            ? (3, Math.Max(6, c.Size / 5), 0)
            : (1, Math.Max(2, c.Size / 12), Math.Max(1, c.Size / 40));
        // { } \ は ASS では特別な意味があるので全角に置き換え、改行は \N にする
        string text = c.Text.Trim().Replace("\\", "＼").Replace("{", "｛").Replace("}", "｝")
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\N");
        var end = TimeSpan.FromSeconds(Math.Ceiling(seconds) + 1);
        return $"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1080
            PlayResY: 1920
            WrapStyle: 0
            ScaledBorderAndShadow: yes

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Caption,{c.FontFamily.Replace(",", "")},{c.Size},{Bgr(c.TextColor)},&H000000FF,{Bgr(c.OutlineColor)},&H80000000,0,0,0,0,100,100,0,0,{border},{outline},{shadow},{align},60,60,{marginV},1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,{(int)end.TotalHours}:{end:mm\:ss}.00,Caption,,0,0,0,,{text}

            """;
    }

    static string Sec(double seconds) => seconds.ToString("0.###", Inv);

    static async Task RunWithProgressAsync(List<string> args, double expectedSeconds, string stage,
        IProgress<Progress> progress, string outputFile, string workDir, CancellationToken ct)
    {
        var psi = NewStartInfo(FfmpegPath, args);
        psi.WorkingDirectory = workDir;
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

    static async Task<(int code, string stdout, string stderr)> RunAsync(string exe, IEnumerable<string> args,
        string? workDir = null)
    {
        var psi = NewStartInfo(exe, args);
        if (workDir != null) psi.WorkingDirectory = workDir;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} を起動できません");
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o, await e);
    }
}
