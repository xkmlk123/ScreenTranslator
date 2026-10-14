using System.Runtime.InteropServices;
using global::Windows.Graphics.Capture;
using global::Windows.Graphics.DirectX.Direct3D11;
using SkiaSharp;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace ScreenTranslator.Services;

/// <summary>
/// WGC 屏幕捕获服务（Windows Graphics Capture）
/// 捕获指定窗口，支持被遮挡窗口
/// </summary>
public class WgcCaptureService : IDisposable
{
    private SharpDX.Direct3D11.Device? _device;
    private DeviceContext? _context;
    private IDirect3DDevice? _d3dDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureItem? _captureItem;
    private GraphicsCaptureSession? _session;

    private SKBitmap? _latestFrame;
    private readonly object _lock = new();

    /// <summary>最近一帧的灰度指纹（256 字节），供变化检测使用。null 表示还没帧。</summary>
    private byte[]? _latestFingerprint;

    /// <summary>ROI 的底层存储。见 <see cref="RoiNormalized"/> 的线程安全说明。</summary>
    private System.Windows.Rect? _roiNormalized;

    /// <summary>
    /// 识别区域：相对捕获区域的归一化矩形（0~1）。null = 整帧。
    ///
    /// 只影响【指纹采样范围】，不影响实际捕获的位图 ——
    /// 这样 ROI 之外的画面变化不会白白触发 OCR。
    ///
    /// 线程安全：写来自 UI 线程（设置变更），读来自 WGC 的 FrameArrived 回调线程。
    /// <c>Rect?</c> 是 40 字节的结构体，读写都不原子，会撕裂，因此两边都过 <c>_lock</c>。
    /// </summary>
    public System.Windows.Rect? RoiNormalized
    {
        get { lock (_lock) return _roiNormalized; }
        set { lock (_lock) _roiNormalized = value; }
    }


    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
        out RECT pvAttribute, int cbAttribute);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // ---- 诊断用：读 WinRT 对象的运行时类名（IInspectable::GetRuntimeClassName 返回 HSTRING）----
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    // ---- 权限诊断（UIPI）：低权限进程无法用 WGC 捕获高权限进程的窗口 ----
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;   // TOKEN_INFORMATION_CLASS

    /// <summary>DWM 扩展框架边界（不含 Win10+ 的不可见调整边框）</summary>
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    // ---- 捕获设备选型：把 D3D 设备建在「目标窗口所在显示器」的适配器上 ----
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 启动捕获。
    ///
    /// 失败时异常消息带 [WGC 步骤N] 前缀 —— 以前直接吞异常只写 Debug.WriteLine，
    /// Release 下用户完全不知道为什么降级到了 GDI。分步标记能一眼看出卡在哪。
    /// </summary>
    public void Start(IntPtr hwnd)
    {
        _hwnd = hwnd;
        int step = 0;
        try
        {
            // ---- 步骤 1：最小化检查 ----
            step = 1;
            if (IsIconic(hwnd))
                throw new InvalidOperationException("目标窗口已最小化，请先还原窗口再开启翻译");

            // ---- 步骤 2：窗口尺寸 ----
            step = 2;
            if (!GetWindowRect(hwnd, out var rect))
                throw new InvalidOperationException("无法获取窗口尺寸");
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException($"窗口尺寸无效（{w}x{h}），请确认窗口正常显示");

            // ---- 步骤 3：系统级支持判定 ----
            // 这是唯一能一次性区分"系统不支持"与"这个窗口不支持"的判据。
            step = 3;
            if (!global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
                throw new InvalidOperationException(
                    "当前系统不支持 Windows 图形捕获（需 Win10 1803+ 且 DWM 正常合成；Windows Server 需安装「桌面体验」）");

            // ---- 步骤 4：D3D 设备（建在「目标窗口所在显示器」的适配器上）----
            // 根因修正：以前直接用 DriverType.Hardware/Warp 建在【默认适配器】上。
            // 本机有两块适配器（含 GameViewer 虚拟显示适配器），若设备建在与被捕获
            // 窗口不同的一块上，WGC 交付帧时就是跨适配器拷贝，DXGI 会直接移除设备
            // —— 实测表现为「第 1 帧侥幸成功、第 2 帧起必报 DXGI_ERROR_DEVICE_REMOVED」，
            // 且与硬件/WARP 无关（WARP 实验同样复现，见二分实验结论）。
            // 现在先用 MonitorFromWindow 定位目标窗口所在显示器，再匹配其所属适配器。
            step = 4;
            var devDiag = new System.Text.StringBuilder();
            _device = CreateCaptureDevice(hwnd, devDiag);
            _context = _device.ImmediateContext;

            // 记录设备所在 DXGI 适配器名（诊断：确认到底用的哪块卡）
            try
            {
                using var dxgiDev4 = _device.QueryInterface<SharpDX.DXGI.Device>();
                using var adapter = dxgiDev4.Adapter;
                AdapterName = adapter.Description.Description ?? "?";
            }
            catch (Exception ax)
            {
                AdapterName = $"取适配器名失败({ax.GetType().Name})";
            }

            AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] ---- 启动捕获 ----\n"
                + devDiag.ToString()
                + $"  选定设备={DeviceKind}/{AdapterName}");

            // ---- 步骤 5：DXGI → WinRT IDirect3DDevice ----
            step = 5;
            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr devicePtr);
            if (hr != 0 || devicePtr == IntPtr.Zero)
                throw new InvalidOperationException($"创建 IDirect3DDevice 失败，HRESULT=0x{hr:X8}");

            // 关键：必须用 CsWinRT 的 FromAbi 把裸指针包成「投影对象」。
            // 以前用 Marshal.GetObjectForIUnknown 得到的是经典 COM RCW（System.__ComObject），
            // 后续 Direct3D11CaptureFramePool.Create（CsWinRT）取它的 IDirect3DDevice
            // 接口（A37624AB-8D5F-4650-9D3E-9EAE3D9BC670，该 IID 属于 IDirect3DDevice）时会报
            // 「Failed to create a CCW ... the specified cast is not valid」。
            // 注意：FromAbi 之后再对 devicePtr 做 Release 会破坏引用计数，不要释放。
            _d3dDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(devicePtr);

            // ---- 步骤 6：CaptureItem ----
            step = 6;
            try
            {
                _captureItem = CaptureItemHelper.CreateItemForWindow(hwnd);
            }
            catch (Exception itemEx)
            {
                // 权限不对等是最常见原因：目标窗口以管理员运行、本程序是普通权限时，
                // UIPI 禁止低完整性进程捕获高完整性进程的窗口，CreateForWindow 会抛
                // E_ACCESSDENIED(0x80070005) 之类。此时给出明确指引而非笼统报错。
                if (IsWindowElevated(hwnd) && !IsSelfElevated())
                    throw new InvalidOperationException(PermissionHint, itemEx);
                throw;
            }
            if (_captureItem == null)
            {
                if (IsWindowElevated(hwnd) && !IsSelfElevated())
                    throw new InvalidOperationException(PermissionHint);
                throw new InvalidOperationException("无法为窗口创建捕获项（该窗口可能不可捕获，如托盘/UWP 受限窗口），请换一个窗口重试");
            }

            // ---- 步骤 7：帧池 ----
            step = 7;
            var size = _captureItem.Size;
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException($"捕获项尺寸无效（{size.Width}x{size.Height}），请确认窗口未最小化");

            try
            {
                // 必须用 CreateFreeThreaded 而非 Create：
                // Create 会把 FrameArrived 回调 marshal 回创建线程（WPF UI 线程 = STA），
                // 而 WGC 在 MTA 侧产生 surface，跨 apartment 后拿到的只是【代理对象】。
                // IDirect3DDxgiInterfaceAccess 是本地 interop 接口，没有注册 proxy/stub，
                // 代理不认识它 → QI 一律 E_NOINTERFACE(0x80004002)，5 条取指针路径全灭。
                // CreateFreeThreaded 让回调直接在 MTA 线程池线程触发，surface 不再跨 apartment。
                // 它同时保证回调串行执行；_context(ImmediateContext) 仅在本回调内使用，无并发风险。
                _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _d3dDevice,
                    global::Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    size);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"创建捕获帧池失败：{ex.Message}", ex);
            }

            // ---- 步骤 8：会话（原来完全没保护）----
            step = 8;
            _framePool.FrameArrived += OnFrameArrived;
            _session = _framePool.CreateCaptureSession(_captureItem);
            _session.StartCapture();

            LastFailureStep = "";
        }
        catch (Exception ex)
        {
            LastFailureStep = $"步骤{step}";
            // 关键：失败时把已建的 D3D 资源清干净。
            // 以前这里什么都不做，反复重试会泄漏 device / framePool。
            Dispose();
            throw new InvalidOperationException($"[WGC {LastFailureStep}] {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 创建捕获用的 D3D11 设备：优先建在「目标窗口所在显示器」对应的适配器上。
    ///
    /// 为什么必须对齐适配器：WGC 要求 frame pool 的 D3D 设备与被捕获内容处于同一
    /// 适配器。本机存在 GameViewer 虚拟显示适配器 + NVIDIA 物理显卡，若建在默认
    /// 适配器上而目标窗口在另一块适配器的显示器上，交付帧即为跨适配器拷贝，
    /// DXGI 会移除设备 —— 实测「第 1 帧成功、第 2 帧起 DXGI_ERROR_DEVICE_REMOVED」。
    ///
    /// 兜底顺序：匹配适配器的硬件设备 → 默认硬件设备 → WARP 软件设备。
    /// 全过程写入诊断日志。
    /// </summary>
    private SharpDX.Direct3D11.Device CreateCaptureDevice(IntPtr hwnd, System.Text.StringBuilder diag)
    {
        IntPtr targetMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        diag.Append($"  目标窗口显示器=0x{targetMon.ToInt64():X}\n");

        SharpDX.DXGI.Adapter? matched = null;
        try
        {
            using var factory = new SharpDX.DXGI.Factory1();
            foreach (var adapter in factory.Adapters)
            {
                string name = "?";
                try { name = adapter.Description.Description ?? "?"; } catch { }

                string outs = "";
                try
                {
                    foreach (var output in adapter.Outputs)
                    {
                        IntPtr mon = output.Description.MonitorHandle;
                        outs += $" [mon=0x{mon.ToInt64():X}]";
                        if (mon == targetMon && matched == null)
                            matched = adapter;
                    }
                }
                catch (Exception oe)
                {
                    outs = $" (枚举输出失败:{oe.GetType().Name})";
                }

                diag.Append($"  适配器: {name}{outs}\n");
            }
        }
        catch (Exception fe)
        {
            diag.Append($"  枚举适配器失败:{fe.GetType().Name}\n");
        }

        // 优先：匹配到的适配器上建硬件设备
        if (matched != null)
        {
            try
            {
                var dev = new SharpDX.Direct3D11.Device(matched, DeviceCreationFlags.BgraSupport);
                DeviceKind = "Hardware";
                diag.Append("  → 匹配适配器上创建硬件设备成功\n");
                return dev;
            }
            catch (Exception he)
            {
                diag.Append($"  → 匹配适配器创建硬件设备失败:{he.GetType().Name}，回退默认硬件设备\n");
            }
        }

        // 回退 1：默认硬件适配器
        try
        {
            var dev = new SharpDX.Direct3D11.Device(
                SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            DeviceKind = "Hardware(默认)";
            diag.Append("  → 默认硬件设备创建成功\n");
            return dev;
        }
        catch (Exception he)
        {
            diag.Append($"  → 默认硬件设备创建失败:{he.GetType().Name}，回退 WARP\n");
        }

        // 回退 2：WARP 软件光栅化（兜底）
        DeviceKind = "Warp";
        diag.Append("  → 使用 WARP 软件设备\n");
        return new SharpDX.Direct3D11.Device(
            SharpDX.Direct3D.DriverType.Warp, DeviceCreationFlags.BgraSupport);
    }

    /// <summary>WGC 启动失败发生在第几步（空串 = 未失败）。用于把真实原因暴露到状态栏。</summary>
    public string LastFailureStep { get; private set; } = "";

    /// <summary>权限不对等时的统一提示文案。</summary>
    private const string PermissionHint =
        "目标窗口以管理员权限运行，而本程序是普通权限 —— Windows 禁止低权限进程捕获高权限窗口。"
        + "请以管理员身份重新运行本程序。";

    /// <summary>
    /// 判断目标窗口所属进程是否已提权（管理员）。
    /// 用于诊断 WGC 捕获失败：UIPI 禁止低完整性进程捕获高完整性进程的窗口。
    /// </summary>
    private static bool IsWindowElevated(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return false;
            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken)) return false;
                try
                {
                    // TokenElevation = DWORD，非 0 表示已提权
                    uint size = sizeof(uint);
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (!GetTokenInformation(hToken, TokenElevation, buf, size, out _))
                            return false;
                        return Marshal.ReadInt32(buf) != 0;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
                finally
                {
                    CloseHandle(hToken);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前进程是否以管理员运行。</summary>
    private static bool IsSelfElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>FrameArrived 回调累计次数（诊断：0 表示会话根本没投递帧）。</summary>
    public long FrameArrivedCount { get; private set; }

    /// <summary>TryGetNextFrame 成功取到帧的次数。</summary>
    public long FrameTakenCount { get; private set; }

    /// <summary>成功转成 SKBitmap 的次数（小于 FrameTakenCount 说明卡在 Surface→Bitmap）。</summary>
    public long FrameConvertedCount { get; private set; }

    /// <summary>最后一次转帧失败的异常消息。会把真实原因暴露到状态栏，而不是静默黑屏。</summary>
    public string LastFrameError { get; private set; } = "";

    /// <summary>【诊断】当前 D3D 设备类型："Hardware" 或 "Warp"。见 Start 步骤 4。</summary>
    public string DeviceKind { get; private set; } = "";

    /// <summary>【诊断】设备所在 DXGI 适配器名（WARP 时通常是 Microsoft Basic Render Driver）。</summary>
    public string AdapterName { get; private set; } = "";

    /// <summary>
    /// 设备失效原因（如 "0x887A0006"）。非空表示 D3D 设备已被移除/挂起
    /// （DEVICE_REMOVED 0x887A0005 / DEVICE_HUNG 0x887A0006，典型为驱动 TDR）。
    /// 上层据此【立即】降级到 GDI —— 此时 WGC 已无法再交付新帧，只会卡在最后一帧。
    /// </summary>
    public string? DeviceLostReason { get; private set; }

    /// <summary>D3D 设备是否已失效。true 时 WGC 已不可用，应立即切 GDI 备用模式。</summary>
    public bool IsDeviceLost => DeviceLostReason != null;

    /// <summary>
    /// 【诊断二分开关】true 时 OnFrameArrived 只计数、不做 CopyResource+Map。
    /// 用于判定 DEVICE_HUNG 是否由我们的拷贝动作引起：置 true 仍崩 → 是
    /// "WGC 内部设备与本程序显式设备共存"的问题；不崩 → 触发点就是拷贝动作。
    /// </summary>
    public bool SkipPixelReadback { get; set; }

    /// <summary>
    /// 是否走 WinRT 官方 SoftwareBitmap 路径（默认 true）。
    ///
    /// 该路径由系统内部完成 GPU→CPU 拷贝，我们完全不碰自建的 D3D DeviceContext，
    /// 因此既不会与 WGC 内部的 context 使用并发冲突（DEVICE_HUNG 的触发点），
    /// 也不会再有手工 QI / 原生 interop 引发的访问违规（上一版 0xc0000005 闪退的根因）。
    /// 置 false 即回退到旧的 ConvertSurfaceToSkBitmap 路径。
    /// </summary>
    public bool UseSoftwareBitmapPath { get; set; } = true;

    /// <summary>
    /// 诊断日志文件路径（输出目录下的「诊断日志.txt」）。
    ///
    /// 为什么落盘而不是剪贴板：剪贴板会被别的进程占用导致复制失败，
    /// 而日志文件是进程独有，只要写就一定成功，且排查时能直接读全文。
    /// </summary>
    public static string DiagLogPath =>
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DiagLogFileName);

    /// <summary>
    /// 追加一行到诊断日志（永不抛异常——日志本身不能成为新的故障源）。
    /// 文件超过 1MB 自动清空重写，避免长时间运行撑爆磁盘。
    /// </summary>
    public static void AppendDiagLog(string text)
    {
        try
        {
            var path = DiagLogPath;
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists && fi.Length > 1024 * 1024)
                System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, text + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 记日志失败不应影响主流程，静默忽略。
        }
    }

    /// <summary>实时诊断日志的文件名。</summary>
    public const string DiagLogFileName = "诊断日志.txt";

    /// <summary>
    /// 导出诊断的文件名。**刻意与实时日志分开**：
    /// 实时日志是运行期 append-only 的证据链，导出若写同一个文件，
    /// 就会在用户最需要证据的那一刻把它清空。
    /// </summary>
    public const string ExportDiagFileName = "导出诊断.txt";

    /// <summary>导出诊断文件的路径（输出目录下的「导出诊断.txt」）。</summary>
    public static string ExportDiagPath =>
        System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExportDiagFileName);

    /// <summary>
    /// 读取实时诊断日志全文（永不抛异常，读不到就返回空串）。
    ///
    /// 导出写的是独立文件、不再覆盖实时日志，因此必须把实时内容**抄进导出**，
    /// 否则用户手上只有半份证据。
    ///
    /// 共享模式必须是 <see cref="System.IO.FileShare.ReadWrite"/>：日志正被
    /// <see cref="AppendDiagLog"/> 以追加方式持有，不放开写共享就会读失败。
    /// </summary>
    public static string ReadDiagLog()
    {
        try
        {
            var path = DiagLogPath;
            if (!System.IO.File.Exists(path)) return "";
            using var fs = new System.IO.FileStream(
                path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite);
            using var sr = new System.IO.StreamReader(
                fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>上一次已写入日志的帧错误。用于节流：设备移除后每帧都失败，逐帧写盘会撑爆。</summary>
    private string _lastLoggedError = "";

    /// <summary>帧节流间隔（毫秒）。WGC 按合成器节奏投递帧（可达高刷），但 OCR 只需几帧/秒，
    /// 多余帧只会在驱动里白白拷贝一次、Map 一次，加重 GPU 负担。</summary>
    private const long FrameThrottleMs = 200;

    /// <summary>上次成功转图的时间戳（TickCount64），用于帧节流。</summary>
    private long _lastConvertTicks;

    /// <summary>
    /// 复用的 staging 纹理。以前每帧都 new Texture2D —— 等于每帧在驱动里
    /// 创建/销毁一次资源。仅在尺寸或格式变化（窗口缩放等）时才重建。
    /// </summary>
    private Texture2D? _stagingTexture;

    /// <summary>
    /// SoftwareBitmap 异步转换进行中的重入守卫（0=空闲，1=忙）。
    /// 回调线程写入、异步续体线程清除，必须用 Interlocked 访问。
    /// </summary>
    private int _softwareBitmapInFlight;

    /// <summary>
    /// SoftwareBitmap 路径诊断日志已写入次数。
    /// 只记前几次，避免每秒 5 条把日志刷爆。
    /// </summary>
    private int _sbDiagCount;

    /// <summary>本次 SoftwareBitmap 转换的起始时间戳（TickCount64），用于 5 秒卡死自愈。</summary>
    private long _sbStartedTicks;


    /// <summary>
    /// 本进程内出现过的帧错误历史（每一条唯错一次），供「导出诊断」整体写出。
    /// 上限 300 条，超出丢最旧的。运行期仍会即时追加到日志文件（不导出也不丢）。
    /// </summary>
    private static readonly List<string> _errorHistory = new();

    /// <summary>帧错误历史快照（只读，供导出时拼接）。</summary>
    public static IReadOnlyList<string> ErrorHistory
    {
        get { lock (_errorHistory) return _errorHistory.ToArray(); }
    }

    /// <summary>
    /// 记录一次转帧失败：既写状态栏（<see cref="LastFrameError"/>），也落盘诊断日志。
    /// 相同错误只写第一次，避免设备失效后逐帧刷屏。
    /// </summary>
    private void SetFrameError(string message)
    {
        LastFrameError = message;
        if (message == _lastLoggedError) return;
        _lastLoggedError = message;

        var line = $"[{DateTime.Now:HH:mm:ss}] 帧错误：{message}"
            + $"\n    到达={FrameArrivedCount} 取帧={FrameTakenCount} 转图={FrameConvertedCount}"
            + $" 设备={DeviceKind}/{AdapterName}";

        AppendDiagLog(line);
        lock (_errorHistory)
        {
            if (_errorHistory.Count >= 300) _errorHistory.RemoveAt(0);
            _errorHistory.Add(line);
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        FrameArrivedCount++;
        try
        {
            // 【诊断】设备若已被移除，之后所有 D3D 调用都会失败。这里提前记下
            // 时间 + 帧号 + 移除原因，用来确认「设备到底是在哪一帧之后死的」。
            if (_device != null && _device.DeviceRemovedReason.Code != 0)
            {
                var rr = _device.DeviceRemovedReason;
                DeviceLostReason = $"0x{(int)rr.Code:X8}";
                SetFrameError($"设备已被移除：reason=0x{(int)rr.Code:X8}");
                return;
            }

            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            FrameTakenCount++;

            // 【诊断二分】只计数、不做像素回读。用于判定 DEVICE_HUNG 是否由
            // 我们自己的 CopyResource + Map 引起：置 true 仍崩 → 是 WGC 内部
            // 设备与本程序显式设备共存的问题；不崩 → 触发点就是拷贝动作。
            if (SkipPixelReadback) return;

            // 【节流】按时间戳丢帧：上一帧成功转图距今不足 FrameThrottleMs 就丢弃本帧。
            // 不能在这里用 await Task.Delay —— 本回调是同步的，改成 async void 会让
            // 多次回调并发共用同一个 ImmediateContext（D3D11 非线程安全），
            // 反而更容易触发 DEVICE_HUNG。丢帧是零成本且线程安全的做法。
            if (Environment.TickCount64 - _lastConvertTicks < FrameThrottleMs) return;

            if (UseSoftwareBitmapPath)
            {
                // ---- WinRT 官方路径：完全不碰自建 D3D context ----
                // 异步转换未结束就丢帧。这里绝不能改成 async void —— 回调并发会
                // 让多个转换共用非线程安全的 D3D context，反而更容易 DEVICE_HUNG。
                // surface 是独立引用计数的对象，frame 在本方法返回时释放不影响它。
                if (System.Threading.Interlocked.CompareExchange(
                        ref _softwareBitmapInFlight, 1, 0) != 0)
                {
                    // 上一次转换迟迟不结束：超过 5 秒判定为卡死，强制复位重来。
                    // 否则守卫会永久占死，之后每一帧都被丢掉、转图恒为 0。
                    long started = _sbStartedTicks;
                    if (started != 0 && Environment.TickCount64 - started > 5000)
                    {
                        System.Threading.Interlocked.Exchange(ref _softwareBitmapInFlight, 0);
                        SetFrameError("SoftwareBitmap 转换超过 5 秒未完成，判定卡死并复位守卫");
                    }
                    return;
                }
                _sbStartedTicks = Environment.TickCount64;
                _ = ConvertViaSoftwareBitmapAsync(frame.Surface);
                return;
            }

            PublishFrame(ConvertSurfaceToSkBitmap(frame.Surface));
        }
        catch (Exception ex)
        {
            // 以前这里把异常全吞了：结果是 GetLatestFrame() 永远返回 null，
            // 上层只能报一个毫无信息量的「未取到画面」。改为记下真实原因（并落盘）。
            System.Threading.Interlocked.Exchange(ref _softwareBitmapInFlight, 0);
            SetFrameError($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把转好的帧发布为「最新帧」并计算指纹（两条路径共用）。</summary>
    private void PublishFrame(SKBitmap? bitmap)
    {
        if (bitmap == null) return;
        FrameConvertedCount++;
        _lastConvertTicks = Environment.TickCount64;

        // 顺手算指纹（微秒级），供 OCR 前置的变化检测使用。
        // 放在这里而不是主流程：此时 bitmap 已在手，零额外拷贝。
        // 在锁内计算是为了让 ROI 的读与 UI 线程的写互斥（Rect? 非原子）。
        lock (_lock)
        {
            _latestFrame?.Dispose();
            _latestFrame = bitmap;
            _latestFingerprint = Services.FrameFingerprint.Compute(
                bitmap, RoiGeometry.ToSkRect(bitmap, _roiNormalized));
        }
    }

    /// <summary>
    /// WinRT 官方 SoftwareBitmap 路径：GPU→CPU 拷贝由系统内部完成，
    /// 我们一行 D3D 调用都不做（这正是之前 DEVICE_HUNG 的触发点）。
    /// </summary>
    private async System.Threading.Tasks.Task ConvertViaSoftwareBitmapAsync(IDirect3DSurface surface)
    {
        int seq = System.Threading.Interlocked.Increment(ref _sbDiagCount);
        bool log = seq <= 3;   // 只记前 3 次，避免刷屏
        try
        {
            if (log) AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] SB 已发起转换（第 {seq} 次）");

            using var sb = await global::Windows.Graphics.Imaging.SoftwareBitmap
                .CreateCopyFromSurfaceAsync(surface);

            if (log) AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] SB 转换完成（第 {seq} 次）："
                + $"{sb.PixelWidth}×{sb.PixelHeight}");

            var bmp = SoftwareBitmapToSkBitmap(sb);
            if (bmp == null)
            {
                SetFrameError("SoftwareBitmap 转 SKBitmap 返回 null（尺寸无效或像素读取失败）");
            }
            else if (log)
            {
                AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] SB 已转为 SKBitmap（第 {seq} 次）："
                    + $"{bmp.Width}×{bmp.Height}");
            }
            PublishFrame(bmp);
        }
        catch (Exception ex)
        {
            AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] SB 转换异常（第 {seq} 次）："
                + $"{ex.GetType().Name}: {ex.Message}");
            SetFrameError($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _softwareBitmapInFlight, 0);
        }
    }

    /// <summary>
    /// SoftwareBitmap(BGRA8) → SKBitmap。
    ///
    /// 全程只用 CsWinRT 投影 API（CopyToBuffer + DataReader），不做任何原生指针
    /// interop —— 上一版闪退（0xc0000005 访问违规）就是手工 QI 引起的，这里刻意规避。
    /// </summary>
    private static SKBitmap? SoftwareBitmapToSkBitmap(
        global::Windows.Graphics.Imaging.SoftwareBitmap sb)
    {
        int w = sb.PixelWidth, h = sb.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        int size = w * h * 4;
        var buffer = new global::Windows.Storage.Streams.Buffer((uint)size);
        sb.CopyToBuffer(buffer);

        var pixels = new byte[size];
        using (var reader = global::Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), size);
        return bitmap;
    }

    /// <summary>
    /// 取 WinRT 对象的底层 IUnknown 指针。
    ///
    /// 不能直接 Marshal.GetObjectForIUnknown / GetIUnknownForObject：
    /// CsWinRT 的投影对象（如这里 frame.Surface）是【普通托管类】而非经典 COM RCW，
    /// 对它调 GetIUnknownForObject，CLR 会把托管对象包成 CCW，返回的指针是
    /// CCW 的身份指针，而不是底层 WinRT 对象的 IUnknown
    /// —— SharpDX 拿这个指针去 QueryInterface(IDXGISurface) 必然失败。
    /// （与步骤 5/6/7 同源：都是 .NET 经典 COM interop 与 CsWinRT 混用导致的。）
    ///
    /// 正确姿势：投影对象实现了 WinRT.IWinRTObject，其 NativeObject.ThisPtr 才是真 IUnknown。
    /// </summary>
    private static IntPtr GetNativeUnknown(object winrtObject)
    {
        if (winrtObject is WinRT.IWinRTObject winrt)
            return winrt.NativeObject.ThisPtr;
        return Marshal.GetIUnknownForObject(winrtObject);   // 老投影兜底
    }

    /// <summary>
    /// IDXGISurface 接口 IID（dxgi.h），已用反射比对
    /// <c>typeof(SharpDX.DXGI.Surface).GUID</c> 验证一致。
    ///
    /// 必须显式 QI 到这个接口：SharpDX 的 ComObject(IntPtr) 构造函数【不做 QI】，
    /// 它会把传入指针直接当作 IDXGISurface 使用，而 ThisPtr 是 IDirect3DSurface 的
    /// 接口指针，两者 vtable 从第 4 个槽起就不同，直接套用会调错方法。
    /// </summary>
    private static readonly Guid IdxgiSurfaceIid =
        new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");

    /// <summary>
    /// IDirect3DDxgiInterfaceAccess 接口 IID。
    /// WinRT 的 IDirect3DSurface 只是【包装器】，本身不实现 IDXGISurface；
    /// 底层 DXGI surface 必须通过该接口的 GetInterface 方法取出。
    ///
    /// 权威来源：MS 官方「Screen capture to video」示例（screen-capture-video）的
    /// Helper wrapper classes 一节，其 C# 声明就是
    ///   [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    ///   interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface([In] ref Guid iid); }
    ///
    /// 历史坑：此处曾误写为 A37624AB-8D5F-4650-9D3E-9EAE3D9BC670 —— 该值不属于
    /// 本接口（实测 surface 的 GetIids 清单里没有它，5 条取指针路径因此全部返回
    /// E_NOINTERFACE(0x80004002)）。这才是「WGC 收到数千帧但转图 0」全线失败的根因。
    /// </summary>
    private static readonly Guid DxgiInterfaceAccessIid =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceFn(IntPtr pThis, ref Guid iid, out IntPtr ppv);

    /// <summary>IInspectable::GetIids（vtable 第 3 槽）：返回对象自称实现的接口 IID 数组。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetIidsFn(IntPtr pThis, out uint iidCount, out IntPtr iids);

    /// <summary>IInspectable::GetRuntimeClassName（vtable 第 4 槽）：返回对象运行时类名 HSTRING。</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRuntimeClassNameFn(IntPtr pThis, out IntPtr className);

    /// <summary>
    /// IInspectable 接口 IID —— 所有真 WinRT 对象必实现。
    /// 诊断用：连它都 QI 失败，说明拿到的指针根本不是 WinRT 对象。
    ///
    /// 注意：此处曾误写为 ECD4B446-17B9-4E77-B0D9-B9BD4B1C2D90 —— 该 GUID 在
    /// WinRT.Runtime.dll / Microsoft.Windows.SDK.NET.dll 中均检索不到，是伪造值。
    /// 后果：「身份体检」无论指针真假都返回 E_NOINTERFACE，把排查方向错误地
    /// 引向「指针身份不可靠」。下面这个值已用二进制检索确认存在于 WinRT.Runtime.dll。
    /// </summary>
    private static readonly Guid IInspectableIid =
        new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    /// <summary>
    /// IAgileObject IID。诊断用：真·敏捷本地对象会应答它；而「非敏捷对象的标准代理」
    /// 一定拒绝——用来辅助判断手里这个指针是不是被代理/包装过。
    /// </summary>
    private static readonly Guid IAgileObjectIid =
        new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    /// <summary>
    /// ComImport 声明（仅路径 3 投影 cast 用，IID 与 DxgiInterfaceAccessIid 相同）。
    /// </summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface([In] ref Guid iid, out IntPtr ppv);
    }

    /// <summary>
    /// 从 WinRT IDirect3DSurface 取出 AddRef 过的 IDXGISurface 裸指针。
    ///
    /// 三条路径逐一尝试（上一轮实测 Marshal.QueryInterface 直连也 E_NOINTERFACE，
    /// 说明对象接口图比预想复杂 —— 干脆全部路径都试，并把每一步的 HRESULT 记进 diag）：
    ///   1. CsWinRT IObjectReference.TryAs（CsWinRT 原生 QI）
    ///   2. Marshal.QueryInterface（经典 QI）
    ///   3. 老 Sample 的投影 cast（IDynamicInterfaceCastable / 经典 RCW QI）
    /// 返回 IntPtr.Zero 时 diag 里有完整失败过程。
    /// </summary>
    private IntPtr AcquireDxgiSurfacePtr(IDirect3DSurface surface, out string diag)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.Append(surface.GetType().Name);
            sb.Append($", IWinRT={surface is WinRT.IWinRTObject}");

            IntPtr pUnk = GetNativeUnknown(surface);
            sb.Append($", ptr=0x{pUnk.ToInt64():X}");

            // ---- 身份判决块：先确定 ptr 到底是什么，再谈任何接口路径 ----
            // 前几轮一直在「再换一条取指针路径」上打转，但必须先回答一个互斥问题：
            //   ptr 是【真实本地 WinRT 对象的指针】，还是【CLR 给托管包装类造的 CCW 身份指针】？
            // 两者都能应答 QI(IInspectable)，光看 QI 结果无法区分，必须用下面的硬证据分开：
            //   cls         对象自报的运行时类名（最直接的物证）
            //   ofp         把 ptr 反向还原成的托管类型（是托管类型 → ptr 就是 CCW）
            //   ccw/ccwEqPtr 经典 interop 视角的指针；若与 ptr 相同 → ptr 就是 CCW
            //   iids/hasAccess 对象自称实现的接口清单；无 A9B3D012 则 QI 失败是对象层事实
            sb.Append($", cls={RuntimeClassNameOf(pUnk)}");
            {
                Guid iidAgile = IAgileObjectIid;
                int hrAgile = Marshal.QueryInterface(pUnk, ref iidAgile, out IntPtr pAgile);
                sb.Append($", agile=0x{hrAgile:X8}");
                if (hrAgile == 0 && pAgile != IntPtr.Zero) Marshal.Release(pAgile);

                var iids = GetIidsOf(pUnk);
                bool hasAccess = false;
                foreach (var g in iids) { if (g == DxgiInterfaceAccessIid) { hasAccess = true; break; } }
                sb.Append($", iids={iids.Length}, hasAccess={(hasAccess ? "Y" : "N")}");

                try
                {
                    object back = Marshal.GetObjectForIUnknown(pUnk);
                    try { sb.Append($", ofp={back.GetType().FullName}"); }
                    finally { Marshal.Release(pUnk); }   // 配平 GetObjectForIUnknown 的 AddRef
                }
                catch (Exception exBack) { sb.Append($", ofpEX={exBack.GetType().Name}"); }
            }
            try
            {
                IntPtr pCcw = Marshal.GetIUnknownForObject(surface);
                try
                {
                    sb.Append($", ccw=0x{pCcw.ToInt64():X}, ccwEqPtr={(pCcw == pUnk ? "Y" : "N")}");
                    // 顺手当一条修复路径：若 ptr 是 CCW，真正的对象也许在这个指针上
                    Guid iidCcw = DxgiInterfaceAccessIid;
                    if (pCcw != pUnk
                        && Marshal.QueryInterface(pCcw, ref iidCcw, out IntPtr pAccCcw) == 0
                        && pAccCcw != IntPtr.Zero)
                    {
                        IntPtr p = CallGetInterface(pAccCcw, sb);
                        Marshal.Release(pAccCcw);
                        if (p != IntPtr.Zero) { diag = sb.ToString(); return p; }
                    }
                }
                finally { Marshal.Release(pCcw); }
            }
            catch (Exception exCcw) { sb.Append($", ccwEX={exCcw.GetType().Name}"); }

            // ---- 路径 0：CsWinRT 官方 ABI 导出 ----
            // MarshalInspectable<T>.FromManaged 是 CsWinRT 官方 ABI 出口，
            // 由它负责剥掉包装层，返回 AddRef 过的真 ABI 指针。
            // 实测 abi 与 ThisPtr 恒等（ptr == abi），说明投影层本就没有多余包装；
            // 真正卡住的是对 IDirect3DDxgiInterfaceAccess 的 QI，而不是指针身份。
            try
            {
                IntPtr pAbi = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface, true);
                sb.Append($", abi=0x{pAbi.ToInt64():X}");
                if (pAbi != IntPtr.Zero)
                {
                    Guid iidA0 = DxgiInterfaceAccessIid;
                    int hr0 = Marshal.QueryInterface(pAbi, ref iidA0, out IntPtr pAcc0);
                    // 同时验证指针身份：官方 ABI 指针 QI IInspectable 必须成功
                    Guid iidIns0 = IInspectableIid;
                    int hrIns0 = Marshal.QueryInterface(pAbi, ref iidIns0, out _);
                    sb.Append($", abiIIns=0x{hrIns0:X8}");
                    if (hr0 == 0 && pAcc0 != IntPtr.Zero)
                    {
                        IntPtr p = CallGetInterface(pAcc0, sb);
                        Marshal.Release(pAcc0);
                        Marshal.Release(pAbi);
                        if (p != IntPtr.Zero) { diag = sb.ToString(); return p; }
                    }
                    sb.Append($", abiQI=0x{hr0:X8}");
                    Marshal.Release(pAbi);
                }
            }
            catch (Exception ex)
            {
                sb.Append($", abiEX={ex.GetType().Name}");
            }

            // 有效性检查：真 WinRT 对象必实现 IInspectable
            Guid iidIns = IInspectableIid;
            int hrIns = Marshal.QueryInterface(pUnk, ref iidIns, out _);
            sb.Append($", IIns=0x{hrIns:X8}");

            // ---- 路径 1：CsWinRT 原生 TryAs ----
            if (surface is WinRT.IWinRTObject wr)
            {
                int hr1 = wr.NativeObject.TryAs(DxgiInterfaceAccessIid, out IntPtr pAcc1);
                sb.Append($", TryAs=0x{hr1:X8}");
                if (hr1 == 0 && pAcc1 != IntPtr.Zero)
                {
                    IntPtr p = CallGetInterface(pAcc1, sb);
                    Marshal.Release(pAcc1);
                    if (p != IntPtr.Zero) { diag = sb.ToString(); return p; }
                }
            }

            // ---- 路径 2：经典 Marshal.QueryInterface ----
            Guid iidA = DxgiInterfaceAccessIid;
            int hr2 = Marshal.QueryInterface(pUnk, ref iidA, out IntPtr pAcc2);
            sb.Append($", QI=0x{hr2:X8}");
            if (hr2 == 0 && pAcc2 != IntPtr.Zero)
            {
                IntPtr p = CallGetInterface(pAcc2, sb);
                Marshal.Release(pAcc2);
                if (p != IntPtr.Zero) { diag = sb.ToString(); return p; }
            }

            // ---- 路径 3：老 Sample 的投影 cast（cast 本身即 QI）----
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)(object)surface;
                Guid iidS = IdxgiSurfaceIid;
                int hr3 = access.GetInterface(ref iidS, out IntPtr p3);
                sb.Append($", Cast=0x{hr3:X8}");
                if (hr3 == 0 && p3 != IntPtr.Zero) { diag = sb.ToString(); return p3; }
            }
            catch (Exception ex)
            {
                sb.Append($", Cast={ex.GetType().Name}");
            }

            // ---- 路径 4：CsWinRT 官方 CastExtensions.As ----
            // 与 C# 直接 cast 不同，As<T> 显式走 QI 并构造 RCW。
            try
            {
                var access4 = WinRT.CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
                Guid iidS4 = IdxgiSurfaceIid;
                int hr4 = access4.GetInterface(ref iidS4, out IntPtr p4);
                sb.Append($", As=0x{hr4:X8}");
                if (hr4 == 0 && p4 != IntPtr.Zero) { diag = sb.ToString(); return p4; }
            }
            catch (Exception ex)
            {
                sb.Append($", As={ex.GetType().Name}");
            }

            diag = sb.ToString();
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            diag = sb + ", EX=" + ex.Message;
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 在 IDirect3DDxgiInterfaceAccess 指针上手动调 GetInterface
    /// （vtable 第 4 槽：0=QI 1=AddRef 2=Release 3=GetInterface），不走 RCW。
    /// 成功返回 AddRef 过的 IDXGISurface 指针（由调用方交给 SharpDX 接管）。
    /// </summary>
    private IntPtr CallGetInterface(IntPtr pAccess, System.Text.StringBuilder sb)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(pAccess);
        IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceFn>(fnPtr);

        Guid iidSurface = IdxgiSurfaceIid;
        int hr = getInterface(pAccess, ref iidSurface, out IntPtr pSurface);
        sb.Append($", GetIface=0x{hr:X8}");
        return hr == 0 ? pSurface : IntPtr.Zero;
    }

    /// <summary>
    /// 直接读 WinRT 对象的运行时类名（诊断用）。
    /// 这是判定「手里这个指针到底是什么东西」最直接的一项：真 surface 应报出
    /// Windows.Graphics.DirectX.Direct3D11.Direct3DSurface 之类的类名；
    /// 若报出托管包装类名或无意义值，说明指针不是本地对象。
    /// 失败一律返回可读标记，绝不抛异常。
    /// </summary>
    private static string RuntimeClassNameOf(IntPtr pThis)
    {
        try
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pThis);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<GetRuntimeClassNameFn>(fnPtr);
            int hr = fn(pThis, out IntPtr hstr);
            if (hr != 0 || hstr == IntPtr.Zero) return $"err0x{hr:X8}";
            try
            {
                IntPtr buf = WindowsGetStringRawBuffer(hstr, out uint len);
                if (buf == IntPtr.Zero) return "null";
                int n = (int)Math.Min(len, 256);   // 防御：异常长度直接截断
                return Marshal.PtrToStringUni(buf, n) ?? "null";
            }
            finally
            {
                WindowsDeleteString(hstr);
            }
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>
    /// 直接调 IInspectable::GetIids（诊断用）：对象【自称】实现了哪些接口。
    /// 若 A9B3D012 不在列表里，就证明 QI 失败是对象层面的真实事实，
    /// 而不是我们哪条取指针路径写错了。失败返回空数组，绝不抛异常。
    /// </summary>
    private static Guid[] GetIidsOf(IntPtr pThis)
    {
        try
        {
            IntPtr vtbl = Marshal.ReadIntPtr(pThis);
            IntPtr fnPtr = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<GetIidsFn>(fnPtr);
            int hr = fn(pThis, out uint count, out IntPtr arr);
            if (hr != 0 || arr == IntPtr.Zero || count == 0) return Array.Empty<Guid>();
            if (count > 64) count = 64;   // 防御：异常大值直接截断

            var list = new Guid[count];
            var raw = new byte[16];
            for (int i = 0; i < count; i++)
            {
                Marshal.Copy(IntPtr.Add(arr, i * 16), raw, 0, 16);
                list[i] = new Guid(raw);
            }
            Marshal.FreeCoTaskMem(arr);
            return list;
        }
        catch
        {
            return Array.Empty<Guid>();
        }
    }

    /// <summary>把 WinRT IDirect3DSurface 转成 SKBitmap（通过 SharpDX 读取像素）</summary>
    private SKBitmap? ConvertSurfaceToSkBitmap(IDirect3DSurface surface)
    {
        if (_device == null || _context == null) return null;

        try
        {
            // 三条路径逐一尝试拿底层 IDXGISurface（详细说明见 AcquireDxgiSurfacePtr）。
            // 失败时 diag 记录了对象类型与每条路径的 HRESULT —— 上屏后可一屏定位。
            IntPtr pSurface = AcquireDxgiSurfacePtr(surface, out string diag);
            if (pSurface == IntPtr.Zero)
                throw new InvalidOperationException($"获取 DXGI Surface 失败（{diag}）");

            // pSurface 已 AddRef，交给 SharpDX 接管（Dispose 时 Release，配平）
                using var dxgiSurface = new SharpDX.DXGI.Surface(pSurface);

                using var texture = dxgiSurface.QueryInterface<Texture2D>();
                var desc = texture.Description;

                // 创建 CPU 可读的 staging 纹理
                var stagingDesc = new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CpuAccessFlags = CpuAccessFlags.Read,
                    OptionFlags = ResourceOptionFlags.None
                };

                // staging 纹理缓存复用：尺寸/格式不变就沿用同一个，避免每帧 new Texture2D
                // （每帧新建 GPU 资源会给驱动带来额外的分配/回收压力）
                if (_stagingTexture == null
                    || _stagingTexture.Description.Width != stagingDesc.Width
                    || _stagingTexture.Description.Height != stagingDesc.Height
                    || _stagingTexture.Description.Format != stagingDesc.Format)
                {
                    _stagingTexture?.Dispose();
                    _stagingTexture = new Texture2D(_device, stagingDesc);
                }
                var staging = _stagingTexture;

                // 【诊断分段 1】GPU→GPU 拷贝；失败时把设备类型/适配器/移除原因一并带出
                try
                {
                    _context.CopyResource(staging, texture);
                }
                catch (Exception cex)
                {
                    string reason = _device == null ? "设备=null" : $"0x{(int)_device.DeviceRemovedReason.Code:X8}";
                    throw new InvalidOperationException(
                        $"CopyResource 失败（设备={DeviceKind}/{AdapterName}, "
                        + $"removedReason={reason}）：{cex.Message}", cex);
                }

                // 【诊断分段 2】锁存读取像素；Map 会同步等 GPU 完成，最易暴露设备移除
                SharpDX.DataBox dataBox;
                try
                {
                    dataBox = _context.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                }
                catch (Exception mex)
                {
                    string reason = _device == null ? "设备=null" : $"0x{(int)_device.DeviceRemovedReason.Code:X8}";
                    throw new InvalidOperationException(
                        $"MapSubresource 失败（设备={DeviceKind}/{AdapterName}, "
                        + $"removedReason={reason}）：{mex.Message}", mex);
                }

                try
                {
                    var bitmap = new SKBitmap(desc.Width, desc.Height,
                        SKColorType.Bgra8888, SKAlphaType.Premul);

                    var pixels = bitmap.GetPixels();
                    int widthBytes = desc.Width * 4; // BGRA = 4 bytes/pixel

                    for (int y = 0; y < desc.Height; y++)
                    {
                        IntPtr src = IntPtr.Add(dataBox.DataPointer, y * dataBox.RowPitch);
                        IntPtr dst = IntPtr.Add(pixels, y * widthBytes);
                        unsafe
                        {
                            System.Buffer.MemoryCopy(
                                (void*)src, (void*)dst, widthBytes, widthBytes);
                        }
                    }

                    return bitmap;
                }
                finally
                {
                    _context.UnmapSubresource(staging, 0);
                }
        }
        catch (Exception ex)
        {
            // 同样不要静默失败：记下原因（并落盘），否则上层只会说「未取到画面」。
            // 附带设备状态：任何一步抛异常时，用 removedReason 判定是否设备已失效。
            string devState = _device == null
                ? "设备=null"
                : $"设备={DeviceKind}/{AdapterName}, removedReason=0x{(int)_device.DeviceRemovedReason.Code:X8}";
            SetFrameError($"{ex.GetType().Name}: {ex.Message}（{devState}）");
            return null;
        }
    }

    /// <summary>获取最新一帧（调用者负责 Dispose）</summary>
    public SKBitmap? GetLatestFrame()
    {
        lock (_lock)
        {
            return _latestFrame?.Copy();
        }
    }

    /// <summary>
    /// 取最近一帧的灰度指纹（副本，调用者可安全持有）。
    /// 用于 OCR 前置的变化检测：与上一帧指纹比较，未变化则整帧跳过 OCR。
    /// </summary>
    public byte[]? GetLatestFingerprint()
    {
        lock (_lock)
        {
            return _latestFingerprint;
        }
    }

    /// <summary>
    /// 捕获区域左上角在屏幕上的物理像素坐标。
    ///
    /// WGC 的 CaptureItem 对应【整个窗口】，原点原则上取窗口左上角；
    /// 但 GetWindowRect 在 Win10/11 上包含约 7~8px 的【不可见调整边框】，
    /// 而 WGC 实际捕获的是可见内容，两者相差一个边框宽度。
    /// 因此优先用 DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)，
    /// 拿到的才是真正的可见框架边界；失败时才退回 GetWindowRect。
    /// </summary>
    public System.Windows.Point GetCaptureOriginPx()
    {
        if (_hwnd == IntPtr.Zero) return new System.Windows.Point(0, 0);

        try
        {
            int hr = DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                out var dwm, Marshal.SizeOf<RECT>());
            if (hr == 0 && (dwm.Right - dwm.Left) > 0 && (dwm.Bottom - dwm.Top) > 0)
                return new System.Windows.Point(dwm.Left, dwm.Top);
        }
        catch { /* 退回 GetWindowRect */ }

        if (!GetWindowRect(_hwnd, out var rect)) return new System.Windows.Point(0, 0);
        return new System.Windows.Point(rect.Left, rect.Top);
    }

    private IntPtr _hwnd;

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        _captureItem = null;
        try { _stagingTexture?.Dispose(); } catch { }
        _stagingTexture = null;
        try { _d3dDevice?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        lock (_lock) { _latestFrame?.Dispose(); _latestFrame = null; }
        // 置空引用并清掉设备失效标记：避免释放后再读 DeviceRemovedReason，
        // 也让上层能区分"已停止"与"设备失效待降级"。
        _context = null;
        _device = null;
        DeviceLostReason = null;
    }
}

