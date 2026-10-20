using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using Xunit;

namespace CcfReminder.Core.Tests;

public class DeadlineOverrideResolverTests
{
    private static readonly DateTimeOffset UpstreamUtc = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CorrectedUtc = new(2026, 2, 5, 3, 59, 0, TimeSpan.Zero);

    private static DeadlineNode Node(
        DeadlineNodeType type = DeadlineNodeType.PaperSubmission,
        string? editionId = "icml26") =>
        new()
        {
            ConferenceId = "icml",
            Year = 2026,
            NodeType = type,
            Track = TrackKind.MainRegular,
            EditionId = editionId,
            RawTime = "2026-01-22 19:59",
            RawTimezone = "PST",
            UtcTime = UpstreamUtc,
        };

    private static DeadlineOverride OverrideFor(DeadlineNode node, DateTimeOffset corrected) =>
        new()
        {
            NodeKey = OverrideNodeKey.TryBuild(node)!,
            ConferenceId = node.ConferenceId,
            EditionId = node.EditionId ?? "",
            NodeType = node.NodeType,
            CorrectedUtc = corrected,
            InputLocalTime = "2026-02-05 11:59",
            InputTimezone = "UTC+8",
            UpstreamUtcAtCorrection = UpstreamUtc,
            UpstreamRawTimeAtCorrection = "2026-01-22 19:59",
            UpstreamRawTimezoneAtCorrection = "PST",
            Reason = "官网延期",
            Source = "https://example.org",
            CorrectedAtUtc = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        };

    private static IReadOnlyList<ConferenceDataset> Datasets(params DeadlineNode[] nodes) =>
        new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) };

    [Fact]
    public void Apply_MatchingKey_ReplacesEffectiveTimeOnly()
    {
        DeadlineNode node = Node();
        DeadlineOverride ov = OverrideFor(node, CorrectedUtc);

        IReadOnlyList<ConferenceDataset> result =
            DeadlineOverrideResolver.ApplyEffectiveTimes(Datasets(node), new[] { ov });

        DeadlineNode effective = Assert.Single(result[0].Nodes);
        Assert.Equal(CorrectedUtc, effective.UtcTime);
        Assert.Null(effective.Issue);
        // 原始快照不被改写：上游原始字段原样保留
        Assert.Equal("2026-01-22 19:59", effective.RawTime);
        Assert.Equal(UpstreamUtc, node.UtcTime);
    }

    [Fact]
    public void Apply_NonMatchingKey_Untouched()
    {
        DeadlineNode paper = Node();
        DeadlineNode otherEdition = Node(editionId: "icml27");
        DeadlineOverride ov = OverrideFor(paper, CorrectedUtc);

        IReadOnlyList<ConferenceDataset> result =
            DeadlineOverrideResolver.ApplyEffectiveTimes(Datasets(paper, otherEdition), new[] { ov });

        Assert.Equal(UpstreamUtc, result[0].Nodes.First(n => n.EditionId == "icml27").UtcTime);
    }

    [Fact]
    public void Apply_EmptyOverrides_ReturnsSameInstance()
    {
        IReadOnlyList<ConferenceDataset> datasets = Datasets(Node());
        Assert.Same(datasets, DeadlineOverrideResolver.ApplyEffectiveTimes(datasets, Array.Empty<DeadlineOverride>()));
    }

    [Fact]
    public void IndexByNodeKey_LastWinsDefensive()
    {
        DeadlineNode node = Node();
        DeadlineOverride first = OverrideFor(node, CorrectedUtc);
        DeadlineOverride second = OverrideFor(node, CorrectedUtc.AddDays(1));
        IReadOnlyDictionary<string, DeadlineOverride> map =
            DeadlineOverrideResolver.IndexByNodeKey(new[] { first, second });
        Assert.Equal(second.CorrectedUtc, map[first.NodeKey].CorrectedUtc);
    }

    // ---- 候选歧义：绝不把修正套到全部候选（问题 3 回归）----

    [Fact]
    public void Apply_DuplicateCandidates_NotAppliedToAny()
    {
        DeadlineNode first = Node(editionId: null);
        DeadlineNode second = Node(editionId: null); // 同键（届次缺失）
        DeadlineOverride ov = OverrideFor(first, CorrectedUtc);

        IReadOnlyList<ConferenceDataset> result =
            DeadlineOverrideResolver.ApplyEffectiveTimes(Datasets(first, second), new[] { ov });

        // 两个候选都保持上游值：修正被保留（由检查器标记待核对），但暂不应用
        Assert.All(result[0].Nodes, n => Assert.Equal(UpstreamUtc, n.UtcTime));
    }

    [Fact]
    public void Apply_DuplicateThenUnique_OnlyUniqueApplied()
    {
        DeadlineNode dupA = Node(editionId: null);
        DeadlineNode dupB = Node(editionId: null);
        DeadlineNode unique = Node(DeadlineNodeType.AbstractRegistration);
        DeadlineOverride dupOverride = OverrideFor(dupA, CorrectedUtc);
        DeadlineOverride uniqueOverride = OverrideFor(unique, CorrectedUtc);

        IReadOnlyList<ConferenceDataset> result = DeadlineOverrideResolver.ApplyEffectiveTimes(
            Datasets(dupA, dupB, unique), new[] { dupOverride, uniqueOverride });

        Assert.Equal(UpstreamUtc, result[0].Nodes.First(n => n.NodeType == DeadlineNodeType.PaperSubmission && n.EditionId is null).UtcTime);
        Assert.Equal(CorrectedUtc,
            result[0].Nodes.First(n => n.NodeType == DeadlineNodeType.AbstractRegistration).UtcTime);
    }

    [Fact]
    public void FindApplicable_OnlyUniqueCandidateKeys()
    {
        DeadlineNode dupA = Node(editionId: null);
        DeadlineNode dupB = Node(editionId: null);
        DeadlineNode unique = Node();
        DeadlineOverride dupOverride = OverrideFor(dupA, CorrectedUtc);
        DeadlineOverride uniqueOverride = OverrideFor(unique, CorrectedUtc);

        IReadOnlyDictionary<string, DeadlineOverride> applicable = DeadlineOverrideResolver.FindApplicable(
            Datasets(dupA, dupB, unique), new[] { dupOverride, uniqueOverride });

        Assert.Single(applicable);
        Assert.True(applicable.ContainsKey(OverrideNodeKey.TryBuild(unique)!));
    }

    [Fact]
    public void ApplyToSnapshot_NoApplicable_ReturnsSameInstance()
    {
        DeadlineNode dupA = Node(editionId: null);
        DeadlineNode dupB = Node(editionId: null);
        var snap = new ConferenceDataSnapshot(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "test", Datasets(dupA, dupB));

        ConferenceDataSnapshot result = DeadlineOverrideResolver.ApplyToSnapshot(
            snap, new[] { OverrideFor(dupA, CorrectedUtc) });

        Assert.Same(snap, result); // 歧义修正不应用：快照原样
    }

    [Fact]
    public void ApplyToSnapshot_UniqueOverride_Applies()
    {
        DeadlineNode node = Node();
        var snap = new ConferenceDataSnapshot(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "test", Datasets(node));

        ConferenceDataSnapshot result = DeadlineOverrideResolver.ApplyToSnapshot(
            snap, new[] { OverrideFor(node, CorrectedUtc) });

        Assert.Equal(CorrectedUtc, result.Conferences[0].Nodes[0].UtcTime);
        Assert.Equal(UpstreamUtc, snap.Conferences[0].Nodes[0].UtcTime); // 原始快照不被改写
    }
}
