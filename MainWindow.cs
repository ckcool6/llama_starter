using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MyMarkupApp;

public sealed class MainWindow : Window
{
    private readonly TextBlock _statusText;

    public MainWindow()
    {
        Title = "现代 WinUI 3 Fluent 应用";

        // 1. 开启云母背景和沉浸式标题栏
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        // ✨ 核心修复：手动调整右上角按钮的颜色，让它们清清楚楚显现出来！
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;          // 按钮背景全透明
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;  // 窗口未激活时也透明
        titleBar.ButtonForegroundColor = Colors.Black;                // 按钮图标设为黑色（清晰可见）
        titleBar.ButtonHoverForegroundColor = Colors.Black;           // 鼠标悬停时图标依然是黑色

        _statusText = new TextBlock
        {
            Text = "体验 Windows 11 原生 Fluent 质感",
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };

        var myButton = new Button
        {
            Content = "体验原生动效",
            HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        myButton.Click += (s, e) => _statusText.Text = "🎉 这才是真正的 Windows 11 原生颜值！";

        var fluentCard = new Border
        {
            Width = 420,
            Padding = new Thickness(36),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Fluent Design UI",
                        FontSize = 24,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    _statusText,
                    myButton
                }
            }
        };

        Content = fluentCard;
    }
}