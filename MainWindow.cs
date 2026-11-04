using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MyMarkupApp;

public sealed class MainWindow : Window
{
    // 配置文件持久化路径（保存在进程同级目录下）
    private static readonly string ConfigFilePath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "config.json"
    );

    private const int MaxGlobalLogLength = 150_000; // 全局日志缓冲最大保留字符（约 150KB），防止无限膨胀爆内存

    private readonly nint _hwnd;
    private readonly StackPanel _cardsContainer;
    private readonly ScrollViewer _cardsScrollViewer;
    private readonly TextBox _exePathBox;
    private readonly TextBox _logConsole;
    private readonly TextBlock _logTitleText;

    private readonly List<LlamaInstanceCard> _cardList = new();
    private readonly StringBuilder _allLogsBuffer = new();
    private readonly object _logLock = new(); // 保证多进程并发吐日志时的线程安全锁

    private LlamaInstanceCard? _selectedLogCard = null;
    private int _cardCounter = 1;
    private ScrollViewer? _logInternalScrollViewer;

    // 定时批量刷新机制（100ms 节流）：彻底解耦进程高频日志与 UI 渲染，杜绝界面卡死
    private readonly DispatcherTimer _logFlushTimer;
    private bool _isLogDirty = false;

    /// <summary>
    /// 递归遍历视觉树，获取 TextBox 内部自带的 ScrollViewer
    /// </summary>
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

        // =========================================================================
        // 1. 顶部操作栏 (Server Exe 路径配置与全局启停)
        // =========================================================================
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
        startAllBtn.Click += async (s, e) =>
        {
            startAllBtn.IsEnabled = false;
            try
            {
                // 使用 .ToList() 创建快照，防止遍历中修改卡片列表导致迭代器崩溃
                foreach (var card in _cardList.ToList())
                {
                    if (card.IsRunning) continue;

                    AppendSystemLog($"Starting Instance #{card.Id}... Waiting for model to load into VRAM.");
                    bool success = await card.StartAsync(TimeSpan.FromSeconds(180));

                    if (!success)
                    {
                        AppendSystemLog($"[ERROR] Instance #{card.Id} failed to start. Sequential startup aborted.");
                        break;
                    }

                    AppendSystemLog($"Instance #{card.Id} is READY. Proceeding to next instance...");
                }
            }
            finally
            {
                startAllBtn.IsEnabled = true;
            }
        };

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

        // =========================================================================
        // 2. 中间卡片动态滚动区
        // =========================================================================
        _cardsContainer = new StackPanel { Spacing = 16 };
        _cardsScrollViewer = new ScrollViewer
        {
            Content = _cardsContainer,
            Margin = new Thickness(24, 0, 24, 12)
        };

        // =========================================================================
        // 3. 底部实时日志面板（使用清晰明确的 Grid 布局，杜绝层级套娃）
        // =========================================================================
        _logTitleText = new TextBlock
        {
            Text = "Live Output Console (All Instances)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

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

        var showAllLogsBtn = CreateIconButton(Symbol.List, "All Logs");
        showAllLogsBtn.FontSize = 12;
        showAllLogsBtn.Click += (s, e) =>
        {
            _selectedLogCard = null;
            _logTitleText.Text = "Live Output Console (All Instances)";
            RefreshConsoleViewImmediate();
        };

        var clearLogBtn = CreateIconButton(Symbol.Clear, "Clear Logs");
        clearLogBtn.FontSize = 12;
        clearLogBtn.Click += (s, e) =>
        {
            if (_selectedLogCard != null)
            {
                _selectedLogCard.ClearLogs();
            }
            else
            {
                lock (_logLock)
                {
                    _allLogsBuffer.Clear();
                }
            }
            _logConsole.Text = string.Empty;
        };

        // 日志区头部栏 (左侧标题 + 右侧操作按钮)
        var logHeaderGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetColumn(_logTitleText, 0);
        logHeaderGrid.Children.Add(_logTitleText);

        var logButtonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { showAllLogsBtn, clearLogBtn }
        };
        Grid.SetColumn(logButtonsPanel, 1);
        logHeaderGrid.Children.Add(logButtonsPanel);

        // 日志区纵向布局 (第 0 行头部，第 1 行输出终端文本框)
        var logContentGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        Grid.SetRow(logHeaderGrid, 0);
        Grid.SetRow(_logConsole, 1);
        logContentGrid.Children.Add(logHeaderGrid);
        logContentGrid.Children.Add(_logConsole);

        var logPanel = new Border
        {
            Margin = new Thickness(24, 0, 24, 16),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = logContentGrid
        };

        // =========================================================================
        // 4. 主窗口根布局拼接
        // =========================================================================
        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        Grid.SetRow(topBar, 0);
        Grid.SetRow(_cardsScrollViewer, 1);
        Grid.SetRow(logPanel, 2);

        rootGrid.Children.Add(topBar);
        rootGrid.Children.Add(_cardsScrollViewer);
        rootGrid.Children.Add(logPanel);

        Content = rootGrid;
        rootGrid.ActualThemeChanged += (s, e) => UpdateTitleBarTheme();
        UpdateTitleBarTheme();

        // =========================================================================
        // 5. 启动日志节流定时器 (100ms 刷新一次界面，消除高频 IO 带来的卡顿)
        // =========================================================================
        _logFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _logFlushTimer.Tick += (s, e) =>
        {
            if (_isLogDirty)
            {
                _isLogDirty = false;
                RefreshConsoleViewImmediate();
            }
        };
        _logFlushTimer.Start();

        // 窗口退出时保存配置并强杀所有后台子进程
        Closed += (s, e) =>
        {
            _logFlushTimer.Stop();
            SaveConfig();
            foreach (var card in _cardList) card.Stop();
        };

        // 初始化读取配置
        LoadConfig();
    }

    public static Button CreateIconButton(Symbol symbol, string text, Style? style = null)
    {
        var btn = new Button();
        if (style != null) btn.Style = style;

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

    private void AddCard(InstanceConfig? config = null)
    {
        int id = config?.Id ?? _cardCounter++;
        if (id >= _cardCounter)
        {
            _cardCounter = id + 1;
        }

        var card = new LlamaInstanceCard(id, () => _exePathBox.Text, AppendCardLog, _hwnd);

        if (config != null)
        {
            card.ApplyConfig(config);
        }

        card.ViewLogRequested += c =>
        {
            _selectedLogCard = c;
            _logTitleText.Text = $"Live Output Console (Instance #{c.Id})";
            RefreshConsoleViewImmediate();
        };

        card.DeleteRequested += c =>
        {
            if (_selectedLogCard == c)
            {
                _selectedLogCard = null;
                _logTitleText.Text = "Live Output Console (All Instances)";
                RefreshConsoleViewImmediate();
            }

            _cardList.Remove(c);
            _cardsContainer.Children.Remove(c);

            if (_cardList.Count == 0)
            {
                _cardCounter = 1;
            }

            SaveConfig();
        };

        _cardList.Add(card);
        _cardsContainer.Children.Add(card);

        _cardsContainer.UpdateLayout();
        _cardsScrollViewer.ChangeView(null, _cardsScrollViewer.ScrollableHeight, null);
    }

    #region 本地 JSON 持久化

    private void SaveConfig()
    {
        try
        {
            var config = new AppConfig
            {
                ServerPath = _exePathBox.Text.Trim(),
                Instances = _cardList.Select(c => c.GetConfig()).ToList()
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(config, options);
            File.WriteAllText(ConfigFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Config] Failed to save: {ex.Message}");
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                var config = JsonSerializer.Deserialize<AppConfig>(json);

                if (config != null)
                {
                    _exePathBox.Text = config.ServerPath ?? string.Empty;

                    if (config.Instances != null && config.Instances.Count > 0)
                    {
                        foreach (var inst in config.Instances)
                        {
                            AddCard(inst);
                        }
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendSystemLog($"[WARN] Failed to load config.json: {ex.Message}");
        }

        // 若无配置文件，默认新增一个空实例卡片
        AddCard();
    }

    #endregion

    #region 高性能日志记录与视图刷新

    private void AppendSystemLog(string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{time}] [System] {message}";

        lock (_logLock)
        {
            _allLogsBuffer.AppendLine(line);
            TrimBufferIfNeeded(_allLogsBuffer);
        }

        _isLogDirty = true;
    }

    private void AppendCardLog(LlamaInstanceCard card, string rawMessage)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string globalLine = $"[{time}] [#{card.Id}] {rawMessage}";

        lock (_logLock)
        {
            _allLogsBuffer.AppendLine(globalLine);
            TrimBufferIfNeeded(_allLogsBuffer);
        }

        // 仅在当前处于“全局日志”或“正在观察该实例”时标记界面需要刷新
        if (_selectedLogCard == null || _selectedLogCard == card)
        {
            _isLogDirty = true;
        }
    }

    private static void TrimBufferIfNeeded(StringBuilder sb)
    {
        if (sb.Length > MaxGlobalLogLength)
        {
            sb.Remove(0, sb.Length - (MaxGlobalLogLength - 20_000));
        }
    }

    private void RefreshConsoleViewImmediate()
    {
        string content;
        if (_selectedLogCard != null)
        {
            content = _selectedLogCard.GetLogs();
        }
        else
        {
            lock (_logLock)
            {
                content = _allLogsBuffer.ToString();
            }
        }

        _logConsole.Text = content;
        ScrollLogToEnd();
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

    #endregion

    private void SetupWindowStyle()
    {
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;

        // 统一更新标题栏按钮颜色
        UpdateTitleBarTheme();

        int newWidth = 1350;
        int newHeight = 920;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, newHeight));

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var centeredX = (displayArea.WorkArea.Width - newWidth) / 2;
            var centeredY = (displayArea.WorkArea.Height - newHeight) / 2;
            AppWindow.Move(new Windows.Graphics.PointInt32(centeredX, centeredY));
        }
    }

    /// <summary>
    /// 根据当前界面的实际深浅色，动态设置右上角最小化/最大化/关闭按钮的前景和悬浮色
    /// </summary>
    private void UpdateTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar == null) return;

        // 背景全透明
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // 判断当前内容实际呈现的是深色还是浅色
        bool isDark = false;
        if (Content is FrameworkElement fe)
        {
            isDark = fe.ActualTheme == ElementTheme.Dark
                     || (fe.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        }

        // 浅色主题用黑色图标，深色主题用白色图标
        var iconColor = isDark ? Colors.White : Colors.Black;

        titleBar.ButtonForegroundColor = iconColor;
        titleBar.ButtonHoverForegroundColor = iconColor;
        titleBar.ButtonPressedForegroundColor = iconColor;

        // 鼠标悬浮和点击时的背景微透微光
        titleBar.ButtonHoverBackgroundColor = isDark
       ? ColorHelper.FromArgb(25, 255, 255, 255)
       : ColorHelper.FromArgb(25, 0, 0, 0);

        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(40, 255, 255, 255)
            : ColorHelper.FromArgb(40, 0, 0, 0);
    }
}