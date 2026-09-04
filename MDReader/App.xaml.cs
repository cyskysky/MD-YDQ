using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MDReader;

/// <summary>
/// Interaction logic for App.xaml
/// 全局异常兜底（行业最佳实践）：任何未处理异常先写本地日志再友好提示，
/// 避免静默崩溃无从排查。
/// </summary>
public partial class App : Application
{
    public static string CrashLogPath => Path.Combine(
        Path.GetTempPath(), "MD阅读器_崩溃.log");

    public static void LogStage(string stage)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {stage}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try { File.WriteAllText(CrashLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] 启动{Environment.NewLine}"); } catch { }
        DispatcherUnhandledException += OnDispatcherUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTask", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI线程", e.Exception);
        try
        {
            ThemedDialog.Show(System.Windows.Application.Current?.MainWindow, 
                $"程序遇到未处理的错误，详情已写入：\n{CrashLogPath}\n\n{e.Exception.GetType().Name}：{e.Exception.Message}",
                "MD阅读器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true; // 已记录并提示，不直接崩溃，尽量保持可用
        try { Shutdown(); } catch { }
    }

    private static void LogCrash(string where, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] [{where}] {ex?.GetType().FullName}: {ex?.Message}{Environment.NewLine}{ex?.StackTrace}{Environment.NewLine}");
        }
        catch { }
    }
}
