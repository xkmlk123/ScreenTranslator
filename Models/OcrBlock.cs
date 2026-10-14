using System.Windows;

namespace ScreenTranslator.Models;

/// <summary>
/// OCR 识别出的文字块
/// </summary>
public class OcrBlock
{
    /// <summary>文字框在【捕获帧】内的坐标（物理像素，左上角为 0,0）</summary>
    public Rect Box { get; set; }

    /// <summary>
    /// 自适应收紧后的显示框（物理像素）。
    ///
    /// 与 <see cref="Box"/> 的区别：Box 是 DBNet 的原始检测框，含无差别外扩；
    /// FittedBox 是按"该框自身的字号"进一步收紧后的结果，用于绘制。
    /// 识别仍用 Box 保证准确率，只有显示用 FittedBox。
    /// 未启用收紧时与 Box 相同。
    /// </summary>
    public Rect FittedBox { get; set; }

    /// <summary>识别到的原文</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>置信度 0~1</summary>
    public float Score { get; set; }

    /// <summary>翻译后的译文</summary>
    public string Translated { get; set; } = string.Empty;

    /// <summary>
    /// 本块是否应当绘制译文。
    ///
    /// 为什么需要这个标志：<see cref="Translated"/> 非空**不等于**"有译文可用"。
    /// 两种情况下译文虽有内容却不该画：
    ///
    /// 1. 翻译失败 —— 模型原样返回原文。此时 Translated 等于原文，
    ///    画上去就是在原文位置重画一遍英文，字体/间距略有差异 → 视觉重影。
    ///    而且用户要的语义是"没翻译就别动它"，所以应保持原文不动。
    ///
    /// 2. 内容本就不可译 —— 图标被 OCR 误读成的符号、纯数字等。
    ///    这类内容上面盖任何东西都是破坏（会挡住图标）。
    ///
    /// 只有"确实得到了与原文不同的译文"时才置 true。
    /// 绘制层必须检查此标志，而不能只判 Translated 是否为空。
    /// </summary>
    public bool HasValidTranslation { get; set; }

    /// <summary>
    /// 该块在其所属段落中被合并后的文本片段（由 TextSegmenter 回填）。
    /// 用于把整段译文按正确的比例切分回各块。
    /// </summary>
    public string SourceTextInSegment { get; set; } = string.Empty;

    /// <summary>
    /// 显示时实际用于绘制译文的矩形（捕获帧物理像素）。
    ///
    /// 与 Box 的区别：Box 是 OCR 检测框（可能略小/略大），
    /// DrawRect 是"向外扩了一点"后的落字框——把行高补偿进上下边界，
    /// 让译文和原文字面高度对齐。由 TextSegmenter 在合并时回填。
    /// </summary>
    public Rect DrawRect { get; set; }
}
