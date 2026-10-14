using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScreenTranslator.Models;

namespace ScreenTranslator.Services;

/// <summary>
/// 大模型 API 翻译服务（OpenAI 兼容协议）。
///
/// 相比早期版本的关键改动：
/// 1. 批量协议由"用 | 分隔"改为"编号行 + JSON 数组"，避免译文本身含分隔符导致错位；
/// 2. 严格回填：条数不匹配时逐条重试，保证永远不会出现"张冠李戴"的译文；
/// 3. 本地小模型改为有限并发的逐条翻译，速度比纯串行快数倍；
/// 4. 空文本/已含目标语言/纯符号的内容直接跳过，不浪费 token。
/// </summary>
public class TranslationService
{
    /// <summary>云端用：跟随系统代理（读 HTTP_PROXY/HTTPS_PROXY 环境变量）</summary>
    private static readonly HttpClient Http = CreateClient(useProxy: true);

    /// <summary>
    /// 本地用：【强制不走代理】。
    ///
    /// 这是"无法连接 Ollama"的根因修复：很多用户机器上开着
    /// HTTP_PROXY/HTTPS_PROXY（加速器/代理客户端），.NET 的 HttpClient
    /// 默认会读这些环境变量、把请求发给代理；代理再连 localhost:11434
    /// 往往失败并返回 502，表现为"Cannot connect to Ollama"，
    /// 而 curl 却正常（curl 默认不对回环地址走代理）。
    /// 本地服务本就不该走代理，这里显式禁用。
    /// </summary>
    private static readonly HttpClient HttpNoProxy = CreateClient(useProxy: false);

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        if (!useProxy)
        {
            // 彻底禁用代理：既不看环境变量，也不用系统代理
            handler.UseProxy = false;
            handler.Proxy = null;
        }

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>按目标地址挑选合适的 HttpClient：本地地址走无代理通道</summary>
    private static HttpClient Pick(string baseUrl)
        => IsLocalAddress(baseUrl) ? HttpNoProxy : Http;

    /// <summary>判断是否为本地/回环地址（这些地址必须直连，不能走代理）</summary>
    private static bool IsLocalAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            string host = uri.Host;
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1"
                || host == "0.0.0.0"
                || host.StartsWith("127.", StringComparison.Ordinal)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>批量翻译（保留原方法签名，内部走段落协议）</summary>
    public Task<List<string>> TranslateAsync(AppConfig config, List<string> texts, int timeoutMs = 20000)
        => TranslateAsync(config, texts, null, timeoutMs);

    /// <summary>
    /// 批量翻译。progress 回调用于回报进度（已完成数/总数）。
    /// </summary>
    public async Task<List<string>> TranslateAsync(
        AppConfig config,
        List<string> texts,
        Action<int, int>? progress,
        int timeoutMs = 20000)
    {
        if (texts == null || texts.Count == 0) return texts ?? new List<string>();

        bool isLocal = config.TranslationMode == "Local";
        string baseUrl = isLocal ? config.LocalApiUrl : config.ApiBaseUrl;
        string model = isLocal ? config.LocalModel : config.Model;
        string apiKey = config.ApiKey;

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return texts;
        if (!isLocal && string.IsNullOrWhiteSpace(apiKey))
            return texts;

        if (isLocal)
            return await TranslateIndividuallyAsync(baseUrl, model, apiKey, config, texts, timeoutMs, progress);
        return await TranslateBatchAsync(baseUrl, model, apiKey, config, texts, timeoutMs, progress);
    }

    // ------------------------------------------------------------------
    // 本地模式：逐条翻译 + 有限并发
    // ------------------------------------------------------------------

