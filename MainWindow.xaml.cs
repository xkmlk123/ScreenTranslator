using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Windows;

namespace ScreenTranslator;

/// <summary>
/// 主控制面板：选择窗口、开启/关闭翻译、设置
/// </summary>
public partial class MainWindow : Window
{
    private AppConfig _config;
    private WgcCaptureService? _capture;
    private GdiCaptureService? _gdiCapture;
    private bool _useGdi;
    private bool _forceGdi;             // 「用 GDI 捕获（备用模式）」勾选状态
    private bool _skipReadback;         // 「WGC 仅计数不取像素（诊断）」勾选状态
    private bool _debugFrameSaved;
    private bool _overlayCaptureSafe;   // 覆盖层是否已排除出屏幕捕获（false → 已启用兜底）
    private string _lastWgcError = "";  // WGC 启动失败的真实原因（上屏，别再只写 Debug）
    private string _currentWindowTitle = "";
    private IntPtr _targetHwnd;

    private OcrService? _ocr;
    private TranslationService? _translator;
    private OverlayWindow? _overlay;
    private HotkeyManager? _hotkeys;
    private System.Windows.Threading.DispatcherTimer? _timer;

    private readonly TranslationCache _cache = new(3000);

    private bool _running;
    private bool _busy;                 // 上一帧尚未处理完，跳过本帧
    private string _lastSignature = "";  // 上一帧文本签名
    private List<TranslationSegment>? _lastSegments;
    private int _errorStreak;

    // 变化检测（阶段 2）：OCR 之前先比较指纹，画面没变就整帧跳过 OCR
    private byte[]? _prevFingerprint;
    private int _skipByChangeCount;      // 累计跳过 OCR 的次数（状态栏展示）

    // 识别区域（阶段 3）：ROI 以归一化比例存放，每次用帧时按当前尺寸反算像素
    private int _lastFrameW;             // 最近一帧的物理尺寸（ROI 换算的基准）
    private int _lastFrameH;

    public MainWindow()
    {
        InitializeComponent();
        _config = ConfigService.Load();
        // 把使用者指定的推理线程数注入 OCR 层（必须在 OcrService 构造之前）
        OcrRuntime.SetUserThreads(_config.OcrThreads);
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => StopTranslation();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadConfigToUi();
        RefreshWindows_Click(null!, null!);
        SetupHotkey();
    }

