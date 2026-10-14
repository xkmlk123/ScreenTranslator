using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScreenTranslator.Services;

/// <summary>
/// 应用级日志：目录、按时间命名、体积滚动、导出、保留策略。
///
/// <para>
/// 【为什么从 WgcCaptureService 搬出来】日志不是捕获服务的职责 —— 全局异常（App）、
/// 捕获降级（WgcCaptureService）、导出（MainWindow）都要写同一份日志。此前散在捕获
/// 服务里，别的模块只能靠调用一个语义上毫不相干的类来记日志。
/// </para>
///
/// <para>
/// 【命名：运行时按时间保存】运行时日志以<strong>本次进程启动时刻</strong>命名：
/// <c>logs/诊断日志_20261014_152103.txt</c>。一次运行 = 一个文件，带来三个直接好处：
/// ① 文件名本身就是时间，不必在内容里辨认「这是哪一次」；
/// ② 「本次运行」的日志天然就是整个文件，不需要运行标记去切分；
/// ③ 跨天的长时间运行不会把一次运行的日志劈成两个文件。
/// </para>
///
/// <para>
/// 【体积滚动】单文件超过上限时从<strong>头部丢弃最旧内容</strong>（旧的被新的覆盖），
/// 而不是整文件清空 —— 清空会把崩溃前刚写的那几行一起抹掉，等于白记。
/// </para>
///
/// <para>
/// 【导出不覆盖既有内容，但只留最新一份】导出写到独立的
/// <c>logs/导出诊断_&lt;导出时刻&gt;.txt</c>：<strong>每次导出都新建文件</strong>，
/// 绝不改写或重置任何既有文件的内容；写完后再删掉上一次的导出文件
/// （<see cref="DeletePreviousExports"/>），因此导出目录里始终只有最新那一份。
/// 运行时日志是证据链，跨运行保留，不受此影响。
/// </para>
/// </summary>
public static class AppLog
{
    // =====================================================================
    //  目录与命名
    // =====================================================================

    /// <summary>日志目录（与 exe 同级的 <c>logs/</c>，首次写入时自动创建）。</summary>
    public static string LogDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    /// <summary>运行时诊断日志的文件名前缀。</summary>
    public const string DiagFilePrefix = "诊断日志_";

    /// <summary>导出诊断的文件名前缀。</summary>
    public const string ExportFilePrefix = "导出诊断_";

    /// <summary>全局异常日志的文件名（滚动累积的单文件，不带时间戳）。</summary>
    public const string ErrorFileName = "error.log";

    /// <summary>调试截图的文件名（仅在诊断时落盘一次）。</summary>
    public const string DebugFrameFileName = "debug_frame.png";

    // =====================================================================
    //  体积上限：旧的被新的覆盖
    // =====================================================================

    /// <summary>运行时日志上限（字节）。超过后从头部丢弃最旧内容。</summary>
    private const long DiagMaxBytes = 1024 * 1024;      // 1 MB

    /// <summary>裁剪后保留的尾部字节数。与上限留出 256KB 迟滞，避免每写一行都裁一次。</summary>
    private const long DiagKeepBytes = 768 * 1024;      // 768 KB

    /// <summary>全局异常日志上限（字节）。</summary>
    private const long ErrorMaxBytes = 512 * 1024;      // 512 KB

    /// <summary>全局异常日志裁剪后保留的尾部字节数。</summary>
    private const long ErrorKeepBytes = 384 * 1024;     // 384 KB

    /// <summary>
    /// <c>logs/</c> 下每类文件保留的最大个数，超出的按修改时间删除最旧的。
    ///
    /// <para>只对<b>运行时日志</b>有实际意义（一次运行一个文件，需要留几份历史）。
    /// 导出文件在导出时就被 <see cref="DeletePreviousExports"/> 收敛到只剩最新一份，
    /// 这里对导出仅作为兜底（正常情况下永远不会触发）。</para>
    /// </summary>
    private const int KeepRecentFiles = 20;