    private async Task<List<string>> TranslateIndividuallyAsync(
        string baseUrl, string model, string apiKey, AppConfig config,
        List<string> texts, int timeoutMs, Action<int, int>? progress)
    {
        var results = new string[texts.Count];
        int done = 0;
        int concurrency = Math.Clamp(config.LocalConcurrency, 1, 8);
        using var semaphore = new SemaphoreSlim(concurrency);

        var tasks = texts.Select(async (text, i) =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                results[i] = await TranslateOneAsync(baseUrl, model, apiKey, config, text, timeoutMs)
                    .ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
                int d = Interlocked.Increment(ref done);
                progress?.Invoke(d, texts.Count);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>
    /// 单条翻译（本地模式用，指令极简）。
    ///
    /// 关键改动：本地模型（Ollama）首次调用可能需要把模型加载进显存，
    /// 期间会出现"连接被拒绝/超时"。这里加入【失败自动重试 + 指数退避】，
    /// 并把最后一次的错误记录下来（LastError），供界面显示，
    /// 不再像以前那样把异常静默吞掉、只返回原文。
    /// </summary>
    public async Task<string> TranslateOneAsync(
        string baseUrl, string model, string apiKey, AppConfig config, string text, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var sourceLang = config.SourceLang == "自动检测" ? "" : config.SourceLang;
        var userPrompt = string.IsNullOrEmpty(sourceLang)
            ? $"把下面内容翻译成{config.TargetLang}，只输出译文，不要解释，不要加引号：\n{text}"
            : $"把下面的{sourceLang}翻译成{config.TargetLang}，只输出译文，不要解释，不要加引号：\n{text}";

        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "你是专业翻译器，只输出译文本身。" },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.2,
            stream = false
        };

        // 首次请求给足时间（本地模型可能要加载），并做 3 次指数退避重试
        int perTryTimeout = Math.Max(timeoutMs, 30000);
        Exception? lastEx = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string? content = await PostChatAsync(baseUrl, model, apiKey, body, perTryTimeout)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    LastError = null;
                    return Cleanup(content);
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
                // 前两次失败后短暂等待再试（Ollama 正在加载模型时等它一下）
                if (attempt < 2)
                    await Task.Delay(800 * (attempt + 1)).ConfigureAwait(false);
            }
        }

        LastError = DescribeLocalError(baseUrl, lastEx);
        return text;
    }

    /// <summary>把底层异常翻译成人能看懂的中文提示，便于定位</summary>
    private static string DescribeLocalError(string baseUrl, Exception? ex)
    {
        if (ex == null) return "模型未返回内容";

        if (ex is TaskCanceledException or OperationCanceledException)
            return $"请求超时（{baseUrl}）—— 本地模型可能正在加载，请稍等或换更小的模型";
        if (ex is HttpRequestException http)
        {
            string msg = http.Message;
            if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("No connection", StringComparison.OrdinalIgnoreCase))
                return $"无法连接 Ollama（{baseUrl}）—— 请确认 Ollama 已启动";
            return $"网络错误：{msg}";
        }
        return ex.Message;
    }

    /// <summary>最后一次翻译失败的原因（null 表示上次成功）。供界面显示诊断信息。</summary>
    public string? LastError { get; private set; }

    /// <summary>探测本地服务是否可达（用于启动前预检，避免"连不上"却无提示）</summary>
    public static async Task<(bool Ok, string Message)> ProbeLocalAsync(string baseUrl, string model)
    {
        try
        {
            // 本地地址走无代理通道（否则会被 HTTP_PROXY 环境变量劫持成 502）
            var client = Pick(baseUrl);
            using var cts = new CancellationTokenSource(6000);
            using var resp = await client.GetAsync($"{baseUrl.TrimEnd('/')}/models", cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, $"Ollama 无响应（HTTP {(int)resp.StatusCode}），请检查服务是否已启动");

            string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            // 严格校验：必须能解析出 Ollama 的模型列表结构才算真正连通。
            // 只看 HTTP 状态码不够 —— 某些代理会对任意地址返回 200，
            // 造成"预检说正常、实际翻译却失败"的假阳性。
            try
            {
                var obj = JObject.Parse(json);
                var data = obj["data"];
                if (data is not JArray arr)
                    return (false, $"地址 {baseUrl} 返回的不是 Ollama 接口（可能被代理/其他服务拦截）");

                var names = arr.Select(t => t["id"]?.ToString() ?? "").Where(n => n.Length > 0).ToList();
                if (names.Count > 0
                    && !names.Any(n => n.StartsWith(model, StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, $"模型「{model}」未安装。已装：" + string.Join(", ", names.Take(5)));
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return (false, $"地址 {baseUrl} 返回内容无法解析（可能被代理拦截）");
            }

            return (true, "Ollama 连接正常");
        }
        catch (OperationCanceledException)
        {
            return (false, $"连接 Ollama 超时（{baseUrl}），请确认服务已启动");
        }
        catch (Exception ex)
        {
            return (false, $"无法连接 Ollama（{baseUrl}）：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 云端模式：编号协议批量翻译
    // ------------------------------------------------------------------

    private async Task<List<string>> TranslateBatchAsync(
        string baseUrl, string model, string apiKey, AppConfig config,
        List<string> texts, int timeoutMs, Action<int, int>? progress)
    {
        // 逐条判断是否需要翻译，能跳过的直接跳过
        var work = new List<(int Index, string Text)>();
        var output = new List<string>(texts);
        for (int i = 0; i < texts.Count; i++)
        {
            if (ShouldSkip(texts[i], config))
            {
                output[i] = texts[i];
                continue;
            }
            work.Add((i, texts[i]));
        }

        if (work.Count == 0)
        {
            progress?.Invoke(texts.Count, texts.Count);
            return output;
        }

        // 编号行协议：每行 "行号\t原文"，模型返回 JSON 数组
        var sb = new StringBuilder();
        for (int k = 0; k < work.Count; k++)
            sb.Append(k + 1).Append('\t').Append(work[k].Text.Replace('\n', ' ')).Append('\n');

        var userPrompt =
            $"把下面每一行翻译成{config.TargetLang}。每行开头的编号必须保留但不要翻译。\n" +
            "严格返回一个 JSON 数组，元素为每行对应的译文，顺序与编号一致，" +
            "不要输出任何多余文字、解释或 Markdown 代码块：\n\n" + sb;

        // 用 JObject 构造请求体：部分服务（如 Ollama、旧版网关）不支持 response_format，
        // 需要能在重试时去掉该字段。
        JObject BuildBody(bool withResponseFormat)
        {
            var jo = new JObject
            {
                ["model"] = model,
                ["temperature"] = 0.2,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "system",
                        ["content"] = "你是专业翻译器。只输出一个 JSON 字符串数组，数组长度必须与输入行数完全一致，" +
                                      "不要输出任何其他内容。"
                    },
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                }
            };
            if (withResponseFormat)
                jo["response_format"] = new JObject { ["type"] = "json_object" };
            return jo;
        }

        List<string>? translated = null;
        bool withResponseFormat = true;

        for (int attempt = 0; attempt < 2 && translated == null; attempt++)
        {
            try
            {
                string? content = await PostChatAsync(baseUrl, model, apiKey, BuildBody(withResponseFormat), timeoutMs)
                    .ConfigureAwait(false);
                translated = ParseTranslatedArray(content, work.Count);

                // 解析失败 → 去掉 response_format 再试一次
                if (translated == null) withResponseFormat = false;
            }
            catch
            {
                withResponseFormat = false;
            }
        }

        if (translated != null && translated.Count == work.Count)
        {
            for (int k = 0; k < work.Count; k++)
                output[work[k].Index] = translated[k];
            progress?.Invoke(texts.Count, texts.Count);
            return output;
        }

        // 批量失败 → 逐条兜底，保证顺序与内容正确
        int done = 0;
        foreach (var item in work)
        {
            output[item.Index] = await TranslateOneAsync(baseUrl, model, apiKey, config, item.Text, timeoutMs)
                .ConfigureAwait(false);
            progress?.Invoke(++done, work.Count);
        }
        return output;
    }

    /// <summary>解析模型返回的译文数组，容错多种返回形态；长度不符返回 null</summary>
    private static List<string>? ParseTranslatedArray(string? content, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string text = StripCodeFence(content.Trim());

        // 1) 直接是数组
        var list = TryParseJsonArray(text);
        if (list != null) return NormalizeLength(list, expectedCount);

        // 2) 包在 {"translations": [...]} 之类的对象里
        try
        {
            var jo = JObject.Parse(text);
            foreach (var prop in jo.Properties())
            {
                if (prop.Value is JArray arr)
                {
                    list = arr.Select(t => t.ToString().Trim()).ToList();
                    var norm = NormalizeLength(list, expectedCount);
                    if (norm != null) return norm;
                }
            }
        }
        catch { /* 继续尝试其他形式 */ }

        return null;
    }

    private static List<string>? TryParseJsonArray(string text)
    {
        try
        {
            var arr = JArray.Parse(text);
            return arr.Select(t => t.ToString().Trim()).ToList();
        }
        catch
        {
            // 尝试截取第一个 [ 到最后一个 ]
            int start = text.IndexOf('[');
            int end = text.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                try
                {
                    var arr = JArray.Parse(text.Substring(start, end - start + 1));
                    return arr.Select(t => t.ToString().Trim()).ToList();
                }
                catch { }
            }
            return null;
        }
    }

    private static List<string>? NormalizeLength(List<string> list, int expected)
        => list.Count == expected ? list : null;

    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```")) return text;
        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0) return text;
        int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline) return text;
        return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    /// <summary>去掉模型偶尔加上的引号/前缀说明</summary>
    private static string Cleanup(string text)
    {
        string s = text.Trim();
        s = Regex.Replace(s, @"^(译文|翻译)\s*[:：]\s*", "");
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '“' && s[^1] == '”') || (s[0] == '\'' && s[^1] == '\'')))
        {
            s = s.Substring(1, s.Length - 2);
        }
        return s.Trim();
    }

    /// <summary>判断该段是否不需要翻译（空、纯符号数字、已含目标语言）</summary>
    private static bool ShouldSkip(string text, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        string t = text.Trim();
        if (t.Length <= 1) return true;

        // 纯符号/数字/空白
        bool hasLetter = false;
        foreach (char c in t)
        {
            if (char.IsLetter(c)) { hasLetter = true; break; }
        }
        if (!hasLetter) return true;

        // 目标语言是中文，且文本里中文字符占多数 → 已经是中文，不必翻译
        if (config.TargetLang.Contains("中文") || config.TargetLang.Contains("Chinese"))
            return IsMostlyChinese(t);

        return false;
    }

    private static bool IsMostlyChinese(string text)
    {
        int cjk = 0, letters = 0;
        foreach (char c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff') cjk++;
            else if (char.IsLetter(c)) letters++;
        }
        // 含中文且中文字符数 >= 拉丁字母数 → 认为已是中文
        return cjk > 0 && cjk >= letters;
    }

    /// <summary>统一的 chat/completions 请求（自动按地址选代理/直连通道）</summary>
    private static async Task<string?> PostChatAsync(
        string baseUrl, string model, string apiKey, object body, int timeoutMs)
    {
        var client = Pick(baseUrl);
        using var cts = new CancellationTokenSource(Math.Max(1000, timeoutMs));
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        req.Content = new StringContent(
            JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        var obj = JObject.Parse(json);
        return obj["choices"]?[0]?["message"]?["content"]?.ToString();
    }
}
