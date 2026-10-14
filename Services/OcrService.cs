using System.IO;
using System.Linq;
using RapidOcrNet;
using SkiaSharp;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>
/// RapidOCR 文字识别服务封装（PP-OCRv5 中英文模型）。
///
/// 关键点 1 —— 必须用【中文】识别模型：
/// NuGet 包自带的默认模型是 PP-OCRv5 **latin**，其词典只有 502 字符、
/// 零个汉字，结构上无法输出中文。中文界面（菜单/按钮/提示/中文网页）
/// 会整片识别不到，表现为"很多文字未识别到"。
/// 这里优先加载 <see cref="OcrModelManager"/> 提供的中文模型
/// （词典 18383 字符 / 15565 汉字，同时保留拉丁字母能力）。
///
/// 关键点 2 —— 放大：
/// 屏幕截图里小字号文字非常多，先做一次 1.5~2 倍上采样再送入识别
/// 能明显提升小字召回率。坐标在返回时按放大倍数还原。
/// </summary>
public class OcrService : IDisposable
{
    /// <summary>
    /// DBNet 检测框的膨胀比例。
    ///
    /// 固定为 1.1（库默认 1.6 会让框明显偏大）。这个值直接影响识别率 ——
    /// 实测再往下调会裁掉字形上下缘，"点击"会被识别成"击"，因此不作为
    /// 用户可调项。「识别框大小」由显示层的 OcrBoxFitter 提供，那个是安全的。
    /// </summary>
    private const float SafeUnClipRatio = 1.1f;

    private readonly RapidOcr _ocr;
    private readonly RapidOcrOptions _options;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>实际加载的识别模型路径（便于界面显示"当前用的是哪个模型"）</summary>
    public string LoadedRecModel { get; private set; } = "(未知)";

    /// <summary>是否为中文模型（词典含汉字）。false 表示只能用 latin 模型，中文识别不到</summary>
    public bool IsChineseModel { get; private set; }

    /// <summary>实际使用的推理线程数（用于界面显示与性能确认）</summary>
    public int UsedThreads { get; private set; }

    /// <summary>实际使用的执行提供程序（"CPU" / "DirectML" 等）</summary>
    public string UsedProvider { get; private set; } = "CPU";

    public OcrService()
    {
        _ocr = new RapidOcr();
        InitWithBestModel();

        // 参数调整：
        // LimitSideLen 提到 1280，保留更多小字细节；
        // 关闭角度分类（屏幕文字基本都是水平的，省一次推理）。
        //
        // TextScore 显式设低：默认 0.5 会把"置信度略低但确实有字"的小字丢掉。
        // 屏幕文字常有抗锯齿/半透明干扰，0.35 能在略增噪声与多召回之间平衡；
        // 真正的过滤交给上层 MinConfidence 与人工设置。
        //
        // UnClipRatio —— DBNet 检测框向外膨胀的比例。
        // 库默认 1.6，框会明显大于文字本身（实测字面高 48px 时框高 64px，
        // 大出 33%），密集多行时相邻框甚至互相压叠。
        // 降到 1.1 后框高 52px（仅大 8%），密集行不再重叠。
        //
        // 这里固定在安全下限 1.1，不再暴露给用户：它直接影响识别率
        // （再小会切掉字形上下缘导致识别崩），留给显示层的
        // OcrBoxFitter 去满足"框大小可调"的需求 —— 那个是安全的。
        _options = RapidOcrOptions.Default with
        {
            LimitSideLen = 1280,
            MaxSideLen = 2560,
            DoAngle = false,
            ReturnWordBox = false,
            // 关闭"宽高比过大的图补垂直空白"以外的自适应，避免极端尺寸被压扁
            WidthHeightRatio = -1,
            UnClipRatio = SafeUnClipRatio
        };
    }

