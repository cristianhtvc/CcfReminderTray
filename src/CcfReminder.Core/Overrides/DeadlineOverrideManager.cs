using CcfReminder.Core.Data;

namespace CcfReminder.Core.Overrides;

/// <summary>
/// 用户修正的内存视图与写入口（PROJECT_BRIEF §4）。
/// 关键不变式：修正文件损坏时——
///  1) 内存快照按零条修正运行（界面回退到纯上游显示，不崩溃）；
///  2) TrySave/TryRemove 一律被拒绝（绝不在损坏文件上静默覆盖空表）；
///  3) 用户必须通过 <see cref="TryReinitializeAfterCorruption"/> 显式选择
///     “备份损坏文件并重新开始”，才会解除写入封锁。
/// 所有写入先落盘成功才更新内存；落盘失败时内存与磁盘均保持旧状态。
/// </summary>
public sealed class DeadlineOverrideManager
{
    private readonly OverridesStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly List<DeadlineOverride> _overrides = new();

    public DeadlineOverrideManager(OverridesStore store, Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        OverridesLoadResult result = store.Load();
        if (result.Status == OverridesLoadStatus.Corrupted)
        {
            IsCorrupted = true;
            CorruptionError = result.Error;
        }
        else
        {
            _overrides.AddRange(result.Overrides);
        }
    }

    /// <summary>修正文件损坏（存在但无法解析/版本不符）。此时写入被封锁。</summary>
    public bool IsCorrupted { get; private set; }
    public string? CorruptionError { get; private set; }

    /// <summary>当前修正列表（损坏时为空——安全的上游回退视图，不代表磁盘真实内容）。</summary>
    public IReadOnlyList<DeadlineOverride> Overrides => _overrides;

    public string StorePath => _store.PathOnDisk;

    /// <summary>修正被保存/删除/重新初始化后触发（界面据此重算菜单与提醒）。</summary>
    public event EventHandler? Changed;

    public DeadlineOverride? FindByNodeKey(string nodeKey) =>
        _overrides.FirstOrDefault(o => o.NodeKey == nodeKey);

    /// <summary>
    /// 保存（新增或替换）一条修正。node 用于唯一性判定与“修正时上游值”留档；
    /// 调用方需已完成时间换算并给出 correctedUtc。
    /// </summary>
    public bool TrySave(
        ConferenceDataSnapshot snapshot,
        DeadlineNode node,
        DateTimeOffset correctedUtc,
        string inputLocalTime,
        string inputTimezone,
        string reason,
        string source,
        out string? error)
    {
        error = null;
        if (IsCorrupted)
        {
            error = "修正文件已损坏，写入被阻止（避免静默覆盖丢失你已有的修正）。" +
                "请在详情窗选择“备份损坏文件并重新初始化”后再保存。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "请填写修正依据（如：当届官网显示延期）。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "请填写修正来源（如：当届官网链接或征稿启事地址）。";
            return false;
        }
        if (!OverrideNodeKey.TryResolveUniqueCandidate(snapshot, node, out DeadlineNode? match, out error))
        {
            return false;
        }
        string nodeKey = OverrideNodeKey.TryBuild(node)!;
        var entry = new DeadlineOverride
        {
            NodeKey = nodeKey,
            ConferenceId = node.ConferenceId,
            EditionId = node.EditionId ?? "",
            NodeType = node.NodeType,
            CorrectedUtc = correctedUtc.ToUniversalTime(),
            InputLocalTime = inputLocalTime.Trim(),
            InputTimezone = inputTimezone.Trim(),
            UpstreamUtcAtCorrection = match!.UtcTime,
            UpstreamRawTimeAtCorrection = match.RawTime,
            UpstreamRawTimezoneAtCorrection = match.RawTimezone,
            Reason = reason.Trim(),
            Source = source.Trim(),
            CorrectedAtUtc = _utcNow().ToUniversalTime(),
        };
        var next = new List<DeadlineOverride>(_overrides.Where(o => o.NodeKey != nodeKey)) { entry };
        return Persist(next, out error);
    }

    /// <summary>恢复上游值（删除该键的修正）。</summary>
    public bool TryRemove(string nodeKey, out string? error)
    {
        error = null;
        if (IsCorrupted)
        {
            error = "修正文件已损坏，写入被阻止（避免静默覆盖丢失你已有的修正）。" +
                "请在详情窗选择“备份损坏文件并重新初始化”后再操作。";
            return false;
        }
        if (_overrides.All(o => o.NodeKey != nodeKey))
        {
            error = "该节点没有已保存的修正。";
            return false;
        }
        var next = _overrides.Where(o => o.NodeKey != nodeKey).ToList();
        return Persist(next, out error);
    }

    /// <summary>
    /// 损坏后的唯一解锁路径：先把损坏原件<strong>复制</strong>为唯一命名备份（原件不动），
    /// 再用空表原子替换原件并解除封锁。必须由用户显式触发（按钮），程序永不自动执行。
    /// 安全性不依赖故障时还能写入任何标记：空表提交失败时损坏原件仍在原路径，
    /// 重启后读取原件自然仍判 Corrupted；旧版中断遗留的 reinit-pending 标记同样兼容。
    /// 失败提示只陈述实际完成的步骤。
    /// </summary>
    public bool TryReinitializeAfterCorruption(out string? backupPath, out string? error)
    {
        backupPath = null;
        error = null;
        if (!IsCorrupted)
        {
            error = "修正文件当前状态正常，无需重新初始化。";
            return false;
        }
        try
        {
            // 原路径缺失＝旧版中断遗留（有 reinit-pending 标记）：没有原件可备份，直接写空表
            if (File.Exists(_store.PathOnDisk))
            {
                backupPath = _store.BackupCorruptFileCopy(_utcNow().UtcDateTime);
            }
            _store.Save(Array.Empty<DeadlineOverride>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (backupPath is not null)
            {
                // 实际完成：备份已复制；未完成：空表替换。原件仍在原路径，重启仍判损坏
                error = $"已把你的修正备份到：\n{backupPath}\n\n但写入新的空修正表失败：{ex.Message}\n\n" +
                    "原修正文件仍在原路径、未被替换；重启后仍会按“损坏”提示，不会当作新用户。\n" +
                    "恢复办法：解决磁盘/权限问题后重试本操作；备份文件仅供人工查看，无需放回。";
            }
            else if (File.Exists(_store.PathOnDisk))
            {
                // 实际完成：无（备份复制失败）；原件未动
                error = $"备份损坏文件失败：{ex.Message}\n原文件未改动，仍保持损坏封锁状态。";
            }
            else
            {
                // 旧版中断遗留（主文件缺失）：本次是写入空表失败，标记仍在
                error = $"写入新的空修正表失败：{ex.Message}\n" +
                    "原路径仍无修正文件；待处理标记保留，重启后仍会按“损坏待处理”提示。";
            }
            return false;
        }
        _store.TryDeletePendingMarker();
        _overrides.Clear();
        IsCorrupted = false;
        CorruptionError = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>先落盘成功才更新内存；落盘失败时内存与磁盘都保持旧状态。</summary>
    private bool Persist(List<DeadlineOverride> next, out string? error)
    {
        try
        {
            _store.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"修正写入失败：{ex.Message}。磁盘上的修正文件未更换，本次修改未生效。";
            return false;
        }
        _overrides.Clear();
        _overrides.AddRange(next);
        Changed?.Invoke(this, EventArgs.Empty);
        error = null;
        return true;
    }
}
