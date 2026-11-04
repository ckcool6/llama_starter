using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MyMarkupApp;

public sealed class MainWindow : Window
{
    private readonly nint _hwnd;
    private readonly StackPanel _cardsContainer;
    private readonly ScrollViewer _cardsScrollViewer;
    private readonly TextBox _exePathBox;
    private readonly TextBox _logConsole;
    private readonly ScrollViewer _logScrollViewer;
    private readonly TextBlock _logTitleText;

    private readonly List<LlamaInstanceCard> _cardList = new();
    private readonly StringBuilder _allLogsBuffer = new();
    private LlamaInstanceCard? _selectedLogCard = null;
    private int _cardCounter = 1;
    private ScrollViewer? _logInternalScrollViewer;

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }
        return null;
    }

    public MainWindow()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "llama-server Multi-Instance Manager (Fluent WinUI 3)";
        SetupWindowStyle();

        // 1. 顶部栏 (Exe 路径 + 统一单色矢量图标按钮)
        _exePathBox = new TextBox
        {
            PlaceholderText = @"C:\llama.cpp\build\bin\llama-server.exe",
            Width = 360
        };

        var browseBtn = CreateIconButton(Symbol.OpenFile, "Browse...");
        browseBtn.Click += async (s, e) => await BrowseExeFileAsync();

        var openFolderBtn = CreateIconButton(Symbol.Folder, "Open");
        openFolderBtn.Click += (s, e) => OpenInExplorer();

        var addBtn = CreateIconButton(Symbol.Add, "Add Instance");
        addBtn.Click += (s, e) => AddCard();

        var startAllBtn = CreateIconButton(Symbol.Play, "Start All", (Style)Application.Current.Resources["AccentButtonStyle"]);
        startAllBtn.Click += (s, e) => { foreach (var card in _cardList) card.Start(); };

        var stopAllBtn = CreateIconButton(Symbol.Stop, "Stop All");
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
        _logTitleText = new TextBlock
        {
            Text = "Live Output Console (All Instances)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 直接在 TextBox 上开启垂直滚动条，并固定高度
       _logConsole = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI"),
            FontSize = 12,
            Height = 160,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent)
        };

        ScrollViewer.SetVerticalScrollBarVisibility(_logConsole, ScrollBarVisibility.Auto);

        _logScrollViewer = new ScrollViewer
        {
            Content = _logConsole,
            Height = 160
        };

        // 切换回所有日志
        var showAllLogsBtn = CreateIconButton(Symbol.List, "All Logs");
        showAllLogsBtn.FontSize = 12;
        showAllLogsBtn.Click += (s, e) =>
        {
            _selectedLogCard = null;
            _logTitleText.Text = "Live Output Console (All Instances)";
            _logConsole.Text = _allLogsBuffer.ToString();
            ScrollLogToEnd();
        };

        // 清空当前视图日志
        var clearLogBtn = CreateIconButton(Symbol.Clear, "Clear Logs");
        clearLogBtn.FontSize = 12;
        clearLogBtn.Click += (s, e) =>
        {
            _logConsole.Text = string.Empty;
            if (_selectedLogCard != null)
            {
                _selectedLogCard.ClearLogs();
            }
            else
            {
                _allLogsBuffer.Clear();
            }
        };

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
                            _logTitleText,
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { showAllLogsBtn, clearLogBtn }
                            }
                        }
                    },
                    _logConsole 
                }
            }
        };
        Grid.SetColumn((FrameworkElement)((Grid)logPanel.Child).Children[0], 0);
        Grid.SetColumn(((FrameworkElement)((Grid)((Grid)logPanel.Child).Children[0]).Children[1]), 1);
        Grid.SetRow(_logConsole, 1); 

        // 4. 根布局
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

        Closed += (s, e) => { foreach (var card in _cardList) card.Stop(); };

        AddCard();
    }

    public static Button CreateIconButton(Symbol symbol, string text, Style? style = null)
    {   
        var btn = new Button();
        if (style != null)
        {
            btn.Style = style;
        }

        btn.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new SymbolIcon(symbol),
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        return btn;
    }

    private async Task BrowseExeFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");

        InitializeWithWindow.Initialize(picker, _hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _exePathBox.Text = file.Path;
            AppendSystemLog($"Set Server Path: {file.Path}");
        }
    }

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
            AppendSystemLog("[WARN] Path does not exist, cannot open in Explorer.");
        }
    }

    private void AddCard()
    {
        var card = new LlamaInstanceCard(_cardCounter++, () => _exePathBox.Text, AppendCardLog, _hwnd);
        
        // 点击 View Log 切换到只显示该卡片日志
        card.ViewLogRequested += c =>
        {
            _selectedLogCard = c;
            _logTitleText.Text = $"Live Output Console (Instance #{c.Id})";
            _logConsole.Text = c.GetLogs();
            ScrollLogToEnd();
        };

        // 删除卡片回调
        card.DeleteRequested += c =>
        {
            // 如果删掉的是当前正在查看的卡片，切回全部日志视图
            if (_selectedLogCard == c)
            {
                _selectedLogCard = null;
                _logTitleText.Text = "Live Output Console (All Instances)";
                _logConsole.Text = _allLogsBuffer.ToString();
                ScrollLogToEnd();
            }

            _cardList.Remove(c);
            _cardsContainer.Children.Remove(c);

            // 方案 A：所有卡片清空时重置计数器从 1 开始
            if (_cardList.Count == 0)
            {
                _cardCounter = 1;
            }
        };

        _cardList.Add(card);
        _cardsContainer.Children.Add(card);

        _cardsContainer.UpdateLayout();
        _cardsScrollViewer.ChangeView(null, _cardsScrollViewer.ScrollableHeight, null);
    }

    private void AppendSystemLog(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{time}] [System] {message}\n";
            _allLogsBuffer.Append(line);

            if (_selectedLogCard == null)
            {
                _logConsole.Text += line;
                ScrollLogToEnd();
            }
        });
    }

    private void AppendCardLog(LlamaInstanceCard card, string rawMessage)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string globalLine = $"[{time}] [#{card.Id}] {rawMessage}\n";
            string instanceLine = $"[{time}] {rawMessage}\n";

            _allLogsBuffer.Append(globalLine);

            // 如果当前在看这个卡片，显示纯净的单实例日志
            if (_selectedLogCard == card)
            {
                _logConsole.Text += instanceLine;
                ScrollLogToEnd();
            }
            // 如果当前是全局视图，显示带 [#{id}] 标识的日志
            else if (_selectedLogCard == null)
            {
                _logConsole.Text += globalLine;
                ScrollLogToEnd();
            }
        });
    }

    private void ScrollLogToEnd()
    {
        _logInternalScrollViewer ??= FindScrollViewer(_logConsole);

        if (_logInternalScrollViewer != null)
        {
            _logConsole.UpdateLayout();
            _logInternalScrollViewer.ChangeView(null, _logInternalScrollViewer.ScrollableHeight, null, disableAnimation: true);
        }
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

        int newWidth = 1350;
        int newHeight = 920;
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