    private void SetupHotkey()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager(this);
        if (!_hotkeys.Register(_config.ToggleHotkey, Toggle_ClickInternal))
        {
            HotkeyHint.Text = $"全局热键 {_config.ToggleHotkey} 注册失败（可能被占用）";
        }
        else
        {
            HotkeyHint.Text = $"全局热键：{_config.ToggleHotkey}（任意窗口下可启停）";
        }
    }

    private void Toggle_ClickInternal()
    {
        // 热键回调在 UI 线程，直接切换
        if (_running) StopTranslation();
        else StartTranslation();
    }

    /// <summary>把配置显示到界面输入框</summary>
    private void LoadConfigToUi()
    {
        ApiUrlBox.Text = _config.ApiBaseUrl;
        ApiKeyBox.Text = _config.ApiKey;
        ModelBox.Text = _config.Model;
        LocalUrlBox.Text = _config.LocalApiUrl;
        LocalModelBox.Text = _config.LocalModel;

        foreach (ComboBoxItem item in ModeCombo.Items)
        {
            if ((string)item.Tag == _config.TranslationMode)
            {
                ModeCombo.SelectedItem = item;
                break;
            }
        }
        UpdateModePanel();

        // 同步"仅显示识别框"开关状态（此时 _config 已就绪，回调里会正确写回）
        if (OcrBoxesOnlyCheck != null) OcrBoxesOnlyCheck.IsChecked = _config.OcrBoxesOnly;
        if (OcrBoxesTextCheck != null) OcrBoxesTextCheck.IsChecked = _config.OcrBoxesShowText;
        if (ShowOriginalOnlyCheck != null) ShowOriginalOnlyCheck.IsChecked = _config.ShowOriginalOnly;

        UpdateRoiInfo();
    }

    /// <summary>把界面输入保存回配置</summary>
    private void SaveUiToConfig()
    {
        if (ModeCombo.SelectedItem is ComboBoxItem item)
            _config.TranslationMode = (string)item.Tag;

        _config.ApiBaseUrl = ApiUrlBox.Text.Trim();
        _config.ApiKey = ApiKeyBox.Text.Trim();
        _config.Model = ModelBox.Text.Trim();
        _config.LocalApiUrl = LocalUrlBox.Text.Trim();
        _config.LocalModel = LocalModelBox.Text.Trim();
        ConfigService.Save(_config);
    }

    private void ModeCombo_Changed(object sender, SelectionChangedEventArgs e) => UpdateModePanel();

    /// <summary>切换"仅显示识别框"：立即清屏，不等下一帧，避免残留旧译文</summary>
    private void OcrBoxesOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.OcrBoxesOnly = OcrBoxesOnlyCheck?.IsChecked == true;
        ConfigService.Save(_config);

        // 模式切换时清掉上一帧遗留的内容与去重签名
        _lastSignature = "";
        _lastSegments = null;
        _overlay?.ClearOverlay();

        if (_running)
        {
            if (OcrBoxesOnlyCheck?.IsChecked == true)
                SetStatus("已切换到【仅显示识别框】模式，正在识别…");
            else
                SetStatus("已退出识别框模式，恢复翻译显示");
            _ = ProcessFrame();
        }
    }

    /// <summary>切换是否标注原文</summary>
    private void OcrBoxesText_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.OcrBoxesShowText = OcrBoxesTextCheck?.IsChecked == true;
        ConfigService.Save(_config);
        if (_running && _config.OcrBoxesOnly) _ = ProcessFrame();
    }

    /// <summary>
    /// 切换"只显示原文（不翻译）"：跳过整条翻译链路，把原文按译文样式画出。
    /// 与"仅显示识别框"互斥使用时，"仅显示识别框"优先（更偏调试）。
    /// </summary>
    private void ShowOriginalOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.ShowOriginalOnly = ShowOriginalOnlyCheck?.IsChecked == true;
        ConfigService.Save(_config);

        // 模式切换时清掉上一帧遗留的内容与去重签名
        _lastSignature = "";
        _lastSegments = null;
        _overlay?.ClearOverlay();

        if (_running)
        {
            if (_config.ShowOriginalOnly)
                SetStatus("已切换到【只显示原文】模式，不翻译，正在识别…");
            else
                SetStatus("已退出【只显示原文】模式，恢复翻译显示");
            _ = ProcessFrame();
        }
    }

    private void UpdateModePanel()
    {
        if (CloudPanel == null || LocalPanel == null || ModeCombo == null) return;
        bool isLocal = ModeCombo.SelectedItem is ComboBoxItem item && (string)item.Tag == "Local";
        CloudPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        LocalPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e)
    {
        var windows = WindowEnumerator.GetVisibleWindows()
            .Where(w => !string.Equals(w.Title, Title, StringComparison.Ordinal))
            .Where(w => !w.Title.Contains("屏幕实时翻译器", StringComparison.Ordinal))
            .ToList();

        WindowCombo.ItemsSource = windows;
        if (windows.Count > 0)
            WindowCombo.SelectedIndex = 0;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => Toggle_ClickInternal();

    private void StartTranslation()
    {
        if (_running) return;

        var win = WindowCombo.SelectedItem as WindowInfo;
        if (win == null)
        {
            MessageBox.Show("请先选择目标窗口", "提示");
            return;
        }

        SaveUiToConfig();

        bool isLocal = _config.TranslationMode == "Local";
        if (isLocal)
        {
            if (string.IsNullOrWhiteSpace(_config.LocalApiUrl) || string.IsNullOrWhiteSpace(_config.LocalModel))
            {
                MessageBox.Show("请填写本地服务地址和模型名称", "提示");
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            MessageBox.Show("请先填写 API Key", "提示");
            return;
        }

        // 本地模式：启动前预检 Ollama 可达性与模型是否存在。
        // 以前连不上时只在内部静默吞掉，用户完全看不到原因，这里提前给出明确提示。
        if (isLocal)
        {
            var (ok, msg) = TranslationService.ProbeLocalAsync(
                _config.LocalApiUrl, _config.LocalModel).GetAwaiter().GetResult();
            if (!ok)
            {
                var r = MessageBox.Show(
                    $"{msg}\n\n是否仍然尝试启动？\n（若用 Ollama，请先运行 `ollama serve` 并 `ollama pull {_config.LocalModel}`）",
                    "无法连接本地模型", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }
        }

        try
        {
            // OCR 模型准备：中文模型的识别词典含 15565 个汉字，
            // 而 NuGet 自带的 latin 模型零个汉字 —— 不换模型的话
            // 界面上的中文（菜单/按钮/提示）会整片识别不到。
            // 首次运行时体积较大（16MB），这里给明确进度与可跳过选项。
            if (!OcrModelManager.IsChineseModelReady())
            {
                var prep = MessageBox.Show(
                    "检测到【中文识别模型】尚未安装。\n\n"
                    + "当前使用的内置模型是拉丁文专用（词典不含任何汉字），\n"
                    + "中文内容（菜单、按钮、中文网页）将完全识别不到。\n\n"
                    + "是否现在下载中文模型？（约 16MB，仅需一次）",
                    "需要下载中文识别模型",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (prep == MessageBoxResult.Yes)
                {
                    var (mOk, mMsg) = OcrModelManager
                        .EnsureChineseModelAsync(msg => SetStatus($"状态：{msg}"))
                        .GetAwaiter().GetResult();

                    if (!mOk)
                    {
                        var r = MessageBox.Show(
                            $"{mMsg}\n\n是否先用内置模型继续？（中文将无法识别）",
                            "模型准备失败", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (r != MessageBoxResult.Yes) return;
                    }
                }
                else
                {
                    var r = MessageBox.Show(
                        "未安装中文模型，界面上的中文将无法识别。\n是否仍要继续？",
                        "继续使用英文模型", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r != MessageBoxResult.Yes) return;
                }
            }

            _ocr ??= new OcrService();
            _translator ??= new TranslationService();

            // 优先 WGC，失败降级 GDI；勾选「用 GDI 捕获（备用模式）」时直接走 GDI。
            _lastWgcError = "";
            _useGdi = false;
            if (_forceGdi)
            {
                _gdiCapture = new GdiCaptureService(win.Hwnd);
                _useGdi = true;
                System.Diagnostics.Debug.WriteLine("手动选择 GDI 捕获（备用模式）");
            }
            else
            {
                try
                {
                    _capture = new WgcCaptureService();
                    _capture.SkipPixelReadback = _skipReadback;   // 诊断二分开关
                    _capture.Start(win.Hwnd);
                }
                catch (Exception wgcEx)
                {
                    _capture?.Dispose();
                    _capture = null;
                    _gdiCapture = new GdiCaptureService(win.Hwnd);
                    _useGdi = true;
                    // 以前只写 Debug.WriteLine，Release 下用户完全看不到降级原因。
                    // 存到字段，稍后拼进状态栏。
                    _lastWgcError = wgcEx is InvalidOperationException ioe
                        ? ioe.Message
                        : $"{wgcEx.GetType().Name}：{wgcEx.Message}";
                    System.Diagnostics.Debug.WriteLine($"WGC 失败，降级 GDI：{_lastWgcError}");
                }
            }

            _overlay = new OverlayWindow();
            _overlay.OriginProvider = GetCaptureOrigin;   // 渲染循环据此做 30Hz 跟随平移
            _overlay.Show();
            _overlay.StartRenderLoop();

            // 确保覆盖层不被自己抓回去（详见方法注释）
            EnsureOverlayCaptureExclusion();

            // 识别区域：告知采集层只对该区域算指纹，并让覆盖层画出绿色范围线
            SyncRoiToCapture();
            ApplyRoiToOverlay();

            _targetHwnd = win.Hwnd;
            _currentWindowTitle = win.Title;
            _debugFrameSaved = false;
            _lastSignature = "";
            _lastSegments = null;
            _errorStreak = 0;
            _prevFingerprint = null;
            _skipByChangeCount = 0;
            _lastFrameW = 0;
            _lastFrameH = 0;

            _timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(300, _config.CaptureIntervalMs))
            };
            _timer.Tick += async (_, _) => await ProcessFrame();
            _timer.Start();

            _running = true;
            ToggleBtn.Content = "■ 停止翻译";
            ToggleBtn.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            string mode = _useGdi
                ? (_lastWgcError.Length > 0 ? "GDI（WGC 失败）" : "GDI")
                : "WGC";
            string ocrMode = _ocr?.IsChineseModel == true ? "中文模型" : "⚠ 英文模型";
            string displayMode = _config.OcrBoxesOnly ? "【仅识别框】" : "翻译";
            string perfInfo = _ocr != null
                ? $"推理：{_ocr.UsedProvider} / {_ocr.UsedThreads} 线程"
                : "";
            StatusText.Text = $"状态：正在运行（{mode}模式 / {displayMode}）\n窗口：{win.Title}\n识别：{ocrMode}｜{perfInfo}"
                + (_lastWgcError.Length > 0 ? $"\nWGC 失败原因：{_lastWgcError}" : "")
                + (_overlayCaptureSafe ? "" : $"\n⚠ 覆盖层防截屏未生效：{_overlay?.CaptureExcludeError}");

            // 立即跑一次，不用等第一个 tick
            _ = ProcessFrame();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：{ex.Message}", "错误");
            StopTranslation();
        }
    }

    private void StopTranslation()
    {
        _timer?.Stop();
        _timer = null;
        _overlay?.StopRenderLoop();
        _overlay?.DropSnapshot();
        _overlay?.Close();
        _overlay = null;
        _capture?.Dispose();
        _capture = null;
        _gdiCapture?.Dispose();
        _gdiCapture = null;
        _useGdi = false;
        _running = false;
        _busy = false;
        ToggleBtn.Content = "▶ 开启翻译";
        ToggleBtn.Background = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        StatusText.Text = "状态：已停止";
    }

    /// <summary>
    /// 处理一帧。
    ///
    /// ---- 线程模型（重要）----
    /// 这个方法由 <see cref="System.Windows.Threading.DispatcherTimer"/> 的 Tick 触发，
    /// 起手在 UI 线程上。但 OCR 单帧要几百毫秒，若直接在 UI 线程跑，
    /// 界面在这期间完全无响应（滑块拖不动、按钮点不动）。
    ///
    /// 因此把「OCR + 框缩放 + 分段 + 签名计算」整段放到后台线程执行
    /// （见 <see cref="OcrService"/> 的线程安全性说明），
    /// 只有最终触碰 <c>OverlayCanvas</c> 的绘制动作才回 UI 线程。
    ///
    /// <paramref name="frame"/> 由本方法创建并释放，生命周期只到
    /// <see cref="RecognizeAndSegment"/> 返回为止 —— 后台任务必须在此之前
    /// 用完像素数据，故用 await 等待完成，不做 fire-and-forget。
    /// </summary>
    private async Task ProcessFrame()
    {
        if (!_running || _busy || _ocr == null || _translator == null) return;
        _busy = true;

        SkiaSharp.SKBitmap? frame = null;
        try
        {
            // ---- 设备失效（DEVICE_REMOVED 0x887A0005 / DEVICE_HUNG 0x887A0006，典型为驱动 TDR）----
            // 必须【先于取帧】判断：设备已死时 GetLatestFrame() 仍会返回最后一帧的副本，
            // 不会走下面的 frame==null 分支，程序就卡死在静止画面上。
            if (!_useGdi && _capture != null && _capture.IsDeviceLost)
                FallbackToGdi($"WGC 设备失效（reason={_capture.DeviceLostReason}），自动降级 GDI（备用模式）");

            frame = _useGdi ? _gdiCapture?.Capture() : _capture?.GetLatestFrame();
            if (frame == null)
            {
                // WGC 模式下要区分三种完全不同的故障，否则只会得到一个没用的「未取到画面」：
                //   到达 0 次 → 会话没投递帧（窗口最小化/不可见，或 StartCapture 未真正生效）
                //   到达 >0 但转图 0 次 → Surface→SKBitmap 转换全线失败（看 LastFrameError）
                //   两者都 >0 但 GetLatestFrame 仍 null → 竞态（首帧尚未到达）
                string wgcDiag = "";
                if (!_useGdi && _capture != null)
                {
                    long arrived = _capture.FrameArrivedCount;
                    long converted = _capture.FrameConvertedCount;
                    string err = _capture.LastFrameError;

                    if (arrived == 0)
                        wgcDiag = "｜WGC 未投递任何帧（窗口可能已最小化/不可见）";
                    else if (converted == 0)
                        wgcDiag = $"｜WGC 已收 {arrived} 帧但全部转换失败"
                                + (err.Length > 0 ? $"：{err}" : "（未知错误）");
                    else
                        wgcDiag = $"｜WGC 已转图 {converted} 帧";

                    System.Diagnostics.Debug.WriteLine(
                        $"[WGC 诊断] arrived={arrived} taken={_capture.FrameTakenCount} " +
                        $"converted={converted} err={err}");
                }

                // ---- 兜底：WGC 持续转换失败（转图 0）也自动降级 GDI ----
                // WGC 启动成功但每帧都在 Surface→Bitmap 上失败时，程序会无限卡住。
                // 阈值 30 帧（约 9s @ 300ms 间隔）确保不是首帧偶尔超时。
                if (!_useGdi && _capture != null
                    && _capture.FrameArrivedCount >= 30
                    && _capture.FrameConvertedCount == 0)
                {
                    string err = _capture.LastFrameError;
                    FallbackToGdi($"WGC 转换持续失败（到达={_capture.FrameArrivedCount}），自动降级 GDI"
                                  + (err.Length > 0 ? $"｜{err}" : ""));
                    frame = _gdiCapture?.Capture();
                }

                if (frame == null)
                {
                    SetStatus("未取到画面（窗口可能已最小化或被遮挡）" + wgcDiag);
                    return;
                }
            }

            SaveDebugFrameOnce(frame);

            // 记住本帧尺寸：ROI 比例 → 像素 的反算基准（框选时也要用）
            _lastFrameW = frame.Width;
            _lastFrameH = frame.Height;

            // ---------- 0. 变化检测（OCR 前置省流）----------
            // 画面没变就整帧跳过 OCR。OCR 是单帧最重的一步（0.6~1.4s），
            // 静止画面（阅读文档/看代码）时每帧白跑一遍是最大无效开销。
            //
            // 注意与后面的「帧间去重」互补：那个发生在 OCR 之后、只省翻译；
            // 这个发生在 OCR 之前、省掉整个 OCR。两个都保留。
            //
            // 渲染不受影响：30Hz 跟随由 Overlay 自己的渲染循环负责，
            // 这里只决定「要不要重跑 OCR」。
            if (_config.UseChangeDetection)
            {
                byte[]? currFp = _useGdi
                    ? _gdiCapture?.GetLatestFingerprint()
                    : _capture?.GetLatestFingerprint();

                if (currFp != null)
                {
                    int minCells = _config.ChangeMinCells is >= 1 and <= 1000
                        ? _config.ChangeMinCells : 3;
                    if (!Services.FrameFingerprint.HasChanged(_prevFingerprint, currFp, minCells))
                    {
                        _prevFingerprint = currFp;
                        _skipByChangeCount++;
                        _errorStreak = 0;
                        // 内容未变 → 不重跑 OCR；只提示一下，画面仍由渲染循环跟随
                        string extra = _lastSegments != null
                            ? $"复用 {_lastSegments.Count} 段"
                            : "无内容";
                        SetStatus($"画面未变化，已跳过 OCR（累计 {_skipByChangeCount} 次）｜ {extra}");
                        return;
                    }
                    _prevFingerprint = currFp;
                }
            }

            // ---------- 1. 后台：OCR + 显示层缩放 + 分段 ----------
            // 这一段是纯 CPU 计算，不碰任何 WPF 元素，可以安全离开 UI 线程。
            // 走 Task.Run 后界面不再被挡住，用户可随时拖动设置滑块。
            //
            // 先在 UI 线程抓一份配置快照：_config 会被 Settings_Click 整体替换
            // （_config = dlg.Config），后台线程直接读 _config 属于数据竞争。
            var ocr = _ocr;

            // 识别区域：把归一化比例反算成【当前帧】的物理像素。
            // 每次都用当前帧尺寸反算，窗口缩放/换窗口时 ROI 自动跟着走。
            var roi = RoiGeometry.ToFramePx(_config, frame.Width, frame.Height);
            if (_overlay != null) _overlay.RoiFramePx = roi;

            var snap = new FrameOptions(_config.MinConfidence, _config.OcrScale,
                                        _config.OcrBoxScale, roi);
            var result = await Task.Run(() => RecognizeAndSegment(ocr, frame, snap));

            if (!_running) return;   // 等待期间用户点了「停止」

            if (result.Blocks.Count == 0)
            {
                _lastSignature = "";
                _lastSegments = null;
                _overlay?.ClearOverlay();
                _errorStreak = 0;
                SetStatus($"OCR：原始 {result.RawCount} 条，过滤后 0 条（阈值 {_config.MinConfidence}）");
                return;
            }

            // ---------- 仅显示识别框模式 ----------
            // 只把 OCR 结果画成框，完全不走翻译与译文绘制。
            // 用途：单独校验 OCR 到底识别到了什么、框在哪。
            if (_config.OcrBoxesOnly)
            {
                _lastSignature = result.Signature;
                _lastSegments = null;

                _overlay?.PublishSnapshot(new OverlayWindow.RenderSnapshot
                {
                    Signature = result.Signature,
                    OcrBoxesOnly = true,
                    Blocks = result.Blocks,
                    ShowText = _config.OcrBoxesShowText,
                    ShowScore = _config.OcrBoxesShowScore
                });

                if (_overlay?.IsRenderLoopRunning != true)
                {
                    var (bx0, by0, bs0) = GetCaptureOrigin();
                    _overlay?.DrawOcrBoxes(result.Blocks, bx0, by0, bs0,
                                           _config.OcrBoxesShowText, _config.OcrBoxesShowScore);
                }

                int lowConf = result.Blocks.Count(b => b.Score < 0.6f);
                int cjk = result.Blocks.Count(b => b.Text.Any(c => c >= 0x4e00 && c <= 0x9fff));
                string ocrTag = _ocr?.IsChineseModel == true ? "中文模型" : "⚠ 英文模型";
                string fitTag = Math.Abs(_config.OcrBoxScale - 1.0) > 0.001
                    ? $"框 {_config.OcrBoxScale * 100:F0}%" : "框 100%";

                SetStatus($"【仅识别框】原始 {result.RawCount} 条 → 显示 {_overlay?.LastDrawnCount ?? 0} 框"
                          + $"（低置信 {lowConf}）｜ 含中文 {cjk} 条 ｜ {fitTag} ｜ 识别：{ocrTag}");
                _errorStreak = 0;
                return;
            }

            // ---------- 只显示原文模式（不翻译）----------
            // 跳过整条翻译链路，把每块的原文直接当作"译文"填回，按译文样式绘制。
            //
            // 这里**故意**把 HasValidTranslation 置 true：
            // 该门控本意是「翻译失败/图标不画」，而本模式下用户就是要看原文，
            // 是它的唯一合法例外（详见 OcrBlock.HasValidTranslation 注释）。
            // 未经 NeedsTranslation 过滤 —— 本模式下纯数字/图标也要原样标出。
            if (_config.ShowOriginalOnly)
            {
                _lastSignature = result.Signature;
                var segs = result.Segments!;
                foreach (var seg in segs)
                {
                    seg.TranslatedText = seg.SourceText;
                    foreach (var b in seg.Blocks)
                    {
                        b.Translated = b.Text;
                        b.HasValidTranslation = true;
                    }
                }
                _lastSegments = segs;
                Redraw(segs);
                SetStatus($"【只显示原文】{segs.Count} 段（未翻译）");
                _errorStreak = 0;
                return;
            }

            // ---------- 2. 帧间去重 ----------
            if (result.Signature == _lastSignature && _lastSegments != null)
            {
                RedrawLastFrame();
                SetStatus($"内容未变，复用上次译文（{_lastSegments.Count} 段）｜ 缓存命中 {_cache.Hits}");
                return;
            }
            _lastSignature = result.Signature;

            // ---------- 3. 翻译（TranslateAwareAsync 内部 await 网络请求）----------
            var segments = result.Segments!;
            await TranslateAwareAsync(segments);

            if (!_running) return;

            _lastSegments = segments;

            // ---------- 4. 绘制 ----------
            Redraw(segments);

            // 段数统计（语义要分清，否则状态栏会误报）：
            //   filtered  —— 无需翻译的段（纯数字/标点/符号/已是目标语言），不画
            //   fallback  —— 送去了翻译但失败（模型原样返回），不画
            //   translatedCount —— 其余有译文的段
            int filtered = _skippedByFilter;
            int fallback = _lastFallbackCount;
            int translatedCount = segments.Count(s => !string.IsNullOrWhiteSpace(s.TranslatedText))
                                - filtered;   // 扣掉"无需翻译"那批（它们把 TranslatedText 设成了原文）
            if (translatedCount < 0) translatedCount = 0;
            int drawn = _overlay?.LastDrawnCount ?? 0;
            int skipped = _overlay?.LastSkippedCount ?? 0;
            var blocks = result.Blocks;

            // 自检 1：真正送去了翻译的段全部失败 → 多半是模型/服务没配好
            if (fallback > 0 && translatedCount == 0)
            {
                string mode = _config.TranslationMode == "Local" ? "本地 Ollama" : "云端 API";
                string reason = _translator?.LastError is { Length: > 0 } err
                    ? "\n原因：" + err
                    : "";
                SetStatus($"⚠ 翻译服务未生效（{mode}）：{fallback} 段原样返回原文。{reason}");
            }
            // 自检 2：翻译成功但一块都没画出来 → 绘制环节问题
            else if (translatedCount > 0 && drawn == 0)
            {
                SetStatus($"⚠ 已翻译 {translatedCount} 段，但绘制 0 块"
                          + $"（跳过 {skipped}）｜ 请检查设置中的 X/Y 偏移、或开启调试框查看");
            }
            else
            {
                string ocrTag = _ocr?.IsChineseModel == true ? "" : "｜⚠ 非中文模型，中文识别不到";
                SetStatus($"OCR：{blocks.Count} 条 → 合并 {segments.Count} 段，"
                          + $"已翻译 {translatedCount} 段，实际绘制 {drawn} 块"
                          + (filtered > 0 ? $"（{filtered} 段无需翻译）" : "")
                          + (skipped > 0 ? $"（跳过 {skipped}）" : "")
                          + (fallback > 0 ? $"｜{fallback} 段未翻译" : "")
                          + ocrTag);
            }
            _errorStreak = 0;
        }
        catch (Exception ex)
        {
            // 用户点「停止」会取消后台任务，TaskCanceledException 属正常路径，不计入错误
            if (ex is OperationCanceledException) return;

            _errorStreak++;
            SetStatus($"处理出错（连续 {_errorStreak} 次）：{ex.Message}");
            if (_errorStreak >= 5)
            {
                SetStatus($"连续出错已自动停止。最后错误：{ex.Message}");
                StopTranslation();
            }
        }
        finally
        {
            frame?.Dispose();
            _busy = false;
        }
    }

    /// <summary>后台帧处理的结果（<see cref="ProcessFrame"/> 与绘制之间传递）</summary>
    private sealed class FrameResult
    {
        public List<Models.OcrBlock> Blocks = new();
        public List<TranslationSegment>? Segments;
        public string Signature = "";
        public int RawCount;
    }

    /// <summary>
    /// 一帧识别所需的配置快照（不可变，供后台线程安全读取）。
    /// 为什么要快照：设置窗口保存后会整体替换 <c>_config</c>，
    /// 后台线程直接读字段会与替换动作产生竞争，读出半新半旧的值。
    /// </summary>
    private readonly record struct FrameOptions(
        float MinConfidence,
        double OcrScale,
        double OcrBoxScale,
        Rect? Roi);

    /// <summary>
    /// 纯计算部分：OCR → 显示层缩放 → 分段 → 帧签名。
    ///
    /// 这个方法**不触碰任何 WPF 元素**，因此可以安全地跑在后台线程上。
    /// 拆出来是为了让"哪些能在后台跑"一目了然 —— 以后若往管线里加步骤，
    /// 只要新步骤不碰 UI，就放在这里。
    ///
    /// 当 <c>opt.Roi</c> 非空时，管线顺序为：
    /// <b>裁剪 → OCR → 把框平移回全帧坐标 → 显示层缩放 → 签名 → 分段</b>。
    /// 平移回全帧是关键一步：下游的签名/分段/绘制全部沿用「全帧坐标」，
    /// 它们无需感知 ROI 的存在，从而零改动、零回归。
    ///
    /// 注意：<paramref name="frame"/> 的释放由调用方负责，本方法只读不释放。
    /// 裁剪出的子图由本方法负责释放（try/finally）。
    /// </summary>
    private static FrameResult RecognizeAndSegment(OcrService ocr, SkiaSharp.SKBitmap frame, FrameOptions opt)
    {
        var result = new FrameResult();

        SkiaSharp.SKBitmap? sub = null;
        try
        {
            SkiaSharp.SKBitmap source = frame;
            double offX = 0, offY = 0;

            // 裁剪：取整与夹取统一交给 RoiGeometry，避免越界 / 漏边
            if (RoiGeometry.ToCropRect(frame.Width, frame.Height, opt.Roi) is var crop
                && crop.HasValue)
            {
                var (l, t, w, h) = crop.Value;
                sub = new SkiaSharp.SKBitmap(w, h, frame.ColorType, frame.AlphaType);
                using (var cv = new SkiaSharp.SKCanvas(sub))
                {
                    cv.DrawBitmap(frame,
                        new SkiaSharp.SKRect(l, t, l + w, t + h),
                        new SkiaSharp.SKRect(0, 0, w, h));
                }
                source = sub;
                offX = l;
                offY = t;
            }

            var blocks = ocr.Recognize(source, opt.MinConfidence, opt.OcrScale, out int rawCount);
            result.Blocks = blocks;
            result.RawCount = rawCount;

            if (blocks.Count == 0) return result;

            // 把裁剪坐标系下的框平移回全帧坐标系（识别本身已用原始框完成，不受影响）
            if (offX != 0 || offY != 0)
            {
                foreach (var b in blocks)
                    b.Box = new Rect(b.Box.X + offX, b.Box.Y + offY, b.Box.Width, b.Box.Height);
            }

            // 检测框显示层缩放（不影响识别：识别已用原始框完成）
            OcrBoxFitter.ApplyTo(blocks, opt.OcrBoxScale);

            // 帧签名：用于判断"内容是否变化"，避免重复翻译
            result.Signature = TextNormalizer.FrameSignature(blocks.Select(b => b.Text));

            // 段落合并：按行/列几何把碎片拼回整句
            result.Segments = TextSegmenter.BuildSegments(blocks);

            return result;
        }
        finally
        {
            sub?.Dispose();
        }
    }


    /// <summary>
    /// 翻译各段落：先查缓存，只把未命中的段落送到大模型，翻译后回填缓存并写回各块。
    /// </summary>
    private async Task TranslateAwareAsync(List<TranslationSegment> segments)
    {
        if (_translator == null) return;

        var pending = new List<TranslationSegment>();
        var pendingTexts = new List<string>();
        _skippedByFilter = 0;   // 本帧被过滤掉（无需翻译）的段数

        foreach (var seg in segments)
        {
            string src = seg.SourceText;
            if (string.IsNullOrWhiteSpace(src)) continue;

            // 无需翻译的（纯数字/标点/符号/已是目标语言）→ 整段不画，保持屏幕原样。
            //
            // 注意这里【不能】把 src 当译文回填：那会在原文位置上重画一遍，
            // 而且图标被 OCR 误读成的符号（▣ ▶ ★ 等）会被原样盖上去，
            // 用户明确要求"没法翻译的就别动它"。传 null = 整段跳过绘制。
            if (!NeedsTranslation(src))
            {
                // TranslatedText 用原文占位：段数统计里不算"已翻译"（见下方扣减 filtered），
                // 但保留原文便于调试观察"这段被判定为无需翻译"。
                seg.TranslatedText = src;
                ApplyTranslationToBlocks(seg, null, IsTargetChinese);  // 绘制层拿不到合法译文 → 不画
                _skippedByFilter++;
                continue;
            }

            if (_config.EnableCache && _cache.TryGet(src, out string cached))
            {
                seg.TranslatedText = cached;
                ApplyTranslationToBlocks(seg, cached, IsTargetChinese);
                continue;
            }

            pending.Add(seg);
            pendingTexts.Add(src);
        }

        if (pending.Count == 0) return;

        var results = await _translator.TranslateAsync(_config, pendingTexts, null,
            Math.Max(5000, _config.CaptureIntervalMs * 8));

        int fallbackCount = 0;   // 模型没翻译、原样返回的条数
        for (int i = 0; i < pending.Count && i < results.Count; i++)
        {
            string src = pending[i].SourceText;
            string translated = results[i] ?? "";

            // 模型原样返回（多半是失败）时不写缓存，避免污染；
            // 同时计数，让状态栏能区分"真翻译"与"失败回退"。
            bool changed = !string.Equals(translated, src, StringComparison.Ordinal);
            if (!changed) fallbackCount++;
            if (_config.EnableCache && changed)
                _cache.Set(src, translated);

            // 关键：翻译失败时传 null 而不是原文。
            // 传原文会让 OverlayWindow 在原文位置把英文重画一遍（字体/间距略异 → 重影），
            // 用户要的语义是"没翻译成功就别动它"。传 null 则整段不画，原文保持可见。
            pending[i].TranslatedText = changed ? translated : null;
            ApplyTranslationToBlocks(pending[i], changed ? translated : null, IsTargetChinese);
        }

        _lastFallbackCount = fallbackCount;
    }

    private int _lastFallbackCount;

    /// <summary>上一次翻译中"模型没翻译、原样返回"的条数（用于状态栏诊断）</summary>
    public int LastFallbackCount => _lastFallbackCount;

    /// <summary>本帧因"无需翻译"而直显的段数（纯数字/标点/符号、已是目标语言）</summary>
    private int _skippedByFilter;

    /// <summary>上一次翻译中因无需翻译而直显的段数</summary>
    public int LastFilteredCount => _skippedByFilter;

    /// <summary>
    /// 把整段译文切分回填到各块。
    /// 权重取"该块在段落中的片段长度"（含拼接空格），保证切分总和恰好覆盖整段译文；
    /// 中文字符按 2 个单位计权，避免中文译文被过度切给前面的块。
    ///
    /// <paramref name="translated"/> 为 null 或空串时表示【本段没有可用译文】，
    /// 此时不设置任何块的内容，也不置 <see cref="Models.OcrBlock.HasValidTranslation"/>，
    /// 让绘制层整段跳过（原文保持可见）。
    /// </summary>
    private static void ApplyTranslationToBlocks(TranslationSegment seg, string? translated,
                                                 bool targetIsChinese = true)
    {
        if (seg.Blocks.Count == 0) return;

        // 无译文 → 整段不画。绝不塞占位符：在图标/文字上盖"…"是破坏性显示。
        if (string.IsNullOrWhiteSpace(translated))
        {
            foreach (var b in seg.Blocks)
            {
                b.Translated = "";
                b.HasValidTranslation = false;
            }
            return;
        }

        if (seg.Blocks.Count == 1)
        {
            seg.Blocks[0].Translated = translated;
            seg.Blocks[0].HasValidTranslation = true;
            return;
        }

        var fragments = seg.Blocks
            .Select(b => string.IsNullOrEmpty(b.SourceTextInSegment) ? b.Text : b.SourceTextInSegment)
            .ToList();

        int cursor = 0;
        for (int i = 0; i < seg.Blocks.Count; i++)
        {
            var block = seg.Blocks[i];

            // 不可译的块（图标误读/纯符号）不参与译文切分，
            // 否则会把译文的字符切到图标位置上，凭空多出一个字。
            if (!NeedsTranslationCore(block.Text, targetIsChinese))
            {
                block.Translated = "";
                block.HasValidTranslation = false;
                continue;
            }

            if (i == seg.Blocks.Count - 1)
            {
                block.Translated = translated[cursor..].Trim();
                block.HasValidTranslation = true;
                break;
            }

            // 按"当前块权重 / 剩余块总权重"切分剩余译文。
            // 用剩余权重而非全段总权重，可避免累积取整误差把后面的块挤成空串。
            double remainingWeight = 0;
            for (int k = i; k < fragments.Count; k++)
                remainingWeight += TextNormalizer.VisualWeight(fragments[k]);
            if (remainingWeight <= 0) remainingWeight = fragments.Count - i;

            double ratio = TextNormalizer.VisualWeight(fragments[i]) / remainingWeight;
            int take = cursor + (int)Math.Round((translated.Length - cursor) * ratio);
            take = FindBreakPoint(translated, cursor, take);

            // 保证本块至少 1 个字符，并为后续每个块预留至少 1 个字符
            int remainingBlocks = seg.Blocks.Count - i - 1;
            take = Math.Max(take, cursor + 1);
            take = Math.Min(take, translated.Length - remainingBlocks);
            if (take <= cursor) take = Math.Min(translated.Length, cursor + 1);

            block.Translated = translated.Substring(cursor, Math.Max(0, take - cursor)).Trim();
            block.HasValidTranslation = !string.IsNullOrWhiteSpace(block.Translated);
            cursor = take;
        }

        // 不再做"空块补占位符"的兜底。
        // 旧实现会给切空的块塞 '…' 或借邻块一个字，目的是"保证原文被覆盖"。
        // 但这在图标/短标签上属于破坏性显示：用户明确要求"没法翻译的就别动它"。
        // 现在空译文 = 该块不画（见 OverlayWindow 的 HasValidTranslation 判断），
        // 原文保持可见，比盖一个莫名其妙的省略号更好。
    }

    /// <summary>
    /// 在目标位置附近寻找合适的断句点（标点或空格）。
    /// 只向后找、且限定在窗口内：向后扫可以保证每个块都非空，
    /// 搜不到就用目标位置原样切分（对中文而言在任意字间断开都是可接受的）。
    /// </summary>
    private static int FindBreakPoint(string text, int start, int desired)
    {
        int end = Math.Clamp(desired, start, text.Length);
        if (end >= text.Length) return text.Length;

        const int window = 8;
        int maxBound = Math.Min(text.Length - 1, end + window);

        for (int i = end; i <= maxBound; i++)
        {
            if (i > start && IsBreakChar(text[i])) return i + 1;
        }

        return end;
    }

    private static bool IsBreakChar(char c)
        => c is ' ' or ',' or '.' or ';' or ':' or '!' or '?' or '、' or '，' or '。' or '；' or '：'
             or '！' or '？' or '/' or '|';

    /// <summary>
    /// 判断某段文本是否需要翻译（实例版：读取当前配置的目标语言）。
    /// 逻辑见 <see cref="NeedsTranslationCore"/>。
    /// </summary>
    private bool NeedsTranslation(string text)
        => NeedsTranslationCore(text, IsTargetChinese);

    /// <summary>
    /// 判断某段文本是否需要翻译（核心逻辑，静态便于在无实例上下文调用，
    /// 如切分译文时按块判断）。
    ///
    /// 分四种情况：
    ///
    /// 1. 没有任何字母也没有汉字 → 无需翻译。
    ///    纯数字（12345）、纯符号（---、...、《》、→）、日期（2024-10-13）、
    ///    时间（13:45:00）、百分比（1.5%）。这类内容送去翻译毫无意义：
    ///    模型要么原样返回（白跑一次网络请求），要么自作主张改动。
    ///    也包括 OCR 把图标误读成的符号（如 ▣ ▶ ★）。
    ///
    /// 2. 只含"数字 + 短字母单位" → 无需翻译。
    ///    如 100ms、5MB、60fps、3km。这里的字母是计量单位缩写，
    ///    不构成可译的自然语言。判据：字母总数 ≤ 4 且汉字为 0。
    ///
    /// 3. 已是目标语言 → 无需翻译。
    ///    目标是中文时，纯中文"确定"、"设置中可调整识别框大小"不必翻译。
    ///
    /// 4. 其余情况 → 需要翻译。含拉丁字母且汉字不占多数，如 "Click to start"。
    ///
    /// 注意"字母"判定用 char.IsLetter（覆盖拉丁/西里尔/假名），
    /// 汉字需用 IsCjk 单独排除，因为汉字也满足 IsLetter。
    /// </summary>
    private static bool NeedsTranslationCore(string text, bool targetIsChinese)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        int letters = 0;   // 非汉字的字母（拉丁/西里尔/假名等）
        int cjk = 0;       // 汉字
        int digits = 0;    // 数字

        foreach (char c in text)
        {
            if (IsCjk(c)) cjk++;
            else if (char.IsLetter(c)) letters++;
            else if (char.IsDigit(c)) digits++;
        }

        // 情况 1：既无字母也无汉字（纯数字/标点/符号/图标误读）→ 不翻译
        if (letters == 0 && cjk == 0) return false;

        // 情况 2：数字 + 短单位（100ms、5MB）→ 不翻译
        if (digits > 0 && cjk == 0 && letters > 0 && letters <= 4) return false;

        // 情况 3：目标是中文且汉字已占多数 → 已是目标语言，不翻译
        if (targetIsChinese && cjk > 0 && cjk >= letters) return false;

        return true;
    }

    /// <summary>是否属于汉字（CJK 统一表意文字，含扩展 A 区）</summary>
    private static bool IsCjk(char c)
        => (c >= '\u4e00' && c <= '\u9fff')     // 基本区
        || (c >= '\u3400' && c <= '\u4dbf')     // 扩展 A
        || (c >= '\uf900' && c <= '\ufaff');    // 兼容表意文字

    /// <summary>目标语言是否为中文</summary>
    private bool IsTargetChinese
        => _config.TargetLang.Contains("中文", StringComparison.Ordinal)
        || _config.TargetLang.Contains("Chinese", StringComparison.OrdinalIgnoreCase);

    /// <summary>重绘上一帧内容（内容未变时复用，只更新位置）</summary>
    private void RedrawLastFrame()
    {
        if (_lastSegments == null) return;
        Redraw(_lastSegments);
    }

    /// <summary>
    /// 发布一帧内容快照。渲染循环（30Hz）会检测到引用变化并建树一次；
    /// 此后窗口移动只靠平移，不再重建可视树。
    ///
    /// 若渲染循环未在跑（例如还没启动），退化为立刻建树一次，行为与旧版一致。
    /// </summary>
    private void Redraw(List<TranslationSegment> segments)
    {
        if (_overlay == null) return;

        _overlay.PublishSnapshot(new OverlayWindow.RenderSnapshot
        {
            Signature = _lastSignature,
            OcrBoxesOnly = false,
            Segments = segments,
            Config = _config
        });

        if (!_overlay.IsRenderLoopRunning)
        {
            var (ox, oy, scale) = GetCaptureOrigin();
            _overlay.Draw(segments, ox, oy, scale, _config);
        }
    }

    /// <summary>
    /// 取"捕获区域左上角在屏幕上的物理坐标"与缩放比。
    ///
    /// 偏移原点必须与【实际捕获区域】一致：
    ///   WGC → 整个窗口（含 DWM 边框），原点取 DWM 扩展框架边界
    ///   GDI → 客户区（PW_CLIENTONLY），原点取 ClientToScreen(0,0)
    /// 两者混用会让译文整体偏移一个边框宽度。
    /// </summary>
    private (double X, double Y, double Scale) GetCaptureOrigin()
    {
        var origin = _useGdi
            ? (_gdiCapture?.GetCaptureOriginPx() ?? new System.Windows.Point(0, 0))
            : (_capture?.GetCaptureOriginPx() ?? new System.Windows.Point(0, 0));
        return (origin.X, origin.Y, 1.0);
    }

    private void SaveDebugFrameOnce(SkiaSharp.SKBitmap frame)
    {
        if (_debugFrameSaved) return;
        try
        {
            string debugPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "debug_frame.png");
            using var image = SkiaSharp.SKImage.FromBitmap(frame);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            using var fs = System.IO.File.OpenWrite(debugPath);
            data.SaveTo(fs);
            _debugFrameSaved = true;
        }
        catch { /* 调试截图失败无所谓 */ }
    }

    private void SetStatus(string detail)
    {
        string mode = _useGdi
            ? (_lastWgcError.Length > 0 ? "GDI（WGC 失败）" : "GDI")
            : "WGC";

        // GDI 降级时把"这一帧到底走的哪条路"上屏：
        // BitBlt 计数持续增长 = PrintWindow 在本窗口失效，存在译文回灌风险。
        string captureTag = "";
        if (_useGdi && _gdiCapture != null)
            captureTag = $"\n抓帧：PrintWindow {_gdiCapture.PrintWindowOkCount} / BitBlt {_gdiCapture.BitBltFallbackCount}"
                       + (_overlayCaptureSafe ? "（防截屏已生效）" : "（⚠ 防截屏未生效，已启用临时隐藏）");
        else if (!_useGdi && _capture != null)
        {
            // WGC 是【异步回调】模型：不像 GDI 那样"调用即同步抓"，
            // 所以必须常驻计数，才能一眼看出它到底有没有在收帧。
            //   到达 0          → 会话压根没投递帧
            //   到达 >0、转图 0 → Surface→SKBitmap 全线失败，看 ⚠ 提示
            //   两者都在涨      → 正常
            captureTag = $"\n帧：到达 {_capture.FrameArrivedCount} / 转图 {_capture.FrameConvertedCount}"
                       + $"\n设备：{_capture.DeviceKind} / {_capture.AdapterName}"
                       + (_capture.LastFrameError.Length > 0 ? $"｜⚠ {_capture.LastFrameError}" : "");
        }

        // WGC 失败原因必须常驻每一条状态 —— 之前只在启动时上屏一次，
        // 第一帧 ProcessFrame 的 SetStatus 就把它顶掉了，用户永远看不到失败在哪一步。
        string wgcTag = (_useGdi && _lastWgcError.Length > 0)
            ? $"\nWGC 失败原因：{_lastWgcError}"
            : "";

        StatusText.Text = $"状态：正在翻译（{mode}模式）\n窗口：{_currentWindowTitle}\n{detail}"
                       + captureTag + wgcTag;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // 记住旧样式值，判断是否需要立刻重绘
        double oldFontScale = _config.FontScale;
        double oldBgScale = _config.BgScale;
        double oldBgPad = _config.BgPadding;
        double oldBgRadius = _config.BgCornerRadius;
        double oldBgOpacity = _config.BgOpacity;
        double oldOffsetX = _config.OffsetX;
        double oldOffsetY = _config.OffsetY;
        double oldOverlayOpacity = _config.OverlayOpacity;
        bool oldHideOriginal = _config.HideOriginalText;
        double oldBoxScale = _config.OcrBoxScale;
        int oldOcrThreads = _config.OcrThreads;
        double oldOcrScale = _config.OcrScale;

        var dlg = new SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _config = dlg.Config;
            ConfigService.Save(_config);
            _cache.Clear();
            SetupHotkey();

            if (_running && _timer != null)
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, _config.CaptureIntervalMs));

            // 设置里可能改了「覆盖层排除出屏幕捕获」→ 重新评估并（重新）接线兜底
            EnsureOverlayCaptureExclusion();

            // ---- 识别框大小变化 ----
            // 缩放只在绘制阶段生效（识别已用原框完成），因此无需重建识别器，
            // 只要清掉上一帧的段落缓存让下一帧重算即可。
            bool boxScaleChanged = Math.Abs(_config.OcrBoxScale - oldBoxScale) > 1e-6;
            if (boxScaleChanged)
                _lastSegments = null;

            // ---- 推理线程数变化 → 重建识别器 ----
            // 线程数只在 SessionOptions 里生效（创建会话时固化），
            // 改值必须重建 OcrService。模型常驻磁盘缓存，重建约几百毫秒。
            bool threadsChanged = _config.OcrThreads != oldOcrThreads;
            if (threadsChanged)
            {
                OcrRuntime.SetUserThreads(_config.OcrThreads);
                try { _ocr?.Dispose(); } catch { }
                _ocr = null;
                _ocr = new OcrService();
                _lastSegments = null;
                _lastSignature = "";
            }

            // 改了任何【显示样式】→ 立即用上一帧结果重绘，让用户即时看到效果，
            // 不必等下一帧或重新开启翻译。这是"调滑块却没反应"的常见困惑点。
            bool styleChanged =
                Math.Abs(_config.FontScale - oldFontScale) > 1e-6 ||
                Math.Abs(_config.BgScale - oldBgScale) > 1e-6 ||
                Math.Abs(_config.BgPadding - oldBgPad) > 1e-6 ||
                Math.Abs(_config.BgCornerRadius - oldBgRadius) > 1e-6 ||
                Math.Abs(_config.BgOpacity - oldBgOpacity) > 1e-6 ||
                Math.Abs(_config.OffsetX - oldOffsetX) > 1e-6 ||
                Math.Abs(_config.OffsetY - oldOffsetY) > 1e-6 ||
                Math.Abs(_config.OverlayOpacity - oldOverlayOpacity) > 1e-6 ||
                _config.HideOriginalText != oldHideOriginal;

            if (_running)
            {
                // 清空签名，防止下一帧因"内容未变"而跳过重绘
                _lastSignature = "";

                if (_lastSegments != null) Redraw(_lastSegments);
                if (threadsChanged)
                    SetStatus($"推理线程数已改为 {(_config.OcrThreads == 0 ? "自动" : _config.OcrThreads.ToString())}"
                              + $"（实际 {_ocr?.UsedThreads ?? 0}）｜ {_ocr?.UsedProvider}");
                else if (boxScaleChanged)
                    SetStatus($"识别框大小已改为 {_config.OcrBoxScale * 100:F0}%");
                else if (styleChanged)
                    SetStatus("样式已更新（字号/背景），可继续微调");
            }
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        _cache.Clear();
        StatusText.Text = $"状态：缓存已清空（{( _running ? "翻译仍在进行" : "已停止")}）";
    }

    /// <summary>
    /// 把捕获后端从 WGC 降级到 GDI（备用模式）。已降级则直接返回（可安全重复调用）。
    /// reason 写入 _lastWgcError（状态栏会显示「GDI（WGC 失败）」）并落诊断日志。
    /// </summary>
    private void FallbackToGdi(string reason)
    {
        if (_useGdi) return;
        _lastWgcError = reason;
        System.Diagnostics.Debug.WriteLine(reason);
        WgcCaptureService.AppendDiagLog($"[{DateTime.Now:HH:mm:ss}] {reason}");

        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _gdiCapture?.Dispose();
        _gdiCapture = new GdiCaptureService(_targetHwnd);
        _useGdi = true;
    }

    /// <summary>
    /// 「用 GDI 捕获（备用模式）」开关。
    /// 运行中勾选 → 立刻切 GDI；未运行时勾选 → 下次「开启翻译」生效。
    /// </summary>
    private void ForceGdi_Changed(object sender, RoutedEventArgs e)
    {
        _forceGdi = ForceGdiCheck.IsChecked == true;
        if (_forceGdi && _running && !_useGdi)
            FallbackToGdi("用户勾选「用 GDI 捕获（备用模式）」，切换到 GDI");
    }

    /// <summary>
    /// 「WGC 仅计数不取像素（诊断）」开关。
    /// 运行中勾选 → 立刻作用到采集层；未运行时勾选 → 下次「开启翻译」生效。
    /// 只对 WGC 有效（GDI 路过该开关）。
    /// </summary>
    private void SkipReadback_Changed(object sender, RoutedEventArgs e)
    {
        _skipReadback = SkipReadbackCheck.IsChecked == true;
        if (_capture != null) _capture.SkipPixelReadback = _skipReadback;
    }

    /// <summary>
    /// 「导出诊断」：把状态栏全文 + 帧错误历史 + **实时诊断日志全文** 写进
    /// 「导出诊断.txt」，然后自动打开给用户看。
    ///
    /// 状态栏是 TextBlock（WPF 默认不可选中），排查时只能靠截图，很麻烦。
    /// 且剪贴板会被别的进程占用导致「复制失败」，所以改用文件通道：
    /// 日志文件是进程独有，写入必成功，排查时可直接读全文。
    ///
    /// 【为什么不再写「诊断日志.txt」】该文件是运行期由
    /// <see cref="WgcCaptureService.AppendDiagLog"/> 持续追加的证据链。
    /// 导出若覆盖写同一个文件，就会在用户最需要证据的那一刻（出问题时点导出）
    /// 把整条链清空。现在导出改写独立的「导出诊断.txt」，并**把实时日志全文抄进去**，
    /// 既保住实时证据，又让导出文件自成一体。
    ///
    /// 【覆盖语义】每次导出都整文件重写「导出诊断.txt」，不追加 ——
    /// 下一次导出自动替换上一次的内容，不会越积越长。
    /// 「诊断日志.txt」只由捕获服务追加，导出不再触碰它。
    /// </summary>
    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ---- 导出诊断 ----");
        sb.AppendLine(StatusText.Text ?? "");

        var hist = WgcCaptureService.ErrorHistory;
        if (hist.Count > 0)
        {
            sb.AppendLine("---- 帧错误历史 ----");
            foreach (var h in hist) sb.AppendLine(h);
        }

        // 把实时日志全文抄进导出：导出不再覆盖实时日志，证据只能靠这一步带出来
        var live = WgcCaptureService.ReadDiagLog();
        if (!string.IsNullOrWhiteSpace(live))
        {
            sb.AppendLine();
            sb.AppendLine($"---- 实时诊断日志全文（{WgcCaptureService.DiagLogFileName}）----");
            sb.AppendLine(live.TrimEnd());
        }

        // 写独立文件（WriteAllText 自带截断）；实时日志不受影响
        try
        {
            System.IO.File.WriteAllText(WgcCaptureService.ExportDiagPath, sb.ToString(),
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"写入导出诊断失败：{ex.Message}", "导出诊断",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 自动打开给用户看（系统默认 .txt 程序，一般是记事本）
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = WgcCaptureService.ExportDiagPath,
                UseShellExecute = true
            });
        }
        catch { /* 打开失败不影响已有文件，忽略 */ }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // =====================================================================
    //  识别区域（ROI）：手动框选 → 存归一化比例 → 用时按当前帧尺寸反算像素
    // =====================================================================

    /// <summary>
    /// 「选择识别区域」：弹出全屏遮罩窗口让用户拖框。
    ///
    /// 任何时候都能点（不影响正在跑的翻译）。期间会暂停定时采样 ——
    /// 遮罩糊住屏幕的这段时间抓帧没有意义，只会白白多跑几次 OCR。
    /// </summary>
    private void PickRoi_Click(object sender, RoutedEventArgs e)
    {
        // 先定下「捕获区域」，否则框出来的屏幕坐标没法换算成窗口内比例
        var region = GetCaptureRegionRect();

        bool resume = _timer?.IsEnabled == true;
        _timer?.Stop();
        try
        {
            var picker = new RegionSelectWindow { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedScreenPx is not Rect sel)
            {
                SetStatus("已取消框选（沿用原有识别区域）");
                return;
            }

            if (region.Width <= 0 || region.Height <= 0)
            {
                MessageBox.Show(this, "无法确定识别范围，请先在上方选择一个目标窗口。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var norm = RoiGeometry.ToNormalized(sel, region);
            if (norm == null)
            {
                MessageBox.Show(this, "所选区域太小，或完全落在目标窗口之外，请重新框选。",
                                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (nx, ny, nw, nh) = norm.Value;
            _config.UseRoi = true;
            _config.RoiX = nx;
            _config.RoiY = ny;
            _config.RoiW = nw;
            _config.RoiH = nh;
            ConfigService.Save(_config);

            // 强制下一帧重算：清掉「画面未变化」「内容未变化」两个门控，
            // 否则新选区要等到画面真的变化才会生效，看起来像「点了没反应」。
            _lastSignature = "";
            _lastSegments = null;
            _prevFingerprint = null;

            SyncRoiToCapture();
            ApplyRoiToOverlay();
            UpdateRoiInfo();

            if (_running)
            {
                _ = ProcessFrame();
            }
            else
            {
                StatusText.Text = $"状态：已停止\n已设置识别区域：{nw * 100:F0}% × {nh * 100:F0}%"
                                + "（开启翻译后生效）";
            }
        }
        finally
        {
            if (resume && _running && _timer != null) _timer.Start();
        }
    }

    /// <summary>「清除区域」：恢复为识别整个目标窗口（默认行为）。</summary>
    private void ClearRoi_Click(object sender, RoutedEventArgs e)
    {
        _config.UseRoi = false;
        _config.RoiX = 0;
        _config.RoiY = 0;
        _config.RoiW = 1;
        _config.RoiH = 1;
        ConfigService.Save(_config);

        _lastSignature = "";
        _lastSegments = null;
        _prevFingerprint = null;

        SyncRoiToCapture();
        ApplyRoiToOverlay();
        UpdateRoiInfo();

        if (_running)
        {
            _ = ProcessFrame();
            SetStatus("已清除识别区域（恢复为识别整个窗口）");
        }
        else
        {
            StatusText.Text = "状态：已停止\n已清除识别区域（恢复为识别整个窗口）";
        }
    }

    /// <summary>刷新主界面的 ROI 说明文字与「清除」按钮可用性。</summary>
    private void UpdateRoiInfo()
    {
        if (RoiInfoText == null) return;

        if (_config?.UseRoi == true)
        {
            RoiInfoText.Text = $"已选区域：{_config.RoiW * 100:F0}% × {_config.RoiH * 100:F0}%"
                             + $"（起点 {_config.RoiX * 100:F0}%, {_config.RoiY * 100:F0}%）"
                             + "｜随窗口缩放自动跟随";
            RoiInfoText.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7A, 0x2B));
            if (ClearRoiBtn != null) ClearRoiBtn.IsEnabled = true;
        }
        else
        {
            RoiInfoText.Text = "未选择区域（识别整个目标窗口）";
            RoiInfoText.Foreground = Brushes.Gray;
            if (ClearRoiBtn != null) ClearRoiBtn.IsEnabled = false;
        }
    }

    /// <summary>
    /// 把当前 ROI 同步给覆盖层：既设常驻值（供后续重建），也立刻画一次。
    /// 立刻画是必要的 —— 内容快照没变时渲染循环不会重建，
    /// 只设值的话新绿框要等到下一次内容变化才出现。
    /// </summary>
    private void EnsureOverlayCaptureExclusion()
    {
        if (_overlay == null) return;

        _overlay.ExcludeFromCapture = _config.OverlayExcludeFromCapture;

        // 关掉排障开关时，TryExcludeFromCapture 内部会显式还原为 WDA_NONE 并返回 false
        _overlayCaptureSafe = _config.OverlayExcludeFromCapture
                           && _overlay.TryExcludeFromCapture();

        if (_gdiCapture == null) return;

        if (_overlayCaptureSafe)
        {
            // 防截屏已生效 → 不需要兜底，抓帧无闪烁、无卡顿
            _gdiCapture.BeforeScreenCapture = null;
            _gdiCapture.AfterScreenCapture = null;
        }
        else if (_config.CaptureIntervalMs >= 500)
        {
            _gdiCapture.BeforeScreenCapture = () => _overlay?.SetCaptureSuppressed(true);
            _gdiCapture.AfterScreenCapture = () => _overlay?.SetCaptureSuppressed(false);
        }
    }

    /// <summary>
    /// 把当前 ROI 同步给覆盖层：既设常驻值（供后续重建），也立刻画一次。
    /// 立刻画是必要的 —— 内容快照没变时渲染循环不会重建，
    /// 只设值的话新绿框要等到下一次内容变化才出现。
    /// </summary>
    private void ApplyRoiToOverlay()
    {
        if (_overlay == null) return;

        var (fw, fh) = GetFrameSizePx();
        var roi = RoiGeometry.ToFramePx(_config, fw, fh);
        _overlay.RoiFramePx = roi;

        if (roi is Rect r)
        {
            var (ox, oy, s) = GetCaptureOrigin();
            _overlay.DrawRoiBox(r, ox, oy, s);
        }
        else
        {
            _overlay.ClearRoiBox();
        }
    }

    /// <summary>
    /// 把 ROI 的归一化矩形交给采集层，让指纹只在 ROI 内取。
    /// 这样 ROI 之外的画面变化（比如窗口别处的高亮闪烁）不会白白触发 OCR。
    /// </summary>
    private void SyncRoiToCapture()
    {
        var roi = _config.UseRoi
            ? new Rect(Math.Clamp(_config.RoiX, 0, 1), Math.Clamp(_config.RoiY, 0, 1),
                       Math.Clamp(_config.RoiW, 0, 1), Math.Clamp(_config.RoiH, 0, 1))
            : (Rect?)null;

        if (_capture != null) _capture.RoiNormalized = roi;
        if (_gdiCapture != null) _gdiCapture.RoiNormalized = roi;
    }

    /// <summary>
    /// 当前捕获帧的物理尺寸。正在跑 → 用真实帧尺寸（最准）；
    /// 没在跑 → 按选中窗口的可见框架边界估算。
    /// </summary>
    private (int W, int H) GetFrameSizePx()
    {
        if (_running && _lastFrameW > 0 && _lastFrameH > 0)
            return (_lastFrameW, _lastFrameH);

        var r = ProbeWindowRect();
        return ((int)Math.Max(0, r.Width), (int)Math.Max(0, r.Height));
    }

    /// <summary>「捕获区域」在屏幕上的物理像素矩形（原点 + 尺寸）。</summary>
    private Rect GetCaptureRegionRect()
    {
        if (_running && _lastFrameW > 0 && _lastFrameH > 0)
        {
            var (ox, oy, _) = GetCaptureOrigin();
            return new Rect(ox, oy, _lastFrameW, _lastFrameH);
        }

        return ProbeWindowRect();
    }

    /// <summary>
    /// 估算目标窗口的可见矩形（未开启翻译时用）。
    /// 优先 DWM 扩展框架边界 —— 与 WGC 实际捕获范围一致；
    /// GDI 走客户区会略小一点，但此路径只在未运行时用于比例换算，误差可接受。
    /// </summary>
    private Rect ProbeWindowRect()
    {
        IntPtr hwnd = _targetHwnd;
        if (hwnd == IntPtr.Zero && WindowCombo?.SelectedItem is WindowInfo sel)
            hwnd = sel.Hwnd;
        if (hwnd == IntPtr.Zero) return Rect.Empty;

        try
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                           out var dwm, Marshal.SizeOf<RECT>());
            if (hr == 0 && dwm.Right > dwm.Left && dwm.Bottom > dwm.Top)
                return new Rect(dwm.Left, dwm.Top, dwm.Right - dwm.Left, dwm.Bottom - dwm.Top);
        }
        catch { /* 退回 GetWindowRect */ }

        if (GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
            return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

        return Rect.Empty;
    }

    #region Win32 坐标（保留供窗口枚举/尺寸查询使用）

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// 取窗口的「可见框架边界」。比 GetWindowRect 准：后者在 Win10/11 上
    /// 含约 7~8px 的不可见调整边框，会导致原点偏移一个边框宽度。
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    #endregion
}
