using CcfReminder.Core;
using Xunit;

namespace CcfReminder.Core.Tests;

public class NextSubmissionSelectorTests
{
    // 固定的“现在”，全部倒计时/过期判断基于它注入
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    private static DeadlineNode Node(
        int year,
        DeadlineNodeType type,
        DateTimeOffset? utc,
        TrackKind track = TrackKind.MainRegular,
        ConversionIssue? issue = null) =>
        new()
        {
            ConferenceId = "test",
            Year = year,
            NodeType = type,
            Track = track,
            UtcTime = utc,
            Issue = utc is null ? issue ?? ConversionIssue.NotProvided : null,
        };

    private static DateTimeOffset Utc(int y, int m, int d) =>
        new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AbstractExpired_FullPaperFuture_SelectsFullPaper()
    {
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 18)), // 已过期
            Node(2027, DeadlineNodeType.PaperSubmission, Utc(2026, 11, 1)),      // 未来
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.Upcoming, r.Kind);
        Assert.Equal(DeadlineNodeType.PaperSubmission, r.Node!.NodeType);
        Assert.Equal("全文提交", r.Label);
    }

    [Fact]
    public void CurrentEditionClosed_NextEditionPublished_SelectsNextEdition()
    {
        var nodes = new[]
        {
            Node(2026, DeadlineNodeType.AbstractRegistration, Utc(2026, 1, 10)),
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 1, 17)),
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2027, 1, 8)),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.Upcoming, r.Kind);
        Assert.Equal(2027, r.Node!.Year);
        Assert.Equal("摘要注册", r.Label);
    }

    [Fact]
    public void PicksEarliest_AcrossEditions()
    {
        var nodes = new[]
        {
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 12, 1)),
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 11, 15)), // 更早
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(2027, r.Node!.Year);
    }

    [Fact]
    public void RebuttalAndDecision_NeverSelected()
    {
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 1)), // 已过期
            Node(2027, DeadlineNodeType.Rebuttal, Utc(2026, 11, 18)),           // 未来但排除
            Node(2027, DeadlineNodeType.DecisionNotification, Utc(2026, 12, 16)),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.SubmissionsClosed, r.Kind);
        Assert.Equal("本届投稿已截止／下届待公布", r.Label);
    }

    [Fact]
    public void SpecialTrack_NeverSelected()
    {
        var nodes = new[]
        {
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 10, 8)), // 主会已过期
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 11, 17), TrackKind.Special), // 特殊 track 未来
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.SubmissionsClosed, r.Kind);
    }

    [Fact]
    public void UnknownTrack_NeverSelected()
    {
        var nodes = new[]
        {
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 10, 8)),
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 11, 17), TrackKind.Unknown),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.SubmissionsClosed, r.Kind);
    }

    [Fact]
    public void DeadlineExactlyAtNow_TreatedAsExpired()
    {
        var nodes = new[] { Node(2026, DeadlineNodeType.PaperSubmission, Now) };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.SubmissionsClosed, r.Kind);
    }

    [Fact]
    public void TbdOnly_NotAnnounced()
    {
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, null, issue: ConversionIssue.NotProvided),
            Node(2027, DeadlineNodeType.PaperSubmission, null, issue: ConversionIssue.NotProvided),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.NotAnnounced, r.Kind);
        Assert.Equal("待公布", r.Label);
    }

    [Fact]
    public void NoNodes_NotAnnounced()
    {
        var r = NextSubmissionSelector.Select(Array.Empty<DeadlineNode>(), Now);
        Assert.Equal(SelectionKind.NotAnnounced, r.Kind);
    }

    [Fact]
    public void AllSubmissionsExpired_SubmissionsClosed()
    {
        var nodes = new[]
        {
            Node(2026, DeadlineNodeType.AbstractRegistration, Utc(2026, 7, 21)),
            Node(2026, DeadlineNodeType.PaperSubmission, Utc(2026, 7, 28)),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.SubmissionsClosed, r.Kind);
    }

    [Fact]
    public void UnreliableConversion_NeedsVerification()
    {
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 1)), // 已过期
            Node(2027, DeadlineNodeType.PaperSubmission, null, issue: ConversionIssue.UnknownTimezone), // 无法换算
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.NeedsVerification, r.Kind);
        Assert.Equal("待核验", r.Label);
    }

    [Fact]
    public void FullPaperTbd_ShowsPendingAnnouncement_NotClosed()
    {
        // 摘要已过期、全文 TBD：不能断定全部投稿结束
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 1)),
            Node(2027, DeadlineNodeType.PaperSubmission, null, issue: ConversionIssue.NotProvided),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.NotAnnounced, r.Kind);
        Assert.Equal("全文提交时间待公布", r.Label);
    }

    [Fact]
    public void InconsistentNode_UtcAndIssueBothSet_NotTrusted()
    {
        // UtcTime 与 Issue 同时有值：矛盾记录不得当作可靠未来 DDL
        var bad = new DeadlineNode
        {
            ConferenceId = "test",
            Year = 2027,
            NodeType = DeadlineNodeType.PaperSubmission,
            Track = TrackKind.MainRegular,
            UtcTime = Utc(2026, 11, 1), // 未来
            Issue = ConversionIssue.UnknownTimezone, // 但同时带错误——矛盾
        };
        var nodes = new[] { Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 1)), bad };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.NeedsVerification, r.Kind);
    }

    [Fact]
    public void InconsistentNode_BothEmpty_NotTrusted()
    {
        // UtcTime 与 Issue 同时为空：同样矛盾
        var bad = new DeadlineNode
        {
            ConferenceId = "test",
            Year = 2027,
            NodeType = DeadlineNodeType.PaperSubmission,
            Track = TrackKind.MainRegular,
        };
        var nodes = new[] { Node(2027, DeadlineNodeType.AbstractRegistration, Utc(2026, 9, 1)), bad };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.NeedsVerification, r.Kind);
    }

    [Fact]
    public void UpcomingWins_OverUnreliableNodes()
    {
        // 有可靠未来节点时直接展示它，不受其他无法换算节点影响
        var nodes = new[]
        {
            Node(2027, DeadlineNodeType.AbstractRegistration, null, issue: ConversionIssue.AmbiguousLocalTime),
            Node(2027, DeadlineNodeType.PaperSubmission, Utc(2026, 11, 1)),
        };
        var r = NextSubmissionSelector.Select(nodes, Now);
        Assert.Equal(SelectionKind.Upcoming, r.Kind);
    }
}
