using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace llamaStarter;

public class LlamaInstanceCard : UserControl
{
    private const int MaxInstanceLogLength = 100_000;
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly int _id;
    private readonly Func<string> _getExePathFunc;
    private readonly Action<LlamaInstanceCard, string> _logCallback;
    private readonly nint _windowHandle;
    private readonly StringBuilder _instanceLogBuffer = new();

    private Process? _process;
    private readonly Button _startStopBtn;
    private readonly SymbolIcon _startStopIcon;
    private readonly TextBlock _startStopText;
    private readonly TextBlock _statusBadge;

    private readonly TextBox _modelBox;
    private readonly TextBox _portBox;
    private readonly TextBox _ctxBox;
    private readonly TextBox _nglBox;
    private readonly TextBox _mmprojBox;
    private readonly TextBox _extraBox;

    // --- 硬件监控相关控件与计时变量 ---
    private readonly ProgressBar _cpuBar = new() { Maximum = 100, Height = 6 };
    private readonly TextBlock _cpuText = new() { Text = "0%", FontSize = 11, Width = 45, TextAlignment = TextAlignment.Right };

    private readonly ProgressBar _ramBar = new() { Maximum = 100, Height = 6 };
    private readonly TextBlock _ramText = new() { Text = "0 MB", FontSize = 11, Width = 55, TextAlignment = TextAlignment.Right };

    private readonly ProgressBar _gpuBar = new() { Maximum = 100, Height = 6 };
    private readonly TextBlock _gpuText = new() { Text = "0%", FontSize = 11, Width = 45, TextAlignment = TextAlignment.Right };

    private readonly ProgressBar _vramBar = new() { Maximum = 100, Height = 6 };
    private readonly TextBlock _vramText = new() { Text = "0 MB", FontSize = 11, Width = 55, TextAlignment = TextAlignment.Right };

    private readonly TextBlock _freeMemoryText = new()
    {
        Text = "Free: RAM - | VRAM -",
        FontSize = 10,
        Foreground = new SolidColorBrush(Colors.Gray),
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private readonly DispatcherTimer _metricsTimer;
    private TimeSpan _prevCpuTime = TimeSpan.Zero;
    private DateTime _prevSampleTime = DateTime.MinValue;

    public int Id => _id;
    public bool IsRunning => _process != null && !_process.HasExited;
    public event Action<LlamaInstanceCard>? DeleteRequested;
    public event Action<LlamaInstanceCard>? ViewLogRequested;

    public LlamaInstanceCard(int id, Func<string> getExePathFunc, Action<LlamaInstanceCard, string> logCallback, nint windowHandle)
    {
        _id = id;
        _getExePathFunc = getExePathFunc;
        _logCallback = logCallback;
        _windowHandle = windowHandle;

        _statusBadge = new TextBlock
        {
            Text = "Stopped",
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        _startStopIcon = new SymbolIcon(Symbol.Play);
        _startStopText = new TextBlock { Text = "Start", VerticalAlignment = VerticalAlignment.Center };

        _startStopBtn = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { _startStopIcon, _startStopText }
            }
        };
        _startStopBtn.Click += (s, e) => ToggleStartStop();

        var viewLogBtn = MainWindow.CreateIconButton(Symbol.Document, "View Log");
        viewLogBtn.Click += (s, e) => ViewLogRequested?.Invoke(this);

        var deleteBtn = MainWindow.CreateIconButton(Symbol.Delete, "Delete");
        deleteBtn.Click += (s, e) =>
        {
            Stop();
            DeleteRequested?.Invoke(this);
        };

        _modelBox = new TextBox { PlaceholderText = "Model path (.gguf)", Width = 360 };
        _portBox = new TextBox { Text = (8080 + id - 1).ToString(), Width = 80 };
        _ctxBox = new TextBox { Text = "4096", Width = 80 };
        _nglBox = new TextBox { Text = "99", Width = 80 };
        _mmprojBox = new TextBox { PlaceholderText = "Multimodal projector (.gguf, optional)", Width = 360 };

        _extraBox = new TextBox
        {
            PlaceholderText = "Click to configure extra args (e.g. --host 0.0.0.0)",
            Width = 360,
            IsReadOnly = true
        };
        _extraBox.Tapped += async (s, e) => await ShowExtraArgsDialogAsync();

        var editExtraBtn = new Button { Content = new SymbolIcon(Symbol.Edit), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(editExtraBtn, "Edit Extra Arguments in dialog");
        editExtraBtn.Click += async (s, e) => await ShowExtraArgsDialogAsync();

        var browseModelBtn = new Button { Content = new SymbolIcon(Symbol.OpenFile), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(browseModelBtn, "Browse Model (.gguf)");
        browseModelBtn.Click += async (s, e) => await PickGgufFileAsync(_modelBox);

        var browseMmprojBtn = new Button { Content = new SymbolIcon(Symbol.OpenFile), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(browseMmprojBtn, "Browse Multimodal Projector (.gguf)");
        browseMmprojBtn.Click += async (s, e) => await PickGgufFileAsync(_mmprojBox);

        // --- 头部操作栏 ---
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = $"Instance #{_id}",
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                },
                _statusBadge,
                // 操作按钮直接跟在状态标签后，左对齐
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(16, 0, 0, 0),
                    Children = { _startStopBtn, viewLogBtn, deleteBtn }
                }
            }
        };

