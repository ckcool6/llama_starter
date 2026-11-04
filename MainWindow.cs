using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MyMarkupApp;

public sealed class MainWindow : Window
{
    private readonly StackPanel _cardsContainer;
    private readonly ScrollViewer _cardsScrollViewer;
    private readonly TextBox _exePathBox;
    private readonly TextBox _logConsole;
    private readonly ScrollViewer _logScrollViewer;

    private readonly List<LlamaInstanceCard> _cardList = new();
    private int _cardCounter = 1;

    public MainWindow()
    {
        Title = "llama-server Multi-Instance Manager (Fluent WinUI 3)";
        SetupWindowStyle();

        // 1. 顶部栏 (Exe 路径 + 浏览/查看 + 全局控制按钮)
        _exePathBox = new TextBox
        {
            PlaceholderText = @"C:\llama.cpp\build\bin\llama-server.exe",
            Width = 280
        };

        // 📂 浏览选择文件按钮
        var browseBtn = new Button { Content = "📁 Browse..." };
        browseBtn.Click += async (s, e) => await BrowseExeFileAsync();

        // 🔍 在资源管理器中打开查看按钮
        var openFolderBtn = new Button { Content = "🔍 Open" };
        openFolderBtn.Click += (s, e) => OpenInExplorer();

        var addBtn = new Button { Content = "➕ Add Instance" };
        addBtn.Click += (s, e) => AddCard();

        var startAllBtn = new Button
        {
            Content = "▶ Start All",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        startAllBtn.Click += (s, e) => { foreach (var card in _cardList) card.Start(); };

        var stopAllBtn = new Button { Content = "⏹ Stop All" };
        stopAllBtn.Click += (s, e) => { foreach (var card in _cardList) card.Stop(); };

        var topBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(24, 48, 24, 16),
            Children =
            {
                new TextBlock { Text = "Server Path:", VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _exePathBox,
                browseBtn,
                openFolderBtn,
                addBtn,
                startAllBtn,
                stopAllBtn
            }
        };

        // 2. 中间卡片滚动区
        _cardsContainer = new StackPanel { Spacing = 16 };
        _cardsScrollViewer = new ScrollViewer
        {
            Content = _cardsContainer,
            Margin = new Thickness(24, 0, 24, 12)
        };

        // 3. 底部实时日志区域
        _logConsole = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        _logScrollViewer = new ScrollViewer
        {
            Content = _logConsole,
            Height = 160
        };

        var clearLogBtn = new Button { Content = "Clear Logs", FontSize = 12 };
        clearLogBtn.Click += (s, e) => _logConsole.Text = string.Empty;

        var logPanel = new Border
        {
            Margin = new Thickness(24, 0, 24, 16),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
                },
                Children =
                {
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        Children =
                        {
                            new TextBlock { Text = "Live Output Console", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                            clearLogBtn
                        }
                    },
                    _logScrollViewer
                }
            }
        };
        Grid.SetColumn(clearLogBtn, 1);
        Grid.SetRow(_logScrollViewer, 1);

        // 4. 根布局 (三行：TopBar / Cards / LogPanel)
        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(topBar, 0);
        Grid.SetRow(_cardsScrollViewer, 1);
        Grid.SetRow(logPanel, 2);

        rootGrid.Children.Add(topBar);
        rootGrid.Children.Add(_cardsScrollViewer);
        rootGrid.Children.Add(logPanel);

        Content = rootGrid;

        // 窗口关闭时自动清理所有子进程
        Closed += (s, e) => { foreach (var card in _cardList) card.Stop(); };

        // 默认先加一个实例卡片
        AddCard();
    }

    /// <summary>
    /// 打开 Windows 文件选择弹窗
    /// </summary>
    private async System.Threading.Tasks.Task BrowseExeFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");

        // WinUI 3 Desktop 需要将当前窗口的句柄挂载到选择器上
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _exePathBox.Text = file.Path;
            AppendLog($"Set Server Path: {file.Path}");
        }
    }

    /// <summary>
    /// 在 Windows 资源管理器中打开并高亮选中该文件
    /// </summary>
    private void OpenInExplorer()
    {
        string path = _exePathBox.Text.Trim();
        if (File.Exists(path))
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        else if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", $"\"{path}\"");
        }
        else
        {
            AppendLog("⚠️ Path does not exist, cannot open in Explorer.");
        }
    }

    private void AddCard()
    {
        var card = new LlamaInstanceCard(_cardCounter++, () => _exePathBox.Text, AppendLog);
        card.DeleteRequested += c =>
        {
            _cardList.Remove(c);
            _cardsContainer.Children.Remove(c);
        };

        _cardList.Add(card);
        _cardsContainer.Children.Add(card);

        _cardsContainer.UpdateLayout();
        _cardsScrollViewer.ChangeView(null, _cardsScrollViewer.ScrollableHeight, null);
    }

    private void AppendLog(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _logConsole.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            _logConsole.Select(_logConsole.Text.Length, 0);
            _logScrollViewer.ChangeView(null, _logScrollViewer.ScrollableHeight, null);
        });
    }

    private void SetupWindowStyle()
    {
    SystemBackdrop = new MicaBackdrop();
    ExtendsContentIntoTitleBar = true;

    var titleBar = AppWindow.TitleBar;
    titleBar.ButtonBackgroundColor = Colors.Transparent;
    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    titleBar.ButtonForegroundColor = Colors.Black;
    titleBar.ButtonHoverForegroundColor = Colors.Black;

    int newWidth = 1280;
    int newHeight = 860;
    AppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, newHeight));

    var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
    if (displayArea != null)
        {
            var centeredX = (displayArea.WorkArea.Width - newWidth) / 2;
            var centeredY = (displayArea.WorkArea.Height - newHeight) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(centeredX, centeredY));
        }
    }
}