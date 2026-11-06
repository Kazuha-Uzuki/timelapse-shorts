using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TimelapseShorts;

// Windows 上で、同梱の ffmpeg とフォントを使って実際に変換し、結果を確かめる（GitHub Actions 用）。
// exe と同じフォルダに ffmpeg.exe / ffprobe.exe / fonts フォルダを置いて実行する。
static class Program
{
    const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
    static int _failures;
    static MainWindow? _window;        // CheckWindow で作った画面（最後にプレビューの表示も確かめる）
    static List<Ffmpeg.PreviewFrame>? _previewFrames; // RunAsync で作ったプレビューの画
    static string? _clipPath;                           // RunAsync で作った動画のプレビュー
    // 設定画面で選ぶ「決まったフォルダ」（テストのあいだだけ使う）
    static readonly string OutputFolder = Path.Combine(Path.GetTempPath(), "wincheck 保存先");
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
        // 本物のアプリと同じく、画面のスレッドで await したら続きも画面のスレッドで動くようにする（Application.Run を
        // しないので自分で入れる。入れないと、① の最初の画などの続きが別のスレッドで動き、画面に触れて落ちる）
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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

    /// <summary>画面を実際に作り（表示はしない）、初期状態・手順・② の連動・入力チェックを確かめる。</summary>
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
        var intro = w.Intro;
        void IntroCall(string method, params object?[] args) => typeof(IntroPanel).GetMethod(method, F)!.Invoke(intro, args);
        void IntroSet(string field, object value) => typeof(IntroPanel).GetField(field, F)!.SetValue(intro, value);
        string Range() => $"{intro.Choice.Start:0.###}〜{intro.Choice.End:0.###}・{intro.SecondsBox.Text} 秒";
        var click = new RoutedEventArgs();

        Check("フォント一覧（★が先頭）", w.FontBox.Items.Count > 9 && w.FontBox.Items[0]!.ToString()!.StartsWith('★'),
            $"{w.FontBox.Items.Count} 個 / 先頭 {w.FontBox.Items[0]}");
        Check("色と位置の選択肢（色は 4 つのセットと「自由に選ぶ（RGB）」）", w.ColorBox.Items.Count == 5
            && w.ColorBox.Items[4]!.ToString() == "自由に選ぶ（RGB）" && w.PositionBox.Items.Count == 4
            && w.PositionBox.Items[3]!.ToString() == "自由（ドラッグ）" && w.CustomColorBox.Visibility == Visibility.Collapsed
            && w.PositionXY.Visibility == Visibility.Collapsed);
        Check("総尺の初期値", w.LengthBox.Text == "2:59", w.LengthBox.Text);
        Check("最初は ① 動画の画面", w.VideoStep.Visibility == Visibility.Visible && w.Intro.Visibility == Visibility.Collapsed
            && w.MakeStep.Visibility == Visibility.Collapsed && w.SourcePicture.Visibility == Visibility.Visible
            && w.PreviewArea.Visibility == Visibility.Collapsed && w.StepVideo.IsChecked == true
            && w.FilesTotalText.Text == "（まだ入れていません）" && w.InfoText.Text == "① で動画を入れてください", w.InfoText.Text);

        // 冒頭の秒数と範囲の連動（冒頭は本編と同じ速さ）
        Check("連動の計算: 秒数⇄元の動画の長さ・上限",
            Math.Abs(IntroPanel.SourceLength(5, 3600, 179, false) - 5 * 3600 / 179.0) < 1e-9
            && Math.Abs(IntroPanel.SecondsFor(IntroPanel.SourceLength(7.3, 3600, 179, true), 3600, 179, true) - 7.3) < 1e-9
            && Math.Abs(IntroPanel.SecondsFor(IntroPanel.SourceLength(3, 20, 60, true), 20, 60, true) - 3) < 1e-9
            && IntroPanel.MaxSeconds(3600, 179, false) == 178 && IntroPanel.MaxSeconds(3600, 179, true) == 89.5
            && IntroPanel.MaxSeconds(60, 5, false) == 4 && IntroPanel.MaxSeconds(20, 60, false) == 19,
            $"上限 {IntroPanel.MaxSeconds(3600, 179, false)} / {IntroPanel.MaxSeconds(3600, 179, true)} / {IntroPanel.MaxSeconds(60, 5, false)}");

        // 1時間の動画（スマホが2つに分けたもの）を入れた状態にする
        string[] files = [@"C:\動画\作業 2026-11-01 (1).mp4", @"C:\動画\作業 2026-11-01 (2).mp4"];
        Get<List<string>>("_files").AddRange(files);
        foreach (var f in files) Get<Dictionary<string, double>>("_durations")[f] = 1800;
        Set("_encoder", "h264_mf");
        Call("RefreshList");
        Check("基本の表示", w.InfoText.Text == "合計 1:00:00 → 2:59（20.1倍速）" && w.StartButton.IsEnabled
                            && w.PreviewButton.IsEnabled && w.PreviewVideoButton.IsEnabled && w.FilesList.Items.Count == 2
                            && w.FilesTotalText.Text == "2本・合計 1:00:00（名前順につなげて1本にします）",
            $"{w.InfoText.Text} / {w.FilesTotalText.Text}");
        Check("済んだ手順に ✓", w.StepLabel(0) == "① 動画" && w.StepLabel(1) == "② 冒頭 ✓"
                                && w.StepLabel(2) == "③ BGM" && w.StepLabel(3) == "④ テキスト",
            $"{w.StepLabel(0)} / {w.StepLabel(1)} / {w.StepLabel(2)} / {w.StepLabel(3)}");
        Set("_encoder", null);
        Refresh();
        Check("エンコーダーが無くてもプレビューはできる（動画のプレビューは動画を作るのでできない）",
            w.PreviewButton.IsEnabled && !w.StartButton.IsEnabled && !w.PreviewVideoButton.IsEnabled);
        Set("_encoder", "h264_mf");
        Refresh();

        // ② 冒頭: 最初は「最後の 5 秒を移す」。60 分を 2:59 に → 20.1 倍速なので、元の動画の最後の 1分41秒
        double speed = 3600 / 179.0;
        var c = intro.Choice;
        Check("冒頭の初期値（最後の 5 秒を移す・フェード 0.3 秒・BGM のフェード 0.5 秒）",
            c is { Enabled: true, Seconds: "5", Copy: false, FadeIn: "0.3", FadeOut: "0.3", End: 3600 }
            && Math.Abs(c.Start - (3600 - 5 * speed)) < 1e-6 && w.FadeInBox.Text == "0.5" && w.FadeOutBox.Text == "0.5"
            && intro.OverallBar.SelStart == c.Start && intro.OverallBar.SelEnd == 3600
            && intro.RangeText.Text == "選んだ所 58:19〜1:00:00（1分41秒・20.1 倍速）"
            && intro.OrderIntroText.Text == "冒頭 5秒" && intro.OrderMainText.Text == "本編 2:54（選んだ所は入らない・20.1倍速）",
            $"{Range()} / {intro.RangeText.Text} / {intro.OrderIntroText.Text} / {intro.OrderMainText.Text}");
        intro.SecondsBox.Text = "10";
        Check("秒数を変えるとバーが変わる（終わりはそのまま）", Math.Abs(intro.Choice.Start - (3600 - 10 * speed)) < 1e-6
            && intro.Choice.End == 3600 && intro.OverallBar.SelStart == intro.Choice.Start, Range());
        intro.EditRange(1000, 1000 + 7 * speed + 0.04); // はじめを 1000 秒にして、7 秒ぶんより少しだけ長く
        Check("バーで範囲を変えると秒数が変わる（0.1 秒刻み）", intro.SecondsBox.Text == "7" && intro.Choice.Start == 1000
            && Math.Abs(intro.Choice.End - (1000 + 7 * speed)) < 1e-6, Range());
        intro.EditRange(2000, 1800); // 終わりを 2000 秒にして、はじめを前へ（200 秒 → 9.9 秒）
        Check("逆向きにドラッグ（終わりはそのまま）", intro.SecondsBox.Text == "9.9" && intro.Choice.End == 2000
            && Math.Abs(intro.Choice.Start - (2000 - 9.9 * speed)) < 1e-6, Range());
        intro.MoveRange(500);
        Check("範囲ごとずらす（長さはそのまま）", intro.SecondsBox.Text == "9.9" && intro.Choice.Start == 500
            && Math.Abs(intro.Choice.End - (500 + 9.9 * speed)) < 1e-6, Range());
        intro.MoveRange(3590);
        Check("ずらしても元の動画からはみ出さない", intro.Choice.End == 3600 && intro.SecondsBox.Text == "9.9", Range());
        IntroSet("_pos", 3000.0);
        IntroCall("FromButton_Click", intro.FromButton, click);
        double start = 3600 - 29.8 * speed;
        Check("「ここから」: 終わりはそのままで、はじめを今の位置に", intro.Choice.End == 3600 && intro.SecondsBox.Text == "29.8"
            && Math.Abs(intro.Choice.Start - start) < 1e-6, Range());
        IntroSet("_pos", 3300.0);
        IntroCall("ToButton_Click", intro.ToButton, click);
        Check("「ここまで」: はじめはそのままで、終わりを今の位置に", intro.SecondsBox.Text == "14.9"
            && Math.Abs(intro.Choice.Start - start) < 1e-6 && Math.Abs(intro.Choice.End - (start + 14.9 * speed)) < 1e-6, Range());
        IntroCall("LastButton_Click", intro.LastButton, click);
        Check("「最後に合わせる」", intro.Choice.End == 3600 && intro.SecondsBox.Text == "14.9"
            && Math.Abs(intro.Choice.Start - (3600 - 14.9 * speed)) < 1e-6, Range());
        intro.SecondsBox.Text = "5";
        intro.CopyRadio.IsChecked = true; // コピー: 本編は 60 分全部を 2:54 に（20.7 倍速）。冒頭も同じ速さ
        Check("コピーにすると、秒数はそのままで範囲の長さが変わる", intro.SecondsBox.Text == "5" && intro.Choice.End == 3600
            && Math.Abs(intro.Choice.Start - (3600 - 5 * 3600 / 174.0)) < 1e-6
            && intro.OrderMainText.Text == "本編 2:54（選んだ所も入る・20.7倍速）", $"{Range()} / {intro.OrderMainText.Text}");
        intro.MoveRadio.IsChecked = true;
        ((RadioButton)w.PlatformPanel.Children[2]).IsChecked = true; // TikTok（最大 10:00）
        w.LengthBox.Text = "9:59"; // 6 倍速
        Check("総尺を変えると、秒数はそのままで範囲の長さが変わる", intro.SecondsBox.Text == "5" && intro.Choice.End == 3600
            && Math.Abs(intro.Choice.Start - (3600 - 5 * 3600 / 599.0)) < 1e-6, Range());
        ((RadioButton)w.PlatformPanel.Children[0]).IsChecked = true; // YouTube ショート（2:59 に戻る）
        Check("投稿先のボタンは横に 4 つ", w.PlatformPanel.Columns == 4 && w.PlatformPanel.Children.Count == 4 && w.LengthBox.Text == "2:59");

