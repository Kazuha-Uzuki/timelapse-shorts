using System.Windows;
using Microsoft.Win32;

namespace TimelapseShorts;

/// <summary>「設定」ボタンで開く小さな画面（画面の色・保存先・作成が終わったとき）。選んだ内容はすぐ settings に入る。</summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;
    readonly bool _ready; // 最初の値を入れ終わったか（入れている間に保存先のダイアログを出さないため）

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        (settings.Theme switch { "Light" => LightRadio, "Dark" => DarkRadio, _ => SystemRadio }).IsChecked = true;
        (settings.UseOutputFolder && settings.OutputFolder != null ? FixedFolderRadio : SameFolderRadio).IsChecked = true;
        SoundCheck.IsChecked = settings.DoneSound;
        OpenFolderCheck.IsChecked = settings.DoneOpenFolder;
        SleepCheck.IsChecked = settings.DoneSleep;
        ShowFolder();
        _ready = true;
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return; // InitializeComponent の途中
        _settings.Theme = LightRadio.IsChecked == true ? "Light" : DarkRadio.IsChecked == true ? "Dark" : "System";
        ApplyTheme(_settings.Theme);
    }

    void Output_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        // 「決まったフォルダ」を選んだのに、まだフォルダを選んでいなければ選んでもらう（やめたら元に戻す）
        if (FixedFolderRadio.IsChecked == true && _settings.OutputFolder == null && !ChooseFolder())
        {
            SameFolderRadio.IsChecked = true;
            return;
        }
        _settings.UseOutputFolder = FixedFolderRadio.IsChecked == true;
    }

    void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder()) FixedFolderRadio.IsChecked = true; // フォルダを選んだら、そこに保存する
    }

    /// <summary>フォルダを選んでもらう。選んだら true。</summary>
    bool ChooseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "できた動画の保存先を選ぶ" };
        if (_settings.OutputFolder != null) dlg.InitialDirectory = _settings.OutputFolder;
        if (dlg.ShowDialog(this) != true) return false;
        _settings.OutputFolder = dlg.FolderName;
        ShowFolder();
        return true;
    }

    void ShowFolder()
    {
        FolderText.Text = _settings.OutputFolder ?? "（まだ選んでいません）";
        FolderText.ToolTip = _settings.OutputFolder;
    }

    void Done_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return; // 最初の値を入れている途中に、まだ入れていない方の値で上書きしないように
        _settings.DoneSound = SoundCheck.IsChecked == true;
        _settings.DoneOpenFolder = OpenFolderCheck.IsChecked == true;
        _settings.DoneSleep = SleepCheck.IsChecked == true;
    }

    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// アプリ全体を Windows 11 風（Fluent テーマ）のライト／ダークにする。
    /// ThemeMode は .NET 10 では「試験的」な機能（WPF0001）。配る exe は .NET 10 に固定なので、その版の動きのまま。
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is not { } app) return;
        app.ThemeMode = theme switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
    }
}
