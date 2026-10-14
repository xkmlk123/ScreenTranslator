using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenTranslator.Services;

/// <summary>
/// 全局热键注册（基于 RegisterHotKey）。
/// 用于在任意窗口前台时启停翻译，无需切回主面板。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5C00;
    private bool _disposed;

    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
    }

    /// <summary>注册一个热键，成功返回 true。key 形如 "Alt+T"、"Ctrl+Shift+S"。</summary>
    public bool Register(string shortcut, Action action)
    {
        if (!TryParse(shortcut, out uint modifiers, out uint vk)) return false;

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, modifiers, vk)) return false;

        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_actions.TryGetValue(id, out var action))
            {
                action();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>解析 "Alt+T" 之类字符串</summary>
    public static bool TryParse(string shortcut, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(shortcut)) return false;

        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in parts)
        {
            string p = raw.ToLowerInvariant();
            switch (p)
            {
                case "alt": modifiers |= MOD_ALT; break;
                case "ctrl":
                case "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    // 主键
                    if (p.Length == 1 && p[0] >= 'a' && p[0] <= 'z')
                        vk = (uint)char.ToUpperInvariant(p[0]);
                    else if (p.Length == 1 && p[0] >= '0' && p[0] <= '9')
                        vk = p[0];
                    else if (p.StartsWith('f') && int.TryParse(p[1..], out int f) && f >= 1 && f <= 24)
                        vk = (uint)(0x70 + f - 1);
                    else
                        return false;
                    break;
            }
        }

        return vk != 0 && modifiers != 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var id in _actions.Keys.ToList())
        {
            try { UnregisterHotKey(_hwnd, id); } catch { }
        }
        _actions.Clear();
        try { _source.RemoveHook(WndProc); } catch { }
    }
}
