using CcfReminder.Core.Data;

namespace CcfReminder.Core.Reminders;

/// <summary>
/// 通知发送出口（托盘气球／测试替身）。
/// 返回 true 表示“显示请求已实际提交给系统”；false/异常表示未提交。
/// 系统层是否弹出横幅、用户是否看见，超出应用可观测范围。
/// </summary>
public interface IReminderNotifier
{
    bool Show(string title, string message);
}

/// <summary>
/// 提醒编排：读取设置与已提醒状态 → 评估 → 发一条汇总 → 持久化状态。
///
/// 送达语义（系统层无法保证用户实际看见通知，故采用）：
/// “请求送达，每档至多一次”——只有显示请求实际提交成功才记录该档已提醒；
/// 提交失败不记录，下个检查周期重试；已记录的档位不重试（可见性由系统设置决定）。
///
/// 并发语义：CheckAndNotify 多触发源（刷新完成、每小时定时器、设置保存后）串行执行；
/// 已有检查在运行时，新调用不丢弃而是合并为一次“待执行检查”（只保留最新快照），
/// 当前检查结束后立即补跑。设置每次执行时从磁盘重新读取（设置窗保存立即生效）；
/// 提醒状态首次从磁盘读取后由本服务独占写入并以内存副本为准。
/// 发送前再次检查排队快照：若已有更新快照在等待，放弃本次发送，由补跑用最新快照评估，
/// 避免旧快照的通知占掉“每日最多一条”。同一天仍最多发一条汇总，状态文件单写入者。
///
/// 无法完全消除的竞态与策略限制（如实说明）：若新快照在“发送决策已做出、通知调用
/// 进行中”之后到达，本次通知仍按旧快照发出，补跑受“每日最多一条”约束当天不再重发。
/// 受影响节点的档位此时并未标记，但这不是“次日补提醒即可”：若新快照中的节点当天即
/// 截止，次日它已过期、评估器直接跳过——即当天新出现或改期的紧急截止可能完全漏报。
/// 这是“每日最多一条”策略的固有限制（非实现缺陷），该窗口已用发送段持锁压缩到最小
/// 但无法缩到零；重要截止时间应以会议官网为准。
///
/// 持久化失败语义：通知已发出但状态保存失败时，内存态先更新（本进程内不重复），
/// 重启后同日可能补发一次（可接受的降级，不会漏报更早档位）。
/// </summary>
public sealed class ReminderService
{
    public const string Title = "会议截稿提醒";

    /// <summary>汇总正文最多列出的节点行数；气球文本容量有限，超出用“点击通知查看全部”收口。</summary>
    internal const int MaxSummaryLines = 3;

    private readonly SettingsStore _settingsStore;
    private readonly ReminderStateStore _stateStore;
    private readonly IReminderNotifier _notifier;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeZoneInfo _localZone;
    private readonly object _gate = new();
    private bool _checkRunning;
    private ConferenceDataSnapshot? _pendingSnapshot; // 合并：只保留最新快照
    private ReminderState? _stateCache; // 状态的唯一写入方是本服务，内存副本防同进程重复

    public ReminderService(
        SettingsStore settingsStore,
        ReminderStateStore stateStore,
        IReminderNotifier notifier,
        Func<DateTimeOffset> utcNow,
        TimeZoneInfo? localZone = null)
    {
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _notifier = notifier;
        _utcNow = utcNow;
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    public ReminderSettings LoadSettings() => _settingsStore.Load();

    public void SaveSettings(ReminderSettings settings) => _settingsStore.Save(settings);

    /// <summary>
    /// 按最新快照评估一次；有到期节点时合并为一条汇总通知。
    /// 已有检查在运行时：本次调用不阻塞、不丢弃，合并为一次待执行检查（保留最新快照），
    /// 当前检查结束后立即补跑，不会等到下一个定时周期。
    /// </summary>
    public void CheckAndNotify(ConferenceDataSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }
        lock (_gate)
        {
            if (_checkRunning)
            {
                _pendingSnapshot = snapshot; // 合并：只保留最新快照
                return;
            }
            _checkRunning = true;
        }

        ConferenceDataSnapshot current = snapshot;
        while (true)
        {
            RunOnce(current);
            lock (_gate)
            {
                if (_pendingSnapshot is null)
                {
                    _checkRunning = false;
                    return;
                }
                current = _pendingSnapshot;
                _pendingSnapshot = null;
            }
        }
    }

