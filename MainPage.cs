using Microsoft.UI.Xaml.Controls;

namespace MyMarkupApp;

// 不用 partial 了，也不需要 InitializeComponent
public sealed class MainPage : Page
{
    public MainPage()
    {
        // ❌ 删掉原本的 this.InitializeComponent();

        // ✔️ 直接用 C# 写这个页面的内容：
        Content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "这是完全纯 C# 的 MainPage（无 XAML）",
                    FontSize = 20
                },
                new Button
                {
                    Content = "页面里的按钮",
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center
                }
            }
        };
    }
}