        intro.SecondsBox.Text = "0";
        Check("冒頭の長さが 0 秒なら作れない", w.InfoText.Text == "冒頭の長さは 1 秒以上にしてください" && !w.StartButton.IsEnabled,
            w.InfoText.Text);
        intro.SecondsBox.Text = "500";
        Check("冒頭の長さの上限（移すなら総尺 − 1 秒）", w.InfoText.Text == "冒頭の長さは 1〜178 秒にしてください（移すとき）"
                                                    && !w.StartButton.IsEnabled, w.InfoText.Text);
        intro.SecondsBox.Text = "5";
        intro.FadeInBox.Text = "3";
        intro.FadeOutBox.Text = "2.5"; // 合計 5.5 秒は、冒頭の 5 秒より長い
        Check("完成品のフェードが長すぎると作れない", w.InfoText.Text.StartsWith("完成品のフェード") && !w.StartButton.IsEnabled,
            w.InfoText.Text);
        intro.IntroCheck.IsChecked = false;
        Check("冒頭に持ってこないなら、冒頭の設定は触れない", w.StartButton.IsEnabled && !intro.SecondsBox.IsEnabled
            && !intro.LastButton.IsEnabled && w.InfoText.Text == "合計 1:00:00 → 2:59（20.1倍速）"
            && w.SumIntroText.Text.EndsWith("・冒頭なし") && intro.OrderMainText.Text == "本編 2:59（20.1倍速）",
            $"{w.InfoText.Text} / {w.SumIntroText.Text} / {intro.OrderMainText.Text}");
        intro.IntroCheck.IsChecked = true;
        intro.FadeInBox.Text = "0.3";
        intro.FadeOutBox.Text = "０．３"; // 全角でも読める
        Check("完成品のフェード（全角）", w.StartButton.IsEnabled, w.InfoText.Text);
        intro.FadeOutBox.Text = "0.3";
        intro.MoveRange(1000);
        Call("RefreshList");
        Check("動画を入れ替えたら、範囲は「最後の ○ 秒」から選び直す", intro.Choice.End == 3600 && intro.SecondsBox.Text == "5", Range());
        Check("⑤ のまとめ（① 動画・② 冒頭・保存先）",
            w.SumVideoText.Text == "2本・合計 1:00:00・横向きは中央を切り抜く"
            && w.SumIntroText.Text == "YouTube ショート 2:59（20.1倍速）・58:19〜1:00:00 を冒頭 5 秒に・移す・フェード 0.3／0.3 秒"
            && w.SaveText.Text == "元の動画と同じフォルダに「作業 2026-11-01 (1)_youtube.mp4」を作ります（「設定」で、決まったフォルダにもできます）",
            $"{w.SumVideoText.Text} / {w.SumIntroText.Text} / {w.SaveText.Text}");

        w.TextCheck.IsChecked = true;
        Check("テキストが空なら作れない", w.InfoText.Text == "入れるテキストを入力してください" && !w.StartButton.IsEnabled
                                      && !w.PreviewButton.IsEnabled, w.InfoText.Text);
        w.CaptionBox.Text = "完成まで3時間";
        Check("テキストあり", w.InfoText.Text.EndsWith("・テキストあり") && w.SampleText.Text == "完成まで3時間"
                              && w.SumTextText.Text.StartsWith("完成まで3時間・★") && w.StepLabel(3) == "④ テキスト ✓",
            $"{w.InfoText.Text} / {w.SumTextText.Text}");
        w.SizeBox.Text = "10";
        Check("文字の大きさの範囲", w.InfoText.Text.StartsWith("文字の大きさ") && !w.StartButton.IsEnabled, w.InfoText.Text);
        w.SizeBox.Text = "８０"; // 全角でも読める
        Check("全角の数字", w.StartButton.IsEnabled, w.InfoText.Text);

        // 色の「自由に選ぶ（RGB）」: 選ぶと円形のパレット（色の輪と明るさ）が出て、最初はそれまでのセットの色から始まる
        var colors = w.CustomColors;
        (string, string) CaptionColors() => ((string, string))typeof(MainWindow).GetMethod("CaptionColors", F)!.Invoke(w, null)!;
        bool SampleIs(Brush? brush, string hex) => brush is SolidColorBrush { Color: var shown } && ColorPanel.Hex(shown) == hex;
        // 輪の上の印（今の色の所）が、その色の色合い・鮮やかさの所にあるか
        bool MarkerAt(string hex)
        {
            var (h, sat, _) = ColorPanel.ToHsv(ColorPanel.Parse(hex)!.Value);
            var (x, y) = ColorPanel.WheelPoint(h, sat);
            return Math.Abs(Canvas.GetLeft(colors.Marker) + colors.Marker.Width / 2 - x) < 1.5
                   && Math.Abs(Canvas.GetTop(colors.Marker) + colors.Marker.Height / 2 - y) < 1.5;
        }
        w.ColorBox.SelectedIndex = 2; // 黄（黒フチ）
        w.ColorBox.SelectedIndex = 4; // 自由に選ぶ（RGB）
        Check("色を自由に選ぶ: 円形のパレットが出て、それまでのセット（黄（黒フチ））の色から始まる",
            w.CustomColorBox.Visibility == Visibility.Visible && colors.TextColor == "FFE600" && colors.OutlineColor == "000000"
            && colors.HexBox.Text == "FFE600" && colors.RBox.Text == "255" && colors.GBox.Text == "230" && colors.BBox.Text == "0"
            && colors.BrightSlider.Value == 100 && colors.WheelImage.Source != null && MarkerAt("FFE600"),
            $"{colors.TextColor} / {colors.OutlineColor} / {colors.HexBox.Text} / 明るさ {colors.BrightSlider.Value}");
        var red = colors.Swatches.Children.OfType<Button>().First(b => (string)b.ToolTip == "#FF3B30");
        red.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check("色を自由に選ぶ: 見本の色をクリックすると文字の色になり、見本・まとめ・変換に渡す色も変わる",
            colors.TextColor == "FF3B30" && colors.OutlineColor == "000000" && SampleIs(w.SampleText.Foreground, "FF3B30")
            && CaptionColors() == ("FF3B30", "000000") && w.SumTextText.Text.Contains("・文字 #FF3B30／フチ #000000・"),
            w.SumTextText.Text);
        colors.OutlineTarget.IsChecked = true; // フチ・帯の色を変える
        colors.RBox.Text = "２８"; // 全角でも読める
        colors.GBox.Text = "61";
        colors.BBox.Text = "122";
        Check("色を自由に選ぶ: フチの色を R/G/B の数値で（明るさ・輪の印も合う）", colors.OutlineColor == "1C3D7A"
            && colors.TextColor == "FF3B30" && colors.BrightSlider.Value == 48 && colors.HexBox.Text == "1C3D7A" && colors.RBox.Text == "28"
            && MarkerAt("1C3D7A"), $"{colors.TextColor} / {colors.OutlineColor} / 明るさ {colors.BrightSlider.Value}");
        double r = ColorPanel.WheelSize / 2;
        colors.PickWheel(new Point(r, 0)); // 輪のいちばん上
        Check("色を自由に選ぶ: 輪のいちばん上は赤（明るさはそのまま 48%）", colors.OutlineColor == "7A0000" && MarkerAt("FF0000"),
            colors.OutlineColor);
        colors.BrightSlider.Value = 100;
        Check("色を自由に選ぶ: 明るさを上げると、数値も変わる", colors.OutlineColor == "FF0000" && colors.RBox.Text == "255"
            && colors.BrightText.Text == "100%", colors.OutlineColor);
        var (gx, gy) = ColorPanel.WheelPoint(120, 1);
        colors.PickWheel(new Point(gx, gy)); // 右下（上から右回りに 120°）
        Check("色を自由に選ぶ: 輪の右下（120°）は緑", colors.OutlineColor == "00FF00", colors.OutlineColor);
        colors.PickWheel(new Point(r, r)); // 真ん中
        Check("色を自由に選ぶ: 輪の真ん中は白", colors.OutlineColor == "FFFFFF", colors.OutlineColor);
        colors.BrightSlider.Value = 0;
        Check("色を自由に選ぶ: 明るさ 0 は黒", colors.OutlineColor == "000000" && colors.BrightText.Text == "0%", colors.OutlineColor);
        colors.PickWheel(new Point(r, 0));
        Check("色を自由に選ぶ: 黒のまま輪を選ぶと、いちばん明るくなる", colors.OutlineColor == "FF0000" && colors.BrightSlider.Value == 100,
            colors.OutlineColor);
        colors.HexBox.Text = "#ff5fa2"; // # つき・小文字でも
        colors.RBox.Text = "300"; // 0〜255 でなければ変えない
        Check("色を自由に選ぶ: #RRGGBB で（読めない数は無視）", colors.OutlineColor == "FF5FA2", colors.OutlineColor);
        w.BackBoxRadio.IsChecked = true;
        Check("色を自由に選ぶ: 背景ありなら、フチの色が帯の色", w.SumTextText.Text.Contains("・文字 #FF3B30／帯 #FF5FA2・")
            && SampleIs(w.SampleBorder.Background, "FF5FA2"), w.SumTextText.Text);
        w.BackNoneRadio.IsChecked = true;
        w.ColorBox.SelectedIndex = 0;
        Check("色を自由に選ぶ: セットに戻すとパレットは隠れ、セットの色になる", w.CustomColorBox.Visibility == Visibility.Collapsed
            && SampleIs(w.SampleText.Foreground, "FFFFFF") && CaptionColors() == ("FFFFFF", "000000"));
        w.ColorBox.SelectedIndex = 4;
        Check("色を自由に選ぶ: もう一度選ぶと、自分で選んだ色のまま", colors.TextColor == "FF3B30" && colors.OutlineColor == "FF5FA2"
            && w.CustomColorBox.Visibility == Visibility.Visible);
        colors.TextTarget.IsChecked = true;
        Check("色を自由に選ぶ: ASS（字幕）の色は 青緑赤 の順", Ffmpeg.BuildAss(new Ffmpeg.Caption("あ", "", "Arial", 80, "FF3B30", "FF5FA2",
            Ffmpeg.TextPosition.Top), 5).Contains(",&H00303BFF,&H000000FF,&H00A25FFF,"));

