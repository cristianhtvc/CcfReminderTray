using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using CcfReminderTray;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>
/// 详情窗修正交互的界面回归（真实 DetailsForm，独立 STA 线程；修正存储落在随机
/// 临时目录——不触碰真实 overrides.json）：
/// 候选歧义/文件损坏时“修正此节点”禁用；上游缺失的修正以孤儿行保留、可查看可恢复，
/// 且在无匹配节点期间不作为有效时间展示。
/// </summary>
public class DetailsFormOverrideUiTests : IDisposable
{
    private static readonly DateTimeOffset UpstreamUtc = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CorrectedUtc = new(2026, 2, 5, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FakeNow = new(2026, 10, 14, 1, 2, 3, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "CcfReminderTests", Guid.NewGuid().ToString("N"));

    public DetailsFormOverrideUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private DeadlineOverrideManager NewManager() =>
        new(new OverridesStore(Path.Combine(_dir, "overrides.json")), () => FakeNow);

    private static DeadlineNode Node(
        DeadlineNodeType type = DeadlineNodeType.PaperSubmission,
        string? editionId = "icml26",
        DateTimeOffset? utc = null,
        string rawTime = "2026-01-22 19:59") =>
        new()
        {
            ConferenceId = "icml",
            Year = 2026,
            NodeType = type,
            Track = TrackKind.MainRegular,
            EditionId = editionId,
            RawTime = rawTime,
            RawTimezone = "PST",
            UtcTime = utc ?? UpstreamUtc,
            SourceUrl = "https://example.org/cfp",
        };

    private static ConferenceDataSnapshot Snapshot(params DeadlineNode[] nodes) =>
        new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "test",
            new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) });

    private static void SaveOverride(DeadlineOverrideManager mgr, ConferenceDataSnapshot snap, DeadlineNode node)
    {
        Assert.True(mgr.TrySave(snap, node, CorrectedUtc, "2026-02-05 11:59", "UTC+8",
            "官网延期", "https://example.org", out string? error), error);
    }

    /// <summary>WinForms 控件要求 STA：在独立 STA 线程中执行，断言异常原样回传。</summary>
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

    [Fact]
    public void UniqueCandidate_EditButtonEnabled()
    {
        DeadlineOverrideManager mgr = NewManager();
        ConferenceDataSnapshot snap = Snapshot(Node());
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => snap);
            form.UpdateContent(snap, null);
            form.SelectRowAt(0);
            Assert.True(form.EditOverrideButton.Enabled);
        });
    }

    [Fact]
    public void IneligibleNode_EditButtonDisabled()
    {
        DeadlineOverrideManager mgr = NewManager();
        ConferenceDataSnapshot snap = Snapshot(Node(DeadlineNodeType.Rebuttal));
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => snap);
            form.UpdateContent(snap, null);
            form.SelectRowAt(0);
            Assert.False(form.EditOverrideButton.Enabled);
        });
    }

    [Fact]
    public void AmbiguousCandidates_EditDisabled_AndEffectiveColumnShowsUpstream()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node(editionId: null);
        SaveOverride(mgr, Snapshot(node), node); // 唯一时保存成功

        // 上游后来出现同键第二个候选（届次标识缺失的多轮）
        ConferenceDataSnapshot ambiguous = Snapshot(Node(editionId: null), Node(editionId: null));
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => ambiguous);
            form.UpdateContent(ambiguous, null);

            Assert.Equal(2, form.RowCount);
            form.SelectRowAt(0);
            Assert.False(form.EditOverrideButton.Enabled); // 候选不唯一：禁止修正入口

            // 修正保留但暂不应用：面板明示，横幅给出待核对计数
            string upstreamLocal = UpstreamUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            for (int i = 0; i < 2; i++)
            {
                form.SelectRowAt(i);
                Assert.False(form.EditOverrideButton.Enabled);
            }
            Assert.Contains("暂不应用", form.DetailPanelText);
            Assert.Contains(upstreamLocal, form.DetailPanelText);
            Assert.Contains("待核对", form.BannerText);
        });
    }

    [Fact]
    public void CorruptedFile_EditAndRemoveDisabled_CorruptionPanelShown()
    {
        File.WriteAllText(Path.Combine(_dir, "overrides.json"), "{ 损坏内容");
        DeadlineOverrideManager mgr = NewManager();
        Assert.True(mgr.IsCorrupted);
        ConferenceDataSnapshot snap = Snapshot(Node());
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => snap);
            form.UpdateContent(snap, null);
            form.SelectRowAt(0);

            Assert.False(form.EditOverrideButton.Enabled);
            Assert.False(form.RemoveOverrideButton.Enabled);
            Assert.True(form.ReinitializeVisible);
            Assert.True(form.DetailPanelVisible);
            Assert.Contains("损坏", form.DetailPanelText);
            Assert.Contains("损坏", form.BannerText);
        });
    }

    [Fact]
    public void OrphanOverride_RowKept_ViewableAndRemovable_NotShownAsEffective()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        SaveOverride(mgr, Snapshot(node), node);

        ConferenceDataSnapshot upstreamGone = Snapshot(); // 上游刷新后节点消失
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => upstreamGone);
            form.UpdateContent(upstreamGone, null);

            Assert.Equal(1, form.RowCount); // 孤儿修正行保留在列表中
            form.SelectRowAt(0);

            // 面板说明：保留、可处理，但在无匹配节点期间不作为有效截止时间
            Assert.True(form.DetailPanelVisible);
            Assert.Contains("仍保留", form.DetailPanelText);
            Assert.Contains("不会作为有效截止时间", form.DetailPanelText);
            Assert.Contains("已找不到该节点", form.DetailPanelText);

            Assert.True(form.RemoveOverrideButton.Enabled); // 可恢复/处理
            Assert.False(form.EditOverrideButton.Enabled);  // 无节点可编辑

            // 横幅提示有待核对修正
            Assert.Contains("待核对", form.BannerText);
        });

        // 恢复（删除）操作本身在管理器上可用：不依赖上游节点存在
        Assert.True(mgr.TryRemove(mgr.Overrides[0].NodeKey, out _));
        Assert.Empty(mgr.Overrides);
    }

    [Fact]
    public void SaveOverride_SelectionPreserved_ThreeWayPanelImmediatelyVisible()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        ConferenceDataSnapshot snap = Snapshot(node);
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => snap);
            form.UpdateContent(snap, null);
            form.SelectRowAt(0);
            Assert.False(form.DetailPanelVisible); // 尚无修正：无面板

            // 模拟编辑窗保存：管理器保存触发 Changed → 详情窗重建列表
            SaveOverride(mgr, snap, node);

            // 选中保留在刚保存的节点行上，三方对照立即可见，无需用户再点一次
            Assert.Equal(0, form.SelectedIndex);
            Assert.True(form.DetailPanelVisible);
            Assert.Contains("你的修正值", form.DetailPanelText);
            Assert.Contains("修正时上游值", form.DetailPanelText);
        });
    }

    [Fact]
    public void UpstreamChanged_RowMarkedAndPanelShowsThreeWayDiff()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode atCorrection = Node();
        SaveOverride(mgr, Snapshot(atCorrection), atCorrection);

        // 上游改期 + 改写法
        DeadlineNode rescheduled = Node(utc: UpstreamUtc.AddDays(14), rawTime: "2026-02-04 19:59");
        ConferenceDataSnapshot changed = Snapshot(rescheduled);
        OnStaThread(() =>
        {
            using var form = new DetailsForm(mgr, () => changed);
            form.UpdateContent(changed, null);
            form.SelectRowAt(0);

            Assert.Contains("三方差异", form.DetailPanelText);
            Assert.Contains("修正时上游值", form.DetailPanelText);
            Assert.Contains("最新上游值", form.DetailPanelText);
            Assert.Contains("你的修正值", form.DetailPanelText);
            Assert.Contains("2026-01-22 19:59", form.DetailPanelText); // 修正时上游原文
            Assert.Contains("2026-02-04 19:59", form.DetailPanelText); // 最新上游原文
            Assert.True(form.EditOverrideButton.Enabled); // 候选唯一：可再修正
        });
    }
}
