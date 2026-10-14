using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace ScreenTranslator.Services;

/// <summary>
/// OCR 识别模型（语言包）管理。
///
/// 背景（这个类为什么存在）：
/// RapidOcrNet 的 NuGet 包内置的是 **PP-OCRv5 latin** 模型 ——
/// 它的识别词典只有 502 个字符、**一个汉字都没有**。
/// 也就是说用默认模型时，界面上的中文（菜单、按钮、提示、中文网页）
/// 在结构上就不可能被识别出来，表现为"很多文字未识别到"。
///
/// 由于用户常在中文 Windows 上看英文资料，界面上中英混排极其常见，
/// 因此必须换成 **中文 PP-OCRv5 识别模型**（词典 18383 字符 / 15565 汉字），
/// 它同时保留拉丁字母识别能力 —— 一个模型覆盖中英混排。
///
/// 识别模型 (16MB) 与词典 (74KB) 太大不适合塞进仓库，
/// 这里实现"按需下载 + SHA256 校验 + 本地缓存"。
/// </summary>
public static class OcrModelManager
{
    /// <summary>模型文件名（下载后放这里）</summary>
    public const string ChineseRecFileName = "ch_PP-OCRv5_rec_mobile.onnx";
    public const string ChineseDictFileName = "ppocrv5_dict.txt";

    /// <summary>模型存放子目录（相对 exe）</summary>
    public const string ModelSubDir = "models/v5";

    private const string RecUrl =
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx";
    private const string DictUrl =
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt";

    /// <summary>备用镜像（modelscope CDN 偶发拒绝时改走 GitHub）</summary>
    private static readonly string[] ExtraRecUrls =
    {
        "https://hf-mirror.com/RapidAI/RapidOCR/resolve/main/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx"
    };

    private const string RecSha256 =
        "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5";

    /// <summary>与主程序同级的模型目录</summary>
    public static string ModelDir =>
        Path.Combine(AppContext.BaseDirectory, ModelSubDir);

    public static string ChineseRecPath => Path.Combine(ModelDir, ChineseRecFileName);
    public static string ChineseDictPath => Path.Combine(ModelDir, ChineseDictFileName);

    /// <summary>中文识别模型是否已就绪（文件存在且大小合理）</summary>
    public static bool IsChineseModelReady()
    {
        try
        {
            var rec = new FileInfo(ChineseRecPath);
            var dict = new FileInfo(ChineseDictPath);
            return rec.Exists && rec.Length > 1_000_000
                && dict.Exists && dict.Length > 10_000;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 中文识别模型缺失时，把中文模型补进模型目录。
    ///
    /// 这里【不覆盖】包自带的 latin 识别模型，而是额外放一份中文模型，
    /// 由 OcrService 优先选用。这样即使下载失败，
    /// latin 模型仍在，程序至少还能识别英文，不会整体不可用。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnsureChineseModelAsync(
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (IsChineseModelReady())
            return (true, "中文识别模型已就绪");

        try
        {
            Directory.CreateDirectory(ModelDir);
        }
        catch (Exception ex)
        {
            return (false, $"无法创建模型目录：{ex.Message}");
        }

        // 不代理：模型站点走直连更稳，也避免本机 HTTP_PROXY 干扰。
        //
        // User-Agent 必须显式设置：modelscope 的大文件会 302 跳到
        // cdn-lfs-cn-1.modelscope.cn，该 CDN 会拒绝 .NET 默认的 UA
        // （返回 403 Forbidden）。带上常规浏览器 UA 才能正常下载。
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        try
        {
            // ---- 1. 识别模型（带 SHA256 校验，先下到临时文件）----
            if (!IsFileUsable(ChineseRecPath, 1_000_000))
            {
                progress?.Invoke("正在下载中文识别模型（约 16MB）…");
                var tmp = ChineseRecPath + ".downloading";
                try
                {
                    await DownloadWithFallbackAsync(http, RecUrl, ExtraRecUrls, tmp, ct)
                        .ConfigureAwait(false);

                    string actual = Sha256Of(tmp);
                    if (!string.Equals(actual, RecSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tmp);
                        return (false, "中文识别模型校验失败（SHA256 不匹配），为安全起见已丢弃。"
                                     + "请检查网络是否被劫持后重试。");
                    }

                    TryDelete(ChineseRecPath);
                    File.Move(tmp, ChineseRecPath);
                }
                catch
                {
                    TryDelete(tmp);
                    throw;
                }
            }

            // ---- 2. 词典（纯文本、体积小，校验"含汉字"这个本质特征）----
            if (!IsFileUsable(ChineseDictPath, 10_000))
            {
                progress?.Invoke("正在下载中文识别词典…");
                await DownloadToFileAsync(http, DictUrl, ChineseDictPath, ct).ConfigureAwait(false);

                if (!DictionaryHasCjk(ChineseDictPath))
                {
                    TryDelete(ChineseDictPath);
                    return (false, "中文词典内容异常（不含汉字），已丢弃。请检查网络。");
                }
            }

            return IsChineseModelReady()
                ? (true, "中文识别模型已就绪")
                : (false, "模型下载完成但文件不完整，请重试");
        }
        catch (OperationCanceledException)
        {
            return (false, "下载已取消");
        }
        catch (Exception ex)
        {
            return (false, $"下载中文识别模型失败：{ex.Message}\n"
                         + $"可手动下载后放到：\n{ModelDir}\n"
                         + $"  文件1：{ChineseRecFileName}\n  文件2：{ChineseDictFileName}");
        }
    }

    private static bool IsFileUsable(string path, long minBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length >= minBytes;
        }
        catch { return false; }
    }

    /// <summary>主地址失败时依次尝试备用镜像，全部失败才抛最后一次异常</summary>
    private static async Task DownloadWithFallbackAsync(
        HttpClient http, string primaryUrl, string[] fallbackUrls,
        string destPath, CancellationToken ct)
    {
        var urls = new List<string> { primaryUrl };
        urls.AddRange(fallbackUrls);

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadToFileAsync(http, url, destPath, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(destPath);
            }
        }
        throw last ?? new InvalidOperationException("无可用下载地址");
    }

    private static async Task DownloadToFileAsync(
        HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // 不要相信 Content-Length（可能被代理伪造），
        // 只按实际写入的字节数与后续校验判断。
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, 81920, ct).ConfigureAwait(false);
        await dst.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    /// <summary>词典里是否存在汉字 —— 这是判断"能否识别中文"的本质特征</summary>
    private static bool DictionaryHasCjk(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                char c = line[0];
                if (c >= 0x4e00 && c <= 0x9fff) return true;
            }
        }
        catch { }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
