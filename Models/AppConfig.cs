using ScreenTranslator.Services;

namespace ScreenTranslator.Models;

/// <summary>
/// 应用配置
/// </summary>
public class AppConfig
{
    /// <summary>翻译模式：Cloud（云端API）/ Local（本地Ollama）</summary>
    public string TranslationMode { get; set; } = "Cloud";

    /// <summary>大模型 API Base Url（OpenAI 兼容）</summary>
    public string ApiBaseUrl { get; set; } = "https://api.deepseek.com";

    /// <summary>API Key</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名称</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>本地 Ollama 服务地址（OpenAI 兼容）</summary>
    public string LocalApiUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>本地 Ollama 模型名称</summary>
    public string LocalModel { get; set; } = "qwen2.5:3b";

    /// <summary>本地模式并发数（1~8，越大越快但更吃显存）</summary>
    public int LocalConcurrency { get; set; } = 3;

    /// <summary>捕获间隔（毫秒）</summary>
    public int CaptureIntervalMs { get; set; } = 2000;

    /// <summary>OCR 最低置信度</summary>
    public float MinConfidence { get; set; } = 0.3f;

    /// <summary>OCR 识别前的最小放大倍数；小于 1 表示不放大，按需自动放大（推荐 1.6）</summary>
    public double OcrScale { get; set; } = 1.6;

    /// <summary>
    /// OCR 推理线程数（0 = 自动，推荐）。
    ///
    /// 实测这个值对性能影响极大，且**不是越大越快**：
    /// 在同一台 20 逻辑核机器上，用满所有核反而比 8 线程慢 3 倍
    /// （ONNX Runtime 的线程同步开销超过并行收益）。
    /// 0 表示使用 <see cref="OcrRuntime.RecommendThreads"/> 的自动推荐值
    /// （约物理核数，钳制在 4~8）。
    /// </summary>
    public int OcrThreads { get; set; } = 0;

    /// <summary>
    /// 识别框显示大小（0.5~1.5，默认 1.00）。
    ///
    /// 这是设置界面「识别框大小」滑块的直接来源：
    ///   · &lt; 1.0 → 框向内收紧（按每个框自身的字号自适应，大字多收、小字少收）
    ///   · = 1.0 → 使用检测框原样
    ///   · &gt; 1.0 → 框向外放松
    ///
    /// 关键：该缩放【只作用于显示】，识别始终用原始检测框，
    /// 因此无论怎么调都不会损伤识别率。
    /// 上/下限由 <see cref="OcrBoxFitter.MinScale"/> / <see cref="OcrBoxFitter.MaxScale"/> 定义。
    /// </summary>
    public double OcrBoxScale { get; set; } = OcrBoxFitter.DefaultScale;

    /// <summary>源语言（本地模式可指定，云端模式自动检测）</summary>
    public string SourceLang { get; set; } = "英文";

    /// <summary>目标语言</summary>
    public string TargetLang { get; set; } = "中文";

    /// <summary>是否启用译文缓存（相同原文不重复调用 API）</summary>
    public bool EnableCache { get; set; } = true;

    /// <summary>译文 X 方向微调（像素，正数右移）</summary>
    public double OffsetX { get; set; } = 0;

    /// <summary>译文 Y 方向微调（像素，正数下移）</summary>
    public double OffsetY { get; set; } = 0;

    /// <summary>译文不透明度（0.2~1.0）</summary>
    public double OverlayOpacity { get; set; } = 1.0;

    /// <summary>覆盖率阈值：与原文差异比例低于该值时才重绘，避免每帧闪烁（0~1）</summary>
    public double RepaintThreshold { get; set; } = 0.25;

    /// <summary>是否隐藏原文，只显示译文</summary>
    public bool HideOriginalText { get; set; } = false;

    /// <summary>
    /// 字号倍率（0.5~3.0，默认 1.0）。
    /// 在"根据原文行高自动定字号"的基础上再乘这个倍率 ——
    /// 自动算法保证贴合原文，倍率让用户按自己视力/喜好微调。
    /// </summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>
    /// 背景倍率（0.5~3.0，默认 1.0）。
    /// 控制白底相对文字实际尺寸的放大比例，1.0 为刚好包住文字。
    /// </summary>
    public double BgScale { get; set; } = 1.0;

    /// <summary>
    /// 背景内边距（像素，0~30，默认 3）。
    /// 白底在文字四周额外多出的空白，用来把原文盖得更干净。
    /// </summary>
    public double BgPadding { get; set; } = 3;

    /// <summary>背景圆角半径（像素，0~20，默认 3）</summary>
    public double BgCornerRadius { get; set; } = 3;

