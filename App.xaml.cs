using System.Windows;
using System.Windows.Threading;
using ScreenTranslator.Services;

namespace ScreenTranslator;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 日志系统初始化 ----
        // 建立 logs/ 目录、清理超量的历史文件、对异常日志做一次体积滚动。
        // 紧接着记一行「程序启动」，让本次运行的日志文件在启动瞬间就落盘 ——
        // 文件名带本次启动时刻，所以「这次是哪次」一看文件名就知道。
        AppLog.Initialize();
        AppLog.Info($"程序启动（版本 {AppLog.AppVersion}，"
                  + $"{AppLog.OsDescription} / {AppLog.ProcessBitness} / "
                  + $"{(AppLog.IsElevated ? "管理员权限" : "普通权限")}）");

        // ---- 全局异常兜底 ----
        // 以前程序里没有任何全局异常处理，一旦 UI 线程抛异常
        // 就是"窗口一闪就没了"，用户完全不知道发生了什么，也没法反馈。
        // 这里把三类未处理异常统一记录下来并弹窗提示。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("DispatcherUnhandledException", e.Exception);
        ShowError(e.Exception);
        e.Handled = true;   // 尽量不让整个程序退出
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Error("DomainUnhandledException", e.ExceptionObject as Exception);
        // 非 UI 线程的致命异常无法阻止进程退出，只能先记下来
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"程序遇到一个问题（已记录到程序目录下的 logs 文件夹）：\n\n"
                + $"{ex.GetType().Name}: {ex.Message}\n\n"
                + $"日志目录：{AppLog.LogDir}\n\n"
                + "如果功能异常，可点程序里的「导出诊断」按钮，"
                + "它会一次性打包当时的状态、帧错误历史与本次运行的完整日志。",
                "出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 弹窗失败也不能再抛 */ }
    }
}
