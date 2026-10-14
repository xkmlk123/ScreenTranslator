using SkiaSharp;

namespace ScreenTranslator.Services;

/// <summary>
/// 帧变化检测指纹。
///
/// 目的：在 **OCR 之前** 判断"画面是否变了"，没变就整帧跳过 OCR。
/// OCR 单帧约 0.6~1.4s，静止画面（阅读文档、看代码）时每帧白跑一遍，
/// 是最大的无效开销。
///
/// 设计要点（都是被实测逼出来的）：
///
/// 1. **固定像素步长采样**，不是固定格子数。
///    早期版本用"固定 16×16 格"，格子尺寸随图像变大而变大：
///    800×600 时每格 50×38px（一个 120×30 的词块能覆盖 2 格），
///    1600×900 时每格 100×56px（同一个词块小于一格 → 完全漏检）。
///    改成"每 <see cref="SampleStep"/> 像素取一点"后，敏感度与图像尺寸无关。
///
/// 2. **每格取 4 个采样点求均值**。单点采样对小面积变化漏检严重；
///    求均值等于对格子做面积平均，格子内任何可观内容都会被"抹"进结果。
///
/// 3. **灰度化 + 差值分档计数**。BT.601 加权灰度对色彩微变不敏感；
///    判定用"变化格占比"而非平均差 —— 平均差会被大面积未变背景稀释。
/// </summary>
public static class FrameFingerprint
{
    /// <summary>
    /// 采样步长（像素）：每 <c>SampleStep</c> 像素取一个采样格。
    ///
    /// 8px 是实测取舍：1600×900 下得到 200×113 = 22600 格，
    /// 一个 32×32 图标可覆盖约 16 格（信息充分、抗噪声）；
    /// 单次指纹计算约 1ms 左右 —— 相对 OCR 的 600~1400ms 仍是噪声级开销。
    /// 步长再小收益递减（计算量线性上升），再大则小目标（图标）格数不足。
    /// </summary>
    public const int SampleStep = 8;

    /// <summary>每个采样格内部再取 <c>SubSamples×SubSamples</c> 个点求均值。</summary>
    public const int SubSamples = 2;

    /// <summary>单格灰度差超过该值才算"这格变了"（0~255）。低于此视为噪声。</summary>
    public const int DefaultBlockThreshold = 8;

    /// <summary>指纹行列数上限（防止超大屏把采样点撑爆）。</summary>
    public const int MaxGridDim = 512;

    /// <summary>
    /// 计算指纹：返回灰度数组（行优先），长度 = rows × cols。
    /// 同时输出行列数，供 <see cref="DiffRatio(byte[], byte[], int, int)"/> 使用。
    /// </summary>
    public static byte[] Compute(SKBitmap bmp, SKRect? roi,
                                 out int rows, out int cols)
    {
        rows = 0; cols = 0;
        if (bmp == null || bmp.Width <= 0 || bmp.Height <= 0) return Array.Empty<byte>();

        // 采样区域（夹在图像范围内）
        float x0 = 0, y0 = 0, w = bmp.Width, h = bmp.Height;
        if (roi is SKRect region && region.Width > 0 && region.Height > 0)
        {
            x0 = Math.Clamp(region.Left, 0, bmp.Width - 1);
            y0 = Math.Clamp(region.Top, 0, bmp.Height - 1);
            w = Math.Clamp(region.Width, 1, bmp.Width - x0);
            h = Math.Clamp(region.Height, 1, bmp.Height - y0);
        }

        cols = Math.Clamp((int)Math.Ceiling(w / SampleStep), 1, MaxGridDim);
        rows = Math.Clamp((int)Math.Ceiling(h / SampleStep), 1, MaxGridDim);

        var fp = new byte[rows * cols];

        IntPtr ptr = bmp.GetPixels();
        int stride = bmp.RowBytes;
        int bpp = bmp.BytesPerPixel;
        if (ptr == IntPtr.Zero || bpp < 3) return fp;

        float cellW = w / cols;
        float cellH = h / rows;
        int maxX = bmp.Width - 1, maxY = bmp.Height - 1;

        unsafe
        {
            byte* basePtr = (byte*)ptr;

            for (int gy = 0; gy < rows; gy++)
            {
                // 每一行的 SubSamples 个采样 y 坐标提前算好（与 gx 无关）
                int py0 = (int)(y0 + (gy + 0.5f / SubSamples) * cellH);
                int py1 = SubSamples > 1
                    ? (int)(y0 + (gy + 1.5f / SubSamples) * cellH)
                    : py0;
                if (py0 < 0) py0 = 0; if (py0 > maxY) py0 = maxY;
                if (py1 < 0) py1 = 0; if (py1 > maxY) py1 = maxY;
                byte* rowA = basePtr + (long)py0 * stride;
                byte* rowB = basePtr + (long)py1 * stride;

                for (int gx = 0; gx < cols; gx++)
                {
                    // 每一列的两个采样 x 坐标
                    int px0 = (int)(x0 + (gx + 0.5f / SubSamples) * cellW);
                    int px1 = SubSamples > 1
                        ? (int)(x0 + (gx + 1.5f / SubSamples) * cellW)
                        : px0;
                    if (px0 < 0) px0 = 0; if (px0 > maxX) px0 = maxX;
                    if (px1 < 0) px1 = 0; if (px1 > maxX) px1 = maxX;

                    byte* pa = rowA + (long)px0 * bpp;
                    byte* pb = rowB + (long)px0 * bpp;
                    byte* pc = rowA + (long)px1 * bpp;
                    byte* pd = rowB + (long)px1 * bpp;

                    // SubSamples == 2 → 每格 4 个采样点求均值
                    int sum = (Gray(pa) + Gray(pc) + Gray(pb) + Gray(pd)) >> 2;

                    fp[gy * cols + gx] = (byte)sum;
                }
            }
        }

        return fp;
    }

