using System.Windows;

namespace ScreenTranslator.Services;

/// <summary>
/// OCR 检测框的显示层缩放（自适应）。
///
/// ---- 为什么需要它 ----
/// DBNet 输出的检测框按"文字轮廓 + 固定比例外扩"得到，这个外扩是
/// <b>无差别</b>的：不管字号是 12px 还是 64px，都按同一个比例膨胀。
/// 实测这会带来两个问题：
///   1. 大字的框冗余比例更高，框看起来"虚胖"；
///   2. 小字反而被包得更松，密集排布时框与框之间显得拥挤。
///
/// 因此这里在<b>显示层</b>对每个框按它自身的字号做一次缩放：
///   · 缩放 &lt; 1 → 向内收紧（大字多收、小字少收，绝对量随框高变化）
///   · 缩放 &gt; 1 → 向外放松
///
/// ---- 关键约束（实测得出，不能违反）----
/// CRNN 识别器要求框内保留<b>完整的字形轮廓及上下留白</b>，
/// 框收得过紧会裁掉字的上下缘导致识别率断崖式下跌。
/// 但本类的缩放<b>只作用于绘制</b>，识别始终使用原始检测框，
/// 因此怎么调都不会损伤识别率 —— 这也是它敢交给用户随便拖的原因。
///
/// 实测（白底黑字、MS YaHei）可安全裁掉的余量约 = 框高 × 0.2，
/// 字号越大占比越高；本类取 0.18 作为保守上限。
/// </summary>
public static class OcrBoxFitter
{
    /// <summary>显示框缩放的最小值（滑块下限）</summary>
    public const double MinScale = 0.50;

    /// <summary>显示框缩放的最大值（滑块上限）</summary>
    public const double MaxScale = 1.50;

    /// <summary>默认缩放（1.0 = 检测框原样）</summary>
    public const double DefaultScale = 1.00;

    /// <summary>
    /// 向内的最大可裁比例（相对框高）。
    /// 实测大字可裁约 0.21、小字约 0.15，取 0.18 作为保守上限。
    /// 缩放 0.50 时对应裁到本上限。
    /// </summary>
    private const double MaxInnerTrim = 0.18;

    /// <summary>向外的最大可扩比例（相对框高），与向内对称，保证拖动手感一致</summary>
    private const double MaxOuterGrow = 0.20;

    /// <summary>
    /// 水平方向可裁掉的比例上限（相对框宽）。
    /// 水平方向对首尾字符极敏感，留得比垂直保守得多。
    /// </summary>
    private const double MaxHorizontalTrim = 0.015;

    /// <summary>把用户给的缩放钳到合法区间</summary>
    public static double Clamp(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>
    /// 对单个检测框做自适应的显示层缩放。
    /// </summary>
    /// <param name="box">原始检测框（捕获帧物理像素）</param>
    /// <param name="scale">显示缩放倍率（0.5~1.5，1.0 为不改动）</param>
    public static Rect Fit(Rect box, double scale)
    {
        if (box.Width <= 1 || box.Height <= 1) return box;

        double s = Clamp(scale);
        if (Math.Abs(s - 1.0) <= 0.0001) return box;

        if (s < 1.0)
        {
            // ---- 收紧 ----
            // 把"缩放缺口"(1-s) 映射到可裁比例上：s=1 → 0，s=0.5 → MaxInnerTrim
            double k = (1.0 - s) / (1.0 - MinScale);       // 0~1
            double trimVTotal = box.Height * MaxInnerTrim * k;

            // 保护下限：任何字号都至少保留 6px 净高，否则太扁识别会失败
            double minKeepHeight = Math.Max(6.0, box.Height * 0.70);
            if (box.Height - trimVTotal < minKeepHeight)
                trimVTotal = Math.Max(0, box.Height - minKeepHeight);

            double cutV = trimVTotal / 2.0;
            double cutH = Math.Min(box.Width * MaxHorizontalTrim * k, box.Width * 0.03);

            double w = box.Width - cutH * 2;
            double h = box.Height - cutV * 2;
            if (w <= 1 || h <= 1) return box;
            return new Rect(box.X + cutH, box.Y + cutV, w, h);
        }
        else
        {
            // ---- 放松 ----
            double k = (s - 1.0) / (MaxScale - 1.0);       // 0~1
            double growV = box.Height * MaxOuterGrow * k / 2.0;
            double growH = box.Width * MaxHorizontalTrim * k;

            return new Rect(
                box.X - growH,
                box.Y - growV,
                box.Width + growH * 2,
                box.Height + growV * 2);
        }
    }

    /// <summary>
    /// 批量缩放，结果写入每个块的 <see cref="Models.OcrBlock.FittedBox"/>。
    /// 缩放为 1.0 时不写入（FittedBox 保持默认空值，绘制时自动回退到 Box）。
    /// </summary>
    public static void ApplyTo(IEnumerable<Models.OcrBlock> blocks, double scale)
    {
        if (Math.Abs(Clamp(scale) - 1.0) <= 0.0001) return;
        foreach (var b in blocks)
            b.FittedBox = Fit(b.Box, scale);
    }
}