/// <summary>
/// 从 HWND 创建 GraphicsCaptureItem 的互操作辅助类。
///
/// 注意：不能用 <c>GraphicsCaptureItem.As&lt;IGraphicsCaptureItemInterop&gt;()</c> ——
/// 新版 CsWinRT（.NET 8 / Windows SDK 26100 投影）里运行时类的静态 <c>As&lt;T&gt;()</c>
/// 对 [ComImport] 接口做的是普通 .NET 类型转换而非 QueryInterface，会抛
/// 「Specified cast is not valid」。正确做法：RoGetActivationFactory 拿到激活工厂，
/// 再用 <c>ObjectReference.AsInterface&lt;T&gt;()</c>（真正的 QI）。
/// </summary>
internal static class CaptureItemHelper
{
    private const string RuntimeClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr windowHandle, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitorHandle, [In] ref Guid iid);
    }

    // CreateForWindow 的 riid 必须是「IGraphicsCaptureItem 接口」IID，
    // 而不是运行时类 CLSID（typeof(GraphicsCaptureItem).GUID 拿到的是 CLSID，是错的）。
    private static readonly Guid IGraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    // IActivationFactory 接口 IID（RoGetActivationFactory 第二个参数）
    private static readonly Guid IActivationFactoryIid =
        new("00000035-0000-0000-C000-000000000046");

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    /// <summary>IActivationFactory 虚表（拿到激活工厂裸指针后用它包装成可 QI 的引用）。</summary>
    [Guid("00000035-0000-0000-C000-000000000046")]
    private unsafe struct IActivationFactoryVftbl
    {
#pragma warning disable CS0649
        public readonly WinRT.IInspectable.Vftbl IInspectableVftbl;
        private readonly void* _ActivateInstance;
#pragma warning restore
        public delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int> ActivateInstance =>
            (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)_ActivateInstance;
    }

    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var marshaler = WinRT.MarshalString.CreateMarshaler(RuntimeClassName);
        try
        {
            IntPtr hstr = WinRT.MarshalString.GetAbi(marshaler);
            Guid iidFactory = IActivationFactoryIid;   // static readonly 字段不能直接 ref，落到局部
            int hr = RoGetActivationFactory(hstr, ref iidFactory, out IntPtr factoryPtr);
            if (hr != 0)
                throw new InvalidOperationException($"RoGetActivationFactory 失败，HRESULT=0x{hr:X8}");

            var factoryRef = WinRT.ObjectReference<IActivationFactoryVftbl>.Attach(ref factoryPtr, IActivationFactoryIid);
            var interop = factoryRef.AsInterface<IGraphicsCaptureItemInterop>();
            Guid iidItem = IGraphicsCaptureItemIid;
            IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iidItem);
            if (itemPtr == IntPtr.Zero) return null;
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            WinRT.MarshalString.DisposeMarshaler(marshaler);
        }
    }
}