    /// <summary>取一个 BGRA 像素的 BT.601 灰度（(77R+150G+29B)>>8）。</summary>
    private static unsafe int Gray(byte* p)
        => (77 * p[2] + 150 * p[1] + 29 * p[0]) >> 8;

    /// <summary>便利重载：采样整个图像。</summary>
    public static byte[] Compute(SKBitmap bmp)
        => Compute(bmp, null, out _, out _);

    /// <summary>便利重载：带 ROI。</summary>
    public static byte[] Compute(SKBitmap bmp, SKRect? roi)
        => Compute(bmp, roi, out _, out _);

    /// <summary>
    /// 两个指纹的「变化格占比」（0~1）。长度不等时视为 100% 变化。
    ///
    /// 注意：这是**诊断/展示用**指标，不作为变化判据 ——
    /// 占比会随屏幕变大而稀释，同样一个图标在不同分辨率下占比差 10 倍。
    /// 判据请用 <see cref="HasChanged"/>（绝对格数）。
    /// </summary>
    public static double DiffRatio(byte[]? a, byte[]? b,
                                   int blockThreshold = DefaultBlockThreshold)
    {
        if (a == null || b == null) return 1.0;
        if (a.Length != b.Length || a.Length == 0) return 1.0;
        return (double)CountChanged(a, b, blockThreshold) / a.Length;
    }

    /// <summary>
    /// 判断是否「画面有实质变化」（需要重跑 OCR）。
    ///
    /// 判据用**绝对变化格数**，不用占比 —— 这是被实测纠正的：
    /// 一个 32×32 图标在 1600×900 上只占约 6 格 / 5700 格 = 0.11%，
    /// 用占比做判据时"这么小的占比该不该算变化"取决于屏幕大小，无法定阈值。
    /// 而实测噪声底是 **0 格**（灰度阈值 8 把像素抖动全滤掉了），
    /// 最小真实变化是 **4 格**，两者分界干净且与图像尺寸无关。
    /// </summary>
    /// <param name="prev">上一帧指纹（null = 首帧，必定算变化）。</param>
    /// <param name="curr">当前帧指纹。</param>
    /// <param name="minChangedCells">最少变化格数，低于此视为噪声。默认 3。</param>
    public static bool HasChanged(byte[]? prev, byte[] curr, int minChangedCells = 3)
    {
        if (prev == null || curr == null) return true;
        if (prev.Length != curr.Length || prev.Length == 0) return true;

        int changed = CountChanged(prev, curr);
        return changed >= minChangedCells;
    }

    /// <summary>统计灰度差超过 <see cref="DefaultBlockThreshold"/> 的格数。</summary>
    public static int CountChanged(byte[]? a, byte[]? b,
                                   int blockThreshold = DefaultBlockThreshold)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return int.MaxValue;

        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            if (d < 0) d = -d;
            if (d > blockThreshold) changed++;
        }
        return changed;
    }
}
