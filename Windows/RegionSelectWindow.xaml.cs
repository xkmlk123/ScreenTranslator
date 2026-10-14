using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScreenTranslator.Windows;

/// <summary>
/// 全屏框选遮罩窗：让用户拖拽出一个矩形，选出要识别的区域。
///
/// 为什么要单独建一个窗口：
/// <see cref="OverlayWindow"/> 设了 <c>WS_EX_TRANSPARENT</c>（鼠标完全穿透），
/// 它收不到任何鼠标事件，没法在上面做框选。
///
/// 关键实现点：
///   · 铺满整个**虚拟桌面**（多显示器下也能框到副屏），与 OverlayWindow 同一套
///     GetSystemMetrics(76..79) + GetDpiForWindow 换算。
///   · **不加** WS_EX_TRANSPARENT（会穿透，收不到鼠标）、
///     **不加** WS_EX_NOACTIVATE（会导致窗口不拿键盘焦点 → Esc 失效）。
///     因此要显式 Activate/Focus/Keyboard.Focus 抢一次焦点。
///   · 用 <c>ShowDialog()</c> 模态返回，结果是公共属性
///     <see cref="SelectedScreenPx"/>（屏幕物理像素），
///     不用自定义事件 → 无事件订阅泄漏、焦点自动归还。
/// </summary>
public partial class RegionSelectWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;   // 分层窗（AllowsTransparency 需要）
    private const int WS_EX_TOOLWINDOW = 0x00000080; // 不在 Alt+Tab 显示

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>框选结果：屏幕物理像素矩形。null = 用户取消（Esc / 尺寸过小）。</summary>
    public Rect? SelectedScreenPx { get; private set; }

    /// <summary>最小可接受尺寸（DIP）。低于此视为误触，按取消处理。</summary>
    private const double MinSizeDip = 10;

    private double _dpiScale = 1.0;
    private int _vx, _vy;

    private bool _dragging;
    private Point _startDip;
    private Rectangle? _rect;

    public RegionSelectWindow()
    {
        InitializeComponent();
        Loaded += RegionSelectWindow_Loaded;
    }

    private void RegionSelectWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // 注意：**不加** WS_EX_TRANSPARENT / WS_EX_NOACTIVATE
        // —— 前者会让鼠标穿透收不到拖拽，后者会让窗口拿不到键盘焦点导致 Esc 失效。
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW);

        // 铺满整个虚拟桌面（多显示器 / 混合 DPI 与 OverlayWindow 用同一套公式）
        uint dpi = GetDpiForWindow(hwnd);
        _dpiScale = (dpi == 0 ? 96 : dpi) / 96.0;
        _vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        _vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vw > 0 && vh > 0)
        {
            Left = _vx / _dpiScale;
            Top = _vy / _dpiScale;
            Width = vw / _dpiScale;
            Height = vh / _dpiScale;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // 抢焦点：保证键盘事件（Esc）能收到
        Activate();
        Focus();
        Keyboard.Focus(this);
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsMouseCaptured) CaptureMouse();

        _dragging = true;
        _startDip = e.GetPosition(this);

        // 青色虚线选择框（40% 填充，让选中区域比周围亮，看得清）
        _rect ??= new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 200, 255)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 200, 255))
        };
        if (!SelectCanvas.Children.Contains(_rect))
            SelectCanvas.Children.Add(_rect);

        Canvas.SetLeft(_rect, _startDip.X);
        Canvas.SetTop(_rect, _startDip.Y);
        _rect.Width = 0;
        _rect.Height = 0;

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _rect == null) return;

        var p = e.GetPosition(this);

        // 反向拖拽也要正确（取 min / abs）
        double x = Math.Min(_startDip.X, p.X);
        double y = Math.Min(_startDip.Y, p.Y);
        double w = Math.Abs(p.X - _startDip.X);
        double h = Math.Abs(p.Y - _startDip.Y);

        Canvas.SetLeft(_rect, x);
        Canvas.SetTop(_rect, y);
        _rect.Width = w;
        _rect.Height = h;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;

        var p = e.GetPosition(this);
        double x = Math.Min(_startDip.X, p.X);
        double y = Math.Min(_startDip.Y, p.Y);
        double w = Math.Abs(p.X - _startDip.X);
        double h = Math.Abs(p.Y - _startDip.Y);

        // 误触（点一下没拖）：按取消处理，不覆盖用户原有选择
        if (w < MinSizeDip || h < MinSizeDip)
        {
            SelectedScreenPx = null;
            DialogResult = false;
            Close();
            return;
        }

        // DIP（本窗口坐标）→ 屏幕物理像素
        SelectedScreenPx = new Rect(
            _vx + x * _dpiScale,
            _vy + y * _dpiScale,
            w * _dpiScale,
            h * _dpiScale);

        DialogResult = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            SelectedScreenPx = null;
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 保险：无论如何都释放鼠标捕获，避免残留捕获卡住其他窗口
        if (IsMouseCaptured) ReleaseMouseCapture();
        base.OnClosed(e);
    }
}
