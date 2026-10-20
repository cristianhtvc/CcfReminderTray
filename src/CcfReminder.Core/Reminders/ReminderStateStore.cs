using CcfReminder.Core.Data;

namespace CcfReminder.Core.Reminders;

/// <summary>
/// 已提醒状态持久化（reminder-state.json）。文件缺失或损坏时回退为空状态
/// （最坏情况是重启后同日重复一条汇总，不会漏报）。
/// </summary>
public class ReminderStateStore
{
    private readonly string _path;

    public ReminderStateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "reminder-state.json");
    }

    public string FilePath => _path;

    public virtual ReminderState Load()
    {
        StateDto? dto = AtomicJsonFile.Read<StateDto>(_path);
        if (dto is null)
        {
            return ReminderState.Empty;
        }
        var notified = new Dictionary<string, IReadOnlySet<int>>();
        if (dto.NotifiedThresholds is not null)
        {
            foreach ((string key, int[]? bands) in dto.NotifiedThresholds)
            {
                if (bands is not null)
                {
                    notified[key] = bands.Where(ReminderSettings.IsAllowedThreshold).ToHashSet();
                }
            }
        }
        return new ReminderState(notified, dto.LastSummaryLocalDate);
    }

    public virtual void Save(ReminderState state)
    {
        var dto = new StateDto
        {
            NotifiedThresholds = state.NotifiedThresholds.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.OrderByDescending(t => t).ToArray()),
            LastSummaryLocalDate = state.LastSummaryLocalDate,
        };
        AtomicJsonFile.Write(_path, dto);
    }

    private sealed class StateDto
    {
        public Dictionary<string, int[]>? NotifiedThresholds { get; set; }
        public string? LastSummaryLocalDate { get; set; }
    }
}
