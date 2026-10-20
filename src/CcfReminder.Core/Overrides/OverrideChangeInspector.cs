using CcfReminder.Core.Data;

namespace CcfReminder.Core.Overrides;

public enum OverrideNoticeKind
{
    /// <summary>上游值与修正时不同（三方对照：修正时上游值／最新上游值／用户修正值）。</summary>
    UpstreamChanged,
    /// <summary>上游已找不到该节点；修正保留，绝不悄悄丢弃。</summary>
    UpstreamNodeMissing,
    /// <summary>同一键在上游出现多个候选，无法自动对照；修正按原样保留。</summary>
    MatchAmbiguous,
}

public sealed record OverrideNotice(
    OverrideNoticeKind Kind,
    DeadlineOverride Override,
    string Summary,
    DateTimeOffset? LatestUpstreamUtc,
    string? LatestUpstreamRawTime,
    string? LatestUpstreamRawTimezone);

/// <summary>
/// 上游刷新后的修正对照（纯函数）：保留全部用户修正，只产出提示——
/// 上游变化给出三方差异、上游节点消失或无法唯一匹配都明确告知，绝不悄悄丢弃修正。
/// </summary>
public static class OverrideChangeInspector
{
    public static IReadOnlyList<OverrideNotice> Inspect(
        IReadOnlyList<DeadlineOverride> overrides,
        ConferenceDataSnapshot? snapshot)
    {
        var notices = new List<OverrideNotice>();
        if (snapshot is null || overrides.Count == 0)
        {
            return notices; // 无数据（如离线启动且缓存无效）时不做判断，不谎报“节点消失”
        }

        foreach (DeadlineOverride o in overrides)
        {
            IReadOnlyList<DeadlineNode> candidates = OverrideNodeKey.FindCandidates(snapshot, o.NodeKey);
            if (candidates.Count == 0)
            {
                notices.Add(new OverrideNotice(
                    OverrideNoticeKind.UpstreamNodeMissing, o,
                    $"上游数据中已找不到 {o.ConferenceId} 的该节点；你的修正记录仍保留，" +
                    "可在详情窗查看、恢复或重新修正，但在上游重新出现唯一匹配节点之前，" +
                    "它不会作为有效截止时间使用。",
                    null, null, null));
                continue;
            }
            if (candidates.Count > 1)
            {
                notices.Add(new OverrideNotice(
                    OverrideNoticeKind.MatchAmbiguous, o,
                    $"上游数据中同一节点键现有 {candidates.Count} 个候选，无法自动对照；" +
                    "你的修正记录按原样保留，但在候选唯一之前暂不应用为有效时间。",
                    null, null, null));
                continue;
            }
            DeadlineNode latest = candidates[0];
            if (!Nullable.Equals(latest.UtcTime, o.UpstreamUtcAtCorrection) ||
                !string.Equals(latest.RawTime, o.UpstreamRawTimeAtCorrection, StringComparison.Ordinal) ||
                !string.Equals(latest.RawTimezone, o.UpstreamRawTimezoneAtCorrection, StringComparison.Ordinal))
            {
                notices.Add(new OverrideNotice(
                    OverrideNoticeKind.UpstreamChanged, o,
                    "上游值与你修正时不同，请核对三方差异（修正时上游值／最新上游值／你的修正值）。",
                    latest.UtcTime, latest.RawTime, latest.RawTimezone));
            }
        }
        return notices;
    }
}
