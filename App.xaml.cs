using System.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using StartupEventArgs = System.Windows.StartupEventArgs;

namespace QuickBoard;

public partial class App : Application
{
    private Mutex mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "QuickBoard.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // 已在运行：通知第一个实例把窗口召出来，而不是弹框
            try { EventWaitHandle.OpenExisting("QuickBoard.ShowSignal")?.Set(); } catch { }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
