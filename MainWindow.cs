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

namespace llamaStarter;

public sealed class MainWindow : Window
{
    // Configuration file persistence path (saved in the same directory as the process)
    private static readonly string ConfigFilePath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "config.json"
    );

    private const int MaxGlobalLogLength = 150_000; // Maximum global log buffer characters (~150KB) to prevent unbounded memory growth

    private readonly nint _hwnd;
    private readonly StackPanel _cardsContainer;
    private readonly ScrollViewer _cardsScrollViewer;
    private readonly TextBox _exePathBox;
    private readonly TextBox _logConsole;
    private readonly TextBlock _logTitleText;

    private readonly List<LlamaInstanceCard> _cardList = new();
    private readonly StringBuilder _allLogsBuffer = new();
    private readonly object _logLock = new(); // Thread safety lock for concurrent logging across multiple processes

    private LlamaInstanceCard? _selectedLogCard = null;
    private int _cardCounter = 1;
    private ScrollViewer? _logInternalScrollViewer;

    // Periodic batch refresh (100ms throttle): completely decouples high-frequency logs from UI rendering to prevent UI freezes
    private readonly DispatcherTimer _logFlushTimer;
    private bool _isLogDirty = false;

    private TrayIconManager? _trayIconManager;
    private bool _isRealExit = false;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
    private const int SW_RESTORE = 9;

    public static Button CreateFontIconButton(string glyph, string text, Style? style = null)
    {
        var btn = new Button();
        if (style != null) btn.Style = style;

        btn.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
        {
            new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            },
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
        }
        };
        return btn;
    }

    /// <summary>
    /// Recursively traverses the visual tree to find the internal ScrollViewer of a TextBox.
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
        // 1. Top operation bar (Server executable path and global controls)
        // =========================================================================
        _exePathBox = new TextBox
        {
            PlaceholderText = @"C:\llama.cpp\build\bin\llama-server.exe",
            Width = 360
        };

        var browseBtn = CreateIconButton(Symbol.OpenFile, "Browse...");
        browseBtn.Click += async (s, e) => await BrowseExeFileAsync();



        var addBtn = CreateIconButton(Symbol.Add, "Add Instance");
        addBtn.Click += (s, e) => AddCard();

        var startAllBtn = CreateFontIconButton("\uE768", "Start All", (Style)Application.Current.Resources["AccentButtonStyle"]);
        startAllBtn.Click += async (s, e) =>
        {
            startAllBtn.IsEnabled = false;
            try
            {
                // Create a snapshot with .ToList() to prevent collection modification during iteration
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

        var stopAllBtn = CreateFontIconButton("\uEE95", "Stop All");
        stopAllBtn.Click += (s, e) => { foreach (var card in _cardList) card.Stop(); };

        var appTitleBlock = new TextBlock
        {
            Text = "llama-server Multi-Instance Manager",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };

        var customTitleBar = new Grid
        {
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { appTitleBlock }
        };

        SetTitleBar(customTitleBar);

        var topBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(24, 8, 24, 16),
            Children =
            {
                new TextBlock { Text = "Server Path:", VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                _exePathBox,
                browseBtn,
                addBtn,
                startAllBtn,
                stopAllBtn
            }
        };

        // =========================================================================
        // 2. Middle dynamic card scroll area
        // =========================================================================
        _cardsContainer = new StackPanel { Spacing = 16 };
        _cardsScrollViewer = new ScrollViewer
        {
            Content = _cardsContainer,
            Margin = new Thickness(24, 0, 24, 12)
        };

        // =========================================================================
        // 3. Bottom live output console panel (explicit Grid layout)
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

        // Console header bar (title on the left + action buttons on the right)
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

        // Console vertical layout (Row 0: header, Row 1: console text box)
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
        // 4. Root grid layout assembly
        // =========================================================================
        var rootGrid = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        Grid.SetRow(customTitleBar, 0);
        Grid.SetRow(topBar, 1);
        Grid.SetRow(_cardsScrollViewer, 2);
        Grid.SetRow(logPanel, 3);

        rootGrid.Children.Add(customTitleBar);
        rootGrid.Children.Add(topBar);
        rootGrid.Children.Add(_cardsScrollViewer);
        rootGrid.Children.Add(logPanel);

        Content = rootGrid;
        rootGrid.ActualThemeChanged += (s, e) => UpdateTitleBarTheme();
        UpdateTitleBarTheme();

        // =========================================================================
        // 5. Start log throttling timer (flushes UI every 100ms to eliminate stutter)
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

        // Intercept close [X] button: minimize to tray unless a real exit was requested
        AppWindow.Closing += (sender, args) =>
        {
            if (!_isRealExit)
            {
                args.Cancel = true;
                AppWindow.Hide();

                // Show system tray notification
                _trayIconManager?.ShowNotification(
                    "llama-server Manager",
                    "The application has been minimized to the system tray. Background model services are still running.\nClick this notification or double-click the tray icon to restore."
                );
            }
        };

        // 2. Initialize system tray icon
        _trayIconManager = new TrayIconManager(_hwnd, "llama-server Multi-Instance Manager");
        _trayIconManager.RestoreRequested += () => DispatcherQueue.TryEnqueue(BringToFront);
        _trayIconManager.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApplication);

        // 3. Clean up tray icon on real application exit
        Closed += (s, e) =>
        {
            _trayIconManager?.Dispose();
            _trayIconManager = null;
            _logFlushTimer.Stop();
            SaveConfig();
            foreach (var card in _cardList) card.Stop();
        };

        // Initialize and load saved configuration
        LoadConfig();
    }

    /// <summary>
    /// Restores and activates the main window from the system tray.
    /// </summary>
    private void BringToFront()
    {
        AppWindow.Show();
        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// Exits the application completely from the tray context menu.
    /// </summary>
    private void ExitApplication()
    {
        _isRealExit = true;
        Close();
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

    #region Local JSON Persistence

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

        // If no configuration file exists, add a default empty instance card
        AddCard();
    }

    #endregion

    #region High-Performance Logging and View Refresh

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

        // Mark UI as dirty only if viewing "All Logs" or currently viewing this specific instance
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

        // Update title bar button theme
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
    /// Dynamically updates title bar button foreground and hover colors based on current app theme.
    /// </summary>
    private void UpdateTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar == null) return;

        // Transparent background
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        // Check whether the active content theme is dark or light
        bool isDark = false;
        if (Content is FrameworkElement fe)
        {
            isDark = fe.ActualTheme == ElementTheme.Dark
                     || (fe.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        }

        // Use black icons for light theme, white icons for dark theme
        var iconColor = isDark ? Colors.White : Colors.Black;

        titleBar.ButtonForegroundColor = iconColor;
        titleBar.ButtonHoverForegroundColor = iconColor;
        titleBar.ButtonPressedForegroundColor = iconColor;

        // Subtle translucent highlight on hover and press
        titleBar.ButtonHoverBackgroundColor = isDark
            ? ColorHelper.FromArgb(25, 255, 255, 255)
            : ColorHelper.FromArgb(25, 0, 0, 0);

        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(40, 255, 255, 255)
            : ColorHelper.FromArgb(40, 0, 0, 0);
    }
}