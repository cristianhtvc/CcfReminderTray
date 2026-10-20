using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using CcfReminderTray;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>
/// 编辑窗“按保存那一刻的最新快照核对”的回归（真实 OverrideEditForm，独立 STA 线程；
/// 存储落在随机临时目录）：打开窗口后后台刷新替换了快照时，唯一性与“修正时上游值”
/// 必须按最新快照留证，绝不悄悄以过期快照保存。
/// </summary>
public class OverrideEditFormTests : IDisposable
{
    private static readonly DateTimeOffset OldUpstreamUtc = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NewUpstreamUtc = new(2026, 1, 29, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FakeNow = new(2026, 10, 14, 1, 2, 3, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "CcfReminderTests", Guid.NewGuid().ToString("N"));

    public OverrideEditFormTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private DeadlineOverrideManager NewManager() =>
        new(new OverridesStore(Path.Combine(_dir, "overrides.json")), () => FakeNow);

    private static DeadlineNode Node(
        DateTimeOffset? utc = null,
        string rawTime = "2026-01-22 19:59",
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
            UtcTime = utc ?? OldUpstreamUtc,
        };

    private static ConferenceDataSnapshot Snapshot(params DeadlineNode[] nodes) =>
        new(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), "test",
            new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) });

    private static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error is null, error?.ToString());
    }

    private static void FillValidInputs(OverrideEditForm form)
    {
        form.TimeInput.Text = "2026-02-05 11:59";
        form.TimezoneInput.Text = "UTC+8";
        form.ReasonInput.Text = "官网延期";
        form.SourceInput.Text = "https://example.org";
    }

    [Fact]
    public void Save_AfterBackgroundRefresh_CapturesLatestUpstreamTriple_NotStale()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode openedWith = Node(); // 窗口打开时的旧快照节点
        ConferenceDataSnapshot stale = Snapshot(openedWith);
        // 打开期间后台刷新：上游改期并改了原始写法
        ConferenceDataSnapshot refreshed = Snapshot(Node(NewUpstreamUtc, "2026-01-28 19:59"));
        ConferenceDataSnapshot current = stale;

        OnStaThread(() =>
        {
            using var form = new OverrideEditForm(
                openedWith, "ICML 2026", () => current, mgr, existing: null, TimeZoneInfo.Local);
            FillValidInputs(form);
            current = refreshed; // 模拟保存前后台刷新完成

            Assert.True(form.TrySaveWithLatestSnapshot(out string? error), error);
        });

        DeadlineOverride ov = Assert.Single(mgr.Overrides);
        // “修正时上游值”必须是保存那一刻的最新值，不是窗口打开时的旧值
        Assert.Equal(NewUpstreamUtc, ov.UpstreamUtcAtCorrection);
        Assert.Equal("2026-01-28 19:59", ov.UpstreamRawTimeAtCorrection);
        Assert.Equal(new DateTimeOffset(2026, 2, 5, 3, 59, 0, TimeSpan.Zero), ov.CorrectedUtc);
    }

    [Fact]
    public void Save_LatestSnapshotAmbiguous_Refused_NothingPersisted()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode openedWith = Node(editionId: null);
        // 保存时最新快照出现同键第二个候选
        ConferenceDataSnapshot ambiguous = Snapshot(Node(editionId: null), Node(editionId: null));

        OnStaThread(() =>
        {
            using var form = new OverrideEditForm(
                openedWith, "ICML 2026", () => ambiguous, mgr, existing: null, TimeZoneInfo.Local);
            FillValidInputs(form);

            Assert.False(form.TrySaveWithLatestSnapshot(out string? error));
            Assert.Contains("候选", error);
        });

        Assert.Empty(mgr.Overrides); // 未悄悄保存
    }

    [Fact]
    public void Save_LatestSnapshotNodeGone_Refused()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode openedWith = Node();
        ConferenceDataSnapshot empty = Snapshot();

        OnStaThread(() =>
        {
            using var form = new OverrideEditForm(
                openedWith, "ICML 2026", () => empty, mgr, existing: null, TimeZoneInfo.Local);
            FillValidInputs(form);

            Assert.False(form.TrySaveWithLatestSnapshot(out string? error));
            Assert.Contains("找不到", error);
        });

        Assert.Empty(mgr.Overrides);
    }

    [Fact]
    public void Save_NoSnapshotAtAll_Refused()
    {
        DeadlineOverrideManager mgr = NewManager();
        OnStaThread(() =>
        {
            using var form = new OverrideEditForm(
                Node(), "ICML 2026", () => null, mgr, existing: null, TimeZoneInfo.Local);
            FillValidInputs(form);

            Assert.False(form.TrySaveWithLatestSnapshot(out string? error));
            Assert.Contains("没有可用的数据快照", error);
        });

        Assert.Empty(mgr.Overrides);
    }

    [Fact]
    public void Save_UnconvertibleInput_ReturnsReasonWithoutSaving()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        ConferenceDataSnapshot snap = Snapshot(node);

        OnStaThread(() =>
        {
            using var form = new OverrideEditForm(
                node, "ICML 2026", () => snap, mgr, existing: null, TimeZoneInfo.Local);
            FillValidInputs(form);
            form.TimeInput.Text = "下周五";

            Assert.False(form.TrySaveWithLatestSnapshot(out string? error));
            Assert.Contains("格式", error);
        });

        Assert.Empty(mgr.Overrides);
    }
}
