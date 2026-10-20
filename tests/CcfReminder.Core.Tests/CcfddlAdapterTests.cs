using CcfReminder.Core;
using CcfReminder.Core.Ccfddl;
using CcfReminder.Core.Data;
using Xunit;

namespace CcfReminder.Core.Tests;

public class CcfddlAdapterTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static (ConferenceDataSnapshot? Snapshot, string? Error) Build(string json)
    {
        bool parsed = CcfddlJsonParser.TryParse(json, out IReadOnlyList<ParsedConference>? confs, out string? parseErr);
        Assert.True(parsed, parseErr);
        CcfddlAdapter.TryBuildSnapshot(confs!, FetchedAt, out ConferenceDataSnapshot? snap, out string? err);
        return (snap, err);
    }

    private static ConferenceDataset Dataset(ConferenceDataSnapshot snap, string id) =>
        snap.Conferences.Single(c => c.Identity.Id == id);

    [Fact]
    public void SixIdentities_MatchedExactly_InCatalogOrder_DecoysExcluded()
    {
        // 干扰项：同名 DAI 但 conference_key 不同，不得被选中
        string decoy = "," + TestJson.Conf("CF/dai", "DAI", "daix", "C",
            TestJson.Edition(2027, "daix27", "AoE", """{"deadline":"2027-01-01 00:00:00"}"""));
        var (snap, err) = Build(TestJson.SixConferences(extraEntries: decoy));

        Assert.Null(err);
        Assert.NotNull(snap);
        Assert.Equal(6, snap!.Conferences.Count);
        Assert.Equal(
            new[] { "icml", "iclr", "nips", "aamas", "aaai", "dai" },
            snap.Conferences.Select(c => c.Identity.Id).ToArray());
        // 干扰届次不得混入 DAI
        Assert.DoesNotContain(Dataset(snap, "dai").Nodes, n => n.EditionId == "daix27");
    }

    [Fact]
    public void Nips_UpstreamKey_MapsToNeurIPSDisplayName()
    {
        var (snap, _) = Build(TestJson.SixConferences());
        Assert.Equal("NeurIPS", Dataset(snap!, "nips").Identity.ShortName);
    }

    [Fact]
    public void IdentityFailure_OnTitleMismatch_WholeSnapshotRejected()
    {
        // nips 的 title 若变成 "NIPS"，身份不可确认 → 整体失败（调用方保留旧缓存）
        string json = TestJson.SixConferences().Replace("\"title\":\"NeurIPS\"", "\"title\":\"NIPS\"");
        var (snap, err) = Build(json);
        Assert.Null(snap);
        Assert.Contains("NeurIPS", err);
    }

    [Fact]
    public void IdentityFailure_OnMissingConference()
    {
        // 只含五个会议（缺 DAI）：身份核对失败，整体拒绝
        string json = "[" + string.Join(",", new[]
        {
            TestJson.Conf("AI/icml", "ICML", "icml", "A", TestJson.Edition(2027, "icml27", "UTC+0", """{"deadline":"2027-01-27 11:59:59"}""")),
            TestJson.Conf("AI/iclr", "ICLR", "iclr", "A", TestJson.Edition(2027, "iclr27", "AoE", """{"deadline":"2027-09-25 23:59:59"}""")),
            TestJson.Conf("AI/nips", "NeurIPS", "nips", "A", TestJson.Edition(2027, "nips27", "AoE", """{"deadline":"2027-05-07 11:59:00"}""")),
            TestJson.Conf("AI/aamas", "AAMAS", "ifaamas", "B", TestJson.Edition(2027, "aamas27", "AoE", """{"deadline":"2027-10-08 23:59:59"}""")),
            TestJson.Conf("AI/aaai", "AAAI", "aaai", "A", TestJson.Edition(2027, "aaai27", "UTC-12", """{"deadline":"2027-07-28 23:59:59"}""")),
        }) + "]";
        var (snap, err) = Build(json);
        Assert.Null(snap);
        Assert.Contains("DAI", err);
    }

    [Fact]
    public void NodeTypes_Mapped_UnknownKeyKeptAsOther_UnknownTrack()
    {
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"abstract_deadline":"2027-09-18 23:59:59","deadline":"2027-09-25 23:59:59","rebuttal_deadline":"2027-11-18 23:59:59","decision_deadline":"2027-12-16 23:59:59","workshop_deadline":"2027-08-01 23:59:59"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));

        Assert.Null(err);
        var nodes = Dataset(snap!, "iclr").Nodes;
        Assert.Contains(nodes, n => n.NodeType == DeadlineNodeType.AbstractRegistration);
        Assert.Contains(nodes, n => n.NodeType == DeadlineNodeType.PaperSubmission);
        Assert.Contains(nodes, n => n.NodeType == DeadlineNodeType.Rebuttal);
        Assert.Contains(nodes, n => n.NodeType == DeadlineNodeType.DecisionNotification);
        var other = nodes.Single(n => n.NodeType == DeadlineNodeType.Other);
        Assert.Equal(TrackKind.Unknown, other.Track);
        Assert.Equal("workshop_deadline", other.RoundLabel);
    }

    [Fact]
    public void RawTimeAndTimezone_PreservedVerbatim()
    {
        var (snap, _) = Build(TestJson.SixConferences());
        var node = Dataset(snap!, "iclr").Nodes.Single(n => n.NodeType == DeadlineNodeType.PaperSubmission);
        Assert.Equal("2027-09-25 23:59:59", node.RawTime);
        Assert.Equal("AoE", node.RawTimezone);
        Assert.Equal("https://example.org/c", node.SourceUrl);
        Assert.Equal(FetchedAt, node.FetchedAt);
        Assert.Equal(VerificationStatus.Unverified, node.Verification);
        Assert.Equal(TimeSpan.Zero, node.UtcTime!.Value.Offset);
    }

    [Fact]
    public void MultiRound_MainTrackVsSpecialTrack_Classified()
    {
        string aamas = TestJson.Edition(2026, "aamas26", "UTC-12",
            """{"abstract_deadline":"2026-10-01 23:59:59","deadline":"2026-10-08 23:59:59","comment":"Main track"}""" + "," +
            """{"deadline":"2026-11-17 23:59:59","comment":"AAAI track"}""");
        var (snap, err) = Build(TestJson.SixConferences(aamas: aamas));

        Assert.Null(err);
        var nodes = Dataset(snap!, "aamas").Nodes;
        var main = nodes.Where(n => n.Track == TrackKind.MainRegular).ToList();
        var special = nodes.Where(n => n.Track == TrackKind.Special).ToList();
        Assert.Equal(2, main.Count); // Main track 的摘要+全文
        Assert.Single(special);
        Assert.Equal("AAAI track", special[0].RoundLabel ?? special[0].Note);
    }

    [Fact]
    public void SingleRound_InformationalComment_StaysMainRegular()
    {
        // ICML 实测样式：单轮但带说明性 comment，不得误判为特殊轮次
        string icml = TestJson.Edition(2026, "icml26", "UTC+0",
            """{"abstract_deadline":"2026-01-24 11:59:59","deadline":"2026-01-29 11:59:59","comment":"Paper Submissions Open on OpenReview Jan 08 2026 12:00AM UTC-0"}""");
        var (snap, err) = Build(TestJson.SixConferences(icml: icml));

        Assert.Null(err);
        var nodes = Dataset(snap!, "icml").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.MainRegular, n.Track));
        Assert.Contains(nodes, n => n.Note != null && n.Note.Contains("OpenReview"));
    }

    [Fact]
    public void Tbd_And_UnknownTimezone_ProduceExplicitIssues()
    {
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"abstract_deadline":"TBD","deadline":"2027-09-25 23:59:59"}""");
        string nips = TestJson.Edition(2027, "nips27", "Mars/Olympus",
            """{"deadline":"2027-05-07 11:59:00"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr, nips: nips));

        Assert.Null(err);
        var tbd = Dataset(snap!, "iclr").Nodes.Single(n => n.NodeType == DeadlineNodeType.AbstractRegistration);
        Assert.Null(tbd.UtcTime);
        Assert.Equal(ConversionIssue.NotProvided, tbd.Issue);
        var badTz = Dataset(snap!, "nips").Nodes.Single();
        Assert.Null(badTz.UtcTime);
        Assert.Equal(ConversionIssue.UnknownTimezone, badTz.Issue);
    }

    [Fact]
    public void Selector_Integration_AbstractExpiredSelectsFullPaper()
    {
        // 固定“现在”在摘要与全文之间：应选全文提交
        var now = new DateTimeOffset(2027, 9, 20, 0, 0, 0, TimeSpan.Zero);
        var (snap, _) = Build(TestJson.SixConferences());
        SelectionResult r = NextSubmissionSelector.Select(Dataset(snap!, "iclr").Nodes, now);
        Assert.Equal(SelectionKind.Upcoming, r.Kind);
        Assert.Equal(DeadlineNodeType.PaperSubmission, r.Node!.NodeType);
        Assert.Equal("全文提交", r.Label);
    }

    [Fact]
    public void TimelineNotArray_Rejected()
    {
        string iclr = """{"year":2027,"id":"iclr27","link":"https://example.org/c","timeline":{"deadline":"2027-09-25 23:59:59"},"timezone":"AoE"}""";
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));
        Assert.Null(snap);
        Assert.Contains("timeline", err);
    }

    [Fact]
    public void KnownDeadlineField_WrongType_Rejected()
    {
        // deadline 存在但不是字符串：类型错误，不得当作“未提供”
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"abstract_deadline":"2027-09-18 23:59:59","deadline":20270925}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));
        Assert.Null(snap);
        Assert.Contains("deadline", err);
    }

    [Fact]
    public void TimelineMissingOrEmpty_IsValid_NoNodes()
    {
        // timeline 缺失或空数组：合法（该届尚未公布），不报错、无节点
        string iclr = """{"year":2028,"id":"iclr28","link":"https://example.org/c","timezone":"AoE"}""";
        string nips = """{"year":2028,"id":"nips28","link":"https://example.org/c","timeline":[],"timezone":"AoE"}""";
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr, nips: nips));
        Assert.Null(err);
        Assert.Empty(Dataset(snap!, "iclr").Nodes);
        Assert.Empty(Dataset(snap!, "nips").Nodes);
        // 选择器对空节点集给出“待公布”
        Assert.Equal(SelectionKind.NotAnnounced,
            NextSubmissionSelector.Select(Dataset(snap!, "iclr").Nodes, FetchedAt).Kind);
    }

    [Fact]
    public void SingleRound_FastTrackComment_IsSpecial_NotInMenu()
    {
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"deadline":"2027-09-25 23:59:59","comment":"fast track"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));
        Assert.Null(err);
        var nodes = Dataset(snap!, "iclr").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.Special, n.Track));
        // 特殊轮次不得进入主菜单
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes, FetchedAt).Kind);
    }

    [Fact]
    public void SingleRound_WorkshopComment_IsSpecial()
    {
        string icml = TestJson.Edition(2027, "icml27", "UTC+0",
            """{"deadline":"2027-01-27 11:59:59","comment":"workshop papers only"}""");
        var (snap, _) = Build(TestJson.SixConferences(icml: icml));
        Assert.All(Dataset(snap!, "icml").Nodes, n => Assert.Equal(TrackKind.Special, n.Track));
    }

    [Fact]
    public void SingleRound_UnexplainableComment_IsUnknown_NotInMenu()
    {
        // 单轮但备注无法解释：不再默认主会，保守标身份不明，只进详情
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"deadline":"2027-09-25 23:59:59","comment":"Round A"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));
        Assert.Null(err);
        var nodes = Dataset(snap!, "iclr").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.Unknown, n.Track));
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes, FetchedAt).Kind);
    }

    [Fact]
    public void TrackAsVerb_InComment_NotJudgedSpecial()
    {
        // “track”作普通动词：不得仅凭该词判特殊（无其他可识别信息 → 身份不明）
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"deadline":"2027-09-25 23:59:59","comment":"Authors can track their submission status online"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));
        Assert.Null(err);
        var nodes = Dataset(snap!, "iclr").Nodes;
        Assert.DoesNotContain(nodes, n => n.Track == TrackKind.Special);
        Assert.All(nodes, n => Assert.Equal(TrackKind.Unknown, n.Track));
    }

    [Fact]
    public void BlueSkyIdeasComment_IsSpecial()
    {
        string nips = TestJson.Edition(2027, "nips27", "AoE",
            """{"deadline":"2027-05-07 11:59:00","comment":"Blue Sky Ideas"}""");
        var (snap, err) = Build(TestJson.SixConferences(nips: nips));
        Assert.Null(err);
        Assert.All(Dataset(snap!, "nips").Nodes,
            n => Assert.Equal(TrackKind.Special, n.Track));
    }

    [Fact]
    public void RegularTrackComment_IsMainRegular()
    {
        string aaai = TestJson.Edition(2027, "aaai27", "UTC-12",
            """{"deadline":"2027-07-28 23:59:59","comment":"Regular track"}""");
        var (snap, err) = Build(TestJson.SixConferences(aaai: aaai));
        Assert.Null(err);
        Assert.All(Dataset(snap!, "aaai").Nodes,
            n => Assert.Equal(TrackKind.MainRegular, n.Track));
    }

    [Fact]
    public void SingleRound_DemoOnOpenReview_IsUnknown_NotInMenu()
    {
        // 实测反例：仅提及 OpenReview 的 demo 备注不得判主会
        string nips = TestJson.Edition(2027, "nips27", "AoE",
            """{"deadline":"2027-05-07 11:59:00","comment":"Demo submissions on OpenReview"}""");
        var (snap, err) = Build(TestJson.SixConferences(nips: nips));

        Assert.Null(err);
        var nodes = Dataset(snap!, "nips").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.Unknown, n.Track));
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes, FetchedAt).Kind);
    }

    [Fact]
    public void NonIcml_IcmlVerifiedPhrasing_IsUnknown()
    {
        // 已核实措辞只适用于 ICML；其他会议出现同样措辞不猜主会
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"deadline":"2027-09-25 23:59:59","comment":"Paper Submissions Open on OpenReview Sep 01 2026 12:00AM UTC-0"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));

        Assert.Null(err);
        Assert.All(Dataset(snap!, "iclr").Nodes,
            n => Assert.Equal(TrackKind.Unknown, n.Track));
    }

    [Fact]
    public void SingleRound_AbstractOnlyComment_NotAssumedMain()
    {
        // 仅凭宽泛词 “abstract” 不足以证明是主会说明性备注：保守标身份不明，
        // 避免把 “Extended abstracts” 一类特殊轮次误收进主菜单
        string icml = TestJson.Edition(2027, "icml27", "UTC+0",
            """{"deadline":"2027-01-27 11:59:59","comment":"Abstract submission is required before the paper deadline"}""");
        var (snap, err) = Build(TestJson.SixConferences(icml: icml));
        Assert.Null(err);
        Assert.All(Dataset(snap!, "icml").Nodes,
            n => Assert.Equal(TrackKind.Unknown, n.Track));
    }

    [Fact]
    public void MultiRound_NoCommentRound_IsUnknown_NotInMenu()
    {
        // 已过期且明确 Main track ＋ 未来但无 comment：未来轮次身份不明，不得进菜单
        var now = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        string aamas = TestJson.Edition(2027, "aamas27", "AoE",
            """{"deadline":"2026-10-08 23:59:59","comment":"Main track"}""" + "," +
            """{"deadline":"2026-11-17 23:59:59"}""");
        var (snap, err) = Build(TestJson.SixConferences(aamas: aamas));

        Assert.Null(err);
        var nodes = Dataset(snap!, "aamas").Nodes;
        var noComment = nodes.Where(n => n.Note is null).ToList();
        Assert.NotEmpty(noComment);
        Assert.All(noComment, n => Assert.Equal(TrackKind.Unknown, n.Track));
        Assert.Equal(DeadlineNodeType.PaperSubmission,
            nodes.Single(n => n.Note == "Main track").NodeType);
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes, now).Kind);
    }

    [Fact]
    public void Comment_WithBothMainAndSpecialClues_IsUnknown_NotInMenu()
    {
        // 主会与特殊线索同时出现：不得因主会词先匹配而进主菜单，保守标身份不明
        string iclr = TestJson.Edition(2027, "iclr27", "AoE",
            """{"deadline":"2027-09-25 23:59:59","comment":"Main track / fast track"}""");
        var (snap, err) = Build(TestJson.SixConferences(iclr: iclr));

        Assert.Null(err);
        var nodes = Dataset(snap!, "iclr").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.Unknown, n.Track));
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes, FetchedAt).Kind);
    }

    [Fact]
    public void MultiRound_UnrecognizedComment_IsUnknown_NotInMenu()
    {
        // 多轮但 comment 无法区分主会/特殊：身份不明，不得猜成特殊或主会
        string aaai = TestJson.Edition(2027, "aaai27", "UTC-12",
            """{"deadline":"2027-07-28 23:59:59","comment":"Round A"}""" + "," +
            """{"deadline":"2027-08-15 23:59:59","comment":"Round B"}""");
        var (snap, err) = Build(TestJson.SixConferences(aaai: aaai));
        Assert.Null(err);
        var nodes = Dataset(snap!, "aaai").Nodes;
        Assert.All(nodes, n => Assert.Equal(TrackKind.Unknown, n.Track));
        Assert.NotEqual(SelectionKind.Upcoming,
            NextSubmissionSelector.Select(nodes!, FetchedAt).Kind);
    }

    [Fact]
    public void Selector_Integration_SpecialTrackNeverInMenu()
    {
        // 主会轮已过期、特殊轮次在未来：菜单不得选特殊轮次
        var now = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        string aamas = TestJson.Edition(2026, "aamas26", "UTC-12",
            """{"abstract_deadline":"2026-10-01 23:59:59","deadline":"2026-10-08 23:59:59","comment":"Main track"}""" + "," +
            """{"deadline":"2026-11-17 23:59:59","comment":"AAAI track"}""");
        var (snap, _) = Build(TestJson.SixConferences(aamas: aamas));
        SelectionResult r = NextSubmissionSelector.Select(Dataset(snap!, "aamas").Nodes, now);
        Assert.NotEqual(SelectionKind.Upcoming, r.Kind);
    }
}
