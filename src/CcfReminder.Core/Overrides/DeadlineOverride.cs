using CcfReminder.Core.Data;

namespace CcfReminder.Core.Overrides;

/// <summary>
/// 一条用户手动修正（PROJECT_BRIEF §4）：只针对身份明确的主会常规摘要注册/全文提交节点。
/// 保留修正时上游值（三方对照用）、修正依据、来源与修正时间；永不改写原始快照。
/// </summary>
public sealed record DeadlineOverride
{
    /// <summary>稳定节点键（不含任何时间成分），见 <see cref="OverrideNodeKey"/>。</summary>
    public required string NodeKey { get; init; }
    public required string ConferenceId { get; init; }
    /// <summary>届次标识；上游缺失时为空串（此时唯一性由候选计数保证）。</summary>
    public required string EditionId { get; init; }
    public required DeadlineNodeType NodeType { get; init; }
    /// <summary>修正后的有效截止时刻（UTC，Offset 为零）。</summary>
    public required DateTimeOffset CorrectedUtc { get; init; }
    /// <summary>用户输入的时间原文（yyyy-MM-dd HH:mm[:ss]）。</summary>
    public required string InputLocalTime { get; init; }
    /// <summary>用户输入时显式声明的时区（"本地时区"/AoE/UTC±N/PT 等）。</summary>
    public required string InputTimezone { get; init; }
    /// <summary>修正时上游换算值（可能为空：上游当时 TBD 或无法换算）。</summary>
    public DateTimeOffset? UpstreamUtcAtCorrection { get; init; }
    /// <summary>修正时上游原始时间字符串。</summary>
    public string? UpstreamRawTimeAtCorrection { get; init; }
    /// <summary>修正时上游原始时区写法。</summary>
    public string? UpstreamRawTimezoneAtCorrection { get; init; }
    /// <summary>修正依据（必填）。</summary>
    public required string Reason { get; init; }
    /// <summary>修正来源（必填，如当届官网链接）。</summary>
    public required string Source { get; init; }
    /// <summary>修正时间（UTC）。</summary>
    public required DateTimeOffset CorrectedAtUtc { get; init; }
}

/// <summary>
/// 稳定节点键与唯一性判定（PROJECT_BRIEF §4“稳定节点标识”）：
/// 键＝会议|届次标识（缺失→空串）|节点类型|轮次归属，刻意不含任何时间成分——
/// 上游改期不会改变键，用户修正因此继续命中同一节点。
/// 只接受主会常规的摘要注册/全文提交；EditionId 缺失、同届多轮或上游重排导致
/// 同一键对应多个候选时，拒绝自动套用/修正并给出原因，绝不猜测其一。
/// </summary>
public static class OverrideNodeKey
{
    /// <summary>主会常规摘要注册/全文提交节点返回稳定键；其他节点返回 null（不开放修正）。</summary>
    public static string? TryBuild(DeadlineNode node) =>
        node.Track == TrackKind.MainRegular &&
        node.NodeType is DeadlineNodeType.AbstractRegistration or DeadlineNodeType.PaperSubmission
            ? string.Join('|', node.ConferenceId, node.EditionId ?? "", node.NodeType, node.Track)
            : null;

    /// <summary>当前快照中键相同的全部候选节点。</summary>
    public static IReadOnlyList<DeadlineNode> FindCandidates(ConferenceDataSnapshot snapshot, string nodeKey) =>
        snapshot.Conferences
            .SelectMany(d => d.Nodes)
            .Where(n => TryBuild(n) == nodeKey)
            .ToList();

    /// <summary>
    /// 修正入口的唯一性判定：键不合法（非主会常规摘要/全文）或候选不唯一时拒绝并解释原因。
    /// </summary>
    public static bool TryResolveUniqueCandidate(
        ConferenceDataSnapshot snapshot,
        DeadlineNode node,
        out DeadlineNode? match,
        out string? error)
    {
        match = null;
        string? key = TryBuild(node);
        if (key is null)
        {
            error = "仅主会常规论文的摘要注册/全文提交节点开放手动修正；" +
                "rebuttal、录用通知、特殊轮次与身份不明节点不可修正。";
            return false;
        }
        IReadOnlyList<DeadlineNode> candidates = FindCandidates(snapshot, key);
        if (candidates.Count == 0)
        {
            error = "当前数据快照中找不到该节点（可能刚被上游移除），已拒绝修正。";
            return false;
        }
        if (candidates.Count > 1)
        {
            error = $"同一节点键对应 {candidates.Count} 个候选节点（届次标识缺失或同届多轮主会），" +
                "无法唯一确定修正对象，已拒绝修正。请先在详情窗核对届次与轮次标识。";
            return false;
        }
        match = candidates[0];
        error = null;
        return true;
    }
}
