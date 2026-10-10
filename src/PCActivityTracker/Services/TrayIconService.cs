using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PCActivityTracker.Services;

/// <summary>
/// Lightweight notification-area icon implemented directly with the documented
/// Shell_NotifyIcon API. This avoids loading Windows Forms and System.Drawing
/// solely for a tray icon while the recorder is resident.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int TrayCallbackMessage = 0x8001;
    private const int WindowMessageNull = 0x0000;
    private const int LeftButtonDoubleClick = 0x0203;
    private const int RightButtonUp = 0x0205;
    private const int ContextMenu = 0x007B;
    private const int NotifySelect = 0x0400;
    private const uint IconId = 1;
    private const uint NotifyAdd = 0;
    private const uint NotifyDelete = 2;
    private const uint NotifySetVersion = 4;
    private const uint NotifyMessage = 0x1;
    private const uint NotifyIcon = 0x2;
    private const uint NotifyTip = 0x4;
    private const uint NotifyVersion4 = 4;
    private const uint MenuString = 0;
    private const uint MenuSeparator = 0x0800;
    private const uint TrackReturnCommand = 0x0100;
    private const uint TrackRightButton = 0x0002;
    private const uint ShowCommand = 1;
    private const uint ExitCommand = 2;
    private static readonly IntPtr InformationIcon = new(32516);

    private readonly Action _showWindow;
    private readonly HwndSource _source;
    private readonly int _taskbarCreatedMessage;
    private bool _disposed;

    public TrayIconService(Action showWindow)
    {
        _showWindow = showWindow;
        var parameters = new HwndSourceParameters("PCActivityTracker.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _ = SetWindowText(_source.Handle, "PCActivityTracker.Tray");
        _source.AddHook(WindowProcedure);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        if (!AddIcon())
        {
            _source.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "通知領域アイコンを作成できませんでした。");
        }
    }

    private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // Explorer recreates the notification area after it restarts. Existing
            // icons are lost, so register this icon again without restarting tracking.
            if (!_disposed && !AddIcon())
                DiagnosticLogService.Error("TrayIcon.Restore",
                    new Win32Exception(Marshal.GetLastWin32Error(), "通知領域アイコンを復元できませんでした。"));
            handled = true;
            return IntPtr.Zero;
        }

        if (message != TrayCallbackMessage) return IntPtr.Zero;
        var notification = unchecked((int)(lParam.ToInt64() & 0xFFFF));
        switch (notification)
        {
            case LeftButtonDoubleClick:
            case NotifySelect:
                _showWindow();
                handled = true;
                break;
            case RightButtonUp:
            case ContextMenu:
                ShowContextMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private bool AddIcon()
    {
        var data = CreateIconData();
        if (!ShellNotifyIcon(NotifyAdd, ref data)) return false;
        data.VersionOrTimeout = NotifyVersion4;
        _ = ShellNotifyIcon(NotifySetVersion, ref data);
        return true;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            _ = AppendMenu(menu, MenuString, ShowCommand, "表示");
            _ = AppendMenu(menu, MenuSeparator, 0, string.Empty);
            _ = AppendMenu(menu, MenuString, ExitCommand, "終了");
            _ = GetCursorPos(out var cursor);
            _ = SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenu(menu, TrackReturnCommand | TrackRightButton,
                cursor.X, cursor.Y, 0, _source.Handle, IntPtr.Zero);
            _ = PostMessage(_source.Handle, WindowMessageNull, IntPtr.Zero, IntPtr.Zero);
            if (command == ShowCommand) _showWindow();
            else if (command == ExitCommand) App.RequestExit();
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _source.Handle,
        Id = IconId,
        Flags = NotifyMessage | NotifyIcon | NotifyTip,
        CallbackMessage = TrayCallbackMessage,
        Icon = LoadIcon(IntPtr.Zero, InformationIcon),
        Tip = "PC Activity Tracker — ローカル記録中",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateIconData();
        _ = ShellNotifyIcon(NotifyDelete, ref data);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);
}
