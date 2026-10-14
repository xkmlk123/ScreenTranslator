using System.Windows;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>
/// 识别区域（ROI）的几何换算。
///
/// ---- 为什么单独抽出来 ----
/// ROI 全程只存【归一化比例】（相对捕获区域，0~1），不存绝对像素。
/// 好处是窗口缩放、换窗口、跨屏移动、DPI 变化后，选区都会自动跟着走，
/// 不需要重新框选，也不会出现"上次框的位置现在跑到别处"的错位。
///
/// 代价是每次用都要做一次「比例 ↔ 像素」反算，而这一步是整条坐标链里
/// 最容易写错的地方（越界、退化、取整方向）。抽成纯静态类后可以脱离
/// WPF 界面单独跑测试 —— 之前指纹判据就是靠实测才发现占比会随分辨率稀释。
///
/// ---- 坐标约定 ----
/// 本类只做纯数值换算，不关心物理/虚拟/DIP 的区分：
/// 调用方保证 <c>region</c> 与 <c>screen</c> 处于同一坐标系即可
/// （当前实现里两者都是屏幕物理像素，见 MainWindow.GetCaptureRegionRect）。
/// </summary>
public static class RoiGeometry
{
    /// <summary>
    /// ROI 的最小边长（物理像素）。小于此值没有识别意义，按无效处理。
    /// 同时也用来兜住"点一下没拖"的误触。
    /// </summary>
    public const int MinRoiPx = 8;

    /// <summary>判断「是否等于整窗」时的容差（比例）。</summary>
    private const double Eps = 0.001;

    /// <summary>
    /// 归一化比例 → 当前帧的物理像素矩形。
    /// 每次都用【当前帧尺寸】反算，因此窗口缩放时 ROI 自动跟随。
    /// </summary>
    /// <returns>
    /// 帧内像素矩形；返回 <c>null</c> 表示「没有 ROI」，下游应走完整帧路径。
    /// </returns>
    public static Rect? ToFramePx(AppConfig? cfg, int frameW, int frameH)
    {
        if (cfg?.UseRoi != true || frameW <= 0 || frameH <= 0) return null;

        // 先夹一次：配置文件可能被手改过，或跨版本残留了越界值
        double x = Math.Clamp(cfg.RoiX, 0, 1);
        double y = Math.Clamp(cfg.RoiY, 0, 1);
        double w = Math.Clamp(cfg.RoiW, 0, 1);
        double h = Math.Clamp(cfg.RoiH, 0, 1);

        // 退化成整窗 → 当作没开 ROI，走原完整路径（零回归）
        if (x <= Eps && y <= Eps && w >= 1 - Eps && h >= 1 - Eps) return null;

        double px = Math.Clamp(x * frameW, 0, frameW - 1);
        double py = Math.Clamp(y * frameH, 0, frameH - 1);

        // 宽度只夹【上界】，下界留给下面的有效性检查。
        // 曾经把下界也写成 MinRoiPx，结果过小的选区被悄悄撑大到 8px，
        // "太小就判无效"这条规则形同虚设 —— 由探针实测抓出。
        double pw = Math.Clamp(w * frameW, 0, frameW - px);
        double ph = Math.Clamp(h * frameH, 0, frameH - py);

        // 夹完再判：这样即使起点贴着右下角、被上界夹到只剩几个像素也能正确拒绝
        if (pw < MinRoiPx || ph < MinRoiPx) return null;
        return new Rect(px, py, pw, ph);
    }

    /// <summary>
    /// 帧内 ROI → 整数裁剪矩形（供 SkiaSharp 裁剪用）。
    ///
    /// 取整方向是个坑：左上向下取整会漏掉半列像素，右下向上取整会越界。
    /// 这里统一用「左上 floor + 右下 ceil」再整体夹进帧内，
    /// 保证不越界的同时尽量不漏边，最后再校验最小边长。
    /// </summary>
    /// <returns>裁剪矩形 (Left, Top, Width, Height)；无需裁剪或无效时返回 <c>null</c>。</returns>
    public static (int L, int T, int W, int H)? ToCropRect(int frameW, int frameH, Rect? roi)
    {
        if (roi == null || frameW <= 0 || frameH <= 0) return null;

        var r = roi.Value;
        int l = Math.Clamp((int)Math.Floor(r.Left), 0, frameW - 1);
        int t = Math.Clamp((int)Math.Floor(r.Top), 0, frameH - 1);
        int rr = Math.Clamp((int)Math.Ceiling(r.Right), 0, frameW);
        int bb = Math.Clamp((int)Math.Ceiling(r.Bottom), 0, frameH);

        int w = rr - l;
        int h = bb - t;
        if (w < MinRoiPx || h < MinRoiPx) return null;

        return (l, t, w, h);
    }

    /// <summary>
    /// 屏幕矩形 → 相对捕获区域的归一化比例。
    ///
    /// 先夹到捕获区域内再算比例：这样"框超出窗口边界"会被裁成窗口内那部分，
    /// 而不是算出一个越界的比例（那会让下游反算时整个 ROI 跑飞）。
    /// </summary>
    /// <returns>归一化 (X, Y, W, H)；区域无效或太小/完全在窗口外时返回 <c>null</c>。</returns>
    public static (double X, double Y, double W, double H)? ToNormalized(Rect screen, Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;

        double x0 = Math.Clamp(screen.X - region.X, 0, region.Width);
        double y0 = Math.Clamp(screen.Y - region.Y, 0, region.Height);
        double x1 = Math.Clamp(screen.X - region.X + screen.Width, 0, region.Width);
        double y1 = Math.Clamp(screen.Y - region.Y + screen.Height, 0, region.Height);

        double w = x1 - x0;
        double h = y1 - y0;

        // 太小，或完全落在捕获区域之外 → 视为无效选择
        if (w < MinRoiPx || h < MinRoiPx) return null;

        return (x0 / region.Width, y0 / region.Height, w / region.Width, h / region.Height);
    }

    /// <summary>
    /// 归一化 ROI → 位图像素坐标的 <see cref="SkiaSharp.SKRect"/>。
    /// 供采集层在算指纹时限定采样范围用。
    /// </summary>
    /// <returns>无 ROI 或 ROI 退化时返回 <c>null</c>（表示采样整幅位图）。</returns>
    public static SkiaSharp.SKRect? ToSkRect(SkiaSharp.SKBitmap bmp, Rect? roi)
    {
        if (bmp == null || roi == null) return null;

        var r = roi.Value;
        if (r.Width <= 0 || r.Height <= 0) return null;

        float x = (float)(r.X * bmp.Width);
        float y = (float)(r.Y * bmp.Height);
        float w = (float)(r.Width * bmp.Width);
        float h = (float)(r.Height * bmp.Height);

        // 防御：比例来自配置，可能算出越界矩形，夹回位图范围内。
        // 同 ToFramePx：只夹上界，下界交给有效性检查，否则小选区会被撑大。
        x = Math.Clamp(x, 0, bmp.Width - 1);
        y = Math.Clamp(y, 0, bmp.Height - 1);
        w = Math.Clamp(w, 0, bmp.Width - x);
        h = Math.Clamp(h, 0, bmp.Height - y);

        // 无效 ROI → null，调用方据此回退为「采样整幅位图」
        if (w < MinRoiPx || h < MinRoiPx) return null;
        return new SkiaSharp.SKRect(x, y, x + w, y + h);
    }
}
