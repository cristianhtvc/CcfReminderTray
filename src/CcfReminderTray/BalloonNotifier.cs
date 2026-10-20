using CcfReminder.Core.Reminders;

namespace CcfReminderTray;

/// <summary>
/// 托盘气球通知（NotifyIcon.ShowBalloonTip）。Windows 11 上由系统通知渠道显示，
/// 展示时长由系统辅助功能设置决定。
/// 同步封送到 UI 线程实际执行显示调用并返回结果：
/// true 仅表示“显示请求已提交给 Windows”（代码层成功）；
/// 系统是否弹出横幅（通知开关、请勿打扰）及用户是否看见，应用层无法获知。
/// </summary>
internal sealed class BalloonNotifier : IReminderNotifier
{
    private readonly NotifyIcon _icon;
    private readonly Control _marshaller;

    public BalloonNotifier(NotifyIcon icon, Control marshaller)
    {
        _icon = icon;
        _marshaller = marshaller;
    }

    public bool Show(string title, string message)
    {
        if (_marshaller.IsDisposed || !_marshaller.IsHandleCreated)
        {
            return false; // UI 句柄不可用：显示请求未发出
        }
        try
        {
            void ShowCore() => _icon.ShowBalloonTip(10000, title, message, ToolTipIcon.Info);
            if (_marshaller.InvokeRequired)
            {
                _marshaller.Invoke(ShowCore); // 同步等待显示调用真正执行
            }
            else
            {
                ShowCore();
            }
            return true;
        }
        catch (Exception)
        {
            return false; // 封送失败/显示调用异常：按未发出处理
        }
    }
}
