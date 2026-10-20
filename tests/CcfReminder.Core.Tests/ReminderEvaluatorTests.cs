using CcfReminder.Core;
using CcfReminder.Core.Data;
using CcfReminder.Core.Reminders;
using Xunit;

namespace CcfReminder.Core.Tests;

public class ReminderEvaluatorTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    internal const string Today = "2026-10-13";

    internal static DeadlineNode Node(
        string conf = "icml",
        DeadlineNodeType type = DeadlineNodeType.PaperSubmission,
        TrackKind track = TrackKind.MainRegular,
        DateTimeOffset? utc = null,
        ConversionIssue? issue = null) =>
        new()
        {
            ConferenceId = conf,
            Year = 2027,
            NodeType = type,
            Track = track,
            EditionId = conf + "27",
            UtcTime = utc,
            Issue = issue ?? (utc is null ? ConversionIssue.NotProvided : null),
        };

    internal static ConferenceDataset Dataset(string conf, params DeadlineNode[] nodes) =>
        new(ConferenceCatalog.All.First(c => c.Id == conf), "A", nodes);

    internal static ReminderSettings Enabled(params int[] thresholds) =>
        new(true, thresholds.ToHashSet(), false);

    private static IReadOnlyList<DueReminder> Eval(
        IReadOnlyList<ConferenceDataset> datasets,
        ReminderSettings settings,
        ReminderState? state = null,
        DateTimeOffset? now = null,
        string today = Today) =>
        ReminderEvaluator.Evaluate(datasets, settings, state ?? ReminderState.Empty, now ?? Now, today);

    [Fact]
    public void Disabled_NotifiesNothing()
    {
        var settings = new ReminderSettings(false, new HashSet<int> { 30, 14, 7, 3, 1 }, false);
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddDays(2))) }, settings);
        Assert.Empty(due);
    }

    [Fact]
    public void ExactlyAtThreshold_IsDue()
    {
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddDays(30))) }, Enabled(30));
        var d = Assert.Single(due);
        Assert.Equal(30, d.ThresholdDays);
        Assert.Equal("ICML", d.ConferenceShortName);
    }

    [Fact]
    public void BeyondAllThresholds_Nothing()
    {
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddDays(31))) }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }

    [Fact]
    public void CatchUp_OnlyMostUrgentBand()
    {
        // 停开期间跨过 30/14/7/3 多档：只补发最紧迫的 3 天档
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddDays(2))) }, Enabled(30, 14, 7, 3, 1));
        var d = Assert.Single(due);
        Assert.Equal(3, d.ThresholdDays);
    }

    [Fact]
    public void SameDay_MultipleConferences_AllInOneBatch()
    {
        var due = Eval(new[]
        {
            Dataset("icml", Node(utc: Now.AddDays(2))),
            Dataset("nips", Node(conf: "nips", utc: Now.AddDays(6))),
        }, Enabled(30, 14, 7, 3, 1));
        Assert.Equal(2, due.Count);
        Assert.Contains(due, d => d is { ConferenceShortName: "ICML", ThresholdDays: 3 });
        Assert.Contains(due, d => d is { ConferenceShortName: "NeurIPS", ThresholdDays: 7 });
    }

    [Fact]
    public void DailyCap_AlreadySummarizedToday_Nothing()
    {
        var state = new ReminderState(new Dictionary<string, IReadOnlySet<int>>(), Today);
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddDays(2))) }, Enabled(30, 14, 7, 3, 1), state);
        Assert.Empty(due);
    }

    [Fact]
    public void AlreadyNotifiedBand_Skipped_AfterRestart()
    {
        DeadlineNode node = Node(utc: Now.AddDays(2));
        var state = new ReminderState(
            new Dictionary<string, IReadOnlySet<int>>
            {
                [ReminderEvaluator.NodeKey(node)] = new HashSet<int> { 3, 7, 14, 30 },
            },
            LastSummaryLocalDate: null);
        var due = Eval(new[] { Dataset("icml", node) }, Enabled(30, 14, 7, 3, 1), state);
        Assert.Empty(due);
    }

    [Fact]
    public void UpdatedDeadline_TreatedAsNewKey_NotifiesAgain()
    {
        DeadlineNode oldNode = Node(utc: Now.AddDays(4));
        var state = new ReminderState(
            new Dictionary<string, IReadOnlySet<int>>
            {
                [ReminderEvaluator.NodeKey(oldNode)] = new HashSet<int> { 7, 14, 30 },
            },
            LastSummaryLocalDate: null);
        // 上游把截止时间改到晚一天：键不同，应重新提醒
        DeadlineNode newNode = Node(utc: Now.AddDays(5));
        var due = Eval(new[] { Dataset("icml", newNode) }, Enabled(30, 14, 7, 3, 1), state);
        Assert.Single(due);
    }

    [Fact]
    public void DeadlineExactlyNow_Skipped()
    {
        // 截止时间与当前时间相等：不提醒
        var due = Eval(new[] { Dataset("icml", Node(utc: Now)) }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }

    [Fact]
    public void Expired_Skipped()
    {
        var due = Eval(new[] { Dataset("icml", Node(utc: Now.AddHours(-1))) }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }

    [Fact]
    public void RebuttalAndDecision_Skipped()
    {
        var due = Eval(new[]
        {
            Dataset("icml",
                Node(type: DeadlineNodeType.Rebuttal, utc: Now.AddDays(1)),
                Node(type: DeadlineNodeType.DecisionNotification, utc: Now.AddDays(1))),
        }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }

    [Fact]
    public void SpecialAndUnknownTrack_Skipped()
    {
        var due = Eval(new[]
        {
            Dataset("icml",
                Node(track: TrackKind.Special, utc: Now.AddDays(1)),
                Node(track: TrackKind.Unknown, utc: Now.AddDays(1))),
        }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }

    [Fact]
    public void TbdAndInconsistent_Skipped()
    {
        DeadlineNode tbd = Node(); // utc null + NotProvided
        DeadlineNode inconsistent = new()
        {
            ConferenceId = "icml",
            Year = 2027,
            NodeType = DeadlineNodeType.PaperSubmission,
            Track = TrackKind.MainRegular,
            EditionId = "icml27",
            UtcTime = Now.AddDays(1),
            Issue = ConversionIssue.InvalidFormat, // 与 UtcTime 同时有值：不可靠
        };
        var due = Eval(new[] { Dataset("icml", tbd, inconsistent) }, Enabled(30, 14, 7, 3, 1));
        Assert.Empty(due);
    }
}
