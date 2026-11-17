namespace ADBDrive.Services;

/// <summary>
/// 单实例 + 跨进程唤醒。
/// 命名 Mutex 判断是否已有实例；命名事件让第二个实例通知第一个实例把窗口拉到前台。
/// 全部使用 Windows 命名内核对象，NativeAOT 下可用。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\ADBDrive.SingleInstance";
    private const string EventName = @"Local\ADBDrive.ShowWindow";

    // 持有句柄直到进程退出；否则命名对象会在 GC 时被关闭，第二个实例就找不到事件了。
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    /// <summary>返回 true 表示本进程是第一个实例，可以正常启动界面。</summary>
    public static bool Acquire()
    {
        try
        {
            // 先建事件再抢 Mutex：保证输掉 Mutex 的那个实例一定能 Set 到同一个事件。
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        }
        catch
        {
            _showEvent = null;
        }

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            return createdNew;
        }
        catch
        {
            // 建 Mutex 失败时放行，宁可多开也不要让程序起不来。
            return true;
        }
    }

    /// <summary>第二个实例调用：唤醒已运行的实例显示窗口。</summary>
    public static void NotifyExistingInstance()
    {
        try
        {
            var evt = _showEvent
                ?? new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            evt.Set();
        }
        catch
        {
            // 唤醒失败就静默退出，不再弹窗打扰。
        }
    }

    /// <summary>
    /// 第一个实例调用：后台等待唤醒事件，收到后回调。
    /// 回调发生在后台线程，调用方需自行切回 UI 线程。
    /// </summary>
    public static void Listen(Action onShowRequested)
    {
        var evt = _showEvent;
        if (evt is null)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!evt.WaitOne())
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                onShowRequested();
            }
        })
        {
            IsBackground = true,
            Name = "ADBDrive.ShowWindowListener",
        };
        thread.Start();
    }
}
