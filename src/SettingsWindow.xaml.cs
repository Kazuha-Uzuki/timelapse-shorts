using System.Windows;

namespace TimelapseShorts;

/// <summary>「設定」ボタンで開く小さな画面。今は画面の色（ライト／ダーク／Windows に合わせる）だけ。</summary>
public partial class SettingsWindow : Window
{
    readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        (settings.Theme switch { "Light" => LightRadio, "Dark" => DarkRadio, _ => SystemRadio }).IsChecked = true;
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return; // InitializeComponent の途中
        _settings.Theme = LightRadio.IsChecked == true ? "Light" : DarkRadio.IsChecked == true ? "Dark" : "System";
        ApplyTheme(_settings.Theme);
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
