using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyMarkupApp;

public sealed class TrayIconManager : IDisposable
{
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYICON = WM_USER + 2048;

    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;

    private const int NIF_MESSAGE = 0x0001;
    private const int NIF_ICON = 0x0002;
    private const int NIF_TIP = 0x0004;
    private const int NIM_MODIFY = 0x0001;

    private const int NIM_ADD = 0x0000;
    private const int NIM_DELETE = 0x0002;

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int CMD_RESTORE = 1001;
    private const int CMD_EXIT = 1002;

    private readonly nint _hwnd;
    private readonly SubclassProc _subclassProc;
    private bool _isCreated;
    private const nuint SubclassId = 999;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;
    private const int NIF_INFO = 0x0010;
    private const int NIIF_INFO = 0x0001;
    private const uint NIN_BALLOONUSERCLICK = 0x0405; // 用户点击了通知气泡

    public TrayIconManager(nint hwnd, string tooltip)
    {
        _hwnd = hwnd;
        _subclassProc = WindowSubclassHandler;

        // 挂载 Win32 窗口消息子类化，监听托盘通知
        SetWindowSubclass(_hwnd, _subclassProc, SubclassId, 0);

        // 创建系统托盘图标
        CreateTrayIcon(tooltip);
    }

    private void CreateTrayIcon(string tooltip)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (int)WM_TRAYICON,
            hIcon = GetAppIcon(),
            szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip
        };

        _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
    }
    private nint WindowSubclassHandler(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        if (uMsg == WM_TRAYICON)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);

            // 左键点击、双击托盘图标，或者直接点击弹出的通知横幅：都还原主窗口
            if (mouseMsg is WM_LBUTTONUP or WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK)
            {
                RestoreRequested?.Invoke();
                return 0;
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
                return 0;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
    private void ShowContextMenu()
    {
        GetCursorPos(out POINT pt);
        nint hMenu = CreatePopupMenu();

        AppendMenu(hMenu, MF_STRING, (nuint)CMD_RESTORE, "显示主窗口");
        AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
        AppendMenu(hMenu, MF_STRING, (nuint)CMD_EXIT, "退出程序");

        // Win32 要求在 TrackPopupMenu 之前将窗口置顶，否则点击外部菜单不会消失
        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, nint.Zero);
        PostMessage(_hwnd, 0, nint.Zero, nint.Zero);
        DestroyMenu(hMenu);

        if (cmd == CMD_RESTORE)
        {
            RestoreRequested?.Invoke();
        }
        else if (cmd == CMD_EXIT)
        {
            ExitRequested?.Invoke();
        }
    }

    private nint GetAppIcon()
    {
        // 尝试从当前进程 Exe 提取图标，若失败则加载默认系统应用图标
        nint hIcon = ExtractIcon(nint.Zero, Environment.ProcessPath ?? string.Empty, 0);
        if (hIcon == nint.Zero || hIcon == 1)
        {
            hIcon = LoadIcon(nint.Zero, (nint)32512); // IDI_APPLICATION
        }
        return hIcon;
    }

    /// <summary>
    /// 弹出系统原生托盘通知
    /// </summary>
    public void ShowNotification(string title, string message)
    {
        if (!_isCreated) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfo = message.Length > 255 ? message[..255] : message,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            dwInfoFlags = NIIF_INFO
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void Dispose()
    {
        if (_isCreated)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }

        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
    }

    #region P/Invoke

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIcon(nint hInst, string pszExeFileName, int nIconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    #endregion
}