    /// <summary>背景不透明度（0~1，默认 0.67；0 表示不画背景）</summary>
    public double BgOpacity { get; set; } = 0.67;

    /// <summary>
    /// 调试模式：在覆盖层上画出 OCR 检测框（原位置/绘制位置），
    /// 用于肉眼校对方框与原文是否重合。正式使用请关闭。
    /// </summary>
    public bool DebugBoxes { get; set; } = false;

    /// <summary>
    /// 仅显示 OCR 识别框（不翻译、不显示译文）。
    ///
    /// 用途：调 OCR 时先把"识别到什么、框在哪"看清楚，
    /// 不被翻译与绘制层的问题干扰。开启后跳过整条翻译链路，
    /// 只在每个识别到的文字块上画一个框 + 原文标签。
    /// </summary>
    public bool OcrBoxesOnly { get; set; } = false;

    /// <summary>仅显示识别框时，是否在框旁标注识别到的原文</summary>
    public bool OcrBoxesShowText { get; set; } = true;

    /// <summary>仅显示识别框时，是否在框旁标注置信度（与原文可各自独立开关）</summary>
    public bool OcrBoxesShowScore { get; set; } = true;

    /// <summary>
    /// 只显示原文、不翻译（跳过整条翻译链路，把原文按译文样式画出）。
    ///
    /// 与 <see cref="HideOriginalText"/> 语义相反：那个是「隐藏原文只显译文」，
    /// 这个是「只显原文、不做翻译」。两者互不影响，可各自独立。
    ///
    /// 实现上会主动把 <c>OcrBlock.HasValidTranslation</c> 置 true ——
    /// 该门控本意是「翻译失败/图标不画」，此处用户明确要求画原文，
    /// 是它唯一的合法例外。
    /// </summary>
    public bool ShowOriginalOnly { get; set; } = false;

    /// <summary>全局热键：开启/停止翻译</summary>
    public string ToggleHotkey { get; set; } = "Alt+T";

    /// <summary>
    /// 是否启用「变化检测」：画面没变时跳过 OCR。
    ///
    /// OCR 单帧约 0.6~1.4s，静止画面（阅读文档/看代码）每帧白跑一遍是最大无效开销。
    /// 开启后先比较 16×16 灰度指纹，变化占比低于 <see cref="ChangeThreshold"/>
    /// 就整帧跳过 OCR（渲染仍按 30Hz 跟随窗口移动）。
    /// </summary>
    public bool UseChangeDetection { get; set; } = true;

    /// <summary>
    /// 变化检测灵敏度：**最少变化格数**，低于此数视为噪声、跳过 OCR。
    ///
    /// 为什么用绝对格数而不是比例阈值：一个 32×32 图标在 1600×900 上
    /// 只占约 20 格 / 22600 格；用比例做判据时阈值会随屏幕大小漂移，
    /// 无法定一个通用值。实测噪声底是 **0 格**（灰度差阈值 8 把像素抖动全滤掉），
    /// 最小真实变化约 **16~20 格**，所以默认 3 有 5 倍以上余量。
    ///
    /// 调大 → 更迟钝（更省 OCR，但小变化可能漏）；调小 → 更灵敏（可能被噪声触发）。
    /// </summary>
    public int ChangeMinCells { get; set; } = 3;

    /// <summary>
    /// 是否启用识别区域（ROI）。false = 识别整个目标窗口（默认，零回归）。
    /// </summary>
    public bool UseRoi { get; set; } = false;

    /// <summary>识别区域相对捕获帧的归一化 X（0~1），随窗口缩放自动跟随。</summary>
    public double RoiX { get; set; } = 0;

    /// <summary>识别区域相对捕获帧的归一化 Y（0~1）。</summary>
    public double RoiY { get; set; } = 0;

    /// <summary>识别区域相对捕获帧的归一化宽度（0~1）。1 = 整帧宽。</summary>
    public double RoiW { get; set; } = 1;

    /// <summary>识别区域相对捕获帧的归一化高度（0~1）。1 = 整帧高。</summary>
    public double RoiH { get; set; } = 1;

    /// <summary>
    /// 覆盖层是否从"屏幕捕获"中排除（默认 true）。
    ///
    /// 关掉后覆盖层会出现在系统截图 / 录屏 / 远程协助画面里。
    /// 对 GDI 降级路径来说这会重新打开「译文被自己抓回去再翻译」的闭环，
    /// 因此**只建议排障时临时关闭**（比如要截图记录覆盖层外观）。
    /// </summary>
    public bool OverlayExcludeFromCapture { get; set; } = true;
}
