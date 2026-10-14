using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenTranslator.Models;
using ScreenTranslator.Services;

namespace ScreenTranslator.Windows;

/// <summary>
/// 设置窗口：配置大模型 API 参数、识别参数与显示样式
/// </summary>
public partial class SettingsWindow : Window
{
    public AppConfig Config { get; private set; }
    private bool _isCloud;

    /// <summary>
    /// 初始化守卫。
    ///
    /// XAML 里给 Slider 设了 Minimum/Value，解析到该标签时 Slider 会立即把
    /// 值向内收拢（coerce），从而触发 ValueChanged —— 而此时后面的
    /// TextBlock（FontScaleLabel 等）还没被创建，仍是 null。
    /// 若处理函数不设防，就会在 InitializeComponent() 里抛 NullReferenceException，
    /// 表现为"一打开设置就闪退"。
    /// 因此：构造期间一律不处理滑块回调，等构造完成再统一刷一次标签。
    /// </summary>
    private bool _initializing = true;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Config = config;

        UrlBox.Text = config.ApiBaseUrl;
        KeyBox.Text = config.ApiKey;
        ModelBox.Text = config.Model;
        LocalUrlBox.Text = config.LocalApiUrl;
        LocalModelBox.Text = config.LocalModel;
        ConcurrencyBox.Text = config.LocalConcurrency.ToString(CultureInfo.InvariantCulture);

