using CcfReminder.Core.Data;

namespace CcfReminder.Core.Overrides;

/// <summary>
/// 有效时间解析：菜单倒计时、详情窗与提醒统一使用的“修正后时间”。
/// 同一条规则贯穿三处：某修正键在当前快照中**恰有唯一候选**时才应用；
/// 候选为零（上游缺失）或多个（届次标识缺失/同届多轮）时保留修正记录、
/// 醒目标记待核对，但**暂不应用**——绝不把一条修正套到全部候选上。
/// 只在返回的新记录上替换 UtcTime（并清除 Issue），原始快照节点不被改写。
/// 提醒去重键（ReminderEvaluator.NodeKey）含 UTC 时刻：修正或恢复上游值后
/// 有效时间变化即视为新键，会按新时间重新进入提醒评估（刻意设计，不是重复发送漏洞）。
/// </summary>
public static class DeadlineOverrideResolver
{
    public static IReadOnlyDictionary<string, DeadlineOverride> IndexByNodeKey(
        IReadOnlyList<DeadlineOverride> overrides)
    {
        // 存储层保证同键唯一（重复键在 Load 时即判损坏）；防御性以最后一条为准
        var map = new Dictionary<string, DeadlineOverride>(StringComparer.Ordinal);
        foreach (DeadlineOverride o in overrides)
        {
            map[o.NodeKey] = o;
        }
        return map;
    }

    /// <summary>当前快照中每个节点键的候选数（仅可修正节点有键）。</summary>
    public static IReadOnlyDictionary<string, int> CountCandidates(
        IReadOnlyList<ConferenceDataset> datasets)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (DeadlineNode node in datasets.SelectMany(d => d.Nodes))
        {
            string? key = OverrideNodeKey.TryBuild(node);
            if (key is not null)
            {
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            }
        }
        return counts;
    }

    /// <summary>实际会应用的修正：键存在且候选恰为唯一。歧义/缺失的修正不在其中。</summary>
    public static IReadOnlyDictionary<string, DeadlineOverride> FindApplicable(
        IReadOnlyList<ConferenceDataset> datasets,
        IReadOnlyList<DeadlineOverride> overrides)
    {
        var applicable = new Dictionary<string, DeadlineOverride>(StringComparer.Ordinal);
        if (overrides.Count == 0)
        {
            return applicable;
        }
        IReadOnlyDictionary<string, int> counts = CountCandidates(datasets);
        foreach (DeadlineOverride o in overrides)
        {
            if (counts.TryGetValue(o.NodeKey, out int c) && c == 1)
            {
                applicable[o.NodeKey] = o; // 同键唯一性由存储层保证
            }
        }
        return applicable;
    }

    /// <summary>应用可应用的修正得到有效数据集；无可应用修正时原样返回（不复制）。</summary>
    public static IReadOnlyList<ConferenceDataset> ApplyEffectiveTimes(
        IReadOnlyList<ConferenceDataset> datasets,
        IReadOnlyList<DeadlineOverride> overrides)
    {
        if (overrides.Count == 0)
        {
            return datasets;
        }
        IReadOnlyDictionary<string, DeadlineOverride> applicable = FindApplicable(datasets, overrides);
        if (applicable.Count == 0)
        {
            return datasets;
        }
        return datasets.Select(d => d with
        {
            Nodes = d.Nodes.Select(n =>
            {
                string? key = OverrideNodeKey.TryBuild(n);
                return key is not null && applicable.TryGetValue(key, out DeadlineOverride? o)
                    ? n with { UtcTime = o.CorrectedUtc, Issue = null }
                    : n;
            }).ToList(),
        }).ToList();
    }

    /// <summary>快照级应用（菜单/提醒的统一入口）；无可应用修正时原样返回。</summary>
    public static ConferenceDataSnapshot ApplyToSnapshot(
        ConferenceDataSnapshot snapshot,
        IReadOnlyList<DeadlineOverride> overrides)
    {
        IReadOnlyList<ConferenceDataset> applied = ApplyEffectiveTimes(snapshot.Conferences, overrides);
        return ReferenceEquals(applied, snapshot.Conferences)
            ? snapshot
            : snapshot with { Conferences = applied };
    }
}
