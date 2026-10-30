using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RekaCoop.Dev;

// Test copies only: name each window after its instance and, with `-cooptile i/n`, keep it windowed in
// slot i of n columns across the primary monitor. The game reapplies its saved fullscreen setting after
// boot and on scene loads, so this re-checks once a second. `-coopscreen m:p` instead puts it on monitor m
// (0 = primary, 1 = the next one, ...) at p percent of that monitor's work area, centred, 16:9.
internal static class WindowTitle
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT rect, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    private const uint SPI_GETWORKAREA = 0x0030;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    private static IntPtr _hwnd;
    private static int _tile = -1, _tiles;
    private static int _monitor = -1, _percent;
    private static float _nextCheck;
    private static int _lastW, _lastH;

    public static void Tick()
    {
        if (Instance.Name == null) return;  // dev test copies only
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (_hwnd == IntPtr.Zero) return;
            ParseTile();
        }
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 1f;

        SetWindowTextW(_hwnd, $"Reka - {Instance.Name.ToUpperInvariant()} ({Instance.Role})");
        if (_tile < 0 && _monitor < 0) return;

        // Screen.mainWindowDisplayInfo is stripped from this build; ask Windows for the work area.
        var area = new RECT();
        int w, h, x, y;
        if (_monitor >= 0)
        {
            area = WorkArea(_monitor);
            int aw = area.Right - area.Left, ah = area.Bottom - area.Top - 40;  // room for the title bar
            w = Math.Min(aw * _percent / 100, ah * _percent / 100 * 16 / 9);
            h = w * 9 / 16;
            x = area.Left + (aw - w) / 2;
            y = area.Top + (ah - h) / 2;
        }
        else
        {
            SystemParametersInfoW(SPI_GETWORKAREA, 0, ref area, 0);
            w = (area.Right - area.Left) / _tiles;
            h = Math.Min(w * 9 / 16, area.Bottom - area.Top - 80);
            x = area.Left + _tile * w;
            y = area.Top;
        }
        if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != w || Screen.height != h)
        {
            Screen.SetResolution(w, h, FullScreenMode.Windowed);
            _lastW = -1;  // reposition after the resize lands
            return;
        }
        if (_lastW == w && _lastH == h) return;
        _lastW = w;
        _lastH = h;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // Monitor n's work area: the primary first, then the rest left to right; the primary if there is no monitor n.
    private static RECT WorkArea(int n)
    {
        var all = new System.Collections.Generic.List<MONITORINFO>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, dc, r, d) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(m, ref info)) all.Add(info);
            return true;
        }, IntPtr.Zero);
        all.Sort((a, b) => (b.Flags & 1).CompareTo(a.Flags & 1) is var c && c != 0 ? c : a.Monitor.Left.CompareTo(b.Monitor.Left));
        if (all.Count == 0) { var area = new RECT(); SystemParametersInfoW(SPI_GETWORKAREA, 0, ref area, 0); return area; }
        return all[n < all.Count ? n : 0].Work;
    }

    private static void ParseTile()
    {
        var args = Environment.GetCommandLineArgs();
        int s = Array.FindIndex(args, a => string.Equals(a, "-coopscreen", StringComparison.OrdinalIgnoreCase));
        if (s >= 0 && s + 1 < args.Length)
        {
            var mp = args[s + 1].Split(':');
            if (mp.Length == 2 && int.TryParse(mp[0], out int m) && int.TryParse(mp[1], out int p) && p > 0 && p <= 100)
            {
                _monitor = m;
                _percent = p;
                return;
            }
        }
        int i = Array.FindIndex(args, a => string.Equals(a, "-cooptile", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return;
        var parts = args[i + 1].Split('/');
        if (parts.Length == 2 && int.TryParse(parts[0], out int tile) && int.TryParse(parts[1], out int tiles) && tiles > 0)
        {
            _tile = tile;
            _tiles = tiles;
        }
    }
}
