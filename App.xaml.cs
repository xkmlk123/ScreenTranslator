using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ScreenTranslator;

public partial class App : System.Windows.Application
{
    /// <summary>异常日志路径（与 exe 同目录，便于用户反馈时直接发送）</summary>
    private static readonly string LogPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 全局异常兜底 ----
        // 以前程序里没有任何全局异常处理，一旦 UI 线程抛异常
        // 就是"窗口一闪就没了"，用户完全不知道发生了什么，也没法反馈。
        // 这里把三类未处理异常统一记录下来并弹窗提示。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("DispatcherUnhandledException", e.Exception);
        ShowError(e.Exception);
        e.Handled = true;   // 尽量不让整个程序退出
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log("DomainUnhandledException", e.ExceptionObject as Exception);
        // 非 UI 线程的致命异常无法阻止进程退出，只能先记下来
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"程序遇到一个问题（已记录到 error.log）：\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                "如果功能异常，可把 error.log 发给我定位。",
                "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 弹窗失败也不能再抛 */ }
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            sb.AppendLine(ex?.ToString() ?? "(null)");
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
        }
        catch { /* 记日志失败无所谓 */ }
    }
}
