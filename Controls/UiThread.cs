using System;
using System.Windows.Threading;

namespace Win11Toolbox.Controls;

/// <summary>
/// 把回调切回 UI 线程。
///
/// 背景：右键菜单 / 托盘 / 轻松设置三个页面的操作都放在 Task.Run 里跑（读注册表、写注册表、
/// 重启资源管理器都慢，不能占 UI 线程），但操作过程会回调页面的 Log() 写状态栏。
/// 那个回调是在线程池线程上执行的，直接碰 TextBlock 就会抛
/// 「调用线程无法访问此对象，因为另一个线程拥有该对象」。
///
/// 两个要点：
/// 1) owner 传要改的那个控件本身（例如 StatusLine），不要传页面。
///    DispatcherObject 的 Dispatcher 是构造时绑定的，传控件能保证「要改谁就用谁的线程」，
///    即使页面本身是在别的线程上构造出来的也不会走错路。
/// 2) 日志走 RunSafe：UI 更新失败只记 error.log，不把异常抛回 Task.Run，
///    否则 async void 的点击处理会把它变成「操作失败」弹窗。
/// </summary>
internal static class UiThread
{
    /// <summary>
    /// 已经在 UI 线程就直接执行，否则同步切过去。
    /// 用 Invoke 而不是 BeginInvoke：日志要保序，否则「完成」会插到各条明细前面。
    /// 调用方都是 await Task.Run(...) 的写法，UI 线程没有被阻塞，不会死锁。
    /// </summary>
    public static void Run(DispatcherObject owner, Action action)
    {
        var dispatcher = owner.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>写日志 / 刷状态栏专用：出错只记录，不向调用方抛。</summary>
    public static void RunSafe(DispatcherObject owner, Action action)
    {
        try
        {
            Run(owner, action);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
