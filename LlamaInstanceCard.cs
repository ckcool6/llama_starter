using System;
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

public class LlamaInstanceCard : UserControl
{
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

        // 单色启停图标与按钮
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

        // 👈 View Log 按钮
        var viewLogBtn = MainWindow.CreateIconButton(Symbol.Document, "View Log");
        viewLogBtn.Click += (s, e) => ViewLogRequested?.Invoke(this);

        // 单色删除按钮
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

        var editExtraBtn = new Button
        {
            Content = new SymbolIcon(Symbol.Edit),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(editExtraBtn, "Edit Extra Arguments in dialog");
        editExtraBtn.Click += async (s, e) => await ShowExtraArgsDialogAsync();

        // 为 -m 配置浏览按钮
        var browseModelBtn = new Button
        {
            Content = new SymbolIcon(Symbol.OpenFile),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(browseModelBtn, "Browse Model (.gguf)");
        browseModelBtn.Click += async (s, e) => await PickGgufFileAsync(_modelBox);

        // 为 --mmproj 配置浏览按钮
        var browseMmprojBtn = new Button
        {
            Content = new SymbolIcon(Symbol.OpenFile),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(browseMmprojBtn, "Browse Multimodal Projector (.gguf)");
        browseMmprojBtn.Click += async (s, e) => await PickGgufFileAsync(_mmprojBox);

        Content = new Border
        {
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    // 顶部头部：状态与操作按钮（Start / View Log / Delete）
                    new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                            new ColumnDefinition { Width = GridLength.Auto }
                        },
                        Children =
                        {
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 10,
                                Children =
                                {
                                    new TextBlock { Text = $"Instance #{_id}", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                                    _statusBadge
                                }
                            },
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { _startStopBtn, viewLogBtn, deleteBtn }
                            }
                        }
                    },
                    // -m (Model) 路径配置 + 浏览按钮
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "-m (Model):", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _modelBox,
                            browseModelBtn
                        }
                    },
                    // 端口、上下文参数
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 12,
                        Children =
                        {
                            new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center },
                            _portBox,
                            new TextBlock { Text = "-c:", VerticalAlignment = VerticalAlignment.Center },
                            _ctxBox,
                            new TextBlock { Text = "-ngl:", VerticalAlignment = VerticalAlignment.Center },
                            _nglBox
                        }
                    },
                    // --mmproj 路径配置 + 浏览按钮
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "--mmproj:", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _mmprojBox,
                            browseMmprojBtn
                        }
                    },
                    // 附加额外参数
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Extra Args:", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _extraBox,
                            editExtraBtn 

                        }
                    }
                }
            }
        };

        Grid.SetColumn((FrameworkElement)((Grid)((StackPanel)((Border)Content).Child).Children[0]).Children[1], 1);
    }

    public string GetLogs() => _instanceLogBuffer.ToString();

    public void ClearLogs() => _instanceLogBuffer.Clear();

    private void Log(string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string logLine = $"[{time}] {message}";
        _instanceLogBuffer.AppendLine(logLine);
        _logCallback(this, message);
    }

    private async Task PickGgufFileAsync(TextBox targetBox)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".gguf");
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, _windowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            targetBox.Text = file.Path;
        }
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

        string args = $"--port {_portBox.Text} -c {_ctxBox.Text} -ngl {_nglBox.Text}";
        if (!string.IsNullOrWhiteSpace(_modelBox.Text)) args += $" -m \"{_modelBox.Text.Trim()}\"";
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
            _process.Exited += (s, e) => DispatcherQueue.TryEnqueue(UpdateStoppedUi);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _statusBadge.Text = $"Running (PID: {_process.Id})";
            _statusBadge.Foreground = new SolidColorBrush(Colors.SeaGreen);

            // 切换为 Stop 状态（单色图标 + 普通按钮样式）
            _startStopIcon.Symbol = Symbol.Stop;
            _startStopText.Text = "Stop";
            _startStopBtn.ClearValue(Button.StyleProperty);

            Log($"[STARTED] Args: {args}");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            UpdateStoppedUi();
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
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
        _statusBadge.Text = "Stopped";
        _statusBadge.Foreground = new SolidColorBrush(Colors.Gray);

        // 恢复为 Start 状态（单色播放图标 + Accent 强调色）
        _startStopIcon.Symbol = Symbol.Play;
        _startStopText.Text = "Start";
        _startStopBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    private async Task ShowExtraArgsDialogAsync()
    {
        // 如果已有参数，按空格或换行拆开，方便多行展示编辑
        string initialText = _extraBox.Text.Replace(" --", "\n--");

        var editBox = new TextBox
        {
            Text = initialText,
            AcceptsReturn = true,
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
            char[] separators = new char[] { '\r', '\n' };
            string[] lines = editBox.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            _extraBox.Text = string.Join(" ", Array.ConvertAll(lines, l => l.Trim()));
        }
    }
}