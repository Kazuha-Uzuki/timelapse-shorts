using System.Windows;
using System.Windows.Threading;

namespace TimelapseShorts;

/// <summary>
/// 「終わったら PC をスリープ」のお知らせ。数え終わるか「今すぐスリープ」で true、
/// 「スリープしない」・Esc・閉じるで false / null を返す（ShowDialog の値）。スリープそのものは呼んだ側がする。
/// </summary>
public partial class SleepWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    int _left;

    public SleepWindow(int seconds = 60)
    {
        InitializeComponent();
        _left = seconds;
        ShowLeft();
        _timer.Tick += (_, _) =>
        {
            if (--_left > 0) ShowLeft();
            else
            {
                _timer.Stop();
                DialogResult = true;
            }
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    void ShowLeft() =>
        CountText.Text = $"あと {_left} 秒で PC をスリープします。\nPC を使っているときは「スリープしない」を押してください。";

    void SleepNowButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
