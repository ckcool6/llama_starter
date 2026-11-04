using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    private static readonly string ConfigFilePath = Path.Combine(
    Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
    "config.json"
    );

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
        startAllBtn.Click += async (s, e) =>
        {
            startAllBtn.IsEnabled = false;
            try
            {
                foreach (var card in _cardList)
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

        var showAllLogsBtn = CreateIconButton(Symbol.List, "All Logs");
        showAllLogsBtn.FontSize = 12;
        showAllLogsBtn.Click += (s, e) =>
        {
            _selectedLogCard = null;
            _logTitleText.Text = "Live Output Console (All Instances)";
            _logConsole.Text = _allLogsBuffer.ToString();
            ScrollLogToEnd();
        };

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

        // 退出时：保存配置到 json 并停止所有进程
        Closed += (s, e) =>
        {
            SaveConfig();
            foreach (var card in _cardList) card.Stop();
        };

        // 启动时读取本地配置
        LoadConfig();
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

    // 支持传入已存在的配置
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
            _logConsole.Text = c.GetLogs();
            ScrollLogToEnd();
        };

        card.DeleteRequested += c =>
        {
            if (_selectedLogCard == c)
            {
                _selectedLogCard = null;
                _logTitleText.Text = "Live Output Console (All Instances)";
                _logConsole.Text = _allLogsBuffer.ToString();
                ScrollLogToEnd();
            }

            _cardList.Remove(c);
            _cardsContainer.Children.Remove(c);

            if (_cardList.Count == 0)
            {
                _cardCounter = 1;
            }

            // 删除卡片后自动保存一次
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

        // 如果没有配置文件或为空，默认添加一张空卡片
        AddCard();
    }

    #endregion

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

            if (_selectedLogCard == card)
            {
                _logConsole.Text += instanceLine;
                ScrollLogToEnd();
            }
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