using System.Runtime.InteropServices;
using SkiaSharp;

namespace ScreenTranslator.Services;

/// <summary>
/// GDI 截图服务（降级方案）
/// 从屏幕 DC 截取窗口客户区所在的屏幕区域
/// 要求窗口可见且不被其他窗口遮挡
/// </summary>
public class GdiCaptureService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
        int wDest, int hDest, IntPtr hdcSource, int xSrc, int ySrc, int rop);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private const uint PW_CLIENTONLY = 0x1;

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines,
        IntPtr lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private IntPtr _hwnd;

    public GdiCaptureService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>截取窗口客户区，返回 BGRA 格式的 SKBitmap</summary>
    public SKBitmap? Capture()
    {
        // 检查窗口是否最小化
        if (IsIconic(_hwnd)) return null;

        // 获取客户区尺寸
        if (!GetClientRect(_hwnd, out var clientRect)) return null;
        int w = clientRect.Right - clientRect.Left;
        int h = clientRect.Bottom - clientRect.Top;
        if (w <= 0 || h <= 0) return null;

        // 获取窗口 DC（用于创建兼容 DC）
        IntPtr hdcWindow = GetDC(_hwnd);
        IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
        IntPtr hbm = CreateCompatibleBitmap(hdcWindow, w, h);
        IntPtr oldBmp = SelectObject(hdcMem, hbm);

        try
        {
            // 用 PrintWindow 让目标窗口自己绘制到内存 DC，不包含覆盖层
            bool ok = PrintWindow(_hwnd, hdcMem, PW_CLIENTONLY);

            // 检测截图是否有效（全黑或全白则视为异常）
            bool valid = false;
            if (ok)
            {
                var testBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                var testPixels = testBmp.GetPixels();
                var testBmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w, biHeight = -h,
                        biPlanes = 1, biBitCount = 32, biCompression = BI_RGB
                    }
                };
                int testResult = GetDIBits(hdcMem, hbm, 0, (uint)h, testPixels, ref testBmi, DIB_RGB_COLORS);
                if (testResult > 0)
                {
                    valid = !IsAllSameColor(testPixels, w * h);
                }
                testBmp.Dispose();
            }

            if (valid)
            {
                PrintWindowOkCount++;
            }
            else
            {
                // PrintWindow 失败或无效，回退到 BitBlt 从【屏幕 DC】截取。
                //
                // ⚠ 危险点：屏幕 DC 反映 DWM 合成后的最终画面，会把置顶的覆盖层
                // （铺满虚拟桌面 + Topmost + WS_EX_LAYERED）一起抓回来。译文于是成为
                // 下一帧 OCR 的输入，形成「译文字号逐帧阶梯式变大」的正反馈闭环。
                //
                // 正常情况下覆盖层已通过 SetWindowDisplayAffinity 排除出捕获，
                // 这里抓到的就是干净的原文；该 API 在某些环境失效时，
                // 由调用方注入的 Before/After 回调临时隐藏覆盖层来兜底。
                BitBltFallbackCount++;

                try
                {
                    // 隐藏 → 等 DWM 重新合成一帧，否则屏幕 DC 里仍是含覆盖层的上一帧
                    if (BeforeScreenCapture != null)
                    {
                        BeforeScreenCapture.Invoke();
                        System.Threading.Thread.Sleep(40);
                    }

                    IntPtr hdcScreen = GetDC(IntPtr.Zero);
                    var point = new POINT { X = 0, Y = 0 };
                    ClientToScreen(_hwnd, ref point);
                    BitBlt(hdcMem, 0, 0, w, h, hdcScreen, point.X, point.Y, SRCCOPY);
                    ReleaseDC(IntPtr.Zero, hdcScreen);
                }
                finally
                {
                    // 无论抓帧是否成功都必须恢复，否则覆盖层永久消失
                    AfterScreenCapture?.Invoke();
                }
            }

            // 准备 BITMAPINFO
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // 负值表示从上到下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            // 分配像素缓冲区
            var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            var pixels = bitmap.GetPixels();

            int result = GetDIBits(hdcMem, hbm, 0, (uint)h, pixels, ref bmi, DIB_RGB_COLORS);
            if (result == 0)
            {
                bitmap.Dispose();
                return null;
            }

            // 顺手算指纹（微秒级），供 OCR 前置的变化检测使用。
            // 有 ROI 时只在框内采样，框外的画面变化就不会白白触发 OCR。
            _latestFingerprint = FrameFingerprint.Compute(bitmap, RoiGeometry.ToSkRect(bitmap, RoiNormalized));

            return bitmap;
        }
        finally
        {
            SelectObject(hdcMem, oldBmp);
            DeleteObject(hbm);
            DeleteDC(hdcMem);
            ReleaseDC(_hwnd, hdcWindow);
        }
    }

    public void Dispose() { }

    /// <summary>
    /// 识别区域：相对捕获区域（客户区）的归一化矩形（0~1）。null = 整帧。
    /// 只影响指纹采样范围，不影响实际捕获的位图。
    ///
    /// GDI 的 Capture() 与设置写入都在 UI 线程，无需加锁。
    /// </summary>
    public System.Windows.Rect? RoiNormalized { get; set; }

    /// <summary>
    /// 走 BitBlt 回退之前调用（兜底：临时隐藏覆盖层，避免译文被自己抓回）。
    /// 为 null 表示不抑制 —— 即覆盖层的防捕获已生效，无需兜底。
    /// </summary>
    public Action? BeforeScreenCapture { get; set; }

    /// <summary>抓帧之后立即调用（恢复覆盖层）。必须与 BeforeScreenCapture 成对出现。</summary>
    public Action? AfterScreenCapture { get; set; }

    /// <summary>
    /// 累计走 BitBlt 回退的次数。
    /// 持续 > 0 说明当前窗口的 PrintWindow 不可用，覆盖了"译文回灌"的高风险路径
    /// （若防捕获也失效，译文会被反复识别再翻译）。
    /// </summary>
    public int BitBltFallbackCount { get; private set; }

    /// <summary>累计 PrintWindow 成功的次数。</summary>
    public int PrintWindowOkCount { get; private set; }

    /// <summary>最近一次 Capture() 得到的灰度指纹（256 字节）。null = 还没捕获过。</summary>
    private byte[]? _latestFingerprint;

    /// <summary>
    /// 取最近一帧的灰度指纹。
    /// 用于 OCR 前置的变化检测：与上一帧比较，未变化则整帧跳过 OCR。
    /// </summary>
    public byte[]? GetLatestFingerprint() => _latestFingerprint;

    /// <summary>
    /// 捕获区域左上角在屏幕上的物理像素坐标。
    ///
    /// GDI 走 PrintWindow + PW_CLIENTONLY，只捕获客户区，
    /// 因此原点用客户区左上角（ClientToScreen(0,0)）。
    /// </summary>
    public System.Windows.Point GetCaptureOriginPx()
    {
        var pt = new POINT { X = 0, Y = 0 };
        if (_hwnd != IntPtr.Zero) ClientToScreen(_hwnd, ref pt);
        return new System.Windows.Point(pt.X, pt.Y);
    }

    /// <summary>
    /// 检测像素是否全部为同一颜色（全黑/全白等异常截图）。
    ///
    /// 采样步长取 97（质数）：原来用 100，而常见的帧宽（1920/2560/1600 等）
    /// 与 100 有较大的公约数，采样点会周期性地落在同一列/同一行上，
    /// 形成"共振" —— 画面明明有内容，却因为整片区域被跳过而误判为全同色，
    /// 进而每帧都掉进 BitBlt 回灌路径。改用质数步长可打散这种周期性。
    /// </summary>
    private static unsafe bool IsAllSameColor(IntPtr pixels, int pixelCount)
    {
        if (pixelCount == 0) return true;
        byte* p = (byte*)pixels;
        int firstB = p[0], firstG = p[1], firstR = p[2];

        const int step = 97;          // 质数，避免与帧宽共振
        const int maxSamples = 4096;  // 上限，保证成本仍在微秒级

        int samples = 0;
        for (int i = 0; i < pixelCount; i += step)
        {
            int offset = i * 4;
            if (p[offset] != firstB || p[offset + 1] != firstG || p[offset + 2] != firstR)
                return false;

            if (++samples >= maxSamples) break;
        }
        return true;
    }
}
