using CcfReminder.Core.Data;

namespace CcfReminder.Core.Reminders;

/// <summary>
/// 设置持久化（settings.json）。文件缺失或损坏时安全回退到默认值（通知与开机启动均关）；
/// 未知阈值档位读取时丢弃，不写入。
/// </summary>
public class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    public string FilePath => _path;

    public virtual ReminderSettings Load()
    {
        SettingsDto? dto = AtomicJsonFile.Read<SettingsDto>(_path);
        if (dto is null)
        {
            return ReminderSettings.Default;
        }
        IReadOnlySet<int> thresholds = dto.ThresholdDays is null
            ? new HashSet<int>(ReminderSettings.AllowedThresholds)
            : dto.ThresholdDays.Where(ReminderSettings.IsAllowedThreshold).ToHashSet();
        return new ReminderSettings(dto.NotificationsEnabled, thresholds, dto.AutoStartEnabled);
    }

    public virtual void Save(ReminderSettings settings)
    {
        var dto = new SettingsDto
        {
            NotificationsEnabled = settings.NotificationsEnabled,
            ThresholdDays = settings.ThresholdDays
                .Where(ReminderSettings.IsAllowedThreshold)
                .OrderByDescending(t => t)
                .ToArray(),
            AutoStartEnabled = settings.AutoStartEnabled,
        };
        AtomicJsonFile.Write(_path, dto);
    }

    private sealed class SettingsDto
    {
        public bool NotificationsEnabled { get; set; }
        public int[]? ThresholdDays { get; set; }
        public bool AutoStartEnabled { get; set; }
    }
}