        Set("_bgmFile", @"C:\dummy\曲.mp3");
        Set("_bgmSeconds", 120.0);
        Call("ShowBgm");
        Refresh();
        Check("BGMあり", w.InfoText.Text.Contains("・BGMあり") && w.FadeInBox.IsEnabled && w.StepLabel(2) == "③ BGM ✓"
                         && w.SumBgmText.Text == "♪ 曲.mp3（2:00）・±0 dB・フェード 0.5／0.5 秒", $"{w.InfoText.Text} / {w.SumBgmText.Text}");
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

        // 手順の切り替え: 左の画は ① が元の動画、③④⑤ がプレビュー。② は左右いっぱい
        w.ShowStep(2);
        bool bgm = w.BgmStep.Visibility == Visibility.Visible && w.PreviewArea.Visibility == Visibility.Visible
                   && w.SourcePicture.Visibility == Visibility.Collapsed && w.VideoStep.Visibility == Visibility.Collapsed
                   && w.StepBgm.IsChecked == true && w.StepLabel(2) == "③ BGM";
        w.ShowStep(1);
        bool introShown = w.Intro.Visibility == Visibility.Visible && w.PreviewArea.Visibility == Visibility.Collapsed
                          && w.SourcePicture.Visibility == Visibility.Collapsed && w.StepLabel(2) == "③ BGM ✓";
        w.ShowStep(0);
        Check("手順の切り替え", bgm && introShown && w.VideoStep.Visibility == Visibility.Visible && w.StepVideo.IsChecked == true);
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

        // 完成品を冒頭に（1秒＝30コマ）＋フェード 0.3 秒（9コマ）: 最初は黒から明るく、完成品の終わりで暗くなり、
        // 本編（30コマ目から）は元の明るさ。フェード 0 なら最初から明るい
        var faded = await LumaAsync(await ConvertAsync(video, total, encoder, "introfade", null, null, intro: 1,
            introFadeIn: 0.3, introFadeOut: 0.3));
        var unfaded = await LumaAsync(await ConvertAsync(video, total, encoder, "intronofade", null, null, intro: 1));
        double bright = faded.Count > 30 ? faded[15] : double.NaN;
        Check("完成品のフェード: 黒から出てきて、黒に消えてから本編",
            faded.Count >= 150 && faded[0] < 30 && faded[4] < bright && bright > 60 && faded[29] < bright * 0.5
            && faded[30] > bright * 0.8 && faded.Skip(30).All(y => y > 40),
            string.Join(" ", new[] { 0, 4, 8, 15, 21, 29, 30, 60 }.Where(i => i < faded.Count).Select(i => $"{i}:{faded[i]:0}")));
        Check("完成品のフェード 0 秒: 最初から明るい", unfaded.Count >= 150 && unfaded[0] > 60 && unfaded[29] > 60,
            string.Join(" ", new[] { 0, 29, 30 }.Where(i => i < unfaded.Count).Select(i => $"{i}:{unfaded[i]:0}")));

        // 保存先を決まったフォルダにすると、元の動画の横ではなくそこに作る
        string outDir = Path.Combine(work, "保存先 フォルダ");
        Directory.CreateDirectory(outDir);
        string moved = await ConvertAsync(video, total, encoder, "outdir", null, null, outputDir: outDir);
        Check("決まったフォルダに保存する", Path.GetDirectoryName(moved) == outDir && File.Exists(moved)
                                         && !File.Exists(Path.Combine(work, "テスト 動画_outdir.mp4")), moved);

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

        await CheckOtherPathsAsync(work, video, video2, total, encoder, bgm);
        await CheckIntroRangesAsync(work, encoder, bgm);
        _video2 = video2;

        // 動画のプレビュー: 完成品 1 秒＋本編 2 秒＝最初の 3 秒だけを 360×640・BGM 入りで。
        // 中身は、同じ設定で作成した動画（faded）の最初の 3 秒と同じはず（大きさだけ違う）
        _clipPath = Path.Combine(work, "動画 プレビュー.mp4");
        var clipWatch = Stopwatch.StartNew();
        await Ffmpeg.RenderPreviewClipAsync([video], total, 5, Ffmpeg.Intro.Last(total, 5, 1, false, 0.3, 0.3), Ffmpeg.Layout.Crop,
            bgm, null, encoder, 3,
            _clipPath, new Progress<Ffmpeg.Progress>(_ => { }), CancellationToken.None);
        double clipTime = clipWatch.Elapsed.TotalSeconds;
        string clipInfo = await FfprobeAsync("-v", "error", "-show_entries", "stream=codec_type,width,height,duration",
            "-of", "csv=p=0", _clipPath);
        var clipDurations = System.Text.RegularExpressions.Regex.Matches(clipInfo, @"(\d+\.\d+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Check("動画のプレビュー: 3 秒・360×640・BGM の音あり",
            clipInfo.Contains("video,360,640") && clipInfo.Contains("audio") && clipDurations.Count == 2
            && clipDurations.All(d => Math.Abs(d - 3) < 0.1),
            clipInfo.ReplaceLineEndings(" | ") + $"（作るのに {clipTime:0.00}秒）");
        var clipLuma = await LumaAsync(_clipPath);
        Check("動画のプレビュー: 作成した動画の最初の 3 秒と同じ（完成品のフェードも）",
            clipLuma.Count == 90 && faded.Count >= 90 && Enumerable.Range(0, 90).All(i => Math.Abs(clipLuma[i] - faded[i]) < 8),
            string.Join(" ", new[] { 0, 8, 15, 29, 30, 89 }.Where(i => i < clipLuma.Count && i < faded.Count)
                .Select(i => $"{i}:{clipLuma[i]:0}/{faded[i]:0}")) + $"（{clipLuma.Count} コマ）");

        await CheckPreviewAsync(video, video2, total, text, cap);

