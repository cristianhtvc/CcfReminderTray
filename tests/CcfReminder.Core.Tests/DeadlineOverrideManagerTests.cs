using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>存储落在每次测试随机新建的 %TEMP% 子目录；时钟注入，不读系统时钟。</summary>
public class DeadlineOverrideManagerTests : IDisposable
{
    private static readonly DateTimeOffset UpstreamUtc = new(2026, 1, 22, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CorrectedUtc = new(2026, 2, 5, 3, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FakeNow = new(2026, 10, 14, 1, 2, 3, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "CcfReminderTests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public DeadlineOverrideManagerTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "overrides.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private DeadlineOverrideManager NewManager() =>
        new(new OverridesStore(_path), () => FakeNow);

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

    private static ConferenceDataSnapshot Snapshot(params DeadlineNode[] nodes) =>
        new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), "test",
            new[] { new ConferenceDataset(new ConferenceInfo("icml", "ICML", "ICML"), null, nodes) });

    private bool Save(DeadlineOverrideManager mgr, ConferenceDataSnapshot snap, DeadlineNode node,
        out string? error, string reason = "官网延期", string source = "https://example.org",
        DateTimeOffset? utc = null) =>
        mgr.TrySave(snap, node, utc ?? CorrectedUtc, "2026-02-05 11:59", "UTC+8",
            reason, source, out error);

    [Fact]
    public void TrySave_HappyPath_CapturesUpstreamTriple_AndFiresChanged()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        int changedCount = 0;
        mgr.Changed += (_, _) => changedCount++;

        Assert.True(Save(mgr, Snapshot(node), node, out string? error));
        Assert.Null(error);
        Assert.Equal(1, changedCount);

        DeadlineOverride ov = Assert.Single(mgr.Overrides);
        Assert.Equal("icml|icml26|PaperSubmission|MainRegular", ov.NodeKey);
        Assert.Equal(CorrectedUtc, ov.CorrectedUtc);
        Assert.Equal(UpstreamUtc, ov.UpstreamUtcAtCorrection);
        Assert.Equal("2026-01-22 19:59", ov.UpstreamRawTimeAtCorrection);
        Assert.Equal("PST", ov.UpstreamRawTimezoneAtCorrection);
        Assert.Equal(FakeNow, ov.CorrectedAtUtc); // 注入时钟，而非系统时钟
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void TrySave_SameKeyTwice_ReplacesNotDuplicates()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        ConferenceDataSnapshot snap = Snapshot(node);

        Assert.True(Save(mgr, snap, node, out _));
        Assert.True(Save(mgr, snap, node, out _, utc: CorrectedUtc.AddDays(3)));

        DeadlineOverride ov = Assert.Single(mgr.Overrides);
        Assert.Equal(CorrectedUtc.AddDays(3), ov.CorrectedUtc);
    }

    [Fact]
    public void Restart_PersistsOverrides()
    {
        DeadlineNode node = Node();
        Assert.True(Save(NewManager(), Snapshot(node), node, out _));

        DeadlineOverrideManager afterRestart = NewManager();
        DeadlineOverride ov = Assert.Single(afterRestart.Overrides);
        Assert.Equal(CorrectedUtc, ov.CorrectedUtc);
    }

    [Fact]
    public void TryRemove_ExistingKey_RemovesAndFiresChanged()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        Assert.True(Save(mgr, Snapshot(node), node, out _));
        int changedCount = 0;
        mgr.Changed += (_, _) => changedCount++;

