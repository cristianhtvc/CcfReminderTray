using CcfReminder.Core.Reminders;
using Xunit;

namespace CcfReminder.Core.Tests;

public class PreferencesStoreTests
{
    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "ccf-reminder-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Settings_Missing_ReturnsDefaults_BothSwitchesOff()
    {
        var store = new SettingsStore(NewDir());
        ReminderSettings s = store.Load();
        Assert.False(s.NotificationsEnabled);   // 通知默认关
        Assert.False(s.AutoStartEnabled);       // 开机启动默认关
        Assert.Equal(
            new HashSet<int> { 30, 14, 7, 3, 1 },
            s.ThresholdDays.ToHashSet());
    }

    [Fact]
    public void Settings_CorruptFile_ReturnsDefaults()
    {
        string dir = NewDir();
        var store = new SettingsStore(dir);
        File.WriteAllText(store.FilePath, "{ not json");
        ReminderSettings s = store.Load();
        Assert.False(s.NotificationsEnabled);
        Assert.False(s.AutoStartEnabled);
        Assert.True(File.Exists(store.FilePath)); // 损坏文件不删除
    }

    [Fact]
    public void Settings_LeftoverTmp_Ignored()
    {
        string dir = NewDir();
        var store = new SettingsStore(dir);
        File.WriteAllText(store.FilePath + ".tmp", "partial garbage");
        Assert.False(store.Load().NotificationsEnabled); // 按默认处理
    }

    [Fact]
    public void Settings_Roundtrip()
    {
        string dir = NewDir();
        var store = new SettingsStore(dir);
        var saved = new ReminderSettings(true, new HashSet<int> { 7, 1 }, true);
        store.Save(saved);
        ReminderSettings loaded = store.Load();
        Assert.True(loaded.NotificationsEnabled);
        Assert.True(loaded.AutoStartEnabled);
        Assert.Equal(new HashSet<int> { 7, 1 }, loaded.ThresholdDays.ToHashSet());
    }

    [Fact]
    public void Settings_UnknownThresholds_Dropped()
    {
        string dir = NewDir();
        var store = new SettingsStore(dir);
        File.WriteAllText(store.FilePath,
            """{"NotificationsEnabled":true,"ThresholdDays":[7,99,-1],"AutoStartEnabled":false}""");
        ReminderSettings s = store.Load();
        Assert.True(s.NotificationsEnabled);
        Assert.Equal(new HashSet<int> { 7 }, s.ThresholdDays.ToHashSet());
    }

    [Fact]
    public void State_CorruptFile_ReturnsEmpty()
    {
        string dir = NewDir();
        var store = new ReminderStateStore(dir);
        File.WriteAllText(store.FilePath, "garbage");
        ReminderState s = store.Load();
        Assert.Null(s.LastSummaryLocalDate);
        Assert.Empty(s.NotifiedThresholds);
    }

    [Fact]
    public void State_Roundtrip()
    {
        string dir = NewDir();
        var store = new ReminderStateStore(dir);
        var state = new ReminderState(
            new Dictionary<string, IReadOnlySet<int>>
            {
                ["icml|icml27|PaperSubmission|123"] = new HashSet<int> { 3, 7, 14, 30 },
            },
            "2026-10-13");
        store.Save(state);

        ReminderState loaded = store.Load();
        Assert.Equal("2026-10-13", loaded.LastSummaryLocalDate);
        Assert.Equal(
            new HashSet<int> { 3, 7, 14, 30 },
            loaded.NotifiedThresholds["icml|icml27|PaperSubmission|123"].ToHashSet());
    }
}