        Check("作業用フォルダが残っていない", Directory.GetDirectories(Path.GetTempPath(), "timelapse_*").Length == 0);
    }

    /// <summary>
    /// ふだんのテストで通らない道も通す。同梱の FFmpeg は作るたびに最新の安定版になり、LGPL 版なので GPL のフィルターは
    /// 入っていない。使えないフィルターがあると、その道だけ「変換に失敗しました」になるので、全部の道で実際に作ってみる
    /// （ぼかした背景は、これが無かったので boxblur が無いことに気づけなかった）。
    /// </summary>
    static async Task CheckOtherPathsAsync(string work, string video, string video2, double total, string encoder, Ffmpeg.Bgm bgm)
    {
        var ct = CancellationToken.None;
        // 横向きの動画を「全体を残す（上下はぼかした背景）」で。中央を切り抜いたものとは違う画になる
        string wide = Path.Combine(work, "横向き.mp4");
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=s=960x540:r=30:d=10", "-c:v", "mpeg4", "-q:v", "5", wide);
        try
        {
            string blurred = await Ffmpeg.ConvertAsync([wide], 10, 5, null, Ffmpeg.Layout.Blur, null, null, "blur", encoder, NoProgress, ct);
            string cropped = await Ffmpeg.ConvertAsync([wide], 10, 5, null, Ffmpeg.Layout.Crop, null, null, "crop", encoder, NoProgress, ct);
            string info = await VideoInfoAsync(blurred);
            Check("横向きの動画: 全体を残す（ぼかした背景）", info == "1080,1920,150"
                                                       && await FrameHashAsync(blurred) != await FrameHashAsync(cropped), info);
            var stills = await Ffmpeg.RenderPreviewAsync([wide], 10, 5, null, Ffmpeg.Layout.Blur, null, ct);
            Check("横向きの動画: ぼかした背景のプレビュー（静止画）", stills.Count == 3);
        }
        catch (Exception ex)
        {
            Check("横向きの動画: 全体を残す（ぼかした背景）", false, ex.Message);
        }

        // iPhone などの HDR（HLG・10 ビット）の動画。普通の色（SDR）に直して作る。完成品のフェードも 10 ビットのままかける
        string hdr = Path.Combine(work, "HDR.mkv");
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=s=540x960:r=30:d=10",
            "-vf", "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc", "-c:v", "ffv1", hdr);
        try
        {
            var detect = typeof(Ffmpeg).GetMethod("GetHdrTransferAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            string? trc = await (Task<string?>)detect.Invoke(null, [hdr])!;
            string sdr = await Ffmpeg.ConvertAsync([hdr], 10, 5, Ffmpeg.Intro.Last(10, 5, 1, false, 0.3, 0.3), Ffmpeg.Layout.Crop,
                null, null, "hdr", encoder, NoProgress, ct);
            string info = await VideoInfoAsync(sdr);
            Check("HDR（HLG）の動画: 普通の色に直して作れる", trc == "arib-std-b67" && info == "1080,1920,150", $"{trc} / {info}");
        }
        catch (Exception ex)
        {
            Check("HDR（HLG）の動画: 普通の色に直して作れる", false, ex.Message);
        }

        // キーフレームが多い動画（3コマごと）: 拾う間隔の方が長いので、キーフレームだけ読む高速モードになる
        string keyed = Path.Combine(work, "キーフレーム多め.mp4");
        await FfmpegAsync("-f", "lavfi", "-i", "testsrc2=s=540x960:r=30:d=30", "-c:v", "mpeg4", "-q:v", "5", "-g", "3", keyed);
        try
        {
            var stages = new List<string>();
            string fast = await Ffmpeg.ConvertAsync([keyed], 30, 5, Ffmpeg.Intro.Last(30, 5, 1, false, 0.3, 0.3), Ffmpeg.Layout.Crop,
                bgm, null, "fast", encoder, new SyncProgress<Ffmpeg.Progress>(p => stages.Add(p.Stage)), ct);
            string info = await VideoInfoAsync(fast);
            Check("キーフレームだけ読む高速モード", stages.Contains("変換中（高速モード）") && info == "1080,1920,150",
                $"{string.Join(" / ", stages.Distinct())} / {info}");
        }
        catch (Exception ex)
        {
            Check("キーフレームだけ読む高速モード", false, ex.Message);
        }

        // 2本をつないで作る（完成品は2本目の終わりから）
        try
        {
            double total2 = await Ffmpeg.GetDurationAsync(video2);
            string joined = await Ffmpeg.ConvertAsync([video, video2], total + total2, 5,
                Ffmpeg.Intro.Last(total + total2, 5, 1, false, 0.3, 0.3), Ffmpeg.Layout.Crop, bgm, null, "joined", encoder, NoProgress, ct);
            string info = await VideoInfoAsync(joined);
            Check("2本をつないで作れる", info == "1080,1920,150", info);
        }
        catch (Exception ex)
        {
            Check("2本をつないで作れる", false, ex.Message);
        }
    }

    static string? _ramp, _video2; // 冒頭を選ぶ画面のテストに使う動画

    /// <summary>
    /// 見ながら選んだ範囲を冒頭に持ってくる（移す・コピー・ゆっくり）。明るさが時刻で決まる動画（0 秒で黒 16、1 秒ごとに +7）を
    /// 使い、出来上がりの各コマの明るさから元の動画の何秒目かを読んで、Ffmpeg.SourceTime の当てはめと同じかを確かめる。
    /// 静止画のプレビュー・動画のプレビューも、同じ当てはめになっているかを確かめる。
    /// </summary>
    static async Task CheckIntroRangesAsync(string work, string encoder, Ffmpeg.Bgm bgm)
    {
        var ct = CancellationToken.None;
        string ramp = Path.Combine(work, "明るさ 時刻.mp4");
        await FfmpegAsync("-f", "lavfi", "-i", "nullsrc=s=540x960:r=30:d=30,geq=lum='16+T*7':cb=128:cr=128",
            "-c:v", "mpeg4", "-q:v", "2", ramp);
        _ramp = ramp;
        const double total = 30, target = 5;

        // 出来上がりのコマの明るさから読んだ秒と、当てはめの秒を比べる。ずれたコマ（つなぎ目の 1 コマなど）を数える
        string Compare(List<double> luma, Ffmpeg.Intro intro, int count, out int bad)
        {
            var off = new List<string>();
            for (int n = 0; n < Math.Min(count, luma.Count); n++)
            {
                double t = n / 30.0, want = Ffmpeg.SourceTime(intro, total, target, t), got = (luma[n] - 16) / 7;
                double speed = t < intro.Seconds ? intro.Speed : Ffmpeg.MainFactor(intro, total, target);
                if (Math.Abs(got - want) > 0.35 + speed / 30) off.Add($"{n}:{got:0.0}/{want:0.0}");
            }
            bad = off.Count + Math.Max(0, count - luma.Count);
            return $"{luma.Count} コマ" + (off.Count > 0 ? " / ずれ " + string.Join(" ", off.Take(8)) : "")
                + " / " + string.Join(" ", new[] { 0, 29, 30, 79, 80, 149 }.Where(i => i < luma.Count)
                    .Select(i => $"{i}:{(luma[i] - 16) / 7:0.0}"));
        }

        var cases = new (string Name, Ffmpeg.Intro Intro)[]
        {
            // 10〜16 秒を 1 秒に（6 倍速）。本編は 0〜10 秒と 16〜30 秒（24 秒を 4 秒に。6 倍速）
            ("移す（途中の 6 秒を 1 秒に）", new(10, 16, 1)),
            // 本編は全部（30 秒を 4 秒に。7.5 倍速）
            ("コピー（本編にも残す）", new(10, 16, 1, Copy: true)),
            // 0.5 秒を 2 秒に（0.25 倍の速さ）。本編は 29.5 秒を 3 秒に
            ("ゆっくり（0.5 秒を 2 秒に）", new(20, 20.5, 2)),
            // いちばん最初の部分を移す（本編は、そのあとだけ）
            ("移す（最初の 3 秒）", new(0, 3, 1)),
        };
        for (int c = 0; c < cases.Length; c++)
        {
            var (name, intro) = cases[c];
            try
            {
                string output = await Ffmpeg.ConvertAsync([ramp], total, target, intro, Ffmpeg.Layout.Crop, null, null,
                    $"range{c}", encoder, NoProgress, ct);
                string detail = Compare(await LumaAsync(output), intro, 150, out int bad);
                Check($"冒頭の範囲: {name}", bad <= 2, detail);
            }
            catch (Exception ex)
            {
                Check($"冒頭の範囲: {name}", false, ex.Message);
            }
        }

        // 数値で（最後の 1 秒を移す）は、今までと同じ（全体を 6 倍速にして、最後の 1 秒を冒頭へ）
        var last = Ffmpeg.Intro.Last(total, target, 1);
        Check("数値で（最後の 1 秒）は今までと同じ範囲・倍速", last == new Ffmpeg.Intro(24, 30, 1)
            && Ffmpeg.MainFactor(last, total, target) == 6 && Ffmpeg.OutputSeconds(last, total, target) == 5, $"{last}");

        try
        {
            // 静止画のプレビュー（最初・真ん中・最後）も同じ当てはめ
            var intro = cases[0].Intro;
            var stills = await Ffmpeg.RenderPreviewAsync([ramp], total, target, intro, Ffmpeg.Layout.Crop, null, ct);
            var notes = new List<string>();
            bool ok = stills.Count == 3;
            for (int i = 0; i < stills.Count; i++)
            {
                string png = Path.Combine(work, $"still{i}.png");
                await File.WriteAllBytesAsync(png, stills[i].Png, ct);
                double got = ((await LumaAsync(png))[0] - 16) / 7, want = Ffmpeg.SourceTime(intro, total, target, stills[i].Seconds);
                notes.Add($"{stills[i].Seconds:0.00}秒→{got:0.0}/{want:0.0}");
                ok &= Math.Abs(got - want) < 0.6;
            }
            Check("冒頭の範囲: 静止画のプレビューも同じ位置", ok, string.Join(" ", notes));

            // 動画のプレビュー（最初の 3 秒）も、同じ当てはめで作る
            string clip = Path.Combine(work, "範囲 プレビュー.mp4");
            await Ffmpeg.RenderPreviewClipAsync([ramp], total, target, intro, Ffmpeg.Layout.Crop, bgm, null, encoder, 3, clip,
                NoProgress, ct);
            var clipLuma = await LumaAsync(clip);
            string detail = Compare(clipLuma, intro, 90, out int bad);
            Check("冒頭の範囲: 動画のプレビューも同じ", bad <= 2 && clipLuma.Count == 90, detail);
        }
        catch (Exception ex)
        {
            Check("冒頭の範囲: プレビュー", false, ex.Message);
        }

        try
        {
            // 冒頭を選ぶ画面で、再生できないときに出す画（つないだ動画の 2 本目の途中）
            byte[] png = await Ffmpeg.RenderSourceFrameAsync([ramp, ramp], [total, total], 40, 360, ct);
            string file = Path.Combine(work, "画.png");
            await File.WriteAllBytesAsync(file, png, ct);
            double got = ((await LumaAsync(file))[0] - 16) / 7;
            Check("冒頭を選ぶ画面の画: つないだ動画の 40 秒目（2 本目の 10 秒目）",
                Math.Abs(got - 10) < 0.5 && BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)) == 360, $"{got:0.0} 秒の画");
        }
        catch (Exception ex)
        {
            Check("冒頭を選ぶ画面の画", false, ex.Message);
        }
    }

    static readonly IProgress<Ffmpeg.Progress> NoProgress = new Progress<Ffmpeg.Progress>(_ => { });

    /// <summary>映像の幅・高さ・コマ数（「1080,1920,150」）。</summary>
    static async Task<string> VideoInfoAsync(string file) =>
        (await FfprobeAsync("-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,nb_frames",
            "-of", "csv=p=0", file)).Trim();

    /// <summary>プレビュー（最初・真ん中・最後の3枚）。大きさ、文字、帯、複数ファイルのときに正しい場所を描くかを確かめる。</summary>
    static async Task CheckPreviewAsync(string video, string video2, double total, string text, (string File, string Family, string Missing) font)
    {
        Task<List<Ffmpeg.PreviewFrame>> Render(List<string> inputs, double seconds, double intro, Ffmpeg.Caption? caption) =>
            Ffmpeg.RenderPreviewAsync(inputs, seconds, 5, Ffmpeg.Intro.Last(seconds, 5, intro), Ffmpeg.Layout.Crop, caption,
                CancellationToken.None);
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
            // 自由な位置: 字幕の真ん中を (X, Y) に置く。字幕だけの画（透明）では、字幕の枠の真ん中がそこに来る
            var free = caption with { Position = Ffmpeg.TextPosition.Custom, X = 300, Y = 1500 };
            byte[] overlay = await Ffmpeg.RenderCaptionAsync(free, 540, 960, CancellationToken.None);
            var image = MainWindow.LoadPng(overlay);
            var box = MainWindow.AlphaBounds(image);
            double cx = (box.X + box.Width / 2) * 2, cy = (box.Y + box.Height / 2) * 2; // 540×960 → 1080×1920
            Check("字幕だけの画: 透明で、自由な位置 (300, 1500) に字幕の真ん中が来る",
                image.PixelWidth == 540 && image.PixelHeight == 960 && !box.IsEmpty && box.Width < 540
                && Math.Abs(cx - 300) < 12 && Math.Abs(cy - 1500) < 12 && Ffmpeg.BuildAss(free, 5).Contains(@"{\an5\pos(300,1500)}"),
                $"字幕の枠 {box}・真ん中 {cx:0}, {cy:0}");
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
        _previewFrames = plain; // 画面のプレビューは字幕なしで描き、字幕だけの画を重ねる
    }

    /// <summary>
    /// 画面を実際に表示して、プレビュー欄（画・切り替え・設定が変わったときの表示）、② の元の動画、
    /// 手順ごと・大きさごとの配置（切れている所がないか）、設定画面とダーク、設定の復元を確かめる。
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
            w.ShowStep(4); // プレビュー欄は ③④⑤ の左
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
            w.BackBoxRadio.IsChecked = true;
            Check("プレビュー欄: 字幕を変えても画は古くならない（字幕は重ねて描き直す）", !w.PreviewStale.IsVisible);
            w.BackNoneRadio.IsChecked = true;
            w.SizeBox.Text = size;
            w.BlurRadio.IsChecked = true;
            bool staleAfterChange = w.PreviewStale.IsVisible;
            w.CropRadio.IsChecked = true;
            Check("プレビュー欄: 画に関わる設定を変えると古いと出て、戻すと消える", staleAfterChange && !w.PreviewStale.IsVisible);
            w.VolumeSlider.Value = -3;
            Check("プレビュー欄: BGM の音量では古くならない", !w.PreviewStale.IsVisible);
            w.VolumeSlider.Value = -6;
            CheckCaptionDrag(w);
            CheckVideoPreview(w);
            CheckIntroMedia(w);

            SettingsWindow.ApplyTheme("Light"); // CI の Windows の色に左右されないよう、まずライトで確かめる
            Check("ライト: 背景が明るい", Luminance(w.Background) > 0.8, $"{w.Background}");
            for (int step = 0; step < 5; step++)
            {
                w.ShowStep(step);
                CheckLayout(w, 1480, 720, "ライト標準", shot: true);  // MainWindow.xaml の Width / Height
                CheckLayout(w, 1240, 650, "ライト最小", shot: false); // MainWindow.xaml の MinWidth / MinHeight
                CheckLayout(w, 1920, 1030, "ライト最大化", shot: false); // フルHDの画面いっぱい（左の画が大きくなる）
            }

            // 設定画面からダークにする（実際の操作と同じ道すじで、設定にも「Dark」が入る）
            var appSettings = (AppSettings)typeof(MainWindow).GetField("_settings", F)!.GetValue(w)!;
            Directory.CreateDirectory(OutputFolder);
            appSettings.OutputFolder = OutputFolder; // 先に入れておく（無いとフォルダを選ぶダイアログが出て止まる）
            var settings = new SettingsWindow(appSettings) { Owner = w };
            settings.Show();
            Check("設定画面: 最初は「Windows に合わせる」「元の動画と同じフォルダ」・終わったときは何もしない",
                settings.SystemRadio.IsChecked == true && settings.SameFolderRadio.IsChecked == true
                && settings.SoundCheck.IsChecked == false && settings.OpenFolderCheck.IsChecked == false
                && settings.SleepCheck.IsChecked == false
                && settings.FolderText.Text == OutputFolder);
            settings.DarkRadio.IsChecked = true;
            settings.FixedFolderRadio.IsChecked = true;
            settings.SoundCheck.IsChecked = true;
            settings.OpenFolderCheck.IsChecked = true;
            // スリープも選んで覚えさせる（このテストは「作成する」を押さないので、本当にスリープすることはない）
            settings.SleepCheck.IsChecked = true;
            Check("設定画面: 色と保存先は別々に選べる", settings.DarkRadio.IsChecked == true && settings.FixedFolderRadio.IsChecked == true
                                                 && settings.SameFolderRadio.IsChecked == false);
            Check("設定画面: 選んだ内容が設定に入る",
                appSettings is { Theme: "Dark", UseOutputFolder: true, DoneSound: true, DoneOpenFolder: true, DoneSleep: true },
                $"{appSettings.Theme} / {appSettings.UseOutputFolder} / {appSettings.DoneSound} / {appSettings.DoneOpenFolder} / {appSettings.DoneSleep}");
            settings.UpdateLayout();
            w.UpdateLayout();
            Check("ダーク: 背景が暗く、文字が明るい", Luminance(w.Background) < 0.2 && Luminance(w.Foreground) > 0.8
                                                && Luminance(w.LengthHint.Foreground) > 0.5,
                $"背景 {w.Background} / 文字 {w.Foreground} / 補足 {w.LengthHint.Foreground}");
            Check("ダーク: ボタンや入力欄も暗い", Luminance(w.LengthBox.Background) < 0.3 && Luminance(w.AddButton.Background) < 0.3,
                $"入力欄 {w.LengthBox.Background} / ボタン {w.AddButton.Background}");
            var settingsClient = (FrameworkElement)VisualTreeHelper.GetChild(settings, 0);
            Shot("settings-dark", settingsClient, settingsClient.RenderSize);
            settings.Close();
            typeof(MainWindow).GetMethod("RefreshInfo", F)!.Invoke(w, null); // 本物では「設定」を閉じたあとに呼ばれる
            Check("⑤ の保存先: 決まったフォルダ", w.SaveText.Text.StartsWith($"決まったフォルダ（{OutputFolder}）に"), w.SaveText.Text);
            for (int step = 0; step < 5; step++)
            {
                w.ShowStep(step);
                CheckLayout(w, 1480, 720, "ダーク標準", shot: false);
                CheckLayout(w, 1240, 650, "ダーク最小", shot: true);
            }
            // 作成が終わったあと（「保存先のフォルダを開く」が出て、⑤ の作成の欄が長くなる）でも収まるか
            w.ShowStep(4);
            string status = w.StatusText.Text;
            w.OpenFolderButton.Visibility = Visibility.Visible;
            w.StatusText.Text = "完成しました（1:02:03）: 作業 2026-11-01 (1)_youtube.mp4";
            CheckLayout(w, 1240, 650, "ダーク最小・作成後", shot: true);
            w.OpenFolderButton.Visibility = Visibility.Collapsed;
            w.StatusText.Text = status;
            CheckSleepWindow(w);

            // 次の起動で戻るか確かめるため、最初とは違う値にしておく
            w.ShowStep(1);
            ((RadioButton)w.PlatformPanel.Children[2]).IsChecked = true; // TikTok
            w.LengthBox.Text = "4:30";
            w.Intro.IntroCheck.IsChecked = true;
            w.Intro.SecondsBox.Text = "7";
            w.Intro.CopyRadio.IsChecked = true;
            w.Intro.FadeInBox.Text = "0.5";
            w.BlurRadio.IsChecked = true;
            w.FadeInBox.Text = "2";
            w.VolumeSlider.Value = -4;
            w.FontBox.SelectedIndex = 1;
            w.ColorBox.SelectedIndex = 4; // 自由に選ぶ（RGB）。色は CheckWindow で選んだ 文字 FF3B30・フチ FF5FA2
            w.PositionBox.SelectedIndex = 3; // 自由（ドラッグ）
            w.TextXBox.Text = "300";
            w.TextYBox.Text = "1600";
            w.BackBoxRadio.IsChecked = true;
            w.SizeBox.Text = "100";

            w.ShowStep(4);
            typeof(MainWindow).GetMethod("ClearButton_Click", F)!.Invoke(w, [w.ClearButton, new RoutedEventArgs()]);
            Check("動画をクリアするとプレビューも消える", w.PreviewImage.Source == null && w.PreviewPlaceholder.IsVisible
                                                    && !w.PreviewStale.IsVisible
                                                    && typeof(MainWindow).GetField("_clipFile", F)!.GetValue(w) == null
                                                    && !w.Intro.Choice.HasRange && w.InfoText.Text == "① で動画を入れてください");
            string font = w.FontBox.SelectedItem!.ToString()!;
            w.Close(); // 閉じるときに設定を書く
            CheckRestore(font);
        }
        catch (Exception ex)
        {
            Check("プレビュー欄", false, ex.ToString());
        }
    }

    /// <summary>
    /// ④ のプレビュー欄: 字幕だけの画（透明。変換と同じ libass で描く）が画に重なり、ドラッグで動かせる。動かすと位置が
    /// 「自由」になり、X/Y の欄と ⑤ のまとめに出る。真ん中の近くではぴったり真ん中に合い、画面からははみ出さない。
    /// 離すと、その位置で描き直す（字幕の真ん中が X/Y に来る）。
    /// </summary>
    static void CheckCaptionDrag(MainWindow w)
    {
        bool Wait(Func<bool> done, double seconds)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.Elapsed.TotalSeconds < seconds) Pump(TimeSpan.FromMilliseconds(50));
            return done();
        }
        string? Key() => (string?)typeof(MainWindow).GetField("_overlayKey", F)!.GetValue(w);
        w.ShowStep(3);
        w.PreviewFirstRadio.IsChecked = true;
        w.PositionBox.SelectedIndex = 0; // 上
        bool drawn = Wait(() => w.CaptionOverlay.IsVisible && Key()?.EndsWith("|Top") == true && !w.CaptionScreenBox().IsEmpty, 20);
        var box = w.CaptionScreenBox();
        double width = w.PreviewFrame.ActualWidth, height = w.PreviewFrame.ActualHeight, k = width / 1080;
        Check("④ 字幕だけの画がプレビューに重なる（上の位置・左右の真ん中）",
            drawn && box.Bottom < height / 2 && Math.Abs(box.X + box.Width / 2 - width / 2) < 4, $"字幕の枠 {box}");
        if (!drawn) return;

        var grab = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        bool began = w.BeginCaptionDrag(grab);
        double y0 = double.TryParse(w.TextYBox.Text, out double y) ? y : double.NaN;
        Check("④ 字幕をつかむと位置が「自由」になり、X/Y は今の字幕の真ん中から", began && w.PositionBox.SelectedIndex == 3
            && w.PositionXY.IsVisible && w.TextXBox.Text == "540" && Math.Abs(y0 - (grab.Y / k)) < 3, $"X {w.TextXBox.Text}・Y {w.TextYBox.Text}");
        w.MoveCaptionDrag(new Point(grab.X + 40 * k, grab.Y + 500 * k)); // 1080×1920 の中で 右へ 40・下へ 500
        Check("④ ドラッグで動き、X/Y の欄も変わる", w.TextXBox.Text == "580" && w.TextYBox.Text == $"{y0 + 500}"
            && w.CaptionPosText.Text == $"X 580・Y {y0 + 500}" && w.CaptionFrame.IsVisible, $"X {w.TextXBox.Text}・Y {w.TextYBox.Text}");
        w.MoveCaptionDrag(new Point(grab.X + 10 * k, grab.Y + (960 - y0 - 9) * k)); // 真ん中から 横 10・縦 9 ずれた所
        Check("④ 真ん中の近くでは、ぴったり真ん中に合う（真ん中の線も出る）", w.TextXBox.Text == "540" && w.TextYBox.Text == "960"
            && w.GuideV.IsVisible && w.GuideH.IsVisible, $"X {w.TextXBox.Text}・Y {w.TextYBox.Text}");
        w.MoveCaptionDrag(new Point(grab.X + 10 * k, grab.Y + (1300 - y0) * k)); // 横は真ん中のまま、縦 1300 へ
        w.UpdateLayout();
        Shot("step3-字幕をドラッグ中", w.PreviewFrame, w.PreviewFrame.RenderSize); // 位置の表示と、真ん中の線
        w.MoveCaptionDrag(new Point(grab.X - 3000 * k, grab.Y + 3000 * k)); // 左下の外へ
        var clamped = w.CaptionScreenBox();
        Check("④ 画面の外へは出ない", clamped.Left >= -0.5 && clamped.Bottom <= height + 0.5 && !w.GuideV.IsVisible,
            $"字幕の枠 {clamped}（枠 {width:0}×{height:0}）");
        w.EndCaptionDrag();
        double x1 = double.Parse(w.TextXBox.Text), y1 = double.Parse(w.TextYBox.Text);
        bool redrawn = Wait(() => Key()?.EndsWith("|Custom") == true && !w.CaptionGuides.IsVisible, 20);
        var after = w.CaptionScreenBox();
        Check("④ 離すと、その位置で描き直す（字幕の真ん中が X/Y）", redrawn
            && Math.Abs(after.X + after.Width / 2 - x1 * k) < 5 && Math.Abs(after.Y + after.Height / 2 - y1 * k) < 5,
            $"X {x1}・Y {y1} / 字幕の枠 {after}");

        w.TextXBox.Text = "540";
        w.TextYBox.Text = "1500";
        Check("④ X/Y を打っても動き、⑤ のまとめに出る", w.SumTextText.Text.Contains("・自由（X 540・Y 1500）")
            && Math.Abs(w.CaptionScreenBox().Y + w.CaptionScreenBox().Height / 2 - 1500 * k) < 5, w.SumTextText.Text);
        w.TextYBox.Text = "２５００"; // 全角も読むが、縦は 1920 まで
        Check("④ X/Y が範囲の外ならエラー", w.InfoText.Text.StartsWith("字幕の位置は") && !w.StartButton.IsEnabled, w.InfoText.Text);
        w.TextYBox.Text = "1500";
        Check("④ 戻せば作れる", w.StartButton.IsEnabled, w.InfoText.Text);
    }

    /// <summary>
    /// 動画のプレビューの表示。作ると「動画」に切り替わって再生し、「最初」などに戻すと静止画に戻る。
    /// 再生できるかは参考として出す（GitHub の PC には音の出る機器が無く、音入りの動画を開けないことがあるため）。
    /// </summary>
    static void CheckVideoPreview(MainWindow w)
    {
        if (_clipPath == null)
        {
            Check("動画のプレビュー欄", false, "前のテストが通っていないので確かめられない");
            return;
        }
        // 写すときは標準の大きさにしておく（GitHub の PC は画面が 1024×768 なので、最初は窓がふだんより狭い）
        w.Width = 1480;
        w.Height = 720;
        w.UpdateLayout();
        // 作ったものはクリアや次の作成で消されるので、写しを渡す
        string clip = Path.Combine(Path.GetTempPath(), $"timelapse-preview-wincheck-{Environment.ProcessId}.mp4");
        File.Copy(_clipPath, clip, true);
        typeof(MainWindow).GetMethod("SetClip", F)!.Invoke(w, [clip, "最初の 3 秒・音つき・1 秒で作成"]);
        Check("動画のプレビュー: 作ると「動画」に切り替わって再生する",
            w.PreviewVideoRadio.IsChecked == true && w.PreviewVideo.IsVisible && !w.PreviewImage.IsVisible
            && !w.PreviewPlaceholder.IsVisible && w.PreviewCaption.Text == "最初の 3 秒・音つき・1 秒で作成", w.PreviewCaption.Text);
        Console.WriteLine($"参考  GitHub の PC での再生: {WaitMedia(w.PreviewVideo)}");
        Pump(TimeSpan.FromSeconds(1.5)); // 少し再生させてから写す
        var client = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        Shot("step4-動画", client, client.RenderSize);

        Check("動画のプレビュー: 作ったあと設定を変えなければ最新", !w.PreviewStale.IsVisible);
        w.VolumeSlider.Value = -3;
        Check("動画のプレビュー: BGM の音量を変えると古いと出る（静止画とちがって）",
            w.PreviewStale.IsVisible && w.PreviewStaleText.Text.Contains("「動画で見る」"), w.PreviewStaleText.Text);
        w.VolumeSlider.Value = -6;
        w.ShowStep(1);
        // 止めたか、もう最後まで見終わっていたか、ここでは開けなかった（どれも音は鳴っていない）
        bool stopped = (bool)typeof(MainWindow).GetField("_clipPaused", F)!.GetValue(w)!
                       || (bool)typeof(MainWindow).GetField("_clipEnded", F)!.GetValue(w)!
                       || w.PreviewVideo.Visibility != Visibility.Visible;
        w.ShowStep(4);
        Check("動画のプレビュー: ② に行くと止まる（音だけ鳴り続けない）", stopped);
        w.PreviewFirstRadio.IsChecked = true;
        Check("動画のプレビュー: 「最初」に戻すと静止画（動画は止める）",
            w.PreviewImage.IsVisible && !w.PreviewVideo.IsVisible && w.PreviewCaption.Text.StartsWith("最初")
            && w.PreviewStaleText.Text.Contains("「プレビュー」"), w.PreviewCaption.Text);
    }

    /// <summary>
    /// ② 冒頭に、本物の動画 2 本（1 本目は色のあるテスト画面、2 本目は明るさが時刻で決まる動画。どちらも 30 秒）を入れる。
    /// 総尺 5 秒なら 12 倍速で、冒頭 1 秒は元の動画の最後の 12 秒。元の動画が出るか（ここで再生できる、またはバーの位置の画）、
    /// 画は正しい時刻か（つないだ 50 秒目＝2 本目の 20 秒目なら、明るさ 16 + 20×7）を確かめる。
    /// ① のスクリーンショットには、1 本目の最初の画（色のあるテスト画面）が写る。
    /// </summary>
    static void CheckIntroMedia(MainWindow w)
    {
        if (_ramp == null || _video2 == null)
        {
            Check("② 冒頭の元の動画", false, "前のテストが通っていないので確かめられない");
            return;
        }
        var files = (List<string>)typeof(MainWindow).GetField("_files", F)!.GetValue(w)!;
        var durations = (Dictionary<string, double>)typeof(MainWindow).GetField("_durations", F)!.GetValue(w)!;
        files.Clear();
        files.AddRange([_video2, _ramp]);
        durations[_ramp] = 30;
        durations[_video2] = 30;
        typeof(MainWindow).GetMethod("RefreshList", F)!.Invoke(w, null);
        w.Width = 1480;
        w.Height = 720;
        w.ShowStep(1);
        w.LengthBox.Text = "5";
        var intro = w.Intro;
        intro.SecondsBox.Text = "1";
        w.UpdateLayout();
        Check("② 冒頭: 60 秒を 5 秒に（12 倍速）。冒頭 1 秒は最後の 12 秒",
            intro.Choice is { Start: 48, End: 60 } && intro.RangeText.Text == "選んだ所 0:48〜1:00（12秒・12 倍速）"
            && intro.OverallBar.SelStart == 48 && intro.ZoomBar.SelEnd == 60,
            $"{intro.Choice.Start}〜{intro.Choice.End} / {intro.RangeText.Text}");

        bool Wait(Func<bool> done, double seconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            var watch = Stopwatch.StartNew();
            timer.Tick += (_, _) => { if (done() || watch.Elapsed.TotalSeconds > seconds) frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            return done();
        }
        var sw = Stopwatch.StartNew();
        Check("② 冒頭: 元の動画が出る", Wait(() => intro.ShowsVideo, 15), $"{sw.Elapsed.TotalSeconds:0.0}秒");
        Console.WriteLine($"参考  ② 冒頭の再生: {((bool?)typeof(IntroPanel).GetField("_canPlay", F)!.GetValue(intro) == true ? "ここで再生できた" : "画で表示")}"
                          + $"（{intro.VideoNote.Text}）");

        // 再生できない PC と同じ道（バーの位置の画を FFmpeg で描く）も確かめる。スクリーンショットにも画が写る。
        // 画を描いている途中に頼んだ位置は、描き終わってから描くので、頼んだ位置を描き終わる（手が空く）まで待つ
        bool Idle() => !(bool)typeof(IntroPanel).GetField("_rendering", F)!.GetValue(intro)!
                       && typeof(IntroPanel).GetField("_wantFrame", F)!.GetValue(intro) == null;
        typeof(IntroPanel).GetMethod("UseFrames", F)!.Invoke(intro, ["（テスト）バーの位置の画を出しています"]);
        typeof(IntroPanel).GetMethod("SeekTo", F)!.Invoke(intro, [50.0]);
        intro.FrameImage.Source = null;
        bool drawn = Wait(() => Idle() && intro.FrameImage.Source != null, 15);
        double luma = drawn ? Luma((BitmapSource)intro.FrameImage.Source!) : double.NaN;
        Check("② 冒頭: バーの位置（50 秒目＝2 本目の 20 秒目）の画を描ける", drawn && Math.Abs((luma - 16) / 7 - 20) < 1.5,
            $"{(luma - 16) / 7:0.0} 秒の明るさ");
        typeof(IntroPanel).GetMethod("SeekTo", F)!.Invoke(intro, [25.0]); // 写すときは、色のある 1 本目
        typeof(IntroPanel).GetMethod("Refresh", F)!.Invoke(intro, null);   // 時刻の表示も合わせる（バーを動かしたときと同じ）
        intro.FrameImage.Source = null;
        Wait(() => Idle() && intro.FrameImage.Source != null, 15);
    }

    /// <summary>画の明るさ（Y。黒 16〜白 235 に合わせた平均）。</summary>
    static double Luma(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        double sum = 0;
        for (int i = 0; i < pixels.Length; i += 4) sum += 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
        return 16 + sum / (pixels.Length / 4) * 219 / 255;
    }

    /// <summary>MediaElement が開けるまで（最大 10 秒）待って、どうなったかを返す。</summary>
    static string WaitMedia(MediaElement media)
    {
        string result = "10 秒待っても開かなかった";
        var frame = new DispatcherFrame();
        RoutedEventHandler opened = (_, _) =>
        {
            result = $"開けた（{media.NaturalVideoWidth}×{media.NaturalVideoHeight}・{media.NaturalDuration}）";
            frame.Continue = false;
        };
        EventHandler<ExceptionRoutedEventArgs> failed = (_, e) =>
        {
            result = "開けなかった: " + e.ErrorException.Message;
            frame.Continue = false;
        };
        media.MediaOpened += opened;
        media.MediaFailed += failed;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        media.MediaOpened -= opened;
        media.MediaFailed -= failed;
        return result;
    }

    /// <summary>画面の処理を time のあいだ進める（再生などを進めるため）。</summary>
    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// スリープのお知らせ。数え終わったら true（＝スリープする）、「スリープしない」なら false。
    /// ここでは窓の返す値だけを確かめる（本当にスリープすると、テスト用の PC が止まってしまう）。
    /// </summary>
    static void CheckSleepWindow(Window owner)
    {
        var sw = Stopwatch.StartNew();
        bool? counted = new SleepWindow(2) { Owner = owner }.ShowDialog();
        Check("スリープのお知らせ: 数え終わるとスリープする", counted == true && sw.Elapsed.TotalSeconds is > 1.5 and < 6,
            $"{counted} / {sw.Elapsed.TotalSeconds:0.0}秒");

        var stop = new SleepWindow { Owner = owner };
        string firstText = "";
        stop.ContentRendered += (_, _) =>
        {
            firstText = stop.CountText.Text;
            var client = (FrameworkElement)VisualTreeHelper.GetChild(stop, 0);
            Shot("sleep-dark", client, client.RenderSize);
            ((IInvokeProvider)new ButtonAutomationPeer(stop.StopButton).GetPattern(PatternInterface.Invoke)!).Invoke();
        };
        sw.Restart();
        bool? stopped = stop.ShowDialog();
        Check("スリープのお知らせ: 「スリープしない」でやめられる", stopped == false && sw.Elapsed.TotalSeconds < 6
                                                          && firstText.StartsWith("あと 60 秒で"),
            $"{stopped} / {sw.Elapsed.TotalSeconds:0.0}秒 / {firstText}");
        Check("スリープのお知らせ: Enter でも「スリープしない」", stop.StopButton.IsDefault && stop.StopButton.IsCancel
                                                             && !stop.SleepNowButton.IsDefault);
    }

    /// <summary>閉じたときの設定が、次に開いた画面に戻るか。設定のファイルが壊れていても開けるか。</summary>
    static void CheckRestore(string font)
    {
        Check("設定を書いた", File.Exists(AppSettings.FilePath));
        var w = new MainWindow();
        Check("前回の設定に戻る",
            w.PlatformPanel.Children.OfType<RadioButton>().ElementAt(2).IsChecked == true && w.LengthBox.Text == "4:30"
            && w.BlurRadio.IsChecked == true
            // 冒頭の秒数・持っていきかた・フェードは覚える。範囲は動画ごとなので覚えない
            && w.Intro.Choice is { Enabled: true, Seconds: "7", Copy: true, FadeIn: "0.5", FadeOut: "0.3", HasRange: false }
            && w.FadeInBox.Text == "2" && w.FadeOutBox.Text == "３" && w.VolumeSlider.Value == -4
            && w.FontBox.SelectedItem?.ToString() == font && w.PositionBox.SelectedIndex == 3
            && w.TextXBox.Text == "300" && w.TextYBox.Text == "1600" && w.PositionXY.Visibility == Visibility.Visible
            // 色は「自由に選ぶ（RGB）」と、選んだ 2 色に戻る
            && w.ColorBox.SelectedIndex == 4 && w.CustomColorBox.Visibility == Visibility.Visible
            && w.CustomColors.TextColor == "FF3B30" && w.CustomColors.OutlineColor == "FF5FA2"
            && w.BackBoxRadio.IsChecked == true && w.SizeBox.Text == "100",
            File.ReadAllText(AppSettings.FilePath).ReplaceLineEndings(" "));
        Check("前回の色（ダーク）で開く", Application.Current.ThemeMode == ThemeMode.Dark && Luminance(w.Background) < 0.2,
            $"{Application.Current.ThemeMode}");
        var restored = (AppSettings)typeof(MainWindow).GetField("_settings", F)!.GetValue(w)!;
        Check("前回の保存先・終わったときの動き（スリープも）に戻る",
            restored is { UseOutputFolder: true, DoneSound: true, DoneOpenFolder: true, DoneSleep: true }
            && restored.OutputFolder == OutputFolder);
        w.Close();

        File.WriteAllText(AppSettings.FilePath, "{ こわれた");
        try
        {
            w = new MainWindow();
            Check("設定のファイルが壊れていても開ける（最初の設定になる）", w.LengthBox.Text == "2:59" && w.CropRadio.IsChecked == true
                && w.Intro.Choice is { Enabled: true, Seconds: "5", Copy: false }, w.LengthBox.Text);
            w.Close();
        }
        catch (Exception ex)
        {
            Check("設定のファイルが壊れていても開ける（最初の設定になる）", false, ex.ToString());
        }
        File.Delete(AppSettings.FilePath);
        try { Directory.Delete(OutputFolder, true); } catch { }
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

    /// <summary>
    /// 今の手順の画面を width×height にして、切れている部品がないか・左の画が 9:16 か・大事な部品が窓（スクロールする欄なら
    /// その見えている所）に収まるかを確かめる。shot ならスクリーンショットも出す。
    /// </summary>
    static void CheckLayout(MainWindow w, double width, double height, string label, bool shot)
    {
        int step = (int)typeof(MainWindow).GetField("_step", F)!.GetValue(w)!;
        string[] names = ["① 動画", "② 冒頭", "③ BGM", "④ テキスト", "⑤ 作成"];
        w.Width = width;
        w.Height = height;
        w.UpdateLayout();
        Pump(TimeSpan.FromMilliseconds(100)); // 大きさに合わせて左の画の幅を決め直すのを待つ
        w.UpdateLayout();
        bool sized = Math.Abs(w.ActualWidth - width) < 1 && Math.Abs(w.ActualHeight - height) < 1;
        var root = (FrameworkElement)w.Content;
        var clipped = Descendants(root).Prepend(root)
            .Where(e => e.IsVisible && LayoutInformation.GetLayoutClip(e) != null && e is not MediaElement)
            .Select(e => !string.IsNullOrEmpty(e.Name) ? e.Name : e is TextBlock t ? $"「{t.Text}」" : e.GetType().Name)
            .ToList();
        // ボタンの文字はテンプレートの中（論理ツリーに無い）なので別に見る: 文字がボタンに収まらず切れていないか
        foreach (var b in Descendants(root).OfType<ButtonBase>().Where(b => b.IsVisible))
            foreach (var cp in VisualDescendants(b).OfType<ContentPresenter>().Where(c => c.TemplatedParent == b))
                if (LayoutInformation.GetLayoutClip(cp) is { } clip
                    && (cp.RenderSize.Width > clip.Bounds.Width + 1 || cp.RenderSize.Height > clip.Bounds.Height + 1))
                    clipped.Add($"ボタン「{(b.Content as string ?? b.Name)}」の文字");
        // 手順のボタンの ✓ が、ボタンの右の余白に収まっているか（文字に重ならない・ボタンからはみ出さない）
        RadioButton[] stepButtons = [w.StepVideo, w.StepIntro, w.StepBgm, w.StepText, w.StepMake];
        TextBlock[] marks = [w.StepVideoMark, w.StepIntroMark, w.StepBgmMark, w.StepTextMark, w.StepMakeMark];
        for (int i = 0; i < stepButtons.Length; i++)
        {
            var b = stepButtons[i];
            if (!marks[i].IsVisible
                || VisualDescendants(b).OfType<ContentPresenter>().FirstOrDefault(c => c.TemplatedParent == b) is not { } cp) continue;
            double textRight = cp.TranslatePoint(new Point(cp.ActualWidth, 0), b).X;
            double markLeft = marks[i].TranslatePoint(new Point(0, 0), b).X, markRight = markLeft + marks[i].ActualWidth;
            if (markLeft < textRight || markRight > b.ActualWidth - 1)
                clipped.Add($"{names[i]} の ✓（文字の右 {textRight:0.#} / ✓ {markLeft:0.#}〜{markRight:0.#} / ボタン {b.ActualWidth:0.#}）");
        }
        // 部品の右下が、窓（スクロールする欄の中なら、その欄の見えている所）に収まっているか
        bool InView(FrameworkElement e)
        {
            DependencyObject? p = e;
            while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
            var box = p as FrameworkElement ?? root;
            var corner = e.TranslatePoint(new Point(e.ActualWidth, e.ActualHeight), box);
            return e.IsVisible && corner.X <= box.ActualWidth + 0.5 && corner.Y <= box.ActualHeight + 0.5;
        }
        FrameworkElement[] keys = step switch
        {
            0 => [w.AddButton, w.BlurRadio],
            1 => [w.Intro.ZoomBar, w.Intro.FooterSlot, w.Intro.SecondsBox],
            2 => [w.BgmSelectButton, w.PreviewVideoButton],
            3 => [w.CaptionBox, w.PreviewButton],
            _ => [w.StartButton, w.PreviewVideoButton],
        };
        var hidden = keys.Where(k => !InView(k)).Select(k => k.Name is { Length: > 0 } n ? n : k.GetType().Name).ToList();
        Border frame = step switch { 0 => w.SourceFrame, 1 => w.Intro.VideoFrame, _ => w.PreviewFrame };
        double ratio = frame.ActualWidth / frame.ActualHeight;
        // 出来上がりの画は、枠いっぱいに出る（黒い余白がない）
        bool fills = step < 2 || w.PreviewImage.Source == null || !w.PreviewImage.IsVisible
                     || (Math.Abs(w.PreviewImage.ActualWidth - frame.ActualWidth) < 1 && Math.Abs(w.PreviewImage.ActualHeight - frame.ActualHeight) < 1);
        Check($"画面の配置（{names[step]} {label} {width}×{height}）: 切れている所がない・左の画が 9:16",
            sized && clipped.Count == 0 && hidden.Count == 0 && Math.Abs(ratio - 9.0 / 16) < 0.01 && fills,
            (sized ? "" : $"窓が {w.ActualWidth}×{w.ActualHeight} にしかならない / ") + string.Join(" / ", clipped)
            + (hidden.Count == 0 ? "" : $" / 見えない: {string.Join(", ", hidden)}")
            + $" [左の画 {frame.ActualWidth:0}×{frame.ActualHeight:0}" + (fills ? "" : $" / 画 {w.PreviewImage.ActualWidth:0}×{w.PreviewImage.ActualHeight:0}") + "]");
        if (!shot) return;
        var client = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        Shot($"step{step}-{label}", client, client.RenderSize);
    }

    static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<FrameworkElement>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>見た目の木（テンプレートの中も）の子孫。</summary>
    static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var d in VisualDescendants(child)) yield return d;
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
        Ffmpeg.Bgm? bgm, Ffmpeg.Caption? caption, double intro = 0, string? outputDir = null,
        double introFadeIn = 0, double introFadeOut = 0) =>
        Ffmpeg.ConvertAsync([video], total, 5, intro > 0 ? Ffmpeg.Intro.Last(total, 5, intro, false, introFadeIn, introFadeOut) : null,
            Ffmpeg.Layout.Crop, bgm, caption, suffix, encoder, new Progress<Ffmpeg.Progress>(_ => { }), CancellationToken.None, outputDir);

    /// <summary>各コマの明るさ（Y の平均。黒が 16 くらい）を、はじめから順に。</summary>
    static async Task<List<double>> LumaAsync(string video) =>
        System.Text.RegularExpressions.Regex.Matches(
                await RunAsync(Ffmpeg.FfmpegPath, "-hide_banner", "-loglevel", "error", "-i", video,
                    "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-", "-f", "null", "-"),
                @"YAVG=([0-9.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

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

/// <summary>その場で呼ぶ IProgress（Progress&lt;T&gt; は別のスレッドであとから呼ぶので、終わった直後に数えると足りないことがある）。</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