        // --- 左侧：参数输入区 ---
        var leftFormPanel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                headerPanel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "-m (Model):", Width = 85, VerticalAlignment = VerticalAlignment.Center }, _modelBox, browseModelBtn }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center }, _portBox,
                        new TextBlock { Text = "-c:", VerticalAlignment = VerticalAlignment.Center }, _ctxBox,
                        new TextBlock { Text = "-ngl:", VerticalAlignment = VerticalAlignment.Center }, _nglBox
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "--mmproj:", Width = 85, VerticalAlignment = VerticalAlignment.Center }, _mmprojBox, browseMmprojBtn }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "Extra Args:", Width = 85, VerticalAlignment = VerticalAlignment.Center }, _extraBox, editExtraBtn }
                }
            }
        };
        Grid.SetColumn(leftFormPanel, 0);

        // --- 右侧：卡片中的仪表盘子卡片 (Hardware Monitor) ---
        var metricsPanel = new Border
        {
            Width = 240,
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Resource Usage", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12 },
                    CreateMetricRow("CPU", _cpuBar, _cpuText),
                    CreateMetricRow("RAM", _ramBar, _ramText),
                    CreateMetricRow("GPU", _gpuBar, _gpuText),
                    CreateMetricRow("VRAM", _vramBar, _vramText),
                    _freeMemoryText
                }
            }
        };
        Grid.SetColumn(metricsPanel, 1);

        // 主卡片采用左右两列 Grid 拼接
        var mainContentGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { leftFormPanel, metricsPanel }
        };

        Content = new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = mainContentGrid
        };

        // 硬件监控定时器（每秒采样一次）
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (s, e) => UpdateMetrics();
    }

    private static Grid CreateMetricRow(string label, ProgressBar bar, TextBlock valueText)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(40) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var labelBlock = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        bar.VerticalAlignment = VerticalAlignment.Center;
        bar.Margin = new Thickness(4, 0, 6, 0);

        Grid.SetColumn(labelBlock, 0);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(valueText, 2);

        grid.Children.Add(labelBlock);
        grid.Children.Add(bar);
        grid.Children.Add(valueText);
        return grid;
    }

    private void UpdateMetrics()
    {
        if (!IsRunning)
        {
            ResetMetricsUi();
            return;
        }

        var m = HardwareMonitor.Collect(_process, ref _prevCpuTime, ref _prevSampleTime);

        // 更新 CPU
        _cpuBar.Value = m.CpuPercent;
        _cpuText.Text = $"{m.CpuPercent:F0}%";

        // 更新 RAM (百分比根据整机总内存计算)
        double ramPercent = m.SystemRamTotalMb > 0 ? (m.RamUsedMb / m.SystemRamTotalMb) * 100 : 0;
        _ramBar.Value = Math.Clamp(ramPercent, 0, 100);
        _ramText.Text = m.RamUsedMb >= 1024 ? $"{m.RamUsedMb / 1024.0:F1} GB" : $"{m.RamUsedMb:F0} MB";

        // 更新 GPU
        _gpuBar.Value = m.GpuPercent;
        _gpuText.Text = $"{m.GpuPercent:F0}%";

        // 更新 VRAM
        double vramPercent = m.SystemVramTotalMb > 0 ? (m.VramUsedMb / m.SystemVramTotalMb) * 100 : 0;
        _vramBar.Value = Math.Clamp(vramPercent, 0, 100);
        _vramText.Text = m.VramUsedMb >= 1024 ? $"{m.VramUsedMb / 1024.0:F1} GB" : $"{m.VramUsedMb:F0} MB";

        // 底部全局剩余内存与显存展示
        string freeRamStr = m.SystemRamFreeMb >= 1024 ? $"{m.SystemRamFreeMb / 1024.0:F1}G" : $"{m.SystemRamFreeMb:F0}M";
        string freeVramStr = m.SystemVramFreeMb >= 1024 ? $"{m.SystemVramFreeMb / 1024.0:F1}G" : $"{m.SystemVramFreeMb:F0}M";
        _freeMemoryText.Text = $"Free: RAM {freeRamStr} | VRAM {freeVramStr}";
    }

    private void ResetMetricsUi()
    {
        _cpuBar.Value = 0;
        _cpuText.Text = "0%";
        _ramBar.Value = 0;
        _ramText.Text = "0 MB";
        _gpuBar.Value = 0;
        _gpuText.Text = "0%";
        _vramBar.Value = 0;
        _vramText.Text = "0 MB";
        _freeMemoryText.Text = "Free: RAM - | VRAM -";
    }

    public string GetLogs()
    {
        lock (_instanceLogBuffer) return _instanceLogBuffer.ToString();
    }

    public void ClearLogs()
    {
        lock (_instanceLogBuffer) _instanceLogBuffer.Clear();
    }

    private void Log(string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string logLine = $"[{time}] {message}";

        lock (_instanceLogBuffer)
        {
            _instanceLogBuffer.AppendLine(logLine);
            if (_instanceLogBuffer.Length > MaxInstanceLogLength)
            {
                _instanceLogBuffer.Remove(0, _instanceLogBuffer.Length - (MaxInstanceLogLength - 10_000));
            }
        }

        _logCallback(this, message);
    }

    private async Task PickGgufFileAsync(TextBox targetBox)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".gguf");
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, _windowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file != null) targetBox.Text = file.Path;
    }

    public void Start()
    {
        if (IsRunning) return;

        string exePath = _getExePathFunc();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Log("[ERROR] Executable path is invalid or file does not exist!");
            return;
        }

        if (string.IsNullOrWhiteSpace(_modelBox.Text))
        {
            Log("[ERROR] Model path (-m) cannot be empty!");
            return;
        }

        string args = $"--port {_portBox.Text.Trim()} -c {_ctxBox.Text.Trim()} -ngl {_nglBox.Text.Trim()} -m \"{_modelBox.Text.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(_mmprojBox.Text)) args += $" --mmproj \"{_mmprojBox.Text.Trim()}\"";
        if (!string.IsNullOrWhiteSpace(_extraBox.Text)) args += $" {_extraBox.Text.Trim()}";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            _process.Exited += (s, e) => DispatcherQueue?.TryEnqueue(UpdateStoppedUi);

            _prevCpuTime = TimeSpan.Zero;
            _prevSampleTime = DateTime.MinValue;

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _statusBadge.Text = $"Running (PID: {_process.Id})";
            _statusBadge.Foreground = new SolidColorBrush(Colors.SeaGreen);

            _startStopIcon.Symbol = Symbol.Stop;
            _startStopText.Text = "Stop";
            _startStopBtn.ClearValue(Button.StyleProperty);

            // 启动硬件监控定时器
            _metricsTimer.Start();

            Log($"[STARTED] Args: {args}");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Start failed: {ex.Message}");
            UpdateStoppedUi();
        }
    }

    public async Task<bool> StartAsync(TimeSpan timeout)
    {
        if (IsRunning) return true;

        Start();
        if (!IsRunning) return false;

        _statusBadge.Text = "Loading Model...";
        _statusBadge.Foreground = new SolidColorBrush(Colors.DarkOrange);

        string url = $"http://127.0.0.1:{_portBox.Text.Trim()}/health";
        var startTime = DateTime.Now;

        while (DateTime.Now - startTime < timeout)
        {
            if (_process == null || _process.HasExited)
            {
                Log("[ERROR] Server process exited unexpectedly during startup.");
                return false;
            }

            try
            {
                var response = await SharedHttpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    _statusBadge.Text = $"Running (PID: {_process.Id})";
                    _statusBadge.Foreground = new SolidColorBrush(Colors.SeaGreen);
                    Log("[READY] Model fully loaded and HTTP server is ready.");
                    return true;
                }
            }
            catch { }

            await Task.Delay(1000);
        }

        Log("[ERROR] Startup timed out waiting for server to be ready.");
        return false;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
            _metricsTimer.Stop();
            _process?.Kill(entireProcessTree: true);
            _process?.Dispose();
            _process = null;
            Log("[STOPPED] Process terminated.");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Stop failed: {ex.Message}");
        }
        finally
        {
            UpdateStoppedUi();
        }
    }

    private void ToggleStartStop()
    {
        if (IsRunning) Stop();
        else Start();
    }

    private void UpdateStoppedUi()
    {
        _metricsTimer.Stop();
        ResetMetricsUi();

        _statusBadge.Text = "Stopped";
        _statusBadge.Foreground = new SolidColorBrush(Colors.Gray);

        _startStopIcon.Symbol = Symbol.Play;
        _startStopText.Text = "Start";
        _startStopBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    private async Task ShowExtraArgsDialogAsync()
    {
        string initialText = _extraBox.Text.Replace(" --", "\n--");

        var editBox = new TextBox
        {
            // WinUI bug: AcceptsReturn must be set first. See: https://github.com/microsoft/microsoft-ui-xaml/issues/10956
            AcceptsReturn = true,
            Text = initialText,
            TextWrapping = TextWrapping.Wrap,
            Height = 200,
            Width = 460,
            PlaceholderText = "Enter parameters here (one per line or space-separated):\n\n--host 0.0.0.0\n--threads 8\n--n-predict 512\n--temp 0.7",
            FontFamily = new FontFamily("Consolas, Cascadia Code, Segoe UI")
        };

        var dialog = new ContentDialog
        {
            Title = $"Extra Arguments - Instance #{_id}",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Enter custom CLI arguments. Line breaks will be automatically converted to spaces:",
                        Foreground = new SolidColorBrush(Colors.Gray),
                        FontSize = 12
                    },
                    editBox
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            char[] separators = { '\r', '\n' };
            string[] lines = editBox.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            _extraBox.Text = string.Join(" ", Array.ConvertAll(lines, l => l.Trim()));
        }
    }

    public InstanceConfig GetConfig()
    {
        return new InstanceConfig
        {
            Id = _id,
            ModelPath = _modelBox.Text.Trim(),
            Port = _portBox.Text.Trim(),
            Ctx = _ctxBox.Text.Trim(),
            Ngl = _nglBox.Text.Trim(),
            MmprojPath = _mmprojBox.Text.Trim(),
            ExtraArgs = _extraBox.Text.Trim()
        };
    }

    public void ApplyConfig(InstanceConfig config)
    {
        _modelBox.Text = config.ModelPath ?? string.Empty;
        _portBox.Text = string.IsNullOrWhiteSpace(config.Port) ? (8080 + _id - 1).ToString() : config.Port;
        _ctxBox.Text = string.IsNullOrWhiteSpace(config.Ctx) ? "4096" : config.Ctx;
        _nglBox.Text = string.IsNullOrWhiteSpace(config.Ngl) ? "99" : config.Ngl;
        _mmprojBox.Text = config.MmprojPath ?? string.Empty;
        _extraBox.Text = config.ExtraArgs ?? string.Empty;
    }
}