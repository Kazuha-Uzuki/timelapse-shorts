using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace TimelapseShorts;

/// <summary>文字入れに使えるフォントの一覧。同梱の Google Fonts（fonts フォルダ）と、この PC のフォント。</summary>
public static class FontCatalog
{
    /// <summary>File は同梱フォントのファイル。PC のフォントは null（使うときに探す）。</summary>
    public sealed record Item(string Display, FontFamily Family, string? File)
    {
        public override string ToString() => Display;
    }

    static readonly XmlLanguage Ja = XmlLanguage.GetLanguage("ja-jp");
    static readonly CultureInfo JaCulture = CultureInfo.GetCultureInfo("ja-jp");
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-us");

    static string Name(IDictionary<CultureInfo, string> names, CultureInfo prefer) =>
        names.TryGetValue(prefer, out var n) ? n : names.TryGetValue(En, out n) ? n : names.Values.First();

    public static List<Item> Load()
    {
        var list = new List<Item>();

        // 同梱の Google Fonts（★印）
        string dir = Path.Combine(AppContext.BaseDirectory, "fonts");
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.ttf").Order())
            {
                try
                {
                    var gt = new GlyphTypeface(new Uri(file));
                    var family = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#" + Name(gt.FamilyNames, En));
                    list.Add(new Item("★ " + Name(gt.FamilyNames, JaCulture), family, file));
                }
                catch { } // 読めないファイルは飛ばす
            }

        // この PC のフォント（「自分用にインストール」したものも含める）。日本語名のものを先に並べる
        IEnumerable<FontFamily> pc = Fonts.SystemFontFamilies;
        string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");
        if (Directory.Exists(userDir))
            pc = pc.Concat(Fonts.GetFontFamilies(userDir + Path.DirectorySeparatorChar));
        list.AddRange(pc
            .Select(f => new Item(f.FamilyNames.TryGetValue(Ja, out var n) ? n : f.Source, f, null))
            .GroupBy(i => i.Display).Select(g => g.First())
            .OrderBy(i => i.Display.All(c => c < 128))
            .ThenBy(i => i.Display, StringComparer.CurrentCulture));
        return list;
    }

    /// <summary>
    /// ffmpeg に渡すフォントファイルとファミリー名を調べ、そのフォントに無い文字（四角や空白になる）も返す。
    /// </summary>
    public static (string File, string Family, string Missing) Resolve(Item item, string text)
    {
        GlyphTypeface gt;
        if (item.File != null)
            gt = new GlyphTypeface(new Uri(item.File));
        else if (!new Typeface(item.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out gt))
            throw new InvalidOperationException($"フォント「{item.Display}」のファイルが見つかりません。別のフォントを選んでください。");

        string file = gt.FontUri.LocalPath;
        if (!File.Exists(file) && file.Contains('#')) file = file[..file.LastIndexOf('#')]; // 「…ttc#1」のような形への備え
        // libass は Windows 用のファミリー名（英語）で探す。WPF の名前（Kiwi Maru など）では見つからないことがある
        string family = Name(gt.Win32FamilyNames, En);
        string missing = string.Concat(text.EnumerateRunes()
            .Where(r => !Rune.IsWhiteSpace(r) && !Rune.IsControl(r) && !gt.CharacterToGlyphMap.ContainsKey(r.Value))
            .Select(r => r.ToString()).Distinct().Take(10));
        return (file, family, missing);
    }
}