        // 云端模式自动检测语言，本地模式可手动指定源语言
        _isCloud = config.TranslationMode != "Local";
        SourceLangBox.IsReadOnly = _isCloud;
        SourceLangBox.Text = _isCloud ? "自动检测" : config.SourceLang;
        SourceLangBox.Background = _isCloud
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0))
            : System.Windows.Media.Brushes.White;
        TargetLangBox.Text = config.TargetLang;

        IntervalBox.Text = config.CaptureIntervalMs.ToString(CultureInfo.InvariantCulture);
        ConfidenceBox.Text = config.MinConfidence.ToString(CultureInfo.InvariantCulture);
        OcrScaleBox.Text = config.OcrScale.ToString(CultureInfo.InvariantCulture);
        OcrThreadsBox.Text = config.OcrThreads.ToString(CultureInfo.InvariantCulture);

        OffsetXBox.Text = config.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYBox.Text = config.OffsetY.ToString(CultureInfo.InvariantCulture);
        OpacityBox.Text = config.OverlayOpacity.ToString(CultureInfo.InvariantCulture);
        HotkeyBox.Text = config.ToggleHotkey;

        // ---- 样式滑块（防御：非法值回退默认）----
        FontScaleSlider.Value = config.FontScale is >= 0.5 and <= 3.0 ? config.FontScale : 1.0;
        BgScaleSlider.Value = config.BgScale is >= 0.5 and <= 3.0 ? config.BgScale : 1.0;
        BgPadSlider.Value = config.BgPadding is >= 0 and <= 20 ? config.BgPadding : 3;
        BgRadiusSlider.Value = config.BgCornerRadius is >= 0 and <= 16 ? config.BgCornerRadius : 3;
        BgOpacitySlider.Value = config.BgOpacity is >= 0 and <= 1 ? config.BgOpacity : 0.67;

        // 识别框大小（0.5~1.5，1.0 为默认）
        BoxSizeSlider.Value = OcrBoxFitter.Clamp(config.OcrBoxScale);

        CacheCheck.IsChecked = config.EnableCache;
        HideOriginalCheck.IsChecked = config.HideOriginalText;
        DebugBoxCheck.IsChecked = config.DebugBoxes;
        OcrBoxesOnlyCheck.IsChecked = config.OcrBoxesOnly;
        OcrBoxesTextCheck.IsChecked = config.OcrBoxesShowText;
        OcrBoxesScoreCheck.IsChecked = config.OcrBoxesShowScore;

        ChangeDetectCheck.IsChecked = config.UseChangeDetection;
        ChangeMinCellsBox.Text = config.ChangeMinCells.ToString(CultureInfo.InvariantCulture);
        OverlayExcludeCaptureCheck.IsChecked = config.OverlayExcludeFromCapture;

        // ---- 诊断与日志 ----
        // 把本次运行的日志路径直接写出来，用户必要时能手抄/复制去定位；
        // ToolTip 里给出文件名，说明「打开日志文件夹」会定位到哪一个。
        if (LogPathText != null) LogPathText.Text = $"路径：{AppLog.LogDir}";
        if (OpenLogDirBtn != null) OpenLogDirBtn.ToolTip = $"本次运行日志：{AppLog.DiagFileName}";

        // 所有控件就绪，解除守卫，再统一刷一次标签/预览
        _initializing = false;
        UpdateStyleLabels();
        RefreshModelStatus();
    }

    /// <summary>刷新"识别模型"状态提示（中文模型缺失会让界面中文完全识别不到）</summary>
    private void RefreshModelStatus()
    {
        if (ModelStatusText == null || ModelStatusBorder == null) return;

        bool ready = OcrModelManager.IsChineseModelReady();
        if (ready)
        {
            ModelStatusText.Text = "识别模型：中文 PP-OCRv5（词典含 15565 个汉字，中英混排均可识别）✓";
            ModelStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9));
            ModelStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xA5, 0xD6, 0xA7));
            if (DownloadModelBtn != null) DownloadModelBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModelStatusText.Text = "识别模型：⚠ 当前只有拉丁文模型（词典不含任何汉字），"
                                 + "界面上的中文将完全识别不到。建议下载中文模型。";
            ModelStatusBorder.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1));
            ModelStatusBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            if (DownloadModelBtn != null) DownloadModelBtn.Visibility = Visibility.Visible;
        }
    }

    /// <summary>点击下载中文识别模型</summary>
    private void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadModelBtn != null)
        {
            DownloadModelBtn.IsEnabled = false;
            DownloadModelBtn.Content = "正在下载…";
        }

        string lastMsg = "";
        var (ok, msg) = OcrModelManager
            .EnsureChineseModelAsync(m => { lastMsg = m; Dispatcher.Invoke(() =>
                { if (ModelStatusText != null) ModelStatusText.Text = "识别模型：" + m; }); })
            .GetAwaiter().GetResult();

        if (DownloadModelBtn != null)
        {
            DownloadModelBtn.IsEnabled = true;
            DownloadModelBtn.Content = "下载中文识别模型（约 16MB）";
        }

        if (!ok)
        {
            MessageBox.Show($"{msg}\n{lastMsg}", "下载失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshModelStatus();
    }

    /// <summary>刷新滑块的数值标签，并同步更新实时预览</summary>
    private void UpdateStyleLabels()
    {
        if (_initializing) return;

        // 逐项空判断：即使某个控件尚未创建也不至于把整个窗口带崩
        if (FontScaleLabel != null && FontScaleSlider != null)
            FontScaleLabel.Text = $"{FontScaleSlider.Value:F2}x";
        if (BgScaleLabel != null && BgScaleSlider != null)
            BgScaleLabel.Text = $"{BgScaleSlider.Value:F2}x";
        if (BgPadLabel != null && BgPadSlider != null)
            BgPadLabel.Text = $"{BgPadSlider.Value:F0} px";
        if (BgRadiusLabel != null && BgRadiusSlider != null)
            BgRadiusLabel.Text = $"{BgRadiusSlider.Value:F0} px";
        if (BgOpacityLabel != null && BgOpacitySlider != null)
            BgOpacityLabel.Text = $"{BgOpacitySlider.Value:F2}";
        if (BoxSizeLabel != null && BoxSizeSlider != null)
            BoxSizeLabel.Text = $"{BoxSizeSlider.Value * 100:F0}%";

        UpdatePreview();
    }

    /// <summary>预览框准备就绪后画一次（此时 ActualWidth 才有效）</summary>
    private void PreviewHost_Loaded(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>
    /// 实时预览：用与 OverlayWindow 相同的公式画一段示例译文，
    /// 让用户在拖动滑块时立刻看到字号/背景的变化，不用保存后再去瞄一眼。
    ///
    /// 注意：不要监听 PreviewCanvas 的 SizeChanged 来触发重绘 ——
    /// 本方法会增删 Canvas 子元素，从而改变其期望尺寸并再次触发 SizeChanged，
    /// 形成"改内容 → 尺寸变 → 再改内容"的无限递归，最终 StackOverflow 闪退。
    /// 这里只在 Loaded 和滑块变化时主动调用一次。
    /// </summary>
    private void UpdatePreview()
    {
        if (PreviewCanvas == null || _previewBusy) return;

        _previewBusy = true;
        try
        {
            PreviewCanvas.Children.Clear();

            double availW = PreviewHost?.ActualWidth > 0 ? PreviewHost.ActualWidth : 380;
            double availH = PreviewHost?.ActualHeight > 0 ? PreviewHost.ActualHeight : 76;

            const double sampleLineHeightDip = 16.0;   // 模拟"原文单行高 16 DIP"
            double fontScale = FontScaleSlider.Value;
            double bgScale = BgScaleSlider.Value;
            double bgPad = BgPadSlider.Value;
            double bgRadius = BgRadiusSlider.Value;
            double bgAlpha = BgOpacitySlider.Value;

            // 与 OverlayWindow.Draw 一致：字号 = 行高 × 0.72 × 用户倍率
            double fontSize = Math.Clamp(sampleLineHeightDip * 0.72, 6.5, 42) * fontScale;

            var tb = new TextBlock
            {
                Text = "译文示例 Sample",
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                LineHeight = fontSize * 1.25,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.NoWrap
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size m = tb.DesiredSize;
            if (m.Width <= 0 || m.Height <= 0) return;

            // 背景尺寸按倍率计算，但【限制在预览框内】，
            // 否则大倍率下会把预览框撑爆、也失去"预览"的意义。
            double bgW = Math.Min(m.Width * bgScale + bgPad * 2, availW - 8);
            double bgH = Math.Min(m.Height * bgScale + bgPad * 2, availH - 8);
            if (bgW <= 0 || bgH <= 0) return;

            // 居中摆放
            double cx = Math.Max(4, (availW - bgW) / 2);
            double cy = Math.Max(4, (availH - bgH) / 2);

            if (bgAlpha > 0.01)
            {
                var bg = new Border
                {
                    Width = bgW,
                    Height = bgH,
                    Background = new SolidColorBrush(Color.FromArgb((byte)(255 * bgAlpha), 255, 255, 255)),
                    CornerRadius = new CornerRadius(bgRadius),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(bg, cx);
                Canvas.SetTop(bg, cy);
                PreviewCanvas.Children.Add(bg);
            }

            // 文字在背景里居中（用裁剪避免文字溢出预览框）
            double tx = cx + (bgW - m.Width) / 2;
            double ty = cy + (bgH - m.Height) / 2;
            Canvas.SetLeft(tb, tx);
            Canvas.SetTop(tb, ty);
            PreviewCanvas.Children.Add(tb);
        }
        catch
        {
            // 预览失败绝不能影响设置窗口本身
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private bool _previewBusy;

    // 五个滑块的回调。构造期间（_initializing）直接忽略，
    // 避免在 XAML 解析阶段访问尚未创建的标签控件。
    private void FontScaleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    private void BgScaleSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    private void BgPadSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    private void BgRadiusSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    private void BgOpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    private void BoxSizeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        UpdateStyleLabels();
    }

    /// <summary>
    /// 「打开日志文件夹」：用资源管理器打开 logs/，并定位到**本次运行**的日志文件。
    ///
    /// 【为什么开文件夹而不是直接开文件】同一目录下还躺着崩溃记录（error.log ——
    /// 要看崩溃往往是想查上一次运行，而诊断日志每次启动会被本次取代，只有它留得下历史）。
    /// 只开单个文件会把它们挡住；用 /select 定位到本次运行的文件，等于两者兼得。
    ///
    /// 【为什么不在这里读出内容显示】日志可能上兆字节，塞进设置窗口既慢又难读，
    /// 交给系统默认程序打开更合适。
    /// </summary>
    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = AppLog.LogDir;
            // 正常情况下 App 启动已建好目录；此处兜底，避免首次运行直接点按钮时开不出窗口
            System.IO.Directory.CreateDirectory(dir);
            string file = AppLog.DiagLogPath;

            string args = System.IO.File.Exists(file)
                ? $"/select,\"{file}\""     // 本次日志已存在 → 打开目录并选中它
                : $"\"{dir}\"";             // 本次还没写过日志 → 只打开目录

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = args,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志文件夹失败：{ex.Message}", "打开日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>把样式项恢复为默认值</summary>
    private void ResetStyle_Click(object sender, RoutedEventArgs e)
    {
        FontScaleSlider.Value = 1.0;
        BgScaleSlider.Value = 1.0;
        BgPadSlider.Value = 3;
        BgRadiusSlider.Value = 3;
        BgOpacitySlider.Value = 0.67;
        BoxSizeSlider.Value = OcrBoxFitter.DefaultScale;
        OffsetXBox.Text = "0";
        OffsetYBox.Text = "0";
        OpacityBox.Text = "1";
        UpdateStyleLabels();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Config.ApiBaseUrl = UrlBox.Text.Trim();
        Config.ApiKey = KeyBox.Text.Trim();
        Config.Model = ModelBox.Text.Trim();
        Config.LocalApiUrl = string.IsNullOrWhiteSpace(LocalUrlBox.Text)
            ? "http://localhost:11434/v1" : LocalUrlBox.Text.Trim();
        Config.LocalModel = string.IsNullOrWhiteSpace(LocalModelBox.Text)
            ? "qwen2.5:3b" : LocalModelBox.Text.Trim();

        Config.LocalConcurrency = ParseInt(ConcurrencyBox.Text, 1, 8, 3);

        Config.SourceLang = _isCloud
            ? "自动检测"
            : (string.IsNullOrWhiteSpace(SourceLangBox.Text) ? "英文" : SourceLangBox.Text.Trim());
        Config.TargetLang = string.IsNullOrWhiteSpace(TargetLangBox.Text) ? "中文" : TargetLangBox.Text.Trim();

        Config.CaptureIntervalMs = ParseInt(IntervalBox.Text, 300, 60000, 2000);
        Config.MinConfidence = ParseFloat(ConfidenceBox.Text, 0f, 1f, 0.3f);
        Config.OcrScale = ParseDouble(OcrScaleBox.Text, 1.0, 3.0, 1.6);
        // 0 = 自动；1~64 手动
        Config.OcrThreads = ParseInt(OcrThreadsBox.Text, 0, 64, 0);

        Config.OffsetX = ParseDouble(OffsetXBox.Text, -400, 400, 0);
        Config.OffsetY = ParseDouble(OffsetYBox.Text, -400, 400, 0);
        Config.OverlayOpacity = ParseDouble(OpacityBox.Text, 0.2, 1.0, 1.0);
        Config.ToggleHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Alt+T" : HotkeyBox.Text.Trim();

        // ---- 样式滑块 ----
        Config.FontScale = Math.Clamp(FontScaleSlider.Value, 0.5, 3.0);
        Config.BgScale = Math.Clamp(BgScaleSlider.Value, 0.5, 3.0);
        Config.BgPadding = Math.Clamp(BgPadSlider.Value, 0, 30);
        Config.BgCornerRadius = Math.Clamp(BgRadiusSlider.Value, 0, 20);
        Config.BgOpacity = Math.Clamp(BgOpacitySlider.Value, 0, 1);

        // 识别框大小（显示层缩放，0.5~1.5）
        Config.OcrBoxScale = OcrBoxFitter.Clamp(BoxSizeSlider.Value);

        Config.EnableCache = CacheCheck.IsChecked == true;
        Config.HideOriginalText = HideOriginalCheck.IsChecked == true;
        Config.DebugBoxes = DebugBoxCheck.IsChecked == true;
        Config.OcrBoxesOnly = OcrBoxesOnlyCheck.IsChecked == true;
        Config.OcrBoxesShowText = OcrBoxesTextCheck.IsChecked == true;
        Config.OcrBoxesShowScore = OcrBoxesScoreCheck.IsChecked == true;

        Config.UseChangeDetection = ChangeDetectCheck.IsChecked == true;
        Config.ChangeMinCells = ParseInt(ChangeMinCellsBox.Text, 1, 1000, 3);
        Config.OverlayExcludeFromCapture = OverlayExcludeCaptureCheck.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private static int ParseInt(string text, int min, int max, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? Math.Clamp(v, min, max) : fallback;

    private static float ParseFloat(string text, float min, float max, float fallback)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? Math.Clamp(v, min, max) : fallback;

    private static double ParseDouble(string text, double min, double max, double fallback)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? Math.Clamp(v, min, max) : fallback;
}
