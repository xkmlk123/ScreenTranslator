using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>
/// 段落合并器：把 OCR 切碎的相邻文字块合并成完整的句子/段落。
///
/// 背景：OCR 会按视觉行切块，一句话常常被切成多块（尤其是网页正文）。
/// 逐块翻译会丢掉上下文，译文生硬甚至语义错误。这里先按版面把同一段落
/// 的块合并，整段送翻译，再把整段译文按各块原文长度比例切回，兼顾
/// 翻译质量与"译文仍贴回原文位置"的显示要求。
/// </summary>
public static class TextSegmenter
{
    /// <summary>
    /// 把文字块按版面合并成段落。返回的每个 Segment 里 Blocks 保持阅读顺序。
    /// </summary>
    /// <param name="blocks">OCR 结果（会被按行排序后处理）</param>
    /// <param name="prevSignatures">
    /// 上一帧每个块的文本签名，用于判断行尾是否需要接续（跨帧句子未结束时补空格）
    /// </param>
    public static List<TranslationSegment> BuildSegments(
        IReadOnlyList<OcrBlock> blocks,
        IReadOnlyDictionary<int, string>? prevSignatures = null)
    {
        var result = new List<TranslationSegment>();
        if (blocks == null || blocks.Count == 0) return result;

        // ---- 1. 按行聚类：先把块按垂直位置分到不同的"行" ----
        var ordered = blocks
            .Select((b, i) => (Block: b, Index: i))
            .OrderBy(t => t.Block.Box.Y + t.Block.Box.Height / 2)
            .ThenBy(t => t.Block.Box.X)
            .ToList();

        var lines = new List<List<(OcrBlock Block, int Index)>>();
        foreach (var item in ordered)
        {
            double cy = item.Block.Box.Y + item.Block.Box.Height / 2;
            var line = lines.LastOrDefault();
            if (line != null)
            {
                double lineCy = line.Average(t => t.Block.Box.Y + t.Block.Box.Height / 2);
                double tol = Math.Max(4.0, item.Block.Box.Height * 0.6);
                if (Math.Abs(cy - lineCy) <= tol)
                {
                    line.Add(item);
                    continue;
                }
            }
            lines.Add(new List<(OcrBlock, int)> { item });
        }

        // 每行内按 X 排序
        foreach (var line in lines)
            line.Sort((a, b) => a.Block.Box.X.CompareTo(b.Block.Box.X));

        // ---- 2. 相邻行合并成段 ----
        List<(OcrBlock Block, int Index)>? current = null;
        double currentRowHeight = 0;
        double currentRightEdge = 0;

        void Flush()
        {
            if (current == null || current.Count == 0) { current = null; return; }
            result.Add(BuildSegment(current, prevSignatures));
            current = null;
        }

        foreach (var line in lines)
        {
            double lineHeight = line.Average(t => t.Block.Box.Height);
            double lineLeft = line.Min(t => t.Block.Box.X);
            double lineRight = line.Max(t => t.Block.Box.X + t.Block.Box.Width);
            double lineTop = line.Min(t => t.Block.Box.Y);

            if (current == null)
            {
                current = new List<(OcrBlock, int)>(line);
                currentRowHeight = lineHeight;
                currentRightEdge = lineRight;
                continue;
            }

            // 垂直间距（当前行顶部 - 上一行底部）
            double prevBottom = current.Max(t => t.Block.Box.Y + t.Block.Box.Height);
            double gap = lineTop - prevBottom;
            double currentLeft = current.Min(t => t.Block.Box.X);

            // 缩进明显变大 → 视为新段落
            bool newParagraph = lineLeft > currentLeft + Math.Max(12.0, lineHeight * 0.9);

            // 垂直间距过大 → 视为新段落（约 1.2 倍行高以上）
            bool bigGap = gap > lineHeight * 1.25;

            // 水平方向完全不重叠（如左右两栏）→ 视为不同段
            bool noOverlap = lineLeft > currentRightEdge + lineHeight * 2
                             || lineRight < currentLeft - lineHeight * 2;

            if (newParagraph || bigGap || noOverlap)
            {
                Flush();
                current = new List<(OcrBlock, int)>(line);
                currentRowHeight = lineHeight;
                currentRightEdge = lineRight;
            }
            else
            {
                current.AddRange(line);
                currentRowHeight = Math.Max(currentRowHeight, lineHeight);
                currentRightEdge = Math.Max(currentRightEdge, lineRight);
            }
        }
        Flush();

        return result;
    }

    /// <summary>用当前块的文本签名重建"上一帧签名表"，用于跨帧判断句子是否被切断</summary>
    public static Dictionary<int, string> BuildSignatures(IReadOnlyList<OcrBlock> blocks)
    {
        var map = new Dictionary<int, string>();
        for (int i = 0; i < blocks.Count; i++)
            map[i] = TextNormalizer.Normalize(blocks[i].Text);
        return map;
    }

