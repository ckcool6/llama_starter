using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MyMarkupApp;

public class LlamaInstanceCard : UserControl
{
    private readonly int _id;
    private readonly Func<string> _getExePathFunc;
    private readonly Action<string> _logCallback;

    private Process? _process;
    private readonly Button _startStopBtn;
    private readonly TextBlock _statusBadge;

    // Parameter input fields
    private readonly TextBox _modelBox;
    private readonly TextBox _portBox;
    private readonly TextBox _ctxBox;
    private readonly TextBox _nglBox;
    private readonly TextBox _mmprojBox;
    private readonly TextBox _extraBox;

    public bool IsRunning => _process != null && !_process.HasExited;
    public event Action<LlamaInstanceCard>? DeleteRequested;

    public LlamaInstanceCard(int id, Func<string> getExePathFunc, Action<string> logCallback)
    {
        _id = id;
        _getExePathFunc = getExePathFunc;
        _logCallback = logCallback;

        // Status badge and control buttons
        _statusBadge = new TextBlock
        {
            Text = "Stopped",
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        _startStopBtn = new Button
        {
            Content = "▶ Start",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        _startStopBtn.Click += (s, e) => ToggleStartStop();

        var deleteBtn = new Button { Content = "🗑️ Delete" };
        deleteBtn.Click += (s, e) =>
        {
            Stop();
            DeleteRequested?.Invoke(this);
        };

        // Default input field initialization
        _modelBox = new TextBox { PlaceholderText = "Model path (.gguf)", Width = 360 };
        _portBox = new TextBox { Text = (8080 + id - 1).ToString(), Width = 80 };
        _ctxBox = new TextBox { Text = "4096", Width = 80 };
        _nglBox = new TextBox { Text = "99", Width = 80 };
        _mmprojBox = new TextBox { PlaceholderText = "Multimodal projector (.gguf, optional)", Width = 260 };
        _extraBox = new TextBox { PlaceholderText = "Extra args (e.g. --host 0.0.0.0)", Width = 360 };

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
                    // Header: Instance title, status, action buttons
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
                                Children = { _startStopBtn, deleteBtn }
                            }
                        }
                    },
                    // Row 1: Model path
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "-m (Model):", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _modelBox
                        }
                    },
                    // Row 2: Common config (Port / Context / NGL)
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
                    // Row 3: mmproj (Vision projector)
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "--mmproj:", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _mmprojBox
                        }
                    },
                    // Row 4: Extra custom arguments
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = "Extra Args:", Width = 85, VerticalAlignment = VerticalAlignment.Center },
                            _extraBox
                        }
                    }
                }
            }
        };

        Grid.SetColumn((FrameworkElement)((Grid)((StackPanel)((Border)Content).Child).Children[0]).Children[1], 1);
    }

    public void Start()
    {
        if (IsRunning) return;

        string exePath = _getExePathFunc();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            _logCallback($"[Instance #{_id}] ❌ Start failed: Executable path is invalid or file does not exist!");
            return;
        }

        // Construct launch arguments
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
            _process.OutputDataReceived += (s, e) => { if (e.Data != null) _logCallback($"[#{_id}] {e.Data}"); };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logCallback($"[#{_id}] {e.Data}"); };
            _process.Exited += (s, e) => DispatcherQueue.TryEnqueue(UpdateStoppedUi);

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _statusBadge.Text = $"Running (PID: {_process.Id})";
            _statusBadge.Foreground = new SolidColorBrush(Colors.SeaGreen);
            _startStopBtn.Content = "⏹ Stop";
            _startStopBtn.Style = null; // Revert to standard style
            _logCallback($"[Instance #{_id}] 🚀 Started successfully! Args: {args}");
        }
        catch (Exception ex)
        {
            _logCallback($"[Instance #{_id}] ❌ Start error: {ex.Message}");
            UpdateStoppedUi();
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try
        {
            _process?.Kill(entireProcessTree: true); // Force-kill entire process tree to prevent VRAM leak
            _process?.Dispose();
            _process = null;
            _logCallback($"[Instance #{_id}] ⏹ Terminated.");
        }
        catch (Exception ex)
        {
            _logCallback($"[Instance #{_id}] Stop error: {ex.Message}");
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
        _startStopBtn.Content = "▶ Start";
        _startStopBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }
}