        Assert.True(mgr.TryRemove(OverrideNodeKey.TryBuild(node)!, out string? error));
        Assert.Null(error);
        Assert.Empty(mgr.Overrides);
        Assert.Equal(1, changedCount);
        // 磁盘上也是空表（文件仍在，版本完好）
        Assert.Equal(OverridesLoadStatus.Loaded, new OverridesStore(_path).Load().Status);
        Assert.Empty(new OverridesStore(_path).Load().Overrides);
    }

    [Fact]
    public void TryRemove_MissingKey_FailsWithReason()
    {
        DeadlineOverrideManager mgr = NewManager();
        Assert.False(mgr.TryRemove("icml|icml26|PaperSubmission|MainRegular", out string? error));
        Assert.Contains("没有已保存的修正", error);
    }

    [Theory]
    [InlineData("", "https://example.org")]
    [InlineData("官网延期", "")]
    [InlineData("  ", "https://example.org")]
    public void TrySave_MissingReasonOrSource_Refused_NoFileWritten(string reason, string source)
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node();
        Assert.False(Save(mgr, Snapshot(node), node, out string? error, reason, source));
        Assert.NotNull(error);
        Assert.Empty(mgr.Overrides);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void TrySave_IneligibleNode_Refused()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode rebuttal = Node(DeadlineNodeType.Rebuttal);
        Assert.False(Save(mgr, Snapshot(rebuttal), rebuttal, out string? error));
        Assert.Contains("不可修正", error);
    }

    [Fact]
    public void TrySave_DuplicateCandidates_Refused()
    {
        DeadlineOverrideManager mgr = NewManager();
        DeadlineNode node = Node(editionId: null);
        ConferenceDataSnapshot snap = Snapshot(node, Node(editionId: null));
        Assert.False(Save(mgr, snap, node, out string? error));
        Assert.Contains("候选", error);
        Assert.Empty(mgr.Overrides);
    }

    [Fact]
    public void CorruptedFile_BlocksWrites_AndPreservesOriginal()
    {
        File.WriteAllText(_path, "{ 损坏内容");
        string before = File.ReadAllText(_path);

        DeadlineOverrideManager mgr = NewManager();
        Assert.True(mgr.IsCorrupted);
        Assert.NotNull(mgr.CorruptionError);
        Assert.Empty(mgr.Overrides); // 安全回退视图

        DeadlineNode node = Node();
        Assert.False(Save(mgr, Snapshot(node), node, out string? saveError));
        Assert.Contains("损坏", saveError);
        Assert.False(mgr.TryRemove("任意键", out string? removeError));
        Assert.Contains("损坏", removeError);

        // 任何写入尝试都没有覆盖损坏文件
        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void ReinitializeAfterCorruption_QuarantinesBackup_ThenUnblocksWrites()
    {
        File.WriteAllText(_path, "{ 损坏内容");
        DeadlineOverrideManager mgr = NewManager();
        int changedCount = 0;
        mgr.Changed += (_, _) => changedCount++;

        Assert.True(mgr.TryReinitializeAfterCorruption(out string? backupPath, out string? error));
        Assert.Null(error);
        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath)); // 损坏文件改名留证，不删除
        Assert.Equal("{ 损坏内容", File.ReadAllText(backupPath));
        Assert.False(mgr.IsCorrupted);
        Assert.Equal(1, changedCount);

        DeadlineNode node = Node();
        Assert.True(Save(mgr, Snapshot(node), node, out _));
        Assert.Single(mgr.Overrides);
    }

    [Fact]
    public void Reinitialize_WhenNotCorrupted_Refused()
    {
        DeadlineOverrideManager mgr = NewManager();
        Assert.False(mgr.TryReinitializeAfterCorruption(out _, out string? error));
        Assert.Contains("无需", error);
    }

    [Fact]
    public void TrySave_DiskFailure_KeepsMemoryAndDiskUnchanged()
    {
        // 目录位置被同名文件占用 → Save 必然失败
        string blockingDir = Path.Combine(_dir, "blocked");
        File.WriteAllText(blockingDir, "占位");
        var mgr = new DeadlineOverrideManager(
            new OverridesStore(Path.Combine(blockingDir, "overrides.json")), () => FakeNow);

        DeadlineNode node = Node();
        Assert.False(Save(mgr, Snapshot(node), node, out string? error));
        Assert.Contains("写入失败", error);
        Assert.Empty(mgr.Overrides); // 内存未更新
    }

    // ---- 重新初始化的失败路径（问题 2 回归；故障注入只作用于临时目录内的 store）----
    // 新流程＝先复制备份、再原子替换原件：安全性不依赖故障时还能写入任何标记

    [Fact]
    public void Reinitialize_BackupCopyFails_OriginalIntact_StillCorruptedAfterRestart()
    {
        File.WriteAllText(_path, "{ 损坏内容");
        var store = new OverridesStore(_path);
        var mgr = new DeadlineOverrideManager(store, () => FakeNow);
        Assert.True(mgr.IsCorrupted);

        store.BeforeBackupCopyForTest = () => throw new IOException("注入：备份复制失败");
        Assert.False(mgr.TryReinitializeAfterCorruption(out string? backupPath, out string? error));
        Assert.Null(backupPath);
        // 提示只陈述实际发生的步骤：备份失败，无任何“已备份/已留标记”的虚报
        Assert.Contains("备份损坏文件失败", error);
        Assert.Contains("注入：备份复制失败", error);
        Assert.DoesNotContain("已把你的修正备份到", error);

        Assert.Equal("{ 损坏内容", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".reinit-pending"));
        Assert.True(mgr.IsCorrupted);
        Assert.True(new DeadlineOverrideManager(new OverridesStore(_path), () => FakeNow).IsCorrupted);
    }

    [Fact]
    public void Reinitialize_CopyOkButCommitFails_OriginalStillThere_RestartCorrupted_NoMarkerNeeded()
    {
        File.WriteAllText(_path, "{ 损坏内容");
        var store = new OverridesStore(_path);
        var mgr = new DeadlineOverrideManager(store, () => FakeNow);

        // 注入“备份复制成功、空表提交失败”（一次性故障）
        store.BeforeSaveCommitForTest = () =>
        {
            store.BeforeSaveCommitForTest = null;
            throw new IOException("注入：空表写入失败");
        };
        Assert.False(mgr.TryReinitializeAfterCorruption(out string? backupPath, out string? error));

        // 提示如实陈述：备份在 X；原件未被替换
        Assert.NotNull(backupPath);
        Assert.True(File.Exists(backupPath));
        Assert.Equal("{ 损坏内容", File.ReadAllText(backupPath));
        Assert.Contains(backupPath, error);
        Assert.Contains("未被替换", error);
        Assert.DoesNotContain("待处理标记", error); // 新流程不声称、也不需要标记

        // 关键安全性：损坏原件仍在原路径——即使标记永远写不进去，重启也必判 Corrupted
        Assert.True(File.Exists(_path));
        Assert.Equal("{ 损坏内容", File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".reinit-pending"));
        Assert.True(mgr.IsCorrupted);

        var afterRestart = new DeadlineOverrideManager(new OverridesStore(_path), () => FakeNow);
        Assert.True(afterRestart.IsCorrupted); // 不依赖标记：原件本身就是损坏的
        DeadlineNode node = Node();
        Assert.False(Save(afterRestart, Snapshot(node), node, out string? saveError));
        Assert.Contains("损坏", saveError); // 封锁仍生效

        // 重试（故障已解除）：原件在 → 再复制一份新备份，原子替换为空表
        Assert.True(afterRestart.TryReinitializeAfterCorruption(out string? secondBackup, out string? retryError));
        Assert.Null(retryError);
        Assert.NotNull(secondBackup);
        Assert.NotEqual(backupPath, secondBackup); // 唯一命名，不覆盖第一份留证
        Assert.False(afterRestart.IsCorrupted);
        Assert.Equal(OverridesLoadStatus.Loaded, new OverridesStore(_path).Load().Status);
        Assert.Empty(new OverridesStore(_path).Load().Overrides);
        Assert.True(Save(afterRestart, Snapshot(node), node, out _));
        Assert.Single(afterRestart.Overrides);
    }

    [Fact]
    public void Reinitialize_LegacyPendingMarkerWithoutMainFile_StillCorrupted_ThenRecovers()
    {
        // 旧版中断遗留的现场：无主文件、有 reinit-pending 标记（兼容路径）
        File.WriteAllText(_path + ".reinit-pending", "你的修正已备份到：某处");
        var mgr = new DeadlineOverrideManager(new OverridesStore(_path), () => FakeNow);
        Assert.True(mgr.IsCorrupted);
        Assert.Contains("中途失败", mgr.CorruptionError);

        Assert.True(mgr.TryReinitializeAfterCorruption(out string? backupPath, out string? error));
        Assert.Null(error);
        Assert.Null(backupPath); // 没有原件可备份
        Assert.False(mgr.IsCorrupted);
        Assert.False(File.Exists(_path + ".reinit-pending")); // 标记已清除
        Assert.Equal(OverridesLoadStatus.Loaded, new OverridesStore(_path).Load().Status);
    }

    [Fact]
    public void Reinitialize_LegacyPending_CommitFailsAgain_MarkerRetained_RestartStillCorrupted()
    {
        // 旧版中断遗留 + 本次写空表仍失败：标记保留，重启仍判损坏（不虚报新状态）
        File.WriteAllText(_path + ".reinit-pending", "你的修正已备份到：某处");
        var store = new OverridesStore(_path);
        var mgr = new DeadlineOverrideManager(store, () => FakeNow);
        Assert.True(mgr.IsCorrupted);

        store.BeforeSaveCommitForTest = () => throw new IOException("注入：空表写入仍失败");
        Assert.False(mgr.TryReinitializeAfterCorruption(out string? backupPath, out string? error));
        Assert.Null(backupPath);
        Assert.Contains("写入新的空修正表失败", error);
        Assert.Contains("标记保留", error); // 只陈述实际状态
        Assert.True(File.Exists(_path + ".reinit-pending"));

        Assert.True(new DeadlineOverrideManager(new OverridesStore(_path), () => FakeNow).IsCorrupted);
    }
}