    private static TranslationSegment BuildSegment(
        List<(OcrBlock Block, int Index)> items,
        IReadOnlyDictionary<int, string>? prevSignatures)
    {
        items.Sort((a, b) =>
        {
            int c = a.Block.Box.Y.CompareTo(b.Block.Box.Y);
            return c != 0 ? c : a.Block.Box.X.CompareTo(b.Block.Box.X);
        });

        var seg = new TranslationSegment { Blocks = items.Select(t => t.Block).ToList() };

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            var block = items[i].Block;
            string text = block.Text;
            if (i > 0 && NeedsSpace(items[i - 1].Block, block))
                sb.Append(' ');
            sb.Append(text);
        }
        seg.SourceText = sb.ToString();

        // 把合并结果写回每个块，让"整段译文 → 各块译文"的切分有据可依
        seg.SourceText = sb.ToString();
        var parts = SplitMergedText(seg.SourceText, items.Select(t => t.Block.Text).ToList());
        for (int i = 0; i < items.Count && i < parts.Count; i++)
            items[i].Block.SourceTextInSegment = parts[i];

        double minX = items.Min(t => t.Block.Box.X);
        double minY = items.Min(t => t.Block.Box.Y);
        double maxX = items.Max(t => t.Block.Box.X + t.Block.Box.Width);
        double maxY = items.Max(t => t.Block.Box.Y + t.Block.Box.Height);
        seg.BoundingBox = new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);

        // 单行高度取各块高度的中位数：多行段落里 BoundingBox.Height 是整段高度，
        // 直接拿它算字号会把字放大到接近两倍，导致译文压不住原文。
        var heights = items.Select(t => t.Block.Box.Height).OrderBy(h => h).ToList();
        seg.LineHeight = heights.Count == 0
            ? seg.BoundingBox.Height
            : heights[heights.Count / 2];

        // ---- 回填各块的绘制矩形 ----
        // 目的：译文逐块绘制、贴回各自原文位置（不再按整段重排）。
        // 上下各外扩一点点，让字面高度与原文字面高度对齐：
        // 中文字形墨迹高约为字号的 1.0 倍，而 OCR 框通常贴着墨迹，
        // 因此需要把行间距的那部分"余量"加回来，否则字号会被算小。
        double lineH = seg.LineHeight > 0 ? seg.LineHeight : seg.BoundingBox.Height;
        foreach (var item in items)
        {
            var b = item.Block;
            // 优先用自适应收紧后的框（更贴近字面高度），未启用时回退到原检测框。
            // 注意：分行/分栏等几何判断仍必须用原始 Box —— 收紧是"显示层"的
            // 微调，不应改变块之间的位置关系。
            var src = b.FittedBox.Width > 0 && b.FittedBox.Height > 0 ? b.FittedBox : b.Box;

            // 框高度偏小时（贴紧墨迹），按段内中位行高补齐；
            // 已经够大的（含行间距）则保持原样。
            double h = Math.Max(src.Height, lineH);
            double centerY = src.Y + src.Height / 2.0;
            b.DrawRect = new System.Windows.Rect(
                src.X,
                centerY - h / 2.0,
                src.Width,
                h);
        }

        return seg;
    }

    /// <summary>判断两个相邻块拼接时是否需要加空格（拉丁字母之间需要）</summary>
    private static bool NeedsSpace(OcrBlock left, OcrBlock right)
    {
        string a = left.Text.TrimEnd();
        string b = right.Text.TrimStart();
        if (a.Length == 0 || b.Length == 0) return false;

        char la = a[^1];
        char rb = b[0];

        // 左块以句末标点结尾 → 已经是一句完整的话，直接拼接不加空格
        if (IsSentenceEnd(la)) return false;

        return IsLatinLetterOrDigit(la) && IsLatinLetterOrDigit(rb);
    }

    private static bool IsSentenceEnd(char c)
        => c is '.' or '!' or '?' or ';' or ':' or '…'
             or '。' or '！' or '？' or '；' or '：' or '、' or '，' or ',';

    private static bool IsLatinLetterOrDigit(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

    /// <summary>
    /// 把合并后的段落文本按各块"在段落中实际占用的片段"切回来。
    /// 关键点：块之间的空格是拼接时人为插入的，不是块自身内容，
    /// 必须把空格计入前一个块的片段，否则后面切分译文时会整体错位
    /// （典型症状：短块拿不到译文、末尾留空）。
    /// </summary>
    private static List<string> SplitMergedText(string merged, List<string> rawTexts)
    {
        var parts = new List<string>(rawTexts.Count);
        int cursor = 0;

        for (int i = 0; i < rawTexts.Count; i++)
        {
            string raw = rawTexts[i];
            if (raw.Length == 0) { parts.Add(string.Empty); continue; }

            int idx = merged.IndexOf(raw, cursor, StringComparison.Ordinal);
            if (idx < 0)
            {
                // 兜底：找不到就按原文本顺序推进
                idx = cursor;
            }

            // 片段包含块尾之后、下一个块之前的分隔空格
            int end = idx + raw.Length;
            while (end < merged.Length && merged[end] == ' ') end++;

            parts.Add(merged.Substring(idx, end - idx));
            cursor = end;
        }

        return parts;
    }
}
