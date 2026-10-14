using System.Text;
using System.Text.RegularExpressions;

namespace ScreenTranslator.Services;

/// <summary>
/// 文本标准化工具：用于帧间去重比较与缓存键生成。
/// 目标是把"人眼看是一样的内容"归一成同一个字符串，避免 OCR 的微小抖动
/// 造成不必要的重新翻译。
/// </summary>
public static class TextNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunct = new(@"\s+([,.;:!?)\]])", RegexOptions.Compiled);
    private static readonly Regex SpaceAfterOpen = new(@"([(\[])\s+", RegexOptions.Compiled);

    /// <summary>标准化文本：裁空白、合并连续空白、统一标点、去除常见 OCR 噪声</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string s = text.Trim();

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            // 所有空白（含全角空格）统一折叠成半角空格，
            // 避免被后面的全角→半角映射误转成别的字符
            if (char.IsWhiteSpace(c) || c == '\u3000')
            {
                sb.Append(' ');
                continue;
            }
            sb.Append(FoldChar(c));
        }

        // 合并连续空白，并去掉标点两侧多余空格，使视觉相同的内容得到同一个键
        s = Whitespace.Replace(sb.ToString(), " ");
        s = SpaceBeforePunct.Replace(s, "$1");
        s = SpaceAfterOpen.Replace(s, "$1");

        return s.Trim();
    }

    /// <summary>把全角标点/全角英数字折叠成半角，使视觉相同的内容得到同一个键</summary>
    private static char FoldChar(char c)
    {
        switch (c)
        {
            case '，': return ',';
            case '。': return '.';
            case '！': return '!';
            case '？': return '?';
            case '：': return ':';
            case '；': return ';';
            case '（': return '(';
            case '）': return ')';
            case '［': return '[';
            case '］': return ']';
            case '｛': return '{';
            case '｝': return '}';
            case '“': return '"';
            case '”': return '"';
            case '‘': return '\'';
            case '’': return '\'';
            case '｀': return '`';
            case '～': return '~';
            case '＠': return '@';
            case '＃': return '#';
            case '＄': return '$';
            case '％': return '%';
            case '＾': return '^';
            case '＆': return '&';
            case '＊': return '*';
            case '－': return '-';
            case '＿': return '_';
            case '＋': return '+';
            case '＝': return '=';
            case '｜': return '|';
            case '＼': return '\\';
            case '／': return '/';
            case '＜': return '<';
            case '＞': return '>';
            case '、': return ',';
            case '·': return '.';
            case '…': return '.';
            case '—': return '-';
        }

        // 剩余全角 ASCII（U+FF01..U+FF5E）→ 半角
        if (c >= '\uFF01' && c <= '\uFF5E')
            return (char)(c - 0xFEE0);

        return c;
    }

    /// <summary>整帧签名：把一帧所有文本标准化后拼接，用于"内容是否变化"的快速判断</summary>
    public static string FrameSignature(IEnumerable<string> texts)
    {
        var sb = new StringBuilder();
        foreach (var t in texts)
        {
            string n = Normalize(t);
            if (n.Length == 0) continue;
            sb.Append(n).Append('\u0001');
        }
        return sb.ToString();
    }

    /// <summary>判断文本是否以句末标点结束（用于跨帧判断句子是否说完）</summary>
    public static bool EndsWithSentenceTerminator(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        char c = text.TrimEnd()[^1];
        return c is '.' or '!' or '?' or '。' or '！' or '？' or ';' or '；'
              or ':' or '：' or '…' or '"' or '”' or ')' or '）' or ',' or '，';
    }

    /// <summary>文本视觉宽度权重：CJK/全角字符 2，拉丁字母数字 1，空白与标点 0.5</summary>
    public static double VisualWeight(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        double sum = 0;
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') sum += 2;          // 中日韩统一表意文字
            else if (c >= '\u3000' && c <= '\u303f') sum += 2;      // CJK 标点
            else if (c >= '\u3040' && c <= '\u30ff') sum += 2;      // 假名
            else if (c >= '\uff00' && c <= '\uffef') sum += 2;      // 全角字符
            else if (char.IsLetterOrDigit(c)) sum += 1;
            else sum += 0.5;
        }
        return sum <= 0 ? 1 : sum;
    }
}
