namespace CcfReminder.Core.Reminders;

/// <summary>
/// 提醒与偏好设置。两项开关默认均关闭，由用户在设置窗主动打开；
/// 开机启动的真实状态以注册表 Run 键为准，此处的 AutoStartEnabled 只是镜像。
/// </summary>
public sealed record ReminderSettings(
    bool NotificationsEnabled,
    IReadOnlySet<int> ThresholdDays,
    bool AutoStartEnabled)
{
    /// <summary>可选提醒阈值（天）。</summary>
    public static readonly int[] AllowedThresholds = { 30, 14, 7, 3, 1 };

    public static bool IsAllowedThreshold(int days) => AllowedThresholds.Contains(days);

    /// <summary>默认：通知关、五档阈值全选、开机启动关。</summary>
    public static ReminderSettings Default => new(
        NotificationsEnabled: false,
        ThresholdDays: new HashSet<int>(AllowedThresholds),
        AutoStartEnabled: false);
}
