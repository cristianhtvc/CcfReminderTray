using CcfReminder.Core.Data;
using CcfReminder.Core.Reminders;
using Xunit;
using static CcfReminder.Core.Tests.ReminderEvaluatorTests;

namespace CcfReminder.Core.Tests;

public class ReminderServiceTests
{
    private sealed class FakeNotifier : IReminderNotifier
    {
        public List<(string Title, string Message)> Calls { get; } = new();
        public bool Throw;
        public bool ReturnsFalse;
        public int DelayMs;

        public bool Show(string title, string message)
        {
            if (Throw)
            {
                throw new InvalidOperationException("injected notifier fault");
            }
            if (ReturnsFalse)
            {
                return false; // 模拟封送失败/句柄不可用：显示请求未提交
            }
            if (DelayMs > 0)
            {
                Thread.Sleep(DelayMs); // 模拟慢速系统调用，放大并发窗口
            }
            Calls.Add((title, message));
            return true;
        }
    }

    /// <summary>可控阻塞的设置存储：第一次 Load 被闸门挡住，用于构造“检查正在执行”的并发窗口。</summary>
    private sealed class GatedSettingsStore : SettingsStore
    {
        private readonly ManualResetEventSlim _gate;
        private int _loads;

        public GatedSettingsStore(string dir, ManualResetEventSlim gate) : base(dir) => _gate = gate;

        public int LoadCount => _loads;

        /// <summary>第一次 Load 返回固定的关闭状态（模拟检查开始时设置尚未保存）。</summary>
        public bool FirstLoadReturnsDisabled;

        public override ReminderSettings Load()
        {
            if (Interlocked.Increment(ref _loads) == 1)
            {
                _gate.Wait();
                if (FirstLoadReturnsDisabled)
                {
                    return new ReminderSettings(false, new HashSet<int> { 3 }, false);
                }
            }
            return base.Load();
        }
    }

