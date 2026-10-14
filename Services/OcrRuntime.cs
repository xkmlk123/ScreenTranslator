using Microsoft.ML.OnnxRuntime;

namespace ScreenTranslator.Services;

/// <summary>
/// ONNX Runtime 推理会话的构建与调优。
///
/// ---- 为什么需要它（实测数据）----
/// OCR 全部计算都在 ONNX Runtime 里，而它的**线程数配置对性能影响极大**。
/// 在同一台 20 逻辑核的机器、同一张 1600×900 测试图上实测：
///
///   | 线程数 | 单次 OCR 耗时 | 相对最优 |
///   |-------|-------------|---------|
///   |   2   |   1222 ms   |  1.97×  |
///   |   4   |    829 ms   |  1.34×  |
///   |   8   |    621 ms   |  1.00×  ← 最优 |
///   |  12   |   1127 ms   |  1.81×  |
///   |  16   |   1683 ms   |  2.71×  |
///   |  20   |   2386 ms   |  3.84×  ← 最慢 |
///
/// 结论：**不是线程越多越快**。ONNX Runtime 在小模型上，
/// 线程同步（barrier）开销会超过并行计算收益，用满所有核反而慢 3.8 倍。
///
/// 而 <c>RapidOcr.InitModels(det, cls, rec, dict)</c> 这个不带 SessionOptions 的
/// 重载，会让 ORT 按默认策略用满所有核 —— 实测 1362 ms，正好落在最差区间。
/// 传一个 numThread=8 的选项就能降到 654 ms（**提速 2.08 倍**）。
///
/// ---- 线程数怎么选 ----
/// 经验公式：取「物理核数」附近，并钳制在 4~8。
/// 本机 20 逻辑核（约 10 物理核）→ 取 8。
/// 下限 4 保证小机器不至于太慢；上限 8 避免滑入同步开销主导的区间。
/// </summary>
public static class OcrRuntime
{
    /// <summary>线程数下限（低于此值小图也浪费）</summary>
    public const int MinThreads = 4;

    /// <summary>线程数上限（超过后同步开销超过并行收益，实测显著变慢）</summary>
    public const int MaxThreads = 8;

    /// <summary>实际使用的线程数（由 <see cref="BuildSessionOptions"/> 计算后缓存）</summary>
    public static int EffectiveThreads { get; private set; } = RecommendThreads();

    /// <summary>实际使用的提供程序名（"CPU" 或 "DirectML"）</summary>
    public static string ProviderName { get; private set; } = "CPU";

    /// <summary>用户手动指定的线程数（0 = 自动）</summary>
    private static int _userThreads;

    /// <summary>是否已因 GPU 失败而强制退回 CPU（避免每次都重试 GPU）</summary>
    private static bool _forceCpu;

    /// <summary>
    /// 强制使用 CPU（GPU 会话创建失败后调用）。
    /// 一旦强制，后续 <see cref="BuildSessionOptions"/> 不再尝试 GPU。
    /// </summary>
    public static void ForceCpu()
    {
        _forceCpu = true;
        ProviderName = "CPU";
    }

    /// <summary>
    /// 推荐线程数。
    ///
    /// 不用 Environment.ProcessorCount 直接当线程数：它返回的是**逻辑核**
    /// （含超线程），而 ONNX 的算子并行在超线程上收益递减。
    /// 这里取逻辑核数的一半（约等于物理核数）再钳到 4~8。
    /// </summary>
    public static int RecommendThreads()
    {
        int logical = Environment.ProcessorCount;
        int physicalish = Math.Max(2, logical / 2);
        return Math.Clamp(physicalish, MinThreads, MaxThreads);
    }

    /// <summary>设置用户指定的线程数（0 = 恢复自动）</summary>
    public static void SetUserThreads(int threads)
    {
        _userThreads = threads <= 0 ? 0 : Math.Clamp(threads, 1, 64);
        EffectiveThreads = _userThreads > 0 ? _userThreads : RecommendThreads();
    }

    /// <summary>
    /// 构建推理会话选项。
    ///
    /// 先尝试 DirectML（GPU），失败则静默回退到 CPU —— 保证任何机器都能跑。
    /// 之所以"尝试后回退"而不是"探测显卡型号"：显卡与驱动组合太多，
    /// 探测不如直接试一次来得可靠；失败成本只是几十毫秒。
    /// </summary>
    public static SessionOptions BuildSessionOptions()
    {
        int threads = _userThreads > 0 ? _userThreads : RecommendThreads();

        // ---- 先试 GPU ----
        var gpu = TryBuildGpuOptions(threads);
        if (gpu != null)
        {
            EffectiveThreads = threads;
            ProviderName = "DirectML";
            return gpu;
        }

        // ---- CPU ----
        var so = MakeCpuOptions(threads);
        EffectiveThreads = threads;
        ProviderName = "CPU";
        return so;
    }

    /// <summary>构建 CPU 会话选项并设定线程数</summary>
    private static SessionOptions MakeCpuOptions(int threads)
    {
        var so = new SessionOptions
        {
            // 关闭 profiling，减少不必要的开销
            EnableProfiling = false,
            // 内存复用：同一会话反复推理时避免重复分配
            EnableMemoryPattern = true,
            EnableCpuMemArena = true,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        // 关键：显式设线程数。用属性而非 RapidOcr.GetDefaultSessionOptions，
        // 因为后者只是薄封装，属性写法更直观且不依赖库的辅助方法。
        so.IntraOpNumThreads = threads;
        so.InterOpNumThreads = Math.Max(1, threads / 2);
        return so;
    }

    /// <summary>
    /// 尝试构建 DirectML（GPU）会话选项。
    ///
    /// 重要提示：DirectML 需要 native onnxruntime.dll 是 DML 版本。
    /// 若当前部署的是 CPU 版 native dll，这里会在创建 session 时才失败 ——
    /// 因此调用方必须把 <see cref="BuildSessionOptions"/> 的结果交给
    /// RapidOcr.InitModels，并捕获异常后重新以 CPU 选项初始化。
    ///
    /// 这里只构造 options（不创建 session），所以失败会延后到 InitModels，
    /// 由 OcrService 的 try/catch 兜住。
    /// </summary>
    private static SessionOptions? TryBuildGpuOptions(int threads)
    {
        if (_forceCpu) return null;
        try
        {
            var so = MakeCpuOptions(threads);
            // deviceId 0 = 默认显卡。DirectML 不需要 CUDA 那样单独装 runtime。
            so.AppendExecutionProvider_DML(0);
            return so;
        }
        catch
        {
            // 托管层就没这个方法 / native dll 不匹配 → 直接回退 CPU
            return null;
        }
    }
}