    /// <summary>
    /// 优先加载中文模型；中文模型缺失时回退到包自带模型，
    /// 保证"至少还能识别英文"，而不是整个 OCR 不可用。
    /// </summary>
    private void InitWithBestModel()
    {
        var detPath = Path.Combine(OcrModelManager.ModelDir, "ch_PP-OCRv5_mobile_det.onnx");
        var clsPath = Path.Combine(OcrModelManager.ModelDir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        bool detOk = File.Exists(detPath);
        bool clsOk = File.Exists(clsPath);

        // 构造推理会话选项（线程数 / GPU）—— 这是性能的关键，见 OcrRuntime 注释
        var sessionOptions = OcrRuntime.BuildSessionOptions();
        UsedThreads = OcrRuntime.EffectiveThreads;
        UsedProvider = OcrRuntime.ProviderName;

        bool chineseReady = OcrModelManager.IsChineseModelReady() && detOk && clsOk;

        // ---- 首选：中文识别模型 + 当前会话选项（可能是 GPU）----
        if (chineseReady)
        {
            try
            {
                _ocr.InitModels(
                    detPath, clsPath,
                    OcrModelManager.ChineseRecPath,
                    OcrModelManager.ChineseDictPath,
                    sessionOptions);
                LoadedRecModel = OcrModelManager.ChineseRecFileName;
                IsChineseModel = true;
                return;
            }
            catch
            {
                // 可能是 GPU 会话创建失败（native dll 不是 DML 版 / 显卡不支持）。
                // 不能因此丢掉中文模型 —— 换纯 CPU 选项重试一次。
                if (UsedProvider != "CPU")
                {
                    OcrRuntime.ForceCpu();
                    sessionOptions = OcrRuntime.BuildSessionOptions();
                    UsedThreads = OcrRuntime.EffectiveThreads;
                    UsedProvider = OcrRuntime.ProviderName;

                    try
                    {
                        _ocr.InitModels(
                            detPath, clsPath,
                            OcrModelManager.ChineseRecPath,
                            OcrModelManager.ChineseDictPath,
                            sessionOptions);
                        LoadedRecModel = OcrModelManager.ChineseRecFileName;
                        IsChineseModel = true;
                        return;
                    }
                    catch
                    {
                        // 仍然失败 → 继续往下走 latin 回退
                    }
                }
            }
        }

        // ---- 回退：包自带（PP-OCRv5 latin）----
        // 说明：库的 InitModels() 无参重载不接受 SessionOptions，
        // 因此回退路径拿不到线程数配置（会退回"用满所有核"的较慢配置）。
        // 这只在中文模型缺失时才会走到，属于降级场景。
        _ocr.InitModels();
        LoadedRecModel = "latin_PP-OCRv5_rec_mobile_infer.onnx（内置）";
        IsChineseModel = false;
    }

    public List<OcrBlock> Recognize(SKBitmap bitmap, float minScore)
        => Recognize(bitmap, minScore, 1.0, out _);

    public List<OcrBlock> Recognize(SKBitmap bitmap, float minScore, out int rawCount)
        => Recognize(bitmap, minScore, 1.0, out rawCount);

    /// <summary>识别图片中的文字（可指定放大倍数）</summary>
    public List<OcrBlock> Recognize(SKBitmap bitmap, float minScore, double ocrScale, out int rawCount)
    {
        rawCount = 0;
        if (bitmap == null || _disposed || bitmap.Width <= 0 || bitmap.Height <= 0)
            return new List<OcrBlock>();

        double totalScale = Math.Clamp(ocrScale <= 0 ? 1.0 : ocrScale, 1.0, 3.0);

        SKBitmap working = bitmap;
        bool owns = false;
        if (totalScale > 1.001)
        {
            int nw = Math.Max(1, (int)Math.Round(bitmap.Width * totalScale));
            int nh = Math.Max(1, (int)Math.Round(bitmap.Height * totalScale));
            working = ResizeBitmap(bitmap, nw, nh);
            owns = true;
        }

        try
        {
            OcrResult? result;
            lock (_lock)
            {
                result = _ocr.Detect(working, _options, CancellationToken.None);
            }

            if (result?.TextBlocks == null)
                return new List<OcrBlock>();

            rawCount = result.TextBlocks.Length;

            var blocks = new List<OcrBlock>(result.TextBlocks.Length);
            foreach (var t in result.TextBlocks)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Text)) continue;

                var block = ToBlock(t, totalScale);
                if (block == null) continue;
                if (block.Score < minScore) continue;
                blocks.Add(block);
            }

