namespace CcfReminder.Core;

public enum SelectionKind
{
    /// <summary>找到下一个尚未过期的投稿节点。</summary>
    Upcoming,
    /// <summary>有投稿节点的时间无法可靠换算（非 TBD），保守显示“待核验”。</summary>
    NeedsVerification,
    /// <summary>存在已可靠换算的投稿节点但全部过期：“本届投稿已截止／下届待公布”。</summary>
    SubmissionsClosed,
    /// <summary>没有任何已公布时间的投稿节点：“待公布”。</summary>
    NotAnnounced,
}

public sealed record SelectionResult(SelectionKind Kind, DeadlineNode? Node, string? Detail = null)
{
    /// <summary>节点类型或状态的中文标注（不含倒计时）。</summary>
    public string Label => Kind switch
    {
        SelectionKind.Upcoming when Node!.NodeType == DeadlineNodeType.AbstractRegistration => "摘要注册",
        SelectionKind.Upcoming => "全文提交",
        SelectionKind.NeedsVerification => "待核验",
        SelectionKind.SubmissionsClosed => "本届投稿已截止／下届待公布",
        _ => Detail ?? "待公布",
    };
}

/// <summary>
/// 为单个会议选出主菜单要展示的节点。
/// 只考虑主会常规论文的摘要注册/全文提交；rebuttal、录用通知、特殊 track、
/// 身份不明的多轮一律排除。绝不按往年日期推算。
/// </summary>
public static class NextSubmissionSelector
{
    public static SelectionResult Select(IReadOnlyList<DeadlineNode> conferenceNodes, DateTimeOffset utcNow)
    {
        var mainSubmissions = conferenceNodes
            .Where(n => n.Track == TrackKind.MainRegular &&
                        n.NodeType is DeadlineNodeType.AbstractRegistration or DeadlineNodeType.PaperSubmission)
            .ToList();

        // 下一个已公布、尚未过期的投稿节点（可跨届，取最早者）。
        // 矛盾记录（UtcTime 与 Issue 同时有值或同时为空）不得当作可靠 DDL。
        DeadlineNode? upcoming = mainSubmissions
            .Where(n => n.IsConsistent && n.UtcTime is { } t && t > utcNow)
            .OrderBy(n => n.UtcTime!.Value)
            .FirstOrDefault();
        if (upcoming is not null)
        {
            return new SelectionResult(SelectionKind.Upcoming, upcoming);
        }

        // 矛盾记录或无法可靠换算（非“未公布”）的投稿节点：不能当它已过期，保守标“待核验”
        bool hasUnreliable = mainSubmissions.Any(
            n => !n.IsConsistent ||
                 (n.UtcTime is null && n.Issue is not null and not ConversionIssue.NotProvided));
        if (hasUnreliable)
        {
            return new SelectionResult(SelectionKind.NeedsVerification, null);
        }

        // 存在“未公布”（TBD/缺失）的投稿节点：不能断定全部投稿结束，保守显示“待公布”；
        // 若已公布节点全部过期且缺的是全文提交，则明示“全文提交时间待公布”
        bool hasNotProvided = mainSubmissions.Any(
            n => n.UtcTime is null && n.Issue is ConversionIssue.NotProvided);
        if (hasNotProvided)
        {
            bool anyExpired = mainSubmissions.Any(n => n.UtcTime is not null);
            bool fullPaperMissing = mainSubmissions.Any(
                n => n.NodeType == DeadlineNodeType.PaperSubmission &&
                     n.UtcTime is null && n.Issue is ConversionIssue.NotProvided);
            string? detail = anyExpired && fullPaperMissing ? "全文提交时间待公布" : null;
            return new SelectionResult(SelectionKind.NotAnnounced, null, detail);
        }

        // 有可靠换算的投稿节点且全部过期
        if (mainSubmissions.Any(n => n.UtcTime is not null))
        {
            return new SelectionResult(SelectionKind.SubmissionsClosed, null);
        }

        // 根本没有投稿节点
        return new SelectionResult(SelectionKind.NotAnnounced, null);
    }
}
