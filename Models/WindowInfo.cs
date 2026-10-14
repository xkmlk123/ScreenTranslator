namespace ScreenTranslator.Models;

/// <summary>
/// 系统窗口信息
/// </summary>
public class WindowInfo
{
    /// <summary>窗口句柄</summary>
    public IntPtr Hwnd { get; set; }

    /// <summary>窗口标题</summary>
    public string Title { get; set; } = string.Empty;

    public override string ToString() => Title;
}
