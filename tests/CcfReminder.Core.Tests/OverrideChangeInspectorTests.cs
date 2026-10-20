using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using Xunit;

namespace CcfReminder.Core.Tests;

public class OverrideChangeInspectorTests
{
    private static readonly DateTimeOffset UpstreamAtCorrection = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RescheduledUtc = new(2026, 2, 5, 3, 59, 0, TimeSpan.Zero);

    private static DeadlineNode Node(
        DateTimeOffset? utc = null,
        string? rawTime = "2026-01-22 19:59",
        string? editionId = "icml26") =>
        new()
        {
            ConferenceId = "icml",
            Year = 2026,
            NodeType = DeadlineNodeType.PaperSubmission,
            Track = TrackKind.MainRegular,
            EditionId = editionId,
            RawTime = rawTime,
            RawTimezone = "PST",
            UtcTime = utc ?? UpstreamAtCorrection,
        };

    private static DeadlineOverride OverrideAtCorrection() =>
        new()
        {
            NodeKey = "icml|icml26|PaperSubmission|MainRegular",
            ConferenceId = "icml",
            EditionId = "icml26",
            NodeType = DeadlineNodeType.PaperSubmission,
            CorrectedUtc = new DateTimeOffset(2026, 2, 5, 3, 59, 0, TimeSpan.Zero),
            InputLocalTime = "2026-02-05 11:59",
            InputTimezone = "UTC+8",
            UpstreamUtcAtCorrection = UpstreamAtCorrection,
            UpstreamRawTimeAtCorrection = "2026-01-22 19:59",
            UpstreamRawTimezoneAtCorrection = "PST",
            Reason = "官网延期",
            Source = "https://example.org",
            CorrectedAtUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private static ConferenceDataSnapshot Snapshot(params DeadlineNode[] nodes) =>
        new(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), "test",
            new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) });

    [Fact]
    public void NoOverrides_NoNotices()
    {
        Assert.Empty(OverrideChangeInspector.Inspect(
            Array.Empty<DeadlineOverride>(), Snapshot(Node())));
    }

    [Fact]
    public void NullSnapshot_NoNotices_NeverCriesMissingOffline()
    {
        Assert.Empty(OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() }, snapshot: null));
    }

    [Fact]
    public void UpstreamUnchanged_NoNotice()
    {
        Assert.Empty(OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() }, Snapshot(Node())));
    }

    [Fact]
    public void UpstreamRescheduled_UpstreamChangedNotice_WithLatestValues()
    {
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() },
            Snapshot(Node(utc: RescheduledUtc, rawTime: "2026-02-04 19:59")));

        OverrideNotice notice = Assert.Single(notices);
        Assert.Equal(OverrideNoticeKind.UpstreamChanged, notice.Kind);
        Assert.Equal(RescheduledUtc, notice.LatestUpstreamUtc);
        Assert.Equal("2026-02-04 19:59", notice.LatestUpstreamRawTime);
        Assert.Contains("三方差异", notice.Summary);
    }

    [Fact]
    public void UpstreamRawTimeOnlyChanged_StillNoticed()
    {
        // UTC 相同但原始字符串变了（如时区写法修正）也要提示
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() },
            Snapshot(Node(rawTime: "2026-01-22 19:59:00")));

        Assert.Single(notices, n => n.Kind == OverrideNoticeKind.UpstreamChanged);
    }

    [Fact]
    public void UpstreamTimezoneOnlyChanged_StillNoticed()
    {
        // UTC 与原始时间都不变、只有时区写法变化，也要触发三方差异提示
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() },
            Snapshot(new DeadlineNode
            {
                ConferenceId = "icml",
                Year = 2026,
                NodeType = DeadlineNodeType.PaperSubmission,
                Track = TrackKind.MainRegular,
                EditionId = "icml26",
                RawTime = "2026-01-22 19:59",
                RawTimezone = "UTC-8", // 原为 PST
                UtcTime = UpstreamAtCorrection,
            }));

        Assert.Single(notices, n => n.Kind == OverrideNoticeKind.UpstreamChanged);
    }

    [Fact]
    public void UpstreamNodeGone_MessageDoesNotClaimStillEffective()
    {
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() }, Snapshot());

        OverrideNotice notice = Assert.Single(notices);
        Assert.Contains("仍保留", notice.Summary);
        Assert.Contains("不会作为有效截止时间", notice.Summary);
        Assert.DoesNotContain("继续作为有效时间", notice.Summary);
    }

    [Fact]
    public void MatchAmbiguous_MessageSaysNotApplied()
    {
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() },
            Snapshot(Node(), Node()));

        OverrideNotice notice = Assert.Single(notices);
        Assert.Contains("按原样保留", notice.Summary);
        Assert.Contains("暂不应用", notice.Summary);
    }

    [Fact]
    public void UpstreamNodeGone_MissingNotice_OverrideKept()
    {
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() }, Snapshot());

        OverrideNotice notice = Assert.Single(notices);
        Assert.Equal(OverrideNoticeKind.UpstreamNodeMissing, notice.Kind);
        Assert.Contains("仍保留", notice.Summary);
        Assert.Null(notice.LatestUpstreamUtc);
    }

    [Fact]
    public void UpstreamDuplicateCandidates_AmbiguousNotice()
    {
        IReadOnlyList<OverrideNotice> notices = OverrideChangeInspector.Inspect(
            new[] { OverrideAtCorrection() },
            Snapshot(Node(), Node()));

        OverrideNotice notice = Assert.Single(notices);
        Assert.Equal(OverrideNoticeKind.MatchAmbiguous, notice.Kind);
        Assert.Contains("按原样保留", notice.Summary);
    }
}
