using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using Xunit;

namespace CcfReminder.Core.Tests;

public class OverrideNodeKeyTests
{
    private static readonly DateTimeOffset SomeUtc = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);

    private static DeadlineNode Node(
        DeadlineNodeType type = DeadlineNodeType.PaperSubmission,
        TrackKind track = TrackKind.MainRegular,
        string conf = "icml",
        string? editionId = "icml26",
        DateTimeOffset? utc = null,
        int year = 2026) =>
        new()
        {
            ConferenceId = conf,
            Year = year,
            NodeType = type,
            Track = track,
            EditionId = editionId,
            RawTime = "2026-01-22 19:59",
            RawTimezone = "PST",
            UtcTime = utc ?? SomeUtc,
        };

    private static ConferenceDataSnapshot Snapshot(params DeadlineNode[] nodes) =>
        new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "test",
            new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) });

    [Fact]
    public void TryBuild_MainRegularSubmission_ReturnsKeyWithoutTime()
    {
        string? key = OverrideNodeKey.TryBuild(Node());
        Assert.Equal("icml|icml26|PaperSubmission|MainRegular", key);
    }

    [Fact]
    public void TryBuild_AbstractRegistration_Eligible()
    {
        Assert.NotNull(OverrideNodeKey.TryBuild(Node(DeadlineNodeType.AbstractRegistration)));
    }

    [Theory]
    [InlineData(DeadlineNodeType.Rebuttal)]
    [InlineData(DeadlineNodeType.DecisionNotification)]
    [InlineData(DeadlineNodeType.Other)]
    public void TryBuild_NonSubmissionTypes_NotEligible(DeadlineNodeType type)
    {
        Assert.Null(OverrideNodeKey.TryBuild(Node(type)));
    }

    [Theory]
    [InlineData(TrackKind.Special)]
    [InlineData(TrackKind.Unknown)]
    public void TryBuild_NonMainRegularTrack_NotEligible(TrackKind track)
    {
        Assert.Null(OverrideNodeKey.TryBuild(Node(track: track)));
    }

    [Fact]
    public void Key_ExcludesTime_UpstreamRescheduleKeepsSameKey()
    {
        var before = Node(utc: new DateTimeOffset(2026, 1, 22, 3, 59, 0, TimeSpan.Zero));
        var after = Node(utc: new DateTimeOffset(2026, 2, 5, 3, 59, 0, TimeSpan.Zero));
        Assert.Equal(OverrideNodeKey.TryBuild(before), OverrideNodeKey.TryBuild(after));
    }

    [Fact]
    public void TryBuild_MissingEditionId_UsesEmptySegment()
    {
        Assert.Equal("icml||PaperSubmission|MainRegular",
            OverrideNodeKey.TryBuild(Node(editionId: null)));
    }

    [Fact]
    public void Resolve_UniqueCandidate_ReturnsMatch()
    {
        DeadlineNode node = Node();
        ConferenceDataSnapshot snap = Snapshot(node, Node(type: DeadlineNodeType.AbstractRegistration));
        Assert.True(OverrideNodeKey.TryResolveUniqueCandidate(snap, node, out DeadlineNode? match, out string? error));
        Assert.Same(node, match);
        Assert.Null(error);
    }

    [Fact]
    public void Resolve_NoCandidate_RefusesWithReason()
    {
        DeadlineNode node = Node();
        ConferenceDataSnapshot snap = Snapshot(Node(type: DeadlineNodeType.AbstractRegistration));
        Assert.False(OverrideNodeKey.TryResolveUniqueCandidate(snap, node, out _, out string? error));
        Assert.Contains("找不到", error);
    }

    [Fact]
    public void Resolve_DuplicateCandidates_RefusesWithReason()
    {
        // 届次标识缺失 + 同届多轮：两个候选同键，必须拒绝而非猜测
        DeadlineNode node = Node(editionId: null);
        ConferenceDataSnapshot snap = Snapshot(node, Node(editionId: null));
        Assert.False(OverrideNodeKey.TryResolveUniqueCandidate(snap, node, out _, out string? error));
        Assert.Contains("2 个候选", error);
        Assert.Contains("拒绝", error);
    }

    [Fact]
    public void Resolve_IneligibleNode_RefusesWithReason()
    {
        DeadlineNode node = Node(DeadlineNodeType.Rebuttal);
        ConferenceDataSnapshot snap = Snapshot(node);
        Assert.False(OverrideNodeKey.TryResolveUniqueCandidate(snap, node, out _, out string? error));
        Assert.Contains("不可修正", error);
    }
}