    /// <summary>状态保存故障注入：通知已发出但 reminder-state.json 写不进去。</summary>
    private sealed class FaultingStateStore : ReminderStateStore
    {
        public FaultingStateStore(string dir) : base(dir) { }
        public bool FailSave = true;
        public override void Save(ReminderState state)
        {
            if (FailSave)
            {
                throw new IOException("injected state-save fault");
            }
            base.Save(state);
        }
    }

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "ccf-reminder-test-" + Guid.NewGuid().ToString("N"));

    private static ConferenceDataSnapshot Snapshot(DateTimeOffset fetchedAt, params ConferenceDataset[] datasets) =>
        new(fetchedAt, "test", datasets);

    private static (ReminderService Service, FakeNotifier Notifier, SettingsStore Settings, ReminderStateStore States)
        NewService(string dir, Func<DateTimeOffset> clock, ReminderStateStore? stateStore = null)
    {
        var settings = new SettingsStore(dir);
        var states = stateStore ?? new ReminderStateStore(dir);
        var notifier = new FakeNotifier();
        var svc = new ReminderService(settings, states, notifier, clock, TimeZoneInfo.Utc);
        return (svc, notifier, settings, states);
    }

    [Fact]
    public void CheckAndNotify_SendsOneMergedSummary_MarksBandsAndDate_NoRepeatSameDay()
    {
        string dir = NewDir();
        var (svc, notifier, settings, states) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));

        ConferenceDataSnapshot snap = Snapshot(Now,
            Dataset("icml", Node(utc: Now.AddDays(2))),
            Dataset("nips", Node(conf: "nips", utc: Now.AddDays(6))));
        svc.CheckAndNotify(snap);

        (string title, string message) = Assert.Single(notifier.Calls);
        Assert.Equal(ReminderService.Title, title);
        Assert.Contains("ICML", message);
        Assert.Contains("NeurIPS", message);       // 同日多会议合并为一条
        Assert.Contains("来源 ccfddl，未逐项核验", message);

        ReminderState state = states.Load();
        Assert.Equal(Today, state.LastSummaryLocalDate);
        Assert.Equal(
            new HashSet<int> { 3, 7, 14, 30 },
            state.NotifiedThresholds[ReminderEvaluator.NodeKey(Node(utc: Now.AddDays(2)))].ToHashSet());

        svc.CheckAndNotify(snap); // 同日再评估：每日上限，不重复
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public void ConcurrentChecks_AtMostOneSummary_NoTmpWriteConflict()
    {
        // 刷新完成与每小时定时器并发触发：串行化后同日最多一条汇总
        string dir = NewDir();
        var (svc, notifier, settings, _) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));
        notifier.DelayMs = 50;

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        Parallel.For(0, 8, _ => svc.CheckAndNotify(snap));

        Assert.Single(notifier.Calls);
        ReminderState state = new ReminderStateStore(dir).Load(); // 状态文件可读且完整
        Assert.Equal(Today, state.LastSummaryLocalDate);
    }

    [Fact]
    public void InFlightCheck_NewSnapshotArrives_CoalescedRunUsesLatestSnapshot()
    {
        // 旧检查正在执行时新快照到达：不丢弃，当前检查结束后立即补跑最新快照
        string dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var settings = new GatedSettingsStore(dir, gate);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 3 }, false));
        var notifier = new FakeNotifier();
        var svc = new ReminderService(settings, new ReminderStateStore(dir), notifier, () => Now, TimeZoneInfo.Utc);

        ConferenceDataSnapshot oldSnap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(10)))); // 3 天档外
        ConferenceDataSnapshot newSnap = Snapshot(Now, Dataset("nips", Node(conf: "nips", utc: Now.AddDays(2))));

        var worker = new Thread(() => svc.CheckAndNotify(oldSnap));
        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => settings.LoadCount == 1, TimeSpan.FromSeconds(10)),
            "首个检查应已进入执行（被闸门挡住）");

        svc.CheckAndNotify(newSnap); // 应立即返回（合并为待执行），不阻塞、不丢弃
        gate.Set();
        worker.Join();

        // 旧快照无到期节点不通知；补跑使用最新快照，立刻发出 NeurIPS 提醒（而非等到下一小时）
        (_, string message) = Assert.Single(notifier.Calls);
        Assert.Contains("NeurIPS", message);
    }

    [Fact]
    public void InFlightCheck_SettingsSavedDuringCheck_PendingRunUsesLatestSettings()
    {
        // 保存设置触发检查时已有检查在运行：补跑使用磁盘上的最新设置
        string dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var settings = new GatedSettingsStore(dir, gate) { FirstLoadReturnsDisabled = true };
        settings.Save(new ReminderSettings(false, new HashSet<int> { 3 }, false)); // 初始：通知关闭
        var notifier = new FakeNotifier();
        var svc = new ReminderService(settings, new ReminderStateStore(dir), notifier, () => Now, TimeZoneInfo.Utc);

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        var worker = new Thread(() => svc.CheckAndNotify(snap));
        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => settings.LoadCount == 1, TimeSpan.FromSeconds(10)),
            "首个检查应已进入执行（被闸门挡住）");

        settings.Save(new ReminderSettings(true, new HashSet<int> { 3 }, false)); // 用户在设置窗保存：开启
        svc.CheckAndNotify(snap); // 设置保存后的即时检查：合并为待执行
        gate.Set();
        worker.Join();

        // 第一次执行看到的是保存前的关闭状态（不通知）；补跑读到最新设置，立即通知
        (_, string message) = Assert.Single(notifier.Calls);
        Assert.Contains("ICML", message);
    }

    [Fact]
    public void InFlightCheck_BothSnapshotsHaveDue_PendingSnapshotWinsOverOldSend()
    {
        // 两份快照均有待提醒节点：旧检查被闸门挡在执行中，新快照到达并入队；
        // 旧检查在发送前发现排队快照 → 放弃旧发送，由补跑用最新快照发出当日唯一一条汇总
        // （否则旧快照的 ICML 通知会占掉“每日最多一条”，NeurIPS 当天被压掉）
        string dir = NewDir();
        var gate = new ManualResetEventSlim(false);
        var settings = new GatedSettingsStore(dir, gate);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 3 }, false));
        var notifier = new FakeNotifier();
        var svc = new ReminderService(settings, new ReminderStateStore(dir), notifier, () => Now, TimeZoneInfo.Utc);

        ConferenceDataSnapshot oldSnap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        ConferenceDataSnapshot newSnap = Snapshot(Now, Dataset("nips", Node(conf: "nips", utc: Now.AddDays(1))));

        var worker = new Thread(() => svc.CheckAndNotify(oldSnap));
        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => settings.LoadCount == 1, TimeSpan.FromSeconds(10)),
            "首个检查应已进入执行（被闸门挡住）");

        svc.CheckAndNotify(newSnap); // 合并为待执行（保留最新快照）
        gate.Set();
        worker.Join();

        (_, string message) = Assert.Single(notifier.Calls); // 每日最多一条，且用的是新快照
        Assert.Contains("NeurIPS", message);
        Assert.DoesNotContain("ICML", message);
    }

    [Fact]
    public void StateSaveFails_AfterSend_NoRepeatWithinProcess()
    {
        // 通知已发出但状态保存失败：内存态兜底，本进程内不重复发送
        string dir = NewDir();
        var faulting = new FaultingStateStore(dir);
        var (svc, notifier, settings, _) = NewService(dir, () => Now, faulting);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        svc.CheckAndNotify(snap);
        svc.CheckAndNotify(snap); // 同日第二次：内存态已记，不重发

        Assert.Single(notifier.Calls);
        Assert.False(File.Exists(faulting.FilePath)); // 磁盘确实没写成（降级语义见服务注释）
    }

    [Fact]
    public void NextDay_TighterBand_NotifiesAgain()
    {
        string dir = NewDir();
        DateTimeOffset current = Now;
        var (svc, notifier, settings, _) = NewService(dir, () => current);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        svc.CheckAndNotify(snap);                    // 剩 2 天 → 3 天档
        current = Now.AddDays(1);
        svc.CheckAndNotify(snap);                    // 次日剩 1 天 → 1 天档，新一天允许再发

        Assert.Equal(2, notifier.Calls.Count);
        Assert.Contains("≤3 天档", notifier.Calls[0].Message);
        Assert.Contains("≤1 天档", notifier.Calls[1].Message);
    }

    [Fact]
    public void DailyCapBoundary_SameDayUrgentNodeSuppressed_NeverRemindedAfterExpiry()
    {
        // 策略边界展示（如实记录，非缺陷修复）：当日名额被占用后，当天新出现/改期的紧急
        // 节点受“每日最多一条”压制；次日它已过期、评估器跳过——该节点从未被提醒。
        // 全程注入时钟，不修改系统时钟。
        string dir = NewDir();
        DateTimeOffset current = Now; // 2026-10-13 12:00 UTC
        var (svc, notifier, settings, _) = NewService(dir, () => current);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 3, 1 }, false));

        // 当天中午：ICML（2 天后截止，3 天档）发出当日唯一一条汇总
        DateTimeOffset icmlDeadline = Now.AddDays(2);
        svc.CheckAndNotify(Snapshot(Now, Dataset("icml", Node(utc: icmlDeadline))));
        Assert.Single(notifier.Calls);

        // 当天傍晚：新快照带来当天就截止的 NeurIPS 全文提交（5 小时后）
        current = Now.AddHours(6);
        DateTimeOffset urgentDeadline = current.AddHours(5);
        svc.CheckAndNotify(Snapshot(current,
            Dataset("icml", Node(utc: icmlDeadline)),
            Dataset("nips", Node(conf: "nips", utc: urgentDeadline))));
        Assert.Single(notifier.Calls); // 每日最多一条：NeurIPS 被压制

        // 次日：NeurIPS 已过期不再评估；ICML 进入 1 天档发出新一天的汇总——
        // 汇总里只有 ICML，NeurIPS 从未出现在任何通知中
        current = Now.AddDays(1).AddHours(1);
        svc.CheckAndNotify(Snapshot(current,
            Dataset("icml", Node(utc: icmlDeadline)),
            Dataset("nips", Node(conf: "nips", utc: urgentDeadline))));

        Assert.Equal(2, notifier.Calls.Count);
        Assert.Contains("ICML", notifier.Calls[1].Message);
        Assert.DoesNotContain("NeurIPS", notifier.Calls[1].Message);
        Assert.All(notifier.Calls, c => Assert.DoesNotContain("NeurIPS", c.Message));
    }

    [Fact]
    public void NullSnapshot_Nothing()
    {
        string dir = NewDir();
        var (svc, notifier, _, _) = NewService(dir, () => Now);
        svc.CheckAndNotify(null);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public void NotifierThrows_StateNotMarked_RetriedLater()
    {
        string dir = NewDir();
        var (svc, notifier, settings, states) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 3 }, false));
        notifier.Throw = true;

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        svc.CheckAndNotify(snap); // 发送异常：不得记录状态

        ReminderState state = states.Load();
        Assert.Null(state.LastSummaryLocalDate);
        Assert.Empty(state.NotifiedThresholds);

        notifier.Throw = false;
        svc.CheckAndNotify(snap); // 恢复后重试成功
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public void NotifierReturnsFalse_StateNotMarked_RetriedLater()
    {
        // 封送失败/句柄不可用（返回 false）：同样不记“已提醒”，下个周期重试
        string dir = NewDir();
        var (svc, notifier, settings, states) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 3 }, false));
        notifier.ReturnsFalse = true;

        ConferenceDataSnapshot snap = Snapshot(Now, Dataset("icml", Node(utc: Now.AddDays(2))));
        svc.CheckAndNotify(snap);

        Assert.Empty(notifier.Calls);
        ReminderState state = states.Load();
        Assert.Null(state.LastSummaryLocalDate);

        notifier.ReturnsFalse = false;
        svc.CheckAndNotify(snap);
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public void Summary_ManyDueNodes_CappedWithClickHint_NotSilentTruncation()
    {
        // 六个会议同日到期：气球正文最多 3 行节点，其余用“点击通知查看全部”收口
        string dir = NewDir();
        var (svc, notifier, settings, _) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));

        ConferenceDataSnapshot snap = Snapshot(Now,
            Dataset("icml", Node(utc: Now.AddDays(2))),
            Dataset("iclr", Node(conf: "iclr", utc: Now.AddDays(2))),
            Dataset("nips", Node(conf: "nips", utc: Now.AddDays(2))),
            Dataset("aamas", Node(conf: "aamas", utc: Now.AddDays(2))),
            Dataset("aaai", Node(conf: "aaai", utc: Now.AddDays(2))),
            Dataset("dai", Node(conf: "dai", utc: Now.AddDays(2))));
        svc.CheckAndNotify(snap);

        (_, string message) = Assert.Single(notifier.Calls);
        Assert.Contains("另有 3 项临近截止，点击通知查看全部", message);
        Assert.Contains("来源 ccfddl，未逐项核验", message);
        // 恰好 3 行节点（“（≤3 天档）”出现 3 次），不静默截断
        Assert.Equal(3, message.Split("（≤3 天档）").Length - 1);
    }

    [Fact]
    public void Summary_WorstCaseSixConferences_BodyWithinBalloonCapacity()
    {
        // BalloonTip 经 Shell NOTIFYICONDATA 传递：正文安全容量 255 字符、标题 63 字符。
        // 最坏合理组合：六会议同日命中最长文案（NeurIPS ＋ 摘要注册 ＋ “29天 23小时” ＋ ≤30 天档）。
        string dir = NewDir();
        var (svc, notifier, settings, _) = NewService(dir, () => Now);
        settings.Save(new ReminderSettings(true, new HashSet<int> { 30, 14, 7, 3, 1 }, false));

        DateTimeOffset deadline = Now.AddDays(29).AddHours(23);
        ConferenceDataSnapshot snap = Snapshot(Now,
            Dataset("icml", Node(type: DeadlineNodeType.AbstractRegistration, utc: deadline)),
            Dataset("iclr", Node(conf: "iclr", type: DeadlineNodeType.AbstractRegistration, utc: deadline)),
            Dataset("nips", Node(conf: "nips", type: DeadlineNodeType.AbstractRegistration, utc: deadline)),
            Dataset("aamas", Node(conf: "aamas", type: DeadlineNodeType.AbstractRegistration, utc: deadline)),
            Dataset("aaai", Node(conf: "aaai", type: DeadlineNodeType.AbstractRegistration, utc: deadline)),
            Dataset("dai", Node(conf: "dai", type: DeadlineNodeType.AbstractRegistration, utc: deadline)));
        svc.CheckAndNotify(snap);

        (string title, string message) = Assert.Single(notifier.Calls);
        Assert.True(title.Length <= 63, $"标题 {title.Length} 字符超出容量");
        Assert.True(message.Length <= 255, $"正文 {message.Length} 字符超出 BalloonTip 容量：\n{message}");
        Assert.Contains("另有 3 项临近截止，点击通知查看全部", message); // 不静默截断
        Assert.Contains("来源 ccfddl，未逐项核验", message);
    }

    [Fact]
    public void SendTestNotification_MarkedAsTest_WorksWhileDisabled_ReportsResult()
    {
        string dir = NewDir();
        var (svc, notifier, _, _) = NewService(dir, () => Now);

        Assert.True(svc.SendTestNotification()); // 默认通知关闭，但测试通知是用户显式操作
        (string title, string message) = Assert.Single(notifier.Calls);
        Assert.Contains("测试通知", title);
        Assert.Contains("测试通知", message);
        Assert.Contains("来源 ccfddl，未逐项核验", message);

        notifier.ReturnsFalse = true;
        Assert.False(svc.SendTestNotification()); // 提交失败如实返回 false
    }
}
