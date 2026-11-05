using System.IO;
using System.Text.Json;

namespace TimelapseShorts;

/// <summary>
/// 次の起動のときに戻す設定。閉じるときに %LOCALAPPDATA%\TimelapseShorts\settings.json に書く。
/// 入力欄の値は打ったままの文字で持つ（読めない値は、画面の入力チェックがいつも通り知らせる）。
/// </summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "System"; // System（Windows に合わせる）/ Light / Dark
    public string? Platform { get; set; }          // 投稿先の Key
    public string? Length { get; set; }            // 総尺（「2:59」など）
    public bool Intro { get; set; } = true;
    public string IntroSeconds { get; set; } = "5";
    public bool Blur { get; set; }                 // 横向きの動画: true = ぼかし背景 / false = 中央を切り抜く
    public string FadeIn { get; set; } = "1";
    public string FadeOut { get; set; } = "3";
    public double Volume { get; set; }
    public string? Font { get; set; }              // フォントの表示名
    public string? Color { get; set; }             // 色のセットの名前
    public int Position { get; set; }              // 0 上 / 1 中央 / 2 下
    public bool TextBack { get; set; }             // 背景あり（帯）
    public string TextSize { get; set; } = "80";
    public bool UseOutputFolder { get; set; }      // true = 決まったフォルダに保存 / false = 元の動画と同じフォルダ
    public string? OutputFolder { get; set; }      // 決まったフォルダ（「同じフォルダ」に戻しても覚えておく）
    public bool DoneSound { get; set; }            // 作成が終わったら音で知らせる
    public bool DoneOpenFolder { get; set; }       // 作成が終わったら保存先のフォルダを開く
    public bool DoneSleep { get; set; }            // 作成が終わったら PC をスリープ（60 秒のお知らせでやめられる）

    /// <summary>保存先。テストでは別の場所に変える。</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TimelapseShorts", "settings.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>読めなければ（初めての起動・壊れたファイル）最初の設定を返す。</summary>
    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>書けなくても（容量不足など）アプリは止めない。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}
