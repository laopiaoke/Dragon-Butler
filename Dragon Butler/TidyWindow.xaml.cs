using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DragonButler;

/// <summary>
/// 「跑完收拾一下」的设置窗（v1.20 / 奇思妙想 #7）。
///
/// <para>
/// 四项一个都不默认开。界面上把「关掉游戏」单独放在最后并染成警示色，
/// 因为它和另外三项不是一类事：前两项是清理我们自己的痕迹，最后一项是动用户的东西。
/// </para>
/// </summary>
public partial class TidyWindow : Window
{
    public TidyWindow()
    {
        InitializeComponent();
        Sync();
    }

    private void Sync()
    {
        var s = AppState.Settings;
        TgTidy.IsChecked = s.TidyEnabled;
        TgCloseTool.IsChecked = s.TidyCloseTool;
        TgRestore.IsChecked = s.TidyRestoreWindows;
        TgCloseGame.IsChecked = s.TidyCloseGame;

        // 总开关关着时下面三项灰着：能点但没效果最容易让人以为坏了。
        bool on = s.TidyEnabled;
        TgCloseTool.IsEnabled = on;
        TgRestore.IsEnabled = on;
        TgCloseGame.IsEnabled = on;

        int remembered = 0;
        try { remembered = TidyService.All().Count; } catch { }
        TbFoot.Text = on
            ? (remembered > 0 ? $"已记住 {remembered} 个窗口位置。" : "还没有记住任何窗口位置——每次开跑前会自动记一次。")
            : "现在什么都没开，跑完不会动任何程序。";
    }

    private void TgTidy_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.TidyEnabled = TgTidy.IsChecked == true;
        AppState.SaveSettings();
        Sync();
    }

    private void TgCloseTool_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.TidyCloseTool = TgCloseTool.IsChecked == true;
        AppState.SaveSettings();
        Sync();
    }

    private void TgRestore_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.TidyRestoreWindows = TgRestore.IsChecked == true;
        AppState.SaveSettings();
        Sync();
    }

    private void TgCloseGame_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.TidyCloseGame = TgCloseGame.IsChecked == true;
        AppState.SaveSettings();
        Sync();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
