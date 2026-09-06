using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WoWSBattleAssistant.Services;

namespace WoWSBattleAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 线程未处理异常：记录日志并提示后继续运行，避免无声闪退
        // （MainWindow 另注册了 AppDomain 兜底写 crash.log，两者互补）
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 后台任务（async void 之外）的未观察异常：仅记录，不阻止进程
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error($"后台任务未处理异常: {args.Exception.Message}");
            args.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var ex = e.Exception;
        AppLog.Error("UI 线程未处理异常", ex);
        try
        {
            MessageBox.Show(
                $"程序遇到未处理的错误：\n{ex.Message}\n\n" +
                "详情已写入应用日志（设置 → 日志），可导出后反馈。",
                "WoWS Battle Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* 弹窗失败不继续抛 */ }
        // 记录并提示后继续运行，不让一个异常把整个程序拖垮
        e.Handled = true;
    }
}
