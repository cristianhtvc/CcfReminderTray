using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using CcfReminder.Core.Reminders;

namespace CcfReminderTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var singleInstance = new SingleInstance();
        if (!singleInstance.TryBecomePrimary())
        {
            // 已有实例在运行：通知它唤起详情窗，本进程随即退出。
            // 明确告知“本次启动被转发到已在运行的实例”：同名进程（安装版/便携版/开发目录的
            // 多份 exe）在任务管理器中难以区分，若无提示，用户可能以为打开了新 exe，
            // 实际看到的却是另一目录旧实例的窗口，设置状态完全不同。
            // 提示由第二实例自身版本的代码决定：旧版本作为第二实例时不会有此提示。
            // 唤起请求是否送达按实际结果如实表述，不在送达失败时声称已唤起。
            bool delivered = singleInstance.NotifyPrimary();
            string delivery = delivered
                ? "已请求正在运行的实例唤起其窗口。"
                : "未能联系到正在运行的实例（它可能正在退出）；请稍后重试，或从它的托盘图标手动打开窗口。";
            MessageBox.Show(
                "会议截稿提醒已在运行（可能是另一目录下的副本）。\n\n" +
                delivery + "\n本次启动的程序随即退出。\n" +
                "若要以当前这份程序为准，请先从托盘退出正在运行的实例，再重新启动。",
                "会议截稿提醒",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        string appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CcfReminderTray");

        // 数据服务：先载入本地缓存（离线可用），后台刷新由 TrayAppContext 触发
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CcfReminderTray/0.4 (personal deadline reminder)");
        var dataService = new ConferenceDataService(
            http, Path.Combine(appDir, "cache"), () => DateTimeOffset.UtcNow);
        dataService.LoadCache();

        // 偏好与提醒：设置/已提醒状态持久化在应用目录；开机启动只动 HKCU Run 自己的值
        var settingsStore = new SettingsStore(appDir);
        var reminderStateStore = new ReminderStateStore(appDir);
        var autoStart = new AutoStartManager(
            new WindowsRunKeyStore(),
            () => Environment.ProcessPath ??
                Path.Combine(AppContext.BaseDirectory, "CcfReminderTray.exe"));
        var preferences = new PreferencesManager(settingsStore, autoStart);

        // 用户手动修正（PROJECT_BRIEF §4）：独立 versioned overrides.json；
        // 损坏时醒目告警并阻止覆盖，绝不当作空表
        var overrideManager = new DeadlineOverrideManager(
            new OverridesStore(Path.Combine(appDir, "overrides.json")));

        Application.Run(new TrayAppContext(
            singleInstance, dataService, settingsStore, reminderStateStore, preferences,
            overrideManager));
    }
}
