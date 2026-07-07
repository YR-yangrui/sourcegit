using System.Runtime.InteropServices;

namespace SourceGit.Updater;

public class UpdaterUI
{
    private IntPtr _hWnd;
    private IntPtr _hLabel;
    private IntPtr _hProgressBar;
    private Win32UI.WndProc? _wndProcDelegate;
    private Win32UI.WndProc? _progressBarWndProc;
    private Win32UI.WndProc? _labelWndProc;
    private IntPtr _hFont;
    private IntPtr _hWhiteBrush;
    private double _currentProgress;
    private string _labelText = "准备中...";

    public void CreateWindow()
    {
        IntPtr hInstance = Win32UI.GetModuleHandle(null);

        _hFont = Win32UI.CreateFont(
            -14,
            0,
            0, 0,
            Win32UI.FW_NORMAL,
            0, 0, 0,
            1,
            0, 0,
            Win32UI.CLEARTYPE_QUALITY,
            0,
            "Microsoft YaHei UI");

        _hWhiteBrush = Win32UI.GetStockObject(Win32UI.WHITE_BRUSH);

        _labelWndProc = LabelWndProc;
        Win32UI.WNDCLASSEX labelWc = new()
        {
            cbSize = Marshal.SizeOf(typeof(Win32UI.WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_labelWndProc),
            hInstance = hInstance,
            hCursor = Win32UI.LoadCursor(IntPtr.Zero, Win32UI.IDC_ARROW),
            hbrBackground = Win32UI.GetSysColorBrush(Win32UI.COLOR_WINDOW),
            lpszClassName = "SourceGitUpdaterLabel"
        };
        Win32UI.RegisterClassEx(ref labelWc);

        _progressBarWndProc = ProgressBarWndProc;
        Win32UI.WNDCLASSEX progressWc = new()
        {
            cbSize = Marshal.SizeOf(typeof(Win32UI.WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_progressBarWndProc),
            hInstance = hInstance,
            hCursor = Win32UI.LoadCursor(IntPtr.Zero, Win32UI.IDC_ARROW),
            hbrBackground = Win32UI.GetSysColorBrush(Win32UI.COLOR_WINDOW),
            lpszClassName = "SourceGitUpdaterProgressBar"
        };
        Win32UI.RegisterClassEx(ref progressWc);

        _wndProcDelegate = WindowProc;
        Win32UI.WNDCLASSEX wc = new()
        {
            cbSize = Marshal.SizeOf(typeof(Win32UI.WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hCursor = Win32UI.LoadCursor(IntPtr.Zero, Win32UI.IDC_ARROW),
            hbrBackground = Win32UI.GetSysColorBrush(Win32UI.COLOR_WINDOW),
            lpszClassName = "SourceGitUpdaterClass"
        };
        Win32UI.RegisterClassEx(ref wc);

        _hWnd = Win32UI.CreateWindowEx(
            0,
            "SourceGitUpdaterClass",
            "SourceGit 自动更新器",
            Win32UI.WS_OVERLAPPED | Win32UI.WS_CAPTION | Win32UI.WS_SYSMENU | Win32UI.WS_MINIMIZEBOX,
            (Win32UI.GetSystemMetrics(0) - 600) / 2,
            (Win32UI.GetSystemMetrics(1) - 150) / 2,
            600, 150,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        LoadWindowIcon();

        _hLabel = Win32UI.CreateWindowEx(
            0,
            "SourceGitUpdaterLabel",
            "准备中...",
            Win32UI.WS_CHILD,
            12, 20, 560, 20,
            _hWnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        _hProgressBar = Win32UI.CreateWindowEx(
            0,
            "SourceGitUpdaterProgressBar",
            "",
            Win32UI.WS_CHILD,
            12, 50, 560, 25,
            _hWnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        Win32UI.ShowWindow(_hLabel, Win32UI.SW_SHOW);
        Win32UI.ShowWindow(_hProgressBar, Win32UI.SW_SHOW);
        Win32UI.ShowWindow(_hWnd, Win32UI.SW_SHOW);
    }

    public void RunMessageLoop()
    {
        Win32UI.MSG msg;
        while (Win32UI.GetMessage(out msg, IntPtr.Zero, 0, 0))
        {
            Win32UI.TranslateMessage(ref msg);
            Win32UI.DispatchMessage(ref msg);
        }
    }

    public void UpdateStatus(string status, double progress)
    {
        _currentProgress = Math.Max(0, Math.Min(100, progress));
        _labelText = status;
        Win32UI.InvalidateRect(_hLabel, IntPtr.Zero, false);
        Win32UI.InvalidateRect(_hProgressBar, IntPtr.Zero, false);
    }

    public void ShowMessageBox(string message, string title, uint type = 0x10)
    {
        Win32UI.MessageBox(_hWnd, message, title, type);
    }

    public int ShowRetryDialog(string message, string title)
    {
        return Win32UI.MessageBox(_hWnd, message, title, 0x35);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32UI.WM_CLOSE)
            return IntPtr.Zero;

        if (msg == Win32UI.WM_DESTROY)
        {
            if (_hFont != IntPtr.Zero)
            {
                Win32UI.DeleteObject(_hFont);
                _hFont = IntPtr.Zero;
            }

            Win32UI.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        if (msg == Win32UI.WM_ERASEBKGND)
            return new IntPtr(1);

        if (msg == Win32UI.WM_PAINT)
        {
            IntPtr hdc = Win32UI.BeginPaint(hWnd, out var ps);
            Win32UI.GetClientRect(hWnd, out var rect);
            Win32UI.FillRect(hdc, ref rect, _hWhiteBrush);
            Win32UI.EndPaint(hWnd, ref ps);
            return IntPtr.Zero;
        }

        if (msg == Win32UI.WM_SHOWWINDOW && wParam != IntPtr.Zero)
        {
            Win32UI.InvalidateRect(_hLabel, IntPtr.Zero, true);
            Win32UI.InvalidateRect(_hProgressBar, IntPtr.Zero, true);
        }

        return Win32UI.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr LabelWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32UI.WM_ERASEBKGND)
            return new IntPtr(1);

        if (msg != Win32UI.WM_PAINT)
            return Win32UI.DefWindowProc(hWnd, msg, wParam, lParam);

        IntPtr hdc = Win32UI.BeginPaint(hWnd, out var ps);
        Win32UI.GetClientRect(hWnd, out var rect);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        IntPtr memDC = Win32UI.CreateCompatibleDC(hdc);
        IntPtr memBitmap = Win32UI.CreateCompatibleBitmap(hdc, width, height);
        IntPtr oldBitmap = Win32UI.SelectObject(memDC, memBitmap);

        Win32UI.FillRect(memDC, ref rect, _hWhiteBrush);
        IntPtr oldFont = Win32UI.SelectObject(memDC, _hFont);
        Win32UI.SetTextColor(memDC, 0x00000000);
        Win32UI.SetBkMode(memDC, Win32UI.TRANSPARENT);
        Win32UI.DrawText(memDC, _labelText, -1, ref rect, Win32UI.DT_LEFT | Win32UI.DT_VCENTER | Win32UI.DT_SINGLELINE);
        Win32UI.SelectObject(memDC, oldFont);

        Win32UI.BitBlt(hdc, 0, 0, width, height, memDC, 0, 0, Win32UI.SRCCOPY);
        Win32UI.SelectObject(memDC, oldBitmap);
        Win32UI.DeleteObject(memBitmap);
        Win32UI.DeleteDC(memDC);
        Win32UI.EndPaint(hWnd, ref ps);
        return IntPtr.Zero;
    }

    private IntPtr ProgressBarWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32UI.WM_ERASEBKGND)
            return new IntPtr(1);

        if (msg != Win32UI.WM_PAINT)
            return Win32UI.DefWindowProc(hWnd, msg, wParam, lParam);

        IntPtr hdc = Win32UI.BeginPaint(hWnd, out var ps);
        Win32UI.GetClientRect(hWnd, out var rect);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        IntPtr memDC = Win32UI.CreateCompatibleDC(hdc);
        IntPtr memBitmap = Win32UI.CreateCompatibleBitmap(hdc, width, height);
        IntPtr oldBitmap = Win32UI.SelectObject(memDC, memBitmap);

        IntPtr borderBrush = Win32UI.CreateSolidBrush(0x00D0D0D0);
        Win32UI.RECT borderRect = new() { Left = 0, Top = 0, Right = width, Bottom = height };
        Win32UI.FillRect(memDC, ref borderRect, borderBrush);
        Win32UI.DeleteObject(borderBrush);

        Win32UI.RECT bgRect = new() { Left = 1, Top = 1, Right = width - 1, Bottom = height - 1 };
        Win32UI.FillRect(memDC, ref bgRect, _hWhiteBrush);

        int progressWidth = (int)((width - 2) * _currentProgress / 100.0);
        if (progressWidth > 0)
        {
            int step = Math.Max(1, progressWidth / 200);
            for (int i = 0; i < progressWidth; i += step)
            {
                int actualWidth = Math.Min(step, progressWidth - i);
                double ratio = (double)i / Math.Max(1, progressWidth);
                int r = (int)(0 + ratio * 100);
                int g = (int)(120 + ratio * 60);
                int b = (int)(215 + ratio * 40);
                int color = (b << 16) | (g << 8) | r;

                IntPtr lineBrush = Win32UI.CreateSolidBrush(color);
                Win32UI.RECT lineRect = new()
                {
                    Left = 1 + i,
                    Top = 1,
                    Right = 1 + i + actualWidth,
                    Bottom = height - 1
                };
                Win32UI.FillRect(memDC, ref lineRect, lineBrush);
                Win32UI.DeleteObject(lineBrush);
            }
        }

        Win32UI.BitBlt(hdc, 0, 0, width, height, memDC, 0, 0, Win32UI.SRCCOPY);
        Win32UI.SelectObject(memDC, oldBitmap);
        Win32UI.DeleteObject(memBitmap);
        Win32UI.DeleteDC(memDC);
        Win32UI.EndPaint(hWnd, ref ps);
        return IntPtr.Zero;
    }

    private void LoadWindowIcon()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("SourceGit.Updater.icon.ico");
            if (stream == null)
                return;

            byte[] iconData = new byte[stream.Length];
            stream.ReadExactly(iconData, 0, iconData.Length);
            IntPtr hIconLarge = LoadIconFromIcoData(iconData, 32);
            IntPtr hIconSmall = LoadIconFromIcoData(iconData, 16);

            if (hIconLarge != IntPtr.Zero)
                Win32UI.SendMessage(_hWnd, Win32UI.WM_SETICON, new IntPtr(Win32UI.ICON_BIG), hIconLarge);

            if (hIconSmall != IntPtr.Zero)
                Win32UI.SendMessage(_hWnd, Win32UI.WM_SETICON, new IntPtr(Win32UI.ICON_SMALL), hIconSmall);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"图标加载失败: {ex.Message}");
        }
    }

    private static IntPtr LoadIconFromIcoData(byte[] icoData, int desiredSize)
    {
        try
        {
            if (icoData.Length < 6)
                return IntPtr.Zero;

            int iconCount = BitConverter.ToUInt16(icoData, 4);
            int bestIndex = -1;
            int bestSizeDiff = int.MaxValue;

            for (int i = 0; i < iconCount; i++)
            {
                int dirEntryOffset = 6 + (i * 16);
                if (dirEntryOffset + 16 > icoData.Length)
                    break;

                int width = icoData[dirEntryOffset];
                if (width == 0)
                    width = 256;

                int sizeDiff = Math.Abs(width - desiredSize);
                if (sizeDiff < bestSizeDiff)
                {
                    bestSizeDiff = sizeDiff;
                    bestIndex = i;
                }
            }

            if (bestIndex == -1)
                return IntPtr.Zero;

            int entryOffset = 6 + (bestIndex * 16);
            int imageSize = BitConverter.ToInt32(icoData, entryOffset + 8);
            int imageOffset = BitConverter.ToInt32(icoData, entryOffset + 12);

            if (imageOffset + imageSize > icoData.Length)
                return IntPtr.Zero;

            byte[] iconImageData = new byte[imageSize];
            Array.Copy(icoData, imageOffset, iconImageData, 0, imageSize);

            return Win32UI.CreateIconFromResourceEx(
                iconImageData,
                (uint)imageSize,
                true,
                0x00030000,
                desiredSize,
                desiredSize,
                0);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