    // =====================================================================
    //  运行身份
    // =====================================================================

    /// <summary>本次进程的启动时刻，全生命周期只取一次。</summary>
    public static readonly DateTime RunStartedAt = DateTime.Now;

    /// <summary>本次运行已持续的时长。</summary>
    public static TimeSpan RunElapsed => DateTime.Now - RunStartedAt;

    private static readonly object Gate = new();
    private static int _diagHeaderWritten;

    /// <summary>本次运行的运行时日志文件名（不含目录）。</summary>
    public static readonly string DiagFileName =
        BuildUniqueName(DiagFilePrefix, RunStartedAt, "yyyyMMdd_HHmmss");

    private static readonly string _exportStampFormat = "yyyyMMdd_HHmmss";

    /// <summary>运行时日志的完整路径。</summary>
    public static string DiagLogPath => Path.Combine(LogDir, DiagFileName);

    /// <summary>全局异常日志的完整路径。</summary>
    public static string ErrorLogPath => Path.Combine(LogDir, ErrorFileName);

    /// <summary>调试截图的完整路径。</summary>
    public static string DebugFramePath => Path.Combine(LogDir, DebugFrameFileName);

    // =====================================================================
    //  环境信息（供日志头与导出头使用）
    // =====================================================================

    /// <summary>程序版本（取不到时返回「未知」，绝不抛）。</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                return asm.GetName().Version?.ToString() ?? "未知";
            }
            catch { return "未知"; }
        }
    }

    /// <summary>系统描述，例如 <c>Microsoft Windows 10.0.26100</c>。</summary>
    public static string OsDescription
    {
        get
        {
            try { return RuntimeInformation.OSDescription; }
            catch { return Environment.OSVersion.VersionString; }
        }
    }

    /// <summary>.NET 运行时描述。</summary>
    public static string FrameworkDescription
    {
        get
        {
            try { return RuntimeInformation.FrameworkDescription; }
            catch { return "未知"; }
        }
    }

    /// <summary>进程位数标记。</summary>
    public static string ProcessBitness => Environment.Is64BitProcess ? "x64" : "x86";

    private static bool? _isElevated;

    /// <summary>当前进程是否以管理员身份运行（结果缓存）。</summary>
    public static bool IsElevated
    {
        get
        {
            if (_isElevated.HasValue) return _isElevated.Value;
            bool value;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                value = principal.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { value = false; }
            _isElevated = value;
            return value;
        }
    }

    // =====================================================================
    //  初始化与保留策略
    // =====================================================================

    /// <summary>
    /// 启动时调用一次：建目录、清理超量的历史文件、对异常日志做一次滚动。
    /// 任何失败都静默忽略（日志系统不能成为新的故障源）。
    /// </summary>
    public static void Initialize()
    {
        try { Directory.CreateDirectory(LogDir); } catch { }
        RetainRecentFiles();
        try
        {
            lock (Gate)
                TrimLocked(ErrorLogPath, ErrorMaxBytes, ErrorKeepBytes,
                    $"（更早的异常记录已超出 {ErrorMaxBytes / 1024} KB 上限被丢弃）");
        }
        catch { }
    }

    /// <summary>
    /// 按修改时间保留每类文件最新的 <see cref="KeepRecentFiles"/> 个，更旧的删除。
    ///
    /// <para>
    /// <strong>只清理本程序自己生成的文件</strong>：限定在 <c>logs/</c> 目录内、
    /// 文件名形如<strong>固定前缀 + <c>yyyyMMdd_HHmmss</c>（可选 <c>-N</c>）+ <c>.txt</c></strong>。
    /// 仅靠前缀不足以判断归属 —— 用户手工建的 <c>诊断日志_重要.txt</c> 前缀一模一样，
    /// 见了前缀就删会误伤。任何不匹配的文件一律不碰。
    /// </para>
    /// </summary>
    public static void RetainRecentFiles()
    {
        RetainByPrefix(DiagFilePrefix);
        RetainByPrefix(ExportFilePrefix);
    }

    private static void RetainByPrefix(string prefix)
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            var files = Directory.GetFiles(LogDir, prefix + "*.txt", SearchOption.TopDirectoryOnly);
            if (files.Length <= KeepRecentFiles) return;

            var ordered = files
                .Select(f => new FileInfo(f))
                .Where(fi => IsGeneratedLogName(fi.Name, prefix))
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .ToList();

            for (int i = KeepRecentFiles; i < ordered.Count; i++)
            {
                // 绝不删本次运行正在写的文件（时钟回拨、并发实例都可能让它在排序里变旧）
                if (IsProtected(ordered[i].FullName)) continue;
                try { ordered[i].Delete(); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 文件名是否为本程序生成的日志：<c>prefix + yyyyMMdd_HHmmss</c>（可选 <c>-N</c>）+ <c>.txt</c>。
    ///
    /// <para>
    /// 这是「能不能删」的唯一判据，因此刻意写得保守：形状只要有一点不符就判为「不是我们的」。
    /// 宁可漏删，不可误删。
    /// </para>
    /// </summary>
    private static bool IsGeneratedLogName(string fileName, string prefix)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;

        // 取掉前缀与 ".txt"：应为 20261014_160000 或 20261014_160000-2
        string core = fileName[prefix.Length..^4];
        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            string suffix = core[(dash + 1)..];
            if (suffix.Length == 0) return false;
            foreach (char c in suffix)
                if (c < '0' || c > '9') return false;
            core = core[..dash];
        }

        if (core.Length != 15 || core[8] != '_') return false;
        for (int i = 0; i < core.Length; i++)
        {
            if (i == 8) continue;
            char c = core[i];
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    /// <summary>本进程正在使用的日志文件，任何情况下都不参与清理。</summary>
    private static bool IsProtected(string fullPath)
    {
        try
        {
            return string.Equals(fullPath, DiagLogPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, ErrorLogPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, DebugFramePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // =====================================================================
    //  运行时日志
    // =====================================================================

    /// <summary>「更早内容已被丢弃」时补写的文件身份行。</summary>
    private static string DiagTrimHeaderLine() =>
        $"{DiagFileName} ｜ 本次运行开始 {RunStartedAt:yyyy-MM-dd HH:mm:ss}"
        + $" ｜（更早内容已超出 {DiagMaxBytes / 1024} KB 上限被丢弃）";

    /// <summary>
    /// 追加一行到运行时诊断日志，自动补 <c>[HH:mm:ss]</c> 前缀。
    /// 本进程第一次写入时先落一段文件头（文件身份 + 程序版本 + 系统环境）。
    /// </summary>
    public static void Info(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                WriteDiagHeaderLocked();
                File.AppendAllText(DiagLogPath,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
                TrimLocked(DiagLogPath, DiagMaxBytes, DiagKeepBytes, DiagTrimHeaderLine());
            }
        }
        catch
        {
            // 记日志失败不应影响主流程，静默忽略。
        }
    }

    /// <summary>本进程首次写日志时落一段文件头（调用方已持锁）。</summary>
    private static void WriteDiagHeaderLocked()
    {
        if (Interlocked.Exchange(ref _diagHeaderWritten, 1) == 1) return;

        var sb = new StringBuilder();
        sb.AppendLine("==== 诊断日志 ====");
        sb.AppendLine($"文件        ：{DiagFileName}");
        sb.AppendLine($"本次运行开始：{RunStartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序版本    ：{AppVersion}");
        sb.AppendLine($"系统        ：{OsDescription} / {ProcessBitness} / "
                    + $"{(IsElevated ? "管理员权限" : "普通权限")}");
        sb.AppendLine($".NET        ：{FrameworkDescription}");
        sb.AppendLine("---- 以下为运行日志 ----");

        File.AppendAllText(DiagLogPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 读取运行时日志全文（永不抛异常，读不到或出错一律返回空串）。
    ///
    /// <para>
    /// 因为日志文件按运行会话命名，<strong>整个文件就是「本次运行」</strong>，
    /// 不需要再按标记切分。共享模式放开 <c>ReadWrite</c>：用户可能正用记事本开着它看。
    /// </para>
    /// </summary>
    public static string ReadDiagLog()
    {
        try
        {
            lock (Gate)
            {
                var path = DiagLogPath;
                if (!File.Exists(path)) return "";

                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(
                    fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
        }
        catch
        {
            return "";
        }
    }

    // =====================================================================
    //  导出
    // =====================================================================

    /// <summary>
    /// 为本次导出生成一个<strong>尚不存在</strong>的文件路径：
    /// <c>logs/导出诊断_20261014_155312.txt</c>。同一秒内重复导出会顺延为 <c>-2</c>、<c>-3</c>…
    ///
    /// <para>关键点：导出永远是<strong>新建文件</strong>，绝不改写或重置任何既有文件；
    /// 写完后由 <see cref="DeletePreviousExports"/> 删掉上一次的导出，只留最新一份。</para>
    /// </summary>
    public static string NewExportPath()
    {
        var now = DateTime.Now;
        try { Directory.CreateDirectory(LogDir); } catch { }
        return Path.Combine(LogDir,
            BuildUniqueName(ExportFilePrefix, now, _exportStampFormat));
    }

    /// <summary>
    /// 删除 <c>logs/</c> 下**除本次导出之外**的全部导出诊断文件，使导出始终只保留最新一份。
    ///
    /// <para>
    /// 导出文件的定位是「当前状态的一份快照」，新的一次导出即取代上一次；
    /// 攒下多份历史导出只会让人分不清哪份是新的。运行时日志
    /// （<c>诊断日志_*.txt</c>）<strong>不受影响</strong> —— 那是要跨运行保留的证据链。
    /// </para>
    ///
    /// <para>
    /// 必须传 <paramref name="keepPath"/>（调用方先落好新文件再调用）：
    /// 保证任何时刻磁盘上至少有一份导出，不会出现「旧的删了、新的没写成」的空窗。
    /// </para>
    ///
    /// <para>
    /// 只匹配本程序自己生成的文件：文件名必须是 <c>前缀 + yyyyMMdd_HHmmss</c>
    /// （可选 <c>-N</c> 顺延号）+ <c>.txt</c>。**光有前缀不够** ——
    /// 用户自己建的 <c>导出诊断_备份.txt</c>、<c>诊断日志_重要.txt</c> 同样是这个前缀，
    /// 绝不能因为前缀像就被删掉。其他文件一律不碰。
    /// </para>
    ///
    /// <para>
    /// 上一份导出若正被记事本打开（持有写锁），删除会失败 —— 此时静默保留，下次导出再删。
    /// 清理永远不能反过来把导出流程搞失败。
    /// </para>
    /// </summary>
    /// <returns>实际删除的文件数（供调用方展示/测试用）。</returns>
    public static int DeletePreviousExports(string keepPath)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(LogDir)) return 0;
            string keepFull = Path.GetFullPath(keepPath);

            foreach (var f in Directory.GetFiles(
                         LogDir, ExportFilePrefix + "*.txt", SearchOption.TopDirectoryOnly))
            {
                var fi = new FileInfo(f);
                if (!IsGeneratedLogName(fi.Name, ExportFilePrefix)) continue;
                if (string.Equals(fi.FullName, keepFull, StringComparison.OrdinalIgnoreCase)) continue;

                try { fi.Delete(); deleted++; } catch { }
            }
        }
        catch { }
        return deleted;
    }

    // =====================================================================
    //  全局异常日志
    // =====================================================================

    /// <summary>
    /// 记录一次未处理异常（含完整堆栈）到 <c>logs/error.log</c>，超限时同样滚动丢弃最旧内容。
    /// </summary>
    public static void Error(string source, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
                sb.AppendLine(ex?.ToString() ?? "(null)");
                sb.AppendLine(new string('-', 60));
                File.AppendAllText(ErrorLogPath, sb.ToString(), Encoding.UTF8);
                TrimLocked(ErrorLogPath, ErrorMaxBytes, ErrorKeepBytes,
                    $"（更早的异常记录已超出 {ErrorMaxBytes / 1024} KB 上限被丢弃）");
            }
        }
        catch
        {
            // 记日志失败无所谓。
        }
    }

    // =====================================================================
    //  内部工具
    // =====================================================================

    /// <summary>
    /// 在 <c>logs/</c> 下取一个不存在的文件名。进程启动时刻精确到秒，
    /// 同秒启动两个实例会撞名 —— 此时顺延 <c>-2</c>、<c>-3</c>… 保证各写各的文件
    /// （不共用文件才不会因共享冲突丢日志）。
    /// </summary>
    private static string BuildUniqueName(string prefix, DateTime stamp, string format)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string core = stamp.ToString(format);
            for (int i = 1; i <= 99; i++)
            {
                string name = i == 1 ? $"{prefix}{core}.txt" : $"{prefix}{core}-{i}.txt";
                if (!File.Exists(Path.Combine(LogDir, name))) return name;
            }
            return $"{prefix}{stamp:yyyyMMdd_HHmmss_fff}.txt";
        }
        catch
        {
            return $"{prefix}{stamp:yyyyMMdd_HHmmss}.txt";
        }
    }

    /// <summary>
    /// 超过上限则从头部丢弃最旧内容，只保留尾部 <paramref name="keepBytes"/> 字节。
    ///
    /// <para>
    /// 【必须按字节处理】日志几乎全是中文，UTF-8 下一个汉字占 3 字节 —— 早期版本用
    /// <c>string.Length</c>（字符数）与字节阈值比较，文件字节数到 2.2MB 时字符数才约 750K，
    /// 低于保留阈值，裁剪被整段跳过，日志于是无限增长。
    /// </para>
    ///
    /// <para>
    /// 【截断点对齐到「条目」而非仅到行】候选行首若是<strong>续行</strong>
    /// （以空格 / 制表符起头），继续前移。否则两行式条目（<c>帧错误：…</c> 加
    /// <c>    到达=…</c>）会被截成只剩续行的孤儿行。
    /// </para>
    /// </summary>
    private static void TrimLocked(string path, long maxBytes, long keepBytes, string headerLine)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length <= maxBytes) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return; }
        if (bytes.Length <= keepBytes) return;

        int cut = bytes.Length - (int)keepBytes;
        if (cut < 0) return;

        // 第一个 '\n' 之后即行首，保证不会断在多字节字符中间
        int nl = Array.IndexOf(bytes, (byte)'\n', cut);
        if (nl < 0) return;
        int start = nl + 1;

        for (int guard = 0; guard < 64 && start < bytes.Length; guard++)
        {
            byte first = bytes[start];
            if (first != (byte)' ' && first != (byte)'\t') break;   // 是正常行首，停
            int next = Array.IndexOf(bytes, (byte)'\n', start);
            if (next < 0) break;
            start = next + 1;
        }

        if (start >= bytes.Length) return;
        int tailLength = bytes.Length - start;
        if (tailLength > keepBytes) return;

        string kept = Encoding.UTF8.GetString(bytes, start, tailLength);
        var head = new StringBuilder();
        head.AppendLine(headerLine);
        File.WriteAllText(path, head.ToString() + kept, Encoding.UTF8);
    }

    /// <summary>把时长格式化成「x 分 y 秒」/「x 小时 y 分 z 秒」。</summary>
    public static string FormatElapsed(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分 {t.Seconds} 秒"
            : $"{t.Minutes} 分 {t.Seconds} 秒";
}
