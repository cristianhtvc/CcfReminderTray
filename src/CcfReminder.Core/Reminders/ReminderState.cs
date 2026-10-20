namespace CcfReminder.Core.Reminders;

/// <summary>
/// 已提醒状态（持久化）：节点键 → 已通知过的阈值档；LastSummaryLocalDate 实现“每个本地日最多一条汇总”。
/// 节点键含 UTC 时刻，截止时间被上游更新后视为新键，可再次提醒。
/// </summary>
public sealed record ReminderState(
    IReadOnlyDictionary<string, IReadOnlySet<int>> NotifiedThresholds,
    string? LastSummaryLocalDate)
{
    public static ReminderState Empty => new(
        new Dictionary<string, IReadOnlySet<int>>(), null);
}
