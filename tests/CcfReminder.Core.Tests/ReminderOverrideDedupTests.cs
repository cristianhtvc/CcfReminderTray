using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using CcfReminder.Core.Reminders;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>
/// 修正与提醒去重的联动：ReminderEvaluator.NodeKey 含 UTC 时刻——
/// 修正后有效时间变化即视为新键重新进入评估（会再提醒一次，刻意设计）；
/// 恢复上游值后回到原键，已提醒过的档位不会重复发送；“每日最多一条”不受影响。
/// </summary>
public class ReminderOverrideDedupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
    private const string Today = "2026-10-14";

    private static readonly ReminderSettings Settings =
        new(NotificationsEnabled: true, ThresholdDays: new HashSet<int> { 30 }, AutoStartEnabled: false);

    private static DeadlineNode UpstreamNode(DateTimeOffset utc) =>
        new()
        {
            ConferenceId = "icml",
            Year = 2026,
            NodeType = DeadlineNodeType.PaperSubmission,
            Track = TrackKind.MainRegular,
            EditionId = "icml26",
            RawTime = "2026-11-01 19:59",
            RawTimezone = "PST",
            UtcTime = utc,
        };

    private static DeadlineOverride OverrideFor(DeadlineNode node, DateTimeOffset correctedUtc) =>
        new()
        {
            NodeKey = OverrideNodeKey.TryBuild(node)!,
            ConferenceId = node.ConferenceId,
            EditionId = "icml26",
            NodeType = node.NodeType,
            CorrectedUtc = correctedUtc,
            InputLocalTime = "2026-11-03 11:59",
            InputTimezone = "UTC+8",
            Reason = "官网延期",
            Source = "https://example.org",
            CorrectedAtUtc = Now,
        };

    private static IReadOnlyList<ConferenceDataset> Datasets(params DeadlineNode[] nodes) =>
        new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) };

    private static ReminderState NotifiedFor(DeadlineNode node, params int[] bands) =>
        new(new Dictionary<string, IReadOnlySet<int>>
        {
            [ReminderEvaluator.NodeKey(node)] = new HashSet<int>(bands),
        }, LastSummaryLocalDate: null);

    [Fact]
    public void CorrectedTime_NewNodeKey_ReArmsReminder()
    {
        DeadlineNode upstream = UpstreamNode(Now.AddDays(20));
        ReminderState state = NotifiedFor(upstream, 30); // 上游时间已提醒过 30 天档

        // 修正到 10 天后：有效时间变化 → 新键 → 再次进入评估
        IReadOnlyList<ConferenceDataset> effective = DeadlineOverrideResolver.ApplyEffectiveTimes(
            Datasets(upstream), new[] { OverrideFor(upstream, Now.AddDays(10)) });

        DueReminder due = Assert.Single(ReminderEvaluator.Evaluate(effective, Settings, state, Now, Today));
        Assert.Equal(30, due.ThresholdDays);
        Assert.Equal(Now.AddDays(10), due.Node.UtcTime);
    }

    [Fact]
    public void CorrectedTime_AlreadyNotifiedForCorrectedKey_NotDueAgain()
    {
        DeadlineNode upstream = UpstreamNode(Now.AddDays(20));
        DateTimeOffset corrected = Now.AddDays(10);
        IReadOnlyList<ConferenceDataset> effective = DeadlineOverrideResolver.ApplyEffectiveTimes(
            Datasets(upstream), new[] { OverrideFor(upstream, corrected) });

        DeadlineNode correctedNode = effective[0].Nodes[0];
        ReminderState state = NotifiedFor(correctedNode, 30);

        Assert.Empty(ReminderEvaluator.Evaluate(effective, Settings, state, Now, Today));
    }

    [Fact]
    public void RestoredUpstream_OriginalKeyAlreadyNotified_NotDue()
    {
        // 恢复上游值＝回到原始快照（不套用修正），原键已提醒过 → 不重复发送
        DeadlineNode upstream = UpstreamNode(Now.AddDays(20));
        ReminderState state = NotifiedFor(upstream, 30);

        Assert.Empty(ReminderEvaluator.Evaluate(Datasets(upstream), Settings, state, Now, Today));
    }

    [Fact]
    public void DailyCap_StillAppliesToCorrectedNodes()
    {
        DeadlineNode upstream = UpstreamNode(Now.AddDays(20));
        IReadOnlyList<ConferenceDataset> effective = DeadlineOverrideResolver.ApplyEffectiveTimes(
            Datasets(upstream), new[] { OverrideFor(upstream, Now.AddDays(10)) });

        ReminderState state = ReminderState.Empty with { LastSummaryLocalDate = Today };
        Assert.Empty(ReminderEvaluator.Evaluate(effective, Settings, state, Now, Today));
    }

    [Fact]
    public void AmbiguousCandidates_ReminderUsesUpstreamTime_NotCorrected()
    {
        // 同键两个候选（届次缺失）：菜单/提醒输入与解析器同规则——修正不应用
        DeadlineNode upstreamA = UpstreamNode(Now.AddDays(20)) with { EditionId = null };
        DeadlineNode upstreamB = UpstreamNode(Now.AddDays(20)) with { EditionId = null };
        DeadlineOverride ov = OverrideFor(upstreamA, Now.AddDays(10));

        IReadOnlyList<ConferenceDataset> effective = DeadlineOverrideResolver.ApplyEffectiveTimes(
            Datasets(upstreamA, upstreamB), new[] { ov });

        // 提醒看到的是上游时间（Now+20d），而不是修正后的 Now+10d
        IReadOnlyList<DueReminder> due =
            ReminderEvaluator.Evaluate(effective, Settings, ReminderState.Empty, Now, Today);
        Assert.All(due, d => Assert.Equal(Now.AddDays(20), d.Node.UtcTime));
    }

    [Fact]
    public void OverrideNodeKeys_DifferFromReminderKeys_AndAreTimeStable()
    {
        // 修正文件用“不含时间”的稳定键（时间变化不丢修正）；
        // 提醒去重用“含时间”的键（时间变化重新评估）。两者各自成立。
        DeadlineNode a = UpstreamNode(Now.AddDays(20));
        DeadlineNode b = UpstreamNode(Now.AddDays(10));
        Assert.Equal(OverrideNodeKey.TryBuild(a), OverrideNodeKey.TryBuild(b));
        Assert.NotEqual(ReminderEvaluator.NodeKey(a), ReminderEvaluator.NodeKey(b));
    }
}
