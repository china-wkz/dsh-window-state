// dsh-window-state helper
//
// Compiled once on first use by the plugin (csc.exe from .NET Framework) and
// cached next to the state file. Keeps the DSH desktop window geometry:
//
//   1. fast-polls until the main window exists, applies the saved geometry
//      before the shell shows it;
//   2. installs WinEvent hooks so the file is rewritten on the events the user
//      actually produces (move / resize / maximize / hide), not on a timer;
//   3. keeps a slow heartbeat (2 s by default) as a safety net for anything a
//      hook did not deliver, and to notice the window going away.
//
// Target language level is C# 5: csc.exe from .NET Framework 4.x has no Roslyn.

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Program
{
    // ---- Win32 -----------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // ---- constants -------------------------------------------------------

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const int SW_MAXIMIZE = 3;

    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const int MinWidth = 520;
    private const int MinHeight = 600;
    private const int DebounceMs = 250;

    // ---- state -----------------------------------------------------------

    private static readonly object Gate = new object();
    private static IntPtr _target = IntPtr.Zero;
    private static uint _targetProcess;
    private static IntPtr _hookChange = IntPtr.Zero;
    private static IntPtr _hookState = IntPtr.Zero;
    private static WinEventDelegate _callback;

    private static string _statePath;
    private static int _interval = 2000;
    private static string _exeName = "DeepSeek Harness.exe";
    private static string _titleHint = "DeepSeek Harness";

    private static int _x;
    private static int _y;
    private static int _width;
    private static int _height;
    private static bool _hasRect;
    private static bool _maximized;
    private static int _dpi = 96;

    private static readonly object SaveGate = new object();
    private static string _lastSignature = "";
    private static int _lastWriteTick;
    private static bool _maximizePending;
    private static int _missedTicks;

    // ---- entry point -----------------------------------------------------

    private static int Main(string[] args)
    {
        ParseArguments(args);
        if (_statePath == null)
        {
            Console.Error.WriteLine("window-state: --state is required");
            return 2;
        }

        LoadState();

        // Phase 1: find the window quickly so the geometry is in place before the
        // shell calls show(). The desktop shell creates it hidden at 1280x820.
        int waited = 0;
        while (waited < 120000)
        {
            IntPtr found = FindMainWindow(true);
            if (found != IntPtr.Zero)
            {
                _target = found;
                break;
            }
            Thread.Sleep(100);
            waited += 100;
        }

        if (_target == IntPtr.Zero)
        {
            // The plugin may start before the shell creates its window on a very
            // slow boot; keep waiting at a lower rate instead of giving up.
            while (FindMainWindow(true) == IntPtr.Zero)
            {
                Thread.Sleep(1000);
            }
            _target = FindMainWindow(true);
        }

        RestoreBounds();
        InstallHooks();

        Timer heartbeat = new Timer(OnHeartbeat, null, _interval, _interval);

        MSG message;
        while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        GC.KeepAlive(heartbeat);
        if (_hookChange != IntPtr.Zero) UnhookWinEvent(_hookChange);
        if (_hookState != IntPtr.Zero) UnhookWinEvent(_hookState);
        return 0;
    }

    // ---- argument parsing ------------------------------------------------

    private static void ParseArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            string value = i + 1 < args.Length ? args[i + 1] : null;
            if (name == "--state" && value != null) { _statePath = value; i++; }
            else if (name == "--interval" && value != null)
            {
                int parsed;
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 500 && parsed <= 60000)
                {
                    _interval = parsed;
                }
                i++;
            }
            else if (name == "--exe" && value != null) { _exeName = value; i++; }
            else if (name == "--title" && value != null) { _titleHint = value; i++; }
        }
    }

    // ---- state file ------------------------------------------------------

    private static void LoadState()
    {
        string text;
        try
        {
            if (!File.Exists(_statePath)) return;
            text = File.ReadAllText(_statePath, Encoding.UTF8);
        }
        catch
        {
            return;
        }

        int rectStart = text.IndexOf("\"rect\"", StringComparison.Ordinal);
        if (rectStart < 0) return;
        int braceStart = text.IndexOf('{', rectStart);
        int braceEnd = braceStart < 0 ? -1 : text.IndexOf('}', braceStart);
        if (braceStart < 0 || braceEnd < 0) return;

        string rectJson = text.Substring(braceStart, braceEnd - braceStart + 1);
        _x = ReadInt(rectJson, "x", 0);
        _y = ReadInt(rectJson, "y", 0);
        _width = ReadInt(rectJson, "width", 0);
        _height = ReadInt(rectJson, "height", 0);
        _dpi = ReadInt(text, "dpi", 96);
        _maximized = ReadBool(text, "maximized", false);
        _hasRect = _width > 0 && _height > 0;
    }

    private static int ReadInt(string json, string key, int fallback)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return fallback;
        int colon = json.IndexOf(':', at);
        if (colon < 0) return fallback;
        int end = colon + 1;
        while (end < json.Length && (json[end] == ' ' || json[end] == '\t')) end++;
        int value;
        if (end < json.Length && json[end] == '-') end++;
        while (end < json.Length && char.IsDigit(json[end])) end++;
        string digits = json.Substring(colon + 1, end - colon - 1).Trim();
        if (digits.Length == 0) return fallback;
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
    }

    private static bool ReadBool(string json, string key, bool fallback)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return fallback;
        int colon = json.IndexOf(':', at);
        if (colon < 0) return fallback;
        int valueAt = colon + 1;
        while (valueAt < json.Length && (json[valueAt] == ' ' || json[valueAt] == '\t')) valueAt++;
        if (string.Compare(json, valueAt, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0) return true;
        if (string.Compare(json, valueAt, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) return false;
        return fallback;
    }

    private static void WriteState()
    {
        string text = "{\"rect\":{\"x\":" + _x.ToString(CultureInfo.InvariantCulture)
            + ",\"y\":" + _y.ToString(CultureInfo.InvariantCulture)
            + ",\"width\":" + _width.ToString(CultureInfo.InvariantCulture)
            + ",\"height\":" + _height.ToString(CultureInfo.InvariantCulture)
            + "},\"maximized\":" + (_maximized ? "true" : "false")
            + ",\"dpi\":" + _dpi.ToString(CultureInfo.InvariantCulture)
            + ",\"savedAt\":\"" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\"}";

        try
        {
            string directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temporary = _statePath + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Copy(temporary, _statePath, true);
            File.Delete(temporary);
        }
        catch
        {
            // Persistence is best effort; never crash the helper over it.
        }
    }

    // ---- window discovery ------------------------------------------------

    private static IntPtr FindMainWindow(bool requireLarge)
    {
        IntPtr best = IntPtr.Zero;
        long bestScore = -1;

        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            StringBuilder cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, 256);
            if (cls.ToString() != "Chrome_WidgetWin_1") return true;

            uint processId;
            GetWindowThreadProcessId(hWnd, out processId);
            if (processId == 0) return true;

            string image = ProcessImage(processId);
            if (image == null) return true;
            if (!string.Equals(Path.GetFileName(image), _exeName, StringComparison.OrdinalIgnoreCase)) return true;

            RECT rect;
            if (!GetWindowRect(hWnd, out rect)) return true;
            long area = (long)rect.Width * (long)rect.Height;
            if (area <= 0) return true;

            // Before the restore the main window is still at the shell's creation
            // size, so ignore transient narrow windows (welcome, update dialogs).
            if (requireLarge && (rect.Width < 900 || rect.Height < 500)) return true;

            StringBuilder title = new StringBuilder(512);
            GetWindowTextW(hWnd, title, 512);
            string text = title.ToString();

            long score;
            if (text.IndexOf(_titleHint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score = 1000000000000L + area;
            }
            else if (area >= 200000)
            {
                score = area;
            }
            else
            {
                score = -1;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static string ProcessImage(uint processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            StringBuilder buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;
            return buffer.ToString(0, (int)size);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---- geometry --------------------------------------------------------

    private static void RestoreBounds()
    {
        lock (Gate)
        {
            if (!_hasRect) return;

            int x = _x, y = _y, width = _width, height = _height;
            if (width < MinWidth) width = MinWidth;
            if (height < MinHeight) height = MinHeight;

            int virtualX = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int virtualY = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int virtualWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int virtualHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (virtualWidth > 0 && width > virtualWidth) width = virtualWidth;
            if (virtualHeight > 0 && height > virtualHeight) height = virtualHeight;

            if (virtualWidth > 0 && virtualHeight > 0)
            {
                if (x < virtualX) x = virtualX;
                if (y < virtualY) y = virtualY;
                if (x + width > virtualX + virtualWidth) x = virtualX + virtualWidth - width;
                if (y + height > virtualY + virtualHeight) y = virtualY + virtualHeight - height;
            }

            SetWindowPos(_target, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            _maximizePending = _maximized;
        }
    }

    private static void Sample(bool force)
    {
        lock (SaveGate)
        {
            if (_target == IntPtr.Zero || !IsWindow(_target)) return;

            RECT rect;
            if (!GetWindowRect(_target, out rect)) return;
            if (rect.Width < 200 || rect.Height < 200) return;
            if (IsIconic(_target)) return;

            bool zoomed = IsZoomed(_target);
            if (!zoomed)
            {
                _x = rect.Left;
                _y = rect.Top;
                _width = rect.Width;
                _height = rect.Height;
                _dpi = CurrentDpi();
                _hasRect = true;
            }
            _maximized = zoomed;

            string signature = _x + "," + _y + "," + _width + "," + _height + "," + (_maximized ? "1" : "0") + "," + _dpi;
            if (!force && signature == _lastSignature) return;

            int now = Environment.TickCount;
            if (!force && now - _lastWriteTick < DebounceMs) return;

            _lastSignature = signature;
            _lastWriteTick = now;
            WriteState();
        }
    }

    private static int CurrentDpi()
    {
        try
        {
            uint dpi = GetDpiForWindow(_target);
            if (dpi >= 96) return (int)dpi;
        }
        catch
        {
            // GetDpiForWindow is missing before Windows 10 1607.
        }
        return 96;
    }

    // ---- events ----------------------------------------------------------

    private static void InstallHooks()
    {
        if (_hookChange != IntPtr.Zero) UnhookWinEvent(_hookChange);
        if (_hookState != IntPtr.Zero) UnhookWinEvent(_hookState);

        uint processId;
        GetWindowThreadProcessId(_target, out processId);
        _targetProcess = processId;

        _callback = OnWinEvent;
        // Scope both hooks to the DSH process: system-wide location events from
        // every other window would otherwise wake this callback constantly.
        _hookChange = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _callback, _targetProcess, 0, WINEVENT_OUTOFCONTEXT);
        _hookState = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, _callback, _targetProcess, 0, WINEVENT_OUTOFCONTEXT);
    }

    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hWnd != _target) return;
        if (idObject != 0 || idChild != 0) return;

        if (eventType == EVENT_OBJECT_DESTROY)
        {
            _target = IntPtr.Zero;
            return;
        }

        if (eventType == EVENT_OBJECT_LOCATIONCHANGE)
        {
            Sample(false);
            return;
        }

        // SHOW / HIDE: the rectangle is still valid; record it once.
        Sample(false);
    }

    private static void OnHeartbeat(object state)
    {
        try
        {
            if (_target == IntPtr.Zero || !IsWindow(_target))
            {
                _missedTicks++;
                if (_missedTicks >= 15)
                {
                    Environment.Exit(0);
                    return;
                }
                IntPtr found = FindMainWindow(false);
                if (found != IntPtr.Zero)
                {
                    _target = found;
                    _missedTicks = 0;
                    InstallHooks();
                    RestoreBounds();
                }
                return;
            }

            _missedTicks = 0;

            if (_maximizePending && IsWindowVisible(_target))
            {
                if (!IsZoomed(_target)) ShowWindow(_target, SW_MAXIMIZE);
                _maximizePending = false;
            }

            Sample(false);
        }
        catch
        {
            // A timer callback must never take the process down.
        }
    }
}
