using CcfReminder.Core.Data;

namespace CcfReminder.Core.Reminders;

/// <summary>一条到期应提醒的节点及其命中的最紧迫阈值档。</summary>
public sealed record DueReminder(string ConferenceShortName, DeadlineNode Node, int ThresholdDays);

/// <summary>
/// 提醒评估（纯函数，可注入时钟与状态）：
/// 只考虑主会常规的摘要注册/全文提交、有可靠 UTC 且未到期的节点；
/// 命中最紧迫（最小）的已跨过阈值档——程序停开期间跨过多个阈值时只补发这一档；
/// 同一节点同一档只提醒一次（状态持久化，重启不重复）；每个本地日最多一批。
/// </summary>
public static class ReminderEvaluator
{
    /// <summary>节点键含 UTC 时刻：上游更新截止时间后视为新键，可再次提醒。</summary>
    public static string NodeKey(DeadlineNode node) =>
        string.Join('|',
            node.ConferenceId,
            node.EditionId ?? "",
            node.NodeType,
            node.UtcTime?.UtcDateTime.Ticks ?? 0);

    public static IReadOnlyList<DueReminder> Evaluate(
        IReadOnlyList<ConferenceDataset> datasets,
        ReminderSettings settings,
        ReminderState state,
        DateTimeOffset utcNow,
        string localToday)
    {
        var due = new List<DueReminder>();
        if (!settings.NotificationsEnabled)
        {
            return due;
        }
        if (state.LastSummaryLocalDate == localToday)
        {
            return due; // 每个本地日最多一条汇总
        }

        foreach (ConferenceDataset dataset in datasets)
        {
            foreach (DeadlineNode node in dataset.Nodes)
            {
                if (node.Track != TrackKind.MainRegular)
                {
                    continue; // 特殊轮次、身份不明不通知
                }
                if (node.NodeType is not (DeadlineNodeType.AbstractRegistration
                    or DeadlineNodeType.PaperSubmission))
                {
                    continue; // rebuttal、录用通知、其他节点不通知
                }
                if (!node.IsConsistent || node.UtcTime is not { } utc)
                {
                    continue; // TBD/无可靠 UTC 不通知
                }
                TimeSpan remaining = utc - utcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    continue; // 已过期或恰好等于当前时刻：不提醒
                }

                // 已勾选且已跨过的档位中取最紧迫（最小）一档
                int? band = settings.ThresholdDays
                    .Where(t => remaining <= TimeSpan.FromDays(t))
                    .OrderBy(t => t)
                    .Select(t => (int?)t)
                    .FirstOrDefault();
                if (band is not int threshold)
                {
                    continue; // 尚未跨过任何已勾选阈值
                }
                if (state.NotifiedThresholds.TryGetValue(NodeKey(node), out IReadOnlySet<int>? sent)
                    && sent.Contains(threshold))
                {
                    continue; // 该档已提醒过（含重启后去重）
                }
                due.Add(new DueReminder(dataset.Identity.ShortName, node, threshold));
            }
        }
        return due;
    }
}