    private void RunOnce(ConferenceDataSnapshot snapshot)
    {
        try
        {
            ReminderSettings settings = _settingsStore.Load();
            ReminderState state = _stateCache ??= _stateStore.Load();
            DateTimeOffset now = _utcNow();
            string localToday = DateOnly
                .FromDateTime(TimeZoneInfo.ConvertTime(now, _localZone).DateTime)
                .ToString("yyyy-MM-dd");

            IReadOnlyList<DueReminder> due =
                ReminderEvaluator.Evaluate(snapshot.Conferences, settings, state, now, localToday);
            if (due.Count == 0)
            {
                return;
            }

            // 发送段持锁：发送前若已有更新快照排队，放弃本次发送，由外层循环随即用最新快照补跑，
            // 避免旧快照的通知占掉“每日最多一条”；发送与记状态期间新快照只能在外排队，
            // 把“决策已做出后新快照到达”的竞态窗口压缩到最小（残余竞态见类注释）
            lock (_gate)
            {
                if (_pendingSnapshot is not null)
                {
                    return; // 不发送、不记状态；补跑会用最新快照重新评估
                }
                if (!_notifier.Show(Title, BuildSummary(due, now)))
                {
                    return; // 显示请求未提交成功：不记状态，下个周期重试
                }

                // 每个节点标记已提醒档位（含更宽档位：均已跨过，不重复提醒）
                var notified = state.NotifiedThresholds.ToDictionary(
                    kv => kv.Key, kv => new HashSet<int>(kv.Value));
                foreach (DueReminder d in due)
                {
                    string key = ReminderEvaluator.NodeKey(d.Node);
                    if (!notified.TryGetValue(key, out HashSet<int>? bands))
                    {
                        bands = new HashSet<int>();
                        notified[key] = bands;
                    }
                    foreach (int t in settings.ThresholdDays.Where(t => t >= d.ThresholdDays))
                    {
                        bands.Add(t);
                    }
                }
                state = new ReminderState(
                    notified.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<int>)kv.Value),
                    localToday);
                _stateCache = state; // 先更新内存态：即使下方持久化失败，本进程内也不重复
                try
                {
                    _stateStore.Save(state);
                }
                catch (Exception)
                {
                    // 通知已发出但状态保存失败：重启后同日可能补发一次（不丢更早档位）
                }
            }
        }
        catch (Exception)
        {
            // 任何提醒失败都不影响刷新/定时器；执行权与待执行合并由外层循环管理
        }
    }

    /// <summary>
    /// 设置窗“发送测试通知”：明确标注测试，不受通知开关与每日上限影响。
    /// 返回值仅表示显示请求已提交给系统，不代表用户已看见横幅。
    /// </summary>
    public bool SendTestNotification()
    {
        try
        {
            return _notifier.Show(
                "测试通知 · " + Title,
                "这是一条测试通知，仅用于确认通知在你的桌面上可见。\n来源 ccfddl，未逐项核验");
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static string BuildSummary(IReadOnlyList<DueReminder> due, DateTimeOffset utcNow)
    {
        var ordered = due
            .OrderBy(d => d.Node.UtcTime)
            .ThenBy(d => d.ConferenceShortName, StringComparer.Ordinal)
            .ToList();
        var lines = ordered.Take(MaxSummaryLines).Select(d =>
            $"{d.ConferenceShortName} {CountdownFormatter.NodeTypeLabel(d.Node.NodeType)}" +
            $" {d.Node.UtcTime!.Value.ToLocalTime():yyyy-MM-dd HH:mm}（本地）" +
            $" · 剩余 {CountdownFormatter.FormatRemaining(utcNow, d.Node.UtcTime!.Value)}" +
            $"（≤{d.ThresholdDays} 天档）");
        string message = string.Join("\n", lines);
        if (ordered.Count > MaxSummaryLines)
        {
            // 不静默截断：明确告知还有几项，点击通知打开详情窗
            message += $"\n另有 {ordered.Count - MaxSummaryLines} 项临近截止，点击通知查看全部";
        }
        return message + "\n来源 ccfddl，未逐项核验";
    }
}