            // 注意：MergeFragments 内部已按阅读顺序排序（OrderBy 中心 Y + ThenBy X），
            // 不要再在外面套一层 SortReadingOrder —— 那是完全等价的重复排序。
            return MergeFragments(blocks);
        }
        finally
        {
            if (owns) working.Dispose();
        }
    }

    private static OcrBlock? ToBlock(TextBlock t, double scale)
    {
        var points = t.BoxPoints;
        if (points == null || points.Length == 0) return null;

        double minX = points.Min(p => (double)p.X) / scale;
        double minY = points.Min(p => (double)p.Y) / scale;
        double maxX = points.Max(p => (double)p.X) / scale;
        double maxY = points.Max(p => (double)p.Y) / scale;

        // 文字识别置信度优先用逐字平均；缺失时退化为文本框置信度
        float avgScore = 0f;
        var charScores = t.CharScores;
        if (charScores != null && charScores.Length > 0)
            avgScore = charScores.Average();
        else
            avgScore = t.BoxScore;

        return new OcrBlock
        {
            Text = t.Text!.Trim(),
            Score = avgScore,
            Box = new System.Windows.Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY))
        };
    }

    /// <summary>按阅读顺序（上到下、左到右）排序</summary>
    private static List<OcrBlock> SortReadingOrder(List<OcrBlock> blocks)
    {
        return blocks
            .OrderBy(b => b.Box.Y + b.Box.Height / 2)
            .ThenBy(b => b.Box.X)
            .ToList();
    }


    /// <summary>
    /// 合并同一行里被检测器切碎的相邻碎片。
    ///
    /// 现象：DBNet 检测器经常把一个词的左右两半、或"图标+文字"切成两个框，
    /// 于是识别结果里出现 "Sys" + "tem"、"Re" + "ad" 这类碎片。
    /// 它们单独翻译会得到错误结果（"tem" 无从翻译），
    /// 因此在这里先把同一行、水平紧邻、行高相近的框拼回一个。
    ///
    /// 判据（保守，避免把真正独立的词错误粘连）：
    ///   · 垂直中心差 &lt; 0.5 倍行高
    ///   · 水平间隙 &lt; 0.6 倍行高（正常词间距约 0.3~0.5 倍行高，
    ///     真正换栏/独立元素通常远大于此）
    ///   · 两者都是拉丁字母/数字，或都是汉字（避免把中英混排硬拼）
    /// </summary>
    private static List<OcrBlock> MergeFragments(List<OcrBlock> blocks)
    {
        if (blocks.Count <= 1) return blocks;

        var ordered = blocks
            .OrderBy(b => b.Box.Y + b.Box.Height / 2)
            .ThenBy(b => b.Box.X)
            .ToList();

        var result = new List<OcrBlock>(ordered.Count);
        var pending = new List<OcrBlock>();

        void Flush()
        {
            if (pending.Count == 0) return;
            if (pending.Count == 1) { result.Add(pending[0]); pending.Clear(); return; }

            // 按 X 拼接，中间按需补空格
            var text = new System.Text.StringBuilder();
            double minX = pending.Min(b => b.Box.X);
            double minY = pending.Min(b => b.Box.Y);
            double maxR = pending.Max(b => b.Box.X + b.Box.Width);
            double maxB = pending.Max(b => b.Box.Y + b.Box.Height);
            double scoreSum = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                if (i > 0)
                {
                    var prev = pending[i - 1];
                    var cur = pending[i];
                    char a = prev.Text[^1], b = cur.Text[0];
                    bool bothLatin = IsLatin(a) && IsLatin(b);
                    double gap = cur.Box.X - (prev.Box.X + prev.Box.Width);
                    double lineH = Math.Max(prev.Box.Height, cur.Box.Height);
                    // 间隙接近一个空格宽 → 补空格；紧贴则直接拼
                    if (bothLatin && gap > Math.Max(2.0, lineH * 0.18)) text.Append(' ');
                }
                text.Append(pending[i].Text);
                scoreSum += pending[i].Score;
            }

            result.Add(new OcrBlock
            {
                Text = text.ToString(),
                Score = (float)(scoreSum / pending.Count),
                Box = new System.Windows.Rect(minX, minY, maxR - minX, maxB - minY)
            });
            pending.Clear();
        }

        foreach (var b in ordered)
        {
            if (pending.Count == 0) { pending.Add(b); continue; }

            var last = pending[^1];
            double lastCx = last.Box.X + last.Box.Width;
            double lastCy = last.Box.Y + last.Box.Height / 2;
            double curCy = b.Box.Y + b.Box.Height / 2;
            double lineH = Math.Max(last.Box.Height, b.Box.Height);

            double gap = b.Box.X - lastCx;
            bool sameLine = Math.Abs(curCy - lastCy) <= lineH * 0.5;
            bool close = gap >= -lineH * 0.3 && gap <= lineH * 0.6;

            char lastCh = last.Text[^1];
            char curCh = b.Text[0];
            bool scriptMatch = (IsLatin(lastCh) && IsLatin(curCh))
                               || (IsCjk(lastCh) && IsCjk(curCh));

            if (sameLine && close && scriptMatch) pending.Add(b);
            else { Flush(); pending.Add(b); }
        }
        Flush();

        return result;
    }

    private static bool IsLatin(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    private static bool IsCjk(char c)
        => c >= 0x4e00 && c <= 0x9fff;

    private static SKBitmap ResizeBitmap(SKBitmap src, int newWidth, int newHeight)
    {
        var dst = new SKBitmap(newWidth, newHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        // 重采样：小字放大后先用双线性插值，边缘比默认最近邻更干净。
        // 说明：SkiaSharp 3.x 的 DrawBitmap(SKBitmap, SKRect, SKPaint) 重载
        // 不接受 SKSamplingOptions（只有 SKPaint 版本），因此这里用
        // SKBitmap.Resize 的等价路径 —— 先用 SKPaint 画到目标画布即可。
        using var paint = new SKPaint { IsAntialias = true };
        var dest = SKRect.Create(0, 0, newWidth, newHeight);
        canvas.DrawBitmap(src, dest, paint);
        return dst;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _ocr.Dispose(); } catch { }
    }
}
