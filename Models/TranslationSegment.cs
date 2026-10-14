namespace ScreenTranslator.Models;

/// <summary>
/// 一个待翻译的文本段：由若干相邻的 OCR 文字块合并而成。
///
/// 合并的目的是把"被 OCR 切成好几段的一句话"还原成整句再送翻译，
/// 从而得到通顺的译文。
///
/// 重要（显示策略）：
/// 段落【只用于翻译上下文】，不作为绘制单位。
/// 绘制时逐块使用 OcrBlock.DrawRect 定位，保证每块译文贴回自己的原文位置。
/// 早期版本按整段包围盒从左上角重排，多行段落会把译文整体撑高、逐行下坠，
/// 这是"译文盖不住原文"的主因，现已废弃该策略。
/// </summary>
public class TranslationSegment
{
    /// <summary>该段包含的 OCR 块（按阅读顺序）</summary>
    public List<OcrBlock> Blocks { get; set; } = new();

    /// <summary>合并后的完整原文（块之间用空格连接）</summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>
    /// 整段译文（翻译完成后回填）。
    ///
    /// <c>null</c> 表示【本段没有可用的译文】，绘制层应整段跳过。
    /// 两种情形会置 null：翻译失败（模型原样返回原文），
    /// 或本段内容不可译（图标误读的符号、纯数字、已是目标语言）。
    ///
    /// 注意不要与"空串"混用：空串是被切分后的中间状态，
    /// null 才是"明确不要绘制"的语义标记。
    /// </summary>
    public string? TranslatedText { get; set; } = string.Empty;

    /// <summary>合并后的整体外接矩形（仅用于调试显示/统计）</summary>
    public System.Windows.Rect BoundingBox { get; set; }

    /// <summary>
    /// 段的"单行高度"（取各块高度的中位数，物理像素）。
    ///
    /// 必须与 BoundingBox.Height 区分：多行段落合并后 BoundingBox.Height 是整段
    /// 高度（如 2 行 ≈ 36px）。字号只应由单行高度决定。
    /// </summary>
    public double LineHeight { get; set; }
}
