using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ScreenTranslator.Models;

namespace ScreenTranslator.Windows;

/// <summary>
/// 全屏透明覆盖层，用于在原文位置显示译文。
///
/// 显示策略（关键）：
/// 逐块绘制。每个 OCR 块用【自己的矩形】放自己的译文，
/// 段落只用来给翻译提供上下文，不作为绘制单位。
///
/// 为什么不用"整段重排"：
/// 整段重排（从段落左上角起排、自动换行、向下扩张）在单行段落上没问题，
/// 但多行段落会让译文整体撑高，从第二行起逐行下坠，
/// 表现为"译文位移、盖不住原文"。逐块绘制从结构上消除了这个问题。
///
/// 窗口特性：鼠标穿透 + 不抢焦点 + 置顶 + 不进任务栏。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;   // 鼠标穿透
    private const int WS_EX_LAYERED = 0x00080000;        // 分层窗口
    private const int WS_EX_NOACTIVATE = 0x08000000;     // 不抢焦点
    private const int WS_EX_TOOLWINDOW = 0x00000080;     // 不在 Alt+Tab 显示
    private const int GWL_EXSTYLE = -20;

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

    // ---- 屏幕捕获排除 ----
    // 用途：让覆盖层不出现在任何"屏幕捕获"结果里（BitBlt 屏幕 DC、系统截图、
    // 录屏、远程协助等），但用户肉眼仍正常看到译文。
    //
    // 为什么必须有：GDI 降级路径在 PrintWindow 失败/被判全同色时，会回退成
    // BitBlt(GetDC(NULL)) 从【屏幕 DC】抓图，而屏幕 DC 反映 DWM 合成后的
    // 最终画面 —— 覆盖层铺满虚拟桌面且 Topmost，译文会被原样抓回，
    // 成为下一帧 OCR 的输入，形成「译文越画越大」的正反馈。
    //
    // 注意：WS_EX_TRANSPARENT 只影响鼠标命中测试，**不防截屏**，不能替代本 API。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

    /// <summary>不限制（默认值）。</summary>
    private const uint WDA_NONE = 0x00000000;
    /// <summary>Win7+：内容只在监视器上显示；被捕获的位置是黑块。</summary>
    private const uint WDA_MONITOR = 0x00000001;
    /// <summary>Win10 2004+（build 19041）：捕获画面里根本不出现该窗口。项目 TFM 26100 已满足。</summary>
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // 虚拟桌面（物理像素），GetSystemMetrics(76..79)
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly List<Rect> _currentLayout = new();
    private int _lastDrawnCount;
    private int _lastSkippedCount;

    // ---------- 渲染循环（阶段 1：渲染与 OCR 解耦） ----------
    // 目标：画面/框以 ~30Hz 跟随窗口移动，而 OCR 仍按自己的节奏（~1Hz）跑。
    // 做法：CompositionTarget.Rendering 是 WPF 原生渲染节拍（与合成器同步、
    // 回调在 UI 线程），用它驱动；每次 Tick 不重建可视树，只改平移量。
    private bool _renderLoopOn;
    private readonly System.Diagnostics.Stopwatch _frameClock = new();
    private long _lastTickMs = -1;
    private double _fpsWindowStartMs = -1;
    private int _fpsWindowFrames;
    private int _measuredFps;

    /// <summary>渲染循环最大帧率（30 = 用户要求的「画面/框跟随 30 帧」）。</summary>
    public int MaxFps { get; set; } = 30;

    /// <summary>由外部（MainWindow）提供的「建树时使用的捕获原点」查询器，返回 (originX, originY, scale)。</summary>
    public Func<(double X, double Y, double Scale)>? OriginProvider { get; set; }

    /// <summary>跟随平移变换（作用在 OverlayCanvas 上，RoiLayer 作为子元素自动继承）。</summary>
    private TranslateTransform? _follow;

    /// <summary>当前已建树的快照引用（引用相等即内容未变 → 只平移，不重建）。</summary>
    private RenderSnapshot? _builtSnapshot;

    /// <summary>最新发布的渲染快照。后台线程 new 完整体后一次性赋值 —— 引用赋值原子，读侧不撕裂。</summary>
    private volatile RenderSnapshot? _snapshot;

    /// <summary>已实测的帧率（供状态栏显示，证明 30Hz 跟随生效）。</summary>
    public int MeasuredFps => _measuredFps;

    /// <summary>上一次建树/平移是否发生了实际重绘（调试用）。</summary>
    public bool LastTickRebuilt { get; private set; }

    /// <summary>
    /// 覆盖层是否已成功从屏幕捕获中排除。
    /// false = SetWindowDisplayAffinity 未生效（远程会话 / 无 DWM / 驱动异常），
    /// 调用方必须启用「抓帧瞬间临时隐藏覆盖层」的兜底路径，否则译文会被自己抓回去。
    /// </summary>
    public bool IsExcludedFromCapture { get; private set; }

    /// <summary>上一次排除失败的说明（成功时为空串），供状态栏诊断。</summary>
    public string CaptureExcludeError { get; private set; } = "";

    /// <summary>
    /// 是否把覆盖层排除出屏幕捕获（对应设置项 OverlayExcludeFromCapture）。
    /// 由 MainWindow 在 Show() 之前注入，默认 true。
    /// </summary>
    public bool ExcludeFromCapture { get; set; } = true;

    /// <summary>
    /// 抓帧期间临时抑制覆盖层（兜底方案）。
    /// 用 Visibility 而不是 Close()：Close 会销毁 HWND，再 Show 要重建，
    /// 已设置的扩展样式（WS_EX_LAYERED 等）会丢失。
    /// </summary>
    public void SetCaptureSuppressed(bool suppressed)
        => this.Visibility = suppressed ? Visibility.Hidden : Visibility.Visible;

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OverlayWindow_Loaded;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // 必须在扩展样式（含 WS_EX_LAYERED）设置完成之后、首帧绘制之前调用，
        // 否则样式变更可能把亲和标记冲掉，或首帧已被抓进捕获结果。
        TryExcludeFromCapture();

        CoverVirtualScreen();
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        _roiLayer = RoiLayer;
    }

    /// <summary>
    /// 铺满整个虚拟桌面。
    ///
    /// 关键点：用 GetSystemMetrics 拿【物理像素】的虚拟桌面范围，
    /// 再用窗口自身的 DPI 换算成 WPF 的 DIP 来设置 Left/Top/Width/Height。
    /// 不能直接用 SystemParameters.VirtualScreenWidth —— 它按【主显示器 DPI】
    /// 换算，当覆盖层落在副屏（不同缩放比）时会导致整体偏移。
    /// </summary>
    public void CoverVirtualScreen()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        double scale = GetWindowDpiScale(hwnd);

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vw <= 0 || vh <= 0) return;

        this.Left = vx / scale;
        this.Top = vy / scale;
        this.Width = vw / scale;
        this.Height = vh / scale;
    }

    /// <summary>取当前窗口所在显示器的 DPI 缩放比（PerMonitorV2 下按窗口真实 DPI）</summary>
    private static double GetWindowDpiScale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        return dpi / 96.0;
    }

    /// <summary>
    /// 让覆盖层不出现在任何屏幕捕获里（BitBlt / 系统截图 / 录屏 / 远程协助）。
    ///
    /// 这是堵住「覆盖层自捕获回灌」闭环的关键一步：
    ///   GDI 降级路径 BitBlt(GetDC(NULL)) 抓的是 DWM 合成后的最终画面，
    ///   覆盖层 Topmost 且铺满虚拟桌面，译文必然被一起抓回 → 下一帧被当成原文再翻译
    ///   → 检测框变大 → 字号 ∝ 框高 → 译文更大 → 循环。
    ///
    /// 硬约束（不满足时 API 直接返回 FALSE）：
    ///   ① 窗口必须是【顶层窗口】  ② 窗口必须属于【当前进程】
    ///   ③ 依赖 DWM 合成（远程会话 / 关闭合成的旧环境可能失效）
    ///
    /// 失败降级：先试 WDA_EXCLUDEFROMCAPTURE(0x11)，失败再降级 WDA_MONITOR(0x01)。
    /// 两者都失败时返回 false，由调用方启用「抓帧瞬间隐藏覆盖层」的兜底。
    /// </summary>
    public bool TryExcludeFromCapture()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            CaptureExcludeError = "覆盖层 HWND 无效";
            IsExcludedFromCapture = false;
            return false;
        }

        // 排障开关：明确关闭排除 → 直接退化为「抓帧瞬间隐藏覆盖层」兜底
        if (!ExcludeFromCapture)
        {
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
            CaptureExcludeError = "设置中已关闭「覆盖层排除出屏幕捕获」";
            IsExcludedFromCapture = false;
            return false;
        }

        if (TrySet(hwnd, WDA_EXCLUDEFROMCAPTURE, "WDA_EXCLUDEFROMCAPTURE", out string? err1))
        {
            CaptureExcludeError = "";
            IsExcludedFromCapture = true;
            return true;
        }

        // 降级：效果更猛（缩略图/远程会话里也会变黑块），但至少能挡住 BitBlt
        if (TrySet(hwnd, WDA_MONITOR, "WDA_MONITOR", out string? err2))
        {
            CaptureExcludeError = "";
            IsExcludedFromCapture = true;
            return true;
        }

        CaptureExcludeError = $"{err1}；{err2}";
        IsExcludedFromCapture = false;
        return false;
    }

    private static bool TrySet(IntPtr hwnd, uint affinity, string name, out string? error)
    {
        error = null;
        try
        {
            if (SetWindowDisplayAffinity(hwnd, affinity))
            {
                // 读回校验：防「返回 TRUE 但没生效」。但注意一个 Win32 特性——
                // 设置 WDA_EXCLUDEFROMCAPTURE(0x11) 成功后，GetWindowDisplayAffinity
                // 读回的是 WDA_MONITOR(0x01) 而非 0x11（Get 侧不回报 0x11；
                // 且 0x11 在 Win10 2004 之前本就等效于 0x01，黑块挡捕获，同样有效）。
                // 所以 0x01 也是「已生效」，不能判失败 —— 否则就是永远误报「防截屏未生效」。
                if (GetWindowDisplayAffinity(hwnd, out uint actual)
                    && (actual == affinity || actual == WDA_MONITOR))
                    return true;

                error = $"{name} 读回校验失败（实际 0x{actual:X2}）";
                return false;
            }

            int win32 = Marshal.GetLastWin32Error();
            error = $"{name} 失败，Win32Error={win32}";
            return false;
        }
        catch (Exception ex)
        {
            // P/Invoke 本身抛异常（极端环境）不能让覆盖层创建流程挂掉
            error = $"{name} 异常：{ex.Message}";
            return false;
        }
    }

    // =====================================================================
    //  渲染快照 + 30Hz 跟随循环
    // =====================================================================

    /// <summary>
    /// 一次内容建树所需的完整快照（不可变，发布后不再改内部）。
    /// 后台线程 <c>new</c> 完整体后一次性赋给 <c>_snapshot</c>（引用赋值原子）；
    /// UI 线程读到引用后即可安全遍历 —— 因为发布者不会再改这个对象。
    /// </summary>
    public sealed class RenderSnapshot
    {
        public required string Signature { get; init; }
        public required bool OcrBoxesOnly { get; init; }
        /// <summary>普通模式：要画的段落（含译文）。</summary>
        public List<TranslationSegment>? Segments { get; init; }
        /// <summary>仅识别框模式：要画的 OCR 块。</summary>
        public IReadOnlyList<OcrBlock>? Blocks { get; init; }
        /// <summary>仅识别框模式的标签开关。</summary>
        public bool ShowText { get; init; }
        public bool ShowScore { get; init; }
        /// <summary>普通模式绘制所需的显示配置（建树时读取，避免后续被替换导致半新半旧）。</summary>
        public AppConfig? Config { get; init; }
    }

    /// <summary>发布一份新的内容快照。由 MainWindow 在 UI 线程调用（引用赋值，无锁）。</summary>
    public void PublishSnapshot(RenderSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>丢弃当前快照（停止翻译/清屏时用，避免渲染循环继续画旧内容）。</summary>
    public void DropSnapshot() => _snapshot = null;

    /// <summary>启动 30Hz 跟随渲染循环（幂等）。</summary>
    public void StartRenderLoop()
    {
        if (_renderLoopOn) return;
        _renderLoopOn = true;

        // 跟随变换挂在 OverlayCanvas 上；RoiLayer 是其子元素，自动继承同一变换，
        // 所以绿框会与内容一起平移，无需为它单独再建一个 TranslateTransform。
        if (_follow == null)
        {
            _follow = new TranslateTransform();
            OverlayCanvas.RenderTransform = _follow;
        }

        _frameClock.Restart();
        _lastTickMs = -1;
        _fpsWindowStartMs = -1;
        _fpsWindowFrames = 0;

        CompositionTarget.Rendering += RenderTick;
    }

    /// <summary>停止渲染循环（幂等）。</summary>
    public void StopRenderLoop()
    {
        if (!_renderLoopOn) return;
        _renderLoopOn = false;
        CompositionTarget.Rendering -= RenderTick;
        _frameClock.Stop();
    }

    /// <summary>渲染循环是否在跑。</summary>
    public bool IsRenderLoopRunning => _renderLoopOn;

    /// <summary>
    /// 渲染节拍回调。核心原则：**绝不每帧重建可视树**。
    /// 快照没变 → 只更新平移量（改 2 个 double）；快照变了 → 重建一次内容层。
    /// </summary>
    private void RenderTick(object? sender, EventArgs e)
    {
        if (!_renderLoopOn) return;

        // 自节流到 MaxFps：CompositionTarget.Rendering 一般 60Hz，
        // 这里降到自己设定的帧率，省掉多余的平移计算与合成压力。
        long now = _frameClock.ElapsedMilliseconds;
        int interval = MaxFps > 0 ? Math.Max(1, 1000 / MaxFps) : 0;
        if (_lastTickMs >= 0 && now - _lastTickMs < interval) return;
        _lastTickMs = now;

        // 帧率统计（滑动 1 秒窗口）
        if (_fpsWindowStartMs < 0) _fpsWindowStartMs = now;
        _fpsWindowFrames++;
        if (now - _fpsWindowStartMs >= 1000)
        {
            _measuredFps = (int)Math.Round(_fpsWindowFrames * 1000.0 / (now - _fpsWindowStartMs));
            _fpsWindowStartMs = now;
            _fpsWindowFrames = 0;
        }

        var snap = _snapshot;
        if (snap == null)
        {
            // 没有内容：只需保证「已建树」状态被清掉，内容层为空
            if (_builtSnapshot != null)
            {
                _builtSnapshot = null;
                ClearContentLayer();
                _currentLayout.Clear();
                _lastDrawnCount = 0;
                _lastSkippedCount = 0;
            }
            LastTickRebuilt = false;
            return;
        }

        var origin = OriginProvider?.Invoke() ?? (0, 0, 1.0);

        // ---- 快照变化 → 重建一次内容层（随 OCR 节奏，~1Hz）----
        // 注意：**原点移动不走重建** —— 那正是「跟随」要处理的场景，
        // 靠下面的平移差量解决。若窗口一动就重建，拖动窗口时等于每帧重建
        // 整棵可视树，30Hz 跟随的意义就没了（这正是阶段 1 要消灭的开销）。
        if (!ReferenceEquals(snap, _builtSnapshot))
        {
            _builtSnapshot = snap;

            // 建树用「当前原点」的绝对坐标；平移量先归零，避免坐标被污染。
            if (_follow != null) { _follow.X = 0; _follow.Y = 0; }
            _lastAppliedOrigin = (origin.X, origin.Y);

            RebuildVisuals(snap, origin.X, origin.Y, origin.Scale);
            LastTickRebuilt = true;
            return;
        }

        // ---- 纯平移路径（30Hz，只改 2 个 double）----
        LastTickRebuilt = false;
        if (_follow != null)
        {
            double dx = origin.X - _lastAppliedOrigin.X;
            double dy = origin.Y - _lastAppliedOrigin.Y;
            if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
            {
                _follow.X += dx;
                _follow.Y += dy;
                _lastAppliedOrigin = (origin.X, origin.Y);
            }
        }
    }

    /// <summary>最后一次已应用的捕获原点（平移差量的基准）。</summary>
    private (double X, double Y) _lastAppliedOrigin;

    /// <summary>按快照重建内容层（一次性）。</summary>
    private void RebuildVisuals(RenderSnapshot snap, double originX, double originY, double scale)
    {
        if (snap.OcrBoxesOnly && snap.Blocks != null)
        {
            DrawOcrBoxes(snap.Blocks, originX, originY, scale,
                         snap.ShowText, snap.ShowScore);
        }
        else if (snap.Segments != null && snap.Config != null)
        {
            Draw(snap.Segments, originX, originY, scale, snap.Config);
        }

        // 绿框与内容一起（在清层之后）重画，保证它始终存在且位于最上层。
        if (RoiFramePx is Rect roi && roi.Width > 0 && roi.Height > 0)
            DrawRoiBox(roi, originX, originY, scale);
        else
            ClearRoiBox();
    }

    /// <summary>
    /// 当前要标记的识别区域（捕获帧物理像素）。null = 未启用 ROI。
    /// 由 MainWindow 在配置/帧尺寸变化时设置；渲染循环会在重建内容层时一并重画绿框。
    /// </summary>
    public Rect? RoiFramePx { get; set; }

    /// <summary>ROI 绿色细线层（常驻，内容刷新不清它）。</summary>
    private Canvas? _roiLayer;

    /// <summary>
    /// 清空内容层，但**保留常驻的 RoiLayer** —— 绿框是「识别范围指示」，
    /// 不该被内容刷新（含空块帧的 ClearOverlay）清掉。
    /// Draw / DrawOcrBoxes / ClearOverlay 三个入口统一走这里，行为一致。
    /// </summary>
    private void ClearContentLayer()
    {
        if (_roiLayer == null) { OverlayCanvas.Children.Clear(); return; }

        for (int i = OverlayCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(OverlayCanvas.Children[i], _roiLayer))
                OverlayCanvas.Children.RemoveAt(i);
        }
    }

    /// <summary>
    /// 用绿色细线标出识别区域（ROI）。仅在 UseRoi 时调用。
    /// 只重画常驻的 RoiLayer，不影响内容层。
    /// </summary>
    /// <param name="roiFramePx">ROI 在捕获帧内的物理像素矩形</param>
    /// <param name="offsetX">捕获区域左上角在屏幕上的 X（物理像素）</param>
    /// <param name="offsetY">捕获区域左上角在屏幕上的 Y（物理像素）</param>
    /// <param name="scale">捕获图像相对窗口坐标的缩放比（默认 1）</param>
    public void DrawRoiBox(Rect roiFramePx, double offsetX, double offsetY, double scale)
    {
        if (_roiLayer == null) return;
        _roiLayer.Children.Clear();

        if (roiFramePx.Width <= 0 || roiFramePx.Height <= 0) return;
        if (scale <= 0) scale = 1;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        double dpiScale = hwnd != IntPtr.Zero ? GetWindowDpiScale(hwnd) : 1.0;

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);

        double x = ((roiFramePx.X * scale + offsetX) - vx) / dpiScale;
        double y = ((roiFramePx.Y * scale + offsetY) - vy) / dpiScale;
        double w = (roiFramePx.Width * scale) / dpiScale;
        double h = (roiFramePx.Height * scale) / dpiScale;

        if (!IsSane(x) || !IsSane(y) || !IsSane(w) || !IsSane(h)) return;
        if (w <= 0.5 || h <= 0.5) return;

        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            Stroke = new SolidColorBrush(Color.FromArgb(230, 0, 200, 0)),  // 绿色细线
            StrokeThickness = 1,                                            // 细线
            Fill = Brushes.Transparent                                      // 只描边，不遮挡文字
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _roiLayer.Children.Add(rect);
    }

    /// <summary>清除绿色识别区域框。</summary>
    public void ClearRoiBox() => _roiLayer?.Children.Clear();

    /// <summary>
    /// 绘制译文（逐块定位）。
    /// </summary>
    /// <param name="segments">按段落合并后的翻译结果（段落仅提供上下文，绘制按块）</param>
    /// <param name="offsetX">捕获区域左上角在屏幕上的 X（物理像素）</param>
    /// <param name="offsetY">捕获区域左上角在屏幕上的 Y（物理像素）</param>
    /// <param name="scale">捕获图像相对窗口坐标的缩放比（默认 1）</param>
    /// <param name="config">显示相关配置</param>
    public void Draw(
        List<TranslationSegment> segments,
        double offsetX,
        double offsetY,
        double scale,
        AppConfig config)
    {
        // 用窗口真实 DPI（GetDpiForWindow）而非 VisualTreeHelper.GetDpi：
        // 后者在分层窗口刚创建、尚未完成显示器关联时可能返回主屏 DPI，
        // 导致副屏上整体错位。
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        double dpiScale = hwnd != IntPtr.Zero ? GetWindowDpiScale(hwnd) : 1.0;
        double dpiX = dpiScale;
        double dpiY = dpiScale;
        if (scale <= 0) scale = 1;

        // 覆盖层窗口左上角对应的屏幕物理坐标（虚拟桌面原点）
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);

        double userOffsetX = config.OffsetX;
        double userOffsetY = config.OffsetY;
        double opacity = Math.Clamp(config.OverlayOpacity, 0.2, 1.0);

        // 用户自定义倍率（带防御：非法值一律回退到 1 / 默认）
        double fontScale = config.FontScale is >= 0.5 and <= 3.0 ? config.FontScale : 1.0;
        double bgScale = config.BgScale is >= 0.5 and <= 3.0 ? config.BgScale : 1.0;
        double bgPad = config.BgPadding is >= 0 and <= 30 ? config.BgPadding : 3;
        double bgRadius = config.BgCornerRadius is >= 0 and <= 20 ? config.BgCornerRadius : 3;
        double bgAlpha = config.BgOpacity is >= 0 and <= 1 ? config.BgOpacity : 0.67;

        ClearContentLayer();
        _currentLayout.Clear();
        _lastDrawnCount = 0;
        _lastSkippedCount = 0;
        int skipped = 0;

        foreach (var seg in segments)
        {
            if (seg.Blocks.Count == 0) continue;

            // 段内单行高度（物理像素），决定本段所有块的字号
            double lineHeightPx = seg.LineHeight > 0 ? seg.LineHeight : seg.BoundingBox.Height;

            // ---- 逐块绘制 ----
            foreach (var block in seg.Blocks)
            {
                // 只在【确实得到了有效译文】时才画。
                //
                // 不能只判 Translated 是否为空：翻译失败时 Translated 会被填成原文，
                // 画上去就是在原文位置重画一遍（字体/间距略异 → 视觉重影）。
                // 图标被 OCR 误读的块同理 —— 上面盖任何东西都是破坏。
                // 这类块保持不画，屏幕维持原样。
                if (!block.HasValidTranslation) continue;

                string text = block.Translated;
                if (string.IsNullOrWhiteSpace(text)) continue;

                // 每块用【自己的矩形】定位；DrawRect 在无合并/未回填时回退到 Box
                Rect r = block.DrawRect.Width > 0 && block.DrawRect.Height > 0
                    ? block.DrawRect
                    : block.Box;

                // 该块自身的行高（优先块本身；块高异常时退回段中位行高）
                double blockLinePx = Math.Max(r.Height, lineHeightPx);
                double lineHeightDip = (blockLinePx * scale) / dpiY;

                // 字号由两部分决定：
                //   ① 自动部分 —— 与原文字高成正比（保证贴合原文、不溢出）
                //   ② 用户倍率 —— FontScale 让用户按视力/喜好微调
                double fontSize = Math.Clamp(lineHeightDip * 0.72, 6.5, 42) * fontScale;

                // 隐藏原文时字号再收一点，显得更干净
                if (config.HideOriginalText)
                    fontSize *= 0.94;

                // 可用宽度：块宽（长句允许适度右扩）。
                // 注意：这里必须给足最小宽度（至少容纳 2 个中文字），
                // 否则很窄的 OCR 块会把译文排成"一字一列"的超高竖条，
                // 直接画出屏幕 → 表现为"完全没有译文"。这是上一版的严重 bug。
                //
                // 同时要按 fontScale 放宽：#用户把字号调到 2 倍时，
                // 若可用宽度还按原块宽算，收缩逻辑会把字号又压回去、倍率形同虚设。
                double baseWidthDip = (r.Width * scale) / dpiX;
                double availWidth = Math.Max(40, baseWidthDip * 1.6) * Math.Max(1.0, fontScale);

                // ---- 字号必须同时满足"不超高"和"不超宽" ----
                // 关键：译文【单行显示，绝不换行】。
                // 中文译文通常比英文原文短，单行放得下；实在放不下就收缩字号，
                // 而不是换行 —— 换行会让块高暴涨、文字飞出可视区域。
                //
                // 注意收缩上限用 availWidth 而不是"已乘倍率再放大"，
                // 因为用户把字号调大后本来就该允许它占更宽，但绝不能溢出块外。
                double textW = MeasureLineWidth(text, fontSize);
                if (textW > availWidth && textW > 0)
                {
                    fontSize *= availWidth / textW;
                    // 收缩后仍不能低于可读下限
                    fontSize = Math.Max(fontSize, 5.0);
                }

                // 极端长的译文（如整段被塞进一个窄块）再兜一层：
                // 若收缩到 5px 仍放不下，允许换行，但限制最多 2 行的高度，
                // 避免占满整屏。正常情况下不会走到这里。
                bool needWrap = MeasureLineWidth(text, fontSize) > availWidth * 1.02;
                double maxHeightDip = Math.Max(lineHeightDip * 2.2, lineHeightDip + 4);

                double lineBox = fontSize * 1.25;

                // 屏幕物理坐标 → 覆盖层窗口 DIP
                double x = ((r.X * scale + offsetX) - vx) / dpiX + userOffsetX;
                double y = ((r.Y * scale + offsetY) - vy) / dpiY + userOffsetY;

                var tb = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(Color.FromArgb(
                        (byte)(255 * opacity), 0, 0, 0)),
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    // 默认单行不换行；仅在极端长译文时才允许换行
                    TextWrapping = needWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    MaxWidth = needWrap ? availWidth : double.PositiveInfinity,
                    LineHeight = lineBox,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextTrimming = TextTrimming.None
                };

                Size measured = MeasureTextBlock(tb, needWrap ? availWidth : textW);

                // 自检：尺寸异常（0/NaN/∞）时跳过，避免画出不可见或错位的元素
                if (!IsSane(measured.Width) || !IsSane(measured.Height)
                    || measured.Width <= 0.5 || measured.Height <= 0.5)
                {
                    skipped++;
                    continue;
                }

                double drawWidth = Math.Min(measured.Width, Math.Max(availWidth, 8));
                double drawHeight = Math.Min(measured.Height, maxHeightDip);

                // ---- 背景尺寸：文字尺寸 × BgScale，再向外加 BgPadding ----
                // BgScale=1 时刚好包住文字；>1 放大、<1 收紧。
                // BgPadding 是四周额外留白（用于把原文盖得更干净）。
                double bgWidth = drawWidth * bgScale + bgPad * 2;
                double bgHeight = drawHeight * bgScale + bgPad * 2;
                if (bgWidth <= 0 || bgHeight <= 0 || !IsSane(bgWidth) || !IsSane(bgHeight))
                {
                    skipped++;
                    continue;
                }

                // 文字在背景里居中：背景变大后，文字要相应内缩定位
                double textShiftX = (bgWidth - drawWidth) / 2.0;
                double textShiftY = (bgHeight - drawHeight) / 2.0;

                // 背景层级：0 表示不画（只要文字）
                if (bgAlpha > 0.01)
                {
                    var bg = new Border
                    {
                        Width = bgWidth,
                        Height = bgHeight,
                        Background = new SolidColorBrush(Color.FromArgb(
                            (byte)(255 * bgAlpha * opacity), 255, 255, 255)),
                        CornerRadius = new CornerRadius(bgRadius)
                    };
                    if (bgRadius > 0.5)
                        bg.Effect = new BlurEffect { Radius = 5, RenderingBias = RenderingBias.Performance };
                    Canvas.SetLeft(bg, x - textShiftX);
                    Canvas.SetTop(bg, y - textShiftY);
                    OverlayCanvas.Children.Add(bg);
                }

                tb.MaxWidth = needWrap ? availWidth : double.PositiveInfinity;
                tb.Width = double.NaN;   // 不锁宽，交给实际排版，避免测量/绘制不一致
                Canvas.SetLeft(tb, x);
                Canvas.SetTop(tb, y);
                OverlayCanvas.Children.Add(tb);

                _currentLayout.Add(new Rect(x - textShiftX, y - textShiftY, bgWidth, bgHeight));
                _lastDrawnCount++;

                // ---- 调试框：画出 OCR 原始框（青）与绘制框（品红） ----
                // 青框贴着原文说明 OCR 坐标正确；品红框应与青框基本重合。
                // 若整体错位 → 原点/DPI 问题；若只有某些行错位 → 分段/回填问题。
                if (config.DebugBoxes)
                {
                    AddDebugRect(
                        ((block.Box.X * scale + offsetX) - vx) / dpiX + userOffsetX,
                        ((block.Box.Y * scale + offsetY) - vy) / dpiY + userOffsetY,
                        (block.Box.Width * scale) / dpiX,
                        (block.Box.Height * scale) / dpiY,
                        Color.FromArgb(200, 0, 200, 220));

                    AddDebugRect(
                        ((r.X * scale + offsetX) - vx) / dpiX + userOffsetX,
                        ((r.Y * scale + offsetY) - vy) / dpiY + userOffsetY,
                        (r.Width * scale) / dpiX,
                        (r.Height * scale) / dpiY,
                        Color.FromArgb(200, 255, 0, 200));
                }
            }
        }

        _lastSkippedCount = skipped;
    }

    /// <summary>判断数值是否可用于布局（非 NaN、非 ∞、非负）</summary>
    private static bool IsSane(double v)
        => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

    /// <summary>
    /// 测量一段文本在【单行、不换行】下需要的宽度（DIP）。
    /// 用于"放不下就收缩字号"的判断 —— 关键是不能走换行，
    /// 换行会让极窄块的中文排成超高竖列、逃出可视区域。
    /// </summary>
    private static double MeasureLineWidth(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        try
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal),
                Math.Max(1, fontSize),
                Brushes.Black,
                1.0);
            ft.SetFontWeight(FontWeights.Bold);
            return ft.WidthIncludingTrailingWhitespace;
        }
        catch
        {
            // 兜底估算：中文按 1.0 字宽、其他按 0.55
            double w = 0;
            foreach (char c in text) w += c > 0x2E80 ? 1.0 : 0.55;
            return w * fontSize;
        }
    }

    /// <summary>画一个 1px 描边的调试矩形</summary>
    private void AddDebugRect(double x, double y, double w, double h, Color color)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = w,
            Height = h,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            Fill = Brushes.Transparent
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        OverlayCanvas.Children.Add(rect);
    }

    /// <summary>测量 TextBlock 在给定最大宽度下的实际尺寸</summary>
    private static Size MeasureTextBlock(TextBlock tb, double maxWidth)
    {
        tb.Measure(new Size(maxWidth, double.PositiveInfinity));
        Size d = tb.DesiredSize;
        if (d.Width > 0 && d.Height > 0) return d;

        var ft = new FormattedText(
            tb.Text ?? string.Empty,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = maxWidth,
            LineHeight = tb.LineHeight
        };
        ft.SetFontWeight(tb.FontWeight);
        return new Size(ft.Width, ft.Height);
    }

    /// <summary>清空所有译文（保留常驻的 ROI 绿色框层）</summary>
    public void ClearOverlay()
    {
        ClearContentLayer();
        _currentLayout.Clear();
        _lastDrawnCount = 0;
        _lastSkippedCount = 0;
    }

    /// <summary>
    /// 仅绘制 OCR 识别框（不翻译、不显示译文）。
    ///
    /// 用途：单独校验 OCR 环节——"它到底识别到了什么、框在哪"。
    /// 把翻译与绘制层的问题完全排除在外，便于逐层定位。
    ///
    /// 每个识别块画：
    ///   · 一个青色实线框（紧贴检测框，即原文位置）
    ///   · 框左上角一个小标签，显示置信度与识别文本（可关）
    /// </summary>
    /// <param name="blocks">OCR 识别结果（坐标基于捕获帧物理像素）</param>
    /// <param name="offsetX">捕获区域左上角在屏幕上的 X（物理像素）</param>
    /// <param name="offsetY">捕获区域左上角在屏幕上的 Y（物理像素）</param>
    /// <param name="scale">捕获图像相对窗口坐标的缩放比（默认 1）</param>
    /// <param name="showText">是否在框旁标注识别到的原文</param>
    /// <param name="showScore">是否在框旁标注置信度（与原文各自独立）</param>
    public void DrawOcrBoxes(
        IReadOnlyList<OcrBlock> blocks,
        double offsetX,
        double offsetY,
        double scale,
        bool showText,
        bool showScore = true)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        double dpiScale = hwnd != IntPtr.Zero ? GetWindowDpiScale(hwnd) : 1.0;
        if (scale <= 0) scale = 1;

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);

        ClearContentLayer();
        _currentLayout.Clear();
        _lastDrawnCount = 0;
        _lastSkippedCount = 0;
        int skipped = 0;

        foreach (var block in blocks)
        {
            // 优先用自适应收紧后的框绘制（未启用时 FittedBox 为空 → 回退到原检测框）
            var r = block.FittedBox.Width > 0 && block.FittedBox.Height > 0
                ? block.FittedBox
                : block.Box;
            if (r.Width <= 0 || r.Height <= 0) { skipped++; continue; }

            double x = ((r.X * scale + offsetX) - vx) / dpiScale;
            double y = ((r.Y * scale + offsetY) - vy) / dpiScale;
            double w = (r.Width * scale) / dpiScale;
            double h = (r.Height * scale) / dpiScale;

            if (!IsSane(x) || !IsSane(y) || !IsSane(w) || !IsSane(h)
                || w <= 0.5 || h <= 0.5)
            {
                skipped++;
                continue;
            }

            // ---- 识别框（青色实线）----
            // 置信度偏低（< 0.6）时用橙色，一眼能看出哪些块是"勉强识别"。
            var boxColor = block.Score >= 0.6f
                ? Color.FromArgb(230, 0, 190, 220)
                : Color.FromArgb(230, 255, 140, 0);

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = w,
                Height = h,
                Stroke = new SolidColorBrush(boxColor),
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            OverlayCanvas.Children.Add(rect);

            // ---- 文本标签（原文 / 置信度，两者独立开关）----
            // 组合出标签：都不开 → null（不画）；只开一个 → 只画那一个。
            string? label = BuildOcrLabel(block, showText, showScore);
            if (label != null)
            {
                double fontSize = Math.Clamp(h * 0.62, 8.0, 20.0);

                var tb = new TextBlock
                {
                    Text = label,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Normal,
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)),
                    TextWrapping = TextWrapping.NoWrap
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size m = tb.DesiredSize;
                if (m.Width > 0 && m.Height > 0)
                {
                    // 标签底色：浅色半透明，保证在任何背景上都读得清
                    var bg = new Border
                    {
                        Width = m.Width + 6,
                        Height = m.Height + 2,
                        Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                        CornerRadius = new CornerRadius(2),
                        BorderBrush = new SolidColorBrush(boxColor),
                        BorderThickness = new Thickness(1)
                    };
                    // 放在框左上方外侧；顶部没有空间时移到框内顶部
                    double ly = y - bg.Height - 1;
                    if (ly < 0) ly = y;
                    Canvas.SetLeft(bg, x);
                    Canvas.SetTop(bg, ly);
                    OverlayCanvas.Children.Add(bg);

                    Canvas.SetLeft(tb, x + 3);
                    Canvas.SetTop(tb, ly + 1);
                    OverlayCanvas.Children.Add(tb);
                }
            }

            _currentLayout.Add(new Rect(x, y, w, h));
            _lastDrawnCount++;
        }

        _lastSkippedCount = skipped;
    }

    /// <summary>
    /// 组合「仅显示识别框」模式的标签文本。原文与置信度各自独立开关：
    /// 都不开 → null（不画标签）；只开一个 → 只画那一个。
    /// </summary>
    private static string? BuildOcrLabel(OcrBlock block, bool showText, bool showScore)
    {
        bool hasText = showText && !string.IsNullOrWhiteSpace(block.Text);
        if (!hasText && !showScore) return null;

        if (!hasText) return $"{block.Score:F2}";

        // 过长文本截断，避免标签糊成一片
        string shown = block.Text.Length > 40 ? block.Text[..40] + "…" : block.Text;
        return showScore ? $"{block.Score:F2} {shown}" : shown;
    }

    /// <summary>当前落点布局（DIP），用于帧间差异比较决定是否重绘</summary>
    public IReadOnlyList<Rect> CurrentLayout => _currentLayout;

    /// <summary>上一次绘制实际画出的块数（用于自检"已翻译但没显示"）</summary>
    public int LastDrawnCount => _lastDrawnCount;

    /// <summary>上一次绘制因尺寸异常被跳过的块数</summary>
    public int LastSkippedCount => _lastSkippedCount;

    /// <summary>精确测量文字宽度</summary>
    public double MeasureTextWidth(string text, double fontSize, FontWeight weight)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei"),
            fontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        formatted.SetFontWeight(weight);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}
