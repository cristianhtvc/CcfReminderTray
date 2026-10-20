using CcfReminder.Core.Reminders;
using Xunit;

namespace CcfReminder.Core.Tests;

public class PreferencesManagerTests
{
    /// <summary>内存注册表替身：绝不触碰真实 Run 键。</summary>
    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public bool ThrowOnRead;
        public bool ThrowOnWrite;
        /// <summary>第一次写入成功后，后续写入/删除全部失败（模拟回滚失败）。</summary>
        public bool FailWritesAfterFirstChange;
        /// <summary>第一次写入/删除先实际生效、随后抛错（模拟值已改变但操作报告失败）。</summary>
        public bool WriteThenThrowOnce;
        private int _writes;
        private bool _thrownOnce;

        public string? GetValue(string name)
        {
            if (ThrowOnRead) throw new UnauthorizedAccessException("injected registry read fault");
            return Values.TryGetValue(name, out string? v) ? v : null;
        }

        public void SetValue(string name, string value)
        {
            if (ThrowOnWrite || (FailWritesAfterFirstChange && _writes >= 1))
            {
                throw new UnauthorizedAccessException("injected registry fault");
            }
            _writes++;
            Values[name] = value;
            ThrowOnceAfterWrite();
        }

        public void DeleteValue(string name)
        {
            if (ThrowOnWrite || (FailWritesAfterFirstChange && _writes >= 1))
            {
                throw new UnauthorizedAccessException("injected registry fault");
            }
            _writes++;
            Values.Remove(name);
            ThrowOnceAfterWrite();
        }

        private void ThrowOnceAfterWrite()
        {
            if (WriteThenThrowOnce && !_thrownOnce)
            {
                _thrownOnce = true;
                throw new IOException("injected post-write fault");
            }
        }
    }

    /// <summary>设置保存故障注入：模拟 settings.json 写盘失败。</summary>
    private sealed class FaultingSettingsStore : SettingsStore
    {
        public FaultingSettingsStore(string dir) : base(dir) { }
        public bool FailSave = true;
        public override void Save(ReminderSettings settings)
        {
            if (FailSave) throw new IOException("injected settings-save fault");
            base.Save(settings);
        }
    }

    private const string ExePath = @"C:\Apps\current\CcfReminderTray.exe";
    private const string OldExePath = @"C:\Apps\old\CcfReminderTray.exe";

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "ccf-reminder-test-" + Guid.NewGuid().ToString("N"));

    private static ReminderSettings Prefs(bool notify, bool autoStart) =>
        new(notify, new HashSet<int> { 7, 1 }, autoStart);

    [Fact]
    public void Load_AutoStartReflectsRegistry_NotSettingsFile()
    {
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        var settings = new SettingsStore(dir);
        settings.Save(Prefs(notify: true, autoStart: true)); // 文件里写 true
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.Load(out string? warning).AutoStartEnabled); // 注册表无值：以注册表为准
        Assert.Null(warning);

        runKeys.Values[AutoStartManager.ValueName] = $"\"{ExePath}\"";
        Assert.True(prefs.Load(out _).AutoStartEnabled);
    }

    [Fact]
    public void Load_RegistryReadFails_WarnsAndShowsAutoStartOff_NoThrow()
    {
        // 打开设置窗时注册表读取失败：不得抛异常离开窗口构造流程，
        // 按“未开启”显示并通过 registryWarning 给出可展示的中文提示
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { ThrowOnRead = true };
        var settings = new SettingsStore(dir);
        settings.Save(Prefs(notify: true, autoStart: true));
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        ReminderSettings loaded = prefs.Load(out string? warning);
        Assert.False(loaded.AutoStartEnabled);
        Assert.True(loaded.NotificationsEnabled); // 其余设置不受影响
        Assert.NotNull(warning);
        Assert.Contains("开机启动状态读取失败", warning);
    }

    [Fact]
    public void GetAutoStartPointer_RegistryReadFails_ThrowsForCallerToHandle()
    {
        // 契约：指向分类的读取失败必须抛异常（Load 已就同一失败给出警告），
        // 由调用方（设置窗）捕获并给出可见反馈；不得在此处静默伪装成“无启动项”
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { ThrowOnRead = true };
        var prefs = new PreferencesManager(new SettingsStore(dir), new AutoStartManager(runKeys, () => ExePath));

        Assert.Throws<UnauthorizedAccessException>(() => prefs.GetAutoStartPointer(out _));
    }

    [Fact]
    public void RegistryWriteThenThrows_RestoreSucceeds_HonestError()
    {
        // Enable 已改变 Run 值、随后才抛异常；恢复原值成功：
        // 如实报告“已尽力恢复注册表原值；设置文件未写入”，不得暗示完全没有改动
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { WriteThenThrowOnce = true };
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Contains("开机启动设置失败", error);
        Assert.Contains("已尽力恢复注册表原值", error);
        Assert.DoesNotContain("未做任何改动", error); // 写入曾生效过，不能声称无改动
        Assert.False(runKeys.Values.ContainsKey(AutoStartManager.ValueName)); // 原值（无值）已恢复
        Assert.False(File.Exists(settings.FilePath)); // 设置文件未写入
    }

    [Fact]
    public void RegistryWriteThenThrows_RestoreAlsoFails_ReportsPossibleChange_WithRecoveryHint()
    {
        // Enable 已改变 Run 值、随后抛异常，恢复原值也失败：
        // 必须明确说“注册表可能已改变”，并给出可操作的恢复办法（指名值名）
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { WriteThenThrowOnce = true, FailWritesAfterFirstChange = true };
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Contains("开机启动设置失败", error);
        Assert.Contains("恢复注册表原值也失败", error);
        Assert.Contains("注册表可能已改变", error);
        Assert.Contains(AutoStartManager.ValueName, error); // 恢复办法指名了值名
        // 不掩饰：注册表确实停留在已写入的新值上
        Assert.Equal($"\"{ExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.False(File.Exists(settings.FilePath));
    }

    [Fact]
    public void Save_Success_WritesRegistryAndSettings()
    {
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.True(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Null(error);
        Assert.Equal($"\"{ExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.True(settings.Load().NotificationsEnabled);
        Assert.Equal(new HashSet<int> { 7, 1 }, settings.Load().ThresholdDays.ToHashSet());
    }

    [Fact]
    public void SettingsSaveFails_RegistryRestoredToPreviousValue_ClearError()
    {
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        var settings = new FaultingSettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        // 情形 A：原本无值 → 保存失败后仍无值
        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Contains("设置保存失败", error);
        Assert.False(runKeys.Values.ContainsKey(AutoStartManager.ValueName));

        // 情形 B：原本已启用 → 取消＋保存失败后恢复原值
        runKeys.Values[AutoStartManager.ValueName] = $"\"{ExePath}\"";
        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: false), out error));
        Assert.Equal($"\"{ExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
    }

    [Fact]
    public void RegistryWriteFails_SettingsFileNotWritten()
    {
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { ThrowOnWrite = true };
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        // 写入路径抛错无法证明注册表未被改动：管理器会尝试恢复原值（此处恢复也失败），
        // 因此如实按“可能已改变”口径报告，而不是声称“未做任何改动”
        Assert.Contains("开机启动设置失败", error);
        Assert.False(File.Exists(settings.FilePath)); // 未留下半套改动
        Assert.False(runKeys.Values.ContainsKey(AutoStartManager.ValueName));
    }

    [Fact]
    public void RegistryReadFails_ReportedCleanly_NothingChanged()
    {
        // GetRawValue 抛错：不得向 UI 抛异常，报告读取失败且不做任何改动
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { ThrowOnRead = true };
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Contains("开机启动状态读取失败", error);
        Assert.False(runKeys.Values.ContainsKey(AutoStartManager.ValueName));
        Assert.False(File.Exists(settings.FilePath));
    }

    [Fact]
    public void RollbackFails_ReportsPossibleInconsistency_WithRecoveryHint()
    {
        // 注册表已变更后设置保存抛错、且回滚也失败：如实告知可能不一致与恢复办法
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore { FailWritesAfterFirstChange = true };
        var settings = new FaultingSettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ExePath));

        Assert.False(prefs.TrySave(Prefs(notify: true, autoStart: true), out string? error));
        Assert.Contains("设置保存失败", error);
        Assert.Contains("回滚失败", error);
        Assert.Contains("可能不一致", error);
        Assert.Contains(AutoStartManager.ValueName, error); // 恢复办法指名了值名
        // 不掩饰：注册表确实停留在未回滚的新值上
        Assert.Equal($"\"{ExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.False(File.Exists(settings.FilePath));
    }

    private const string InstallExePath =
        @"C:\Users\x\AppData\Local\Programs\CcfReminderTray\CcfReminderTray.exe";
    private const string PortableExePath =
        @"C:\Users\x\AppData\Local\Programs\CcfReminderTray-portable\CcfReminderTray.exe";

    [Fact]
    public void ValuePointsThisCopy_UncheckedSave_DeletesValue()
    {
        // 最终规则：取消勾选并保存，仅当 Run 值指向当前这份程序时才删除
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{ExePath}\"";
        var prefs = new PreferencesManager(
            new SettingsStore(dir), new AutoStartManager(runKeys, () => ExePath));

        Assert.True(prefs.TrySave(Prefs(notify: true, autoStart: false), out _));
        Assert.False(runKeys.Values.ContainsKey(AutoStartManager.ValueName));
    }

    [Fact]
    public void ValuePointsElsewhere_UncheckedSave_PreservesValue_OtherEntriesUntouched()
    {
        // 最终规则：值指向其他位置（另一副本或已移动的旧路径）时，取消勾选保存不得删除；
        // 其他程序的启动项当然也不动
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{OldExePath}\"";
        runKeys.Values["OtherApp"] = @"C:\other\app.exe";
        var prefs = new PreferencesManager(
            new SettingsStore(dir), new AutoStartManager(runKeys, () => ExePath));

        Assert.True(prefs.TrySave(Prefs(notify: false, autoStart: false), out _));
        Assert.Equal($"\"{OldExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.Equal(@"C:\other\app.exe", runKeys.Values["OtherApp"]);
    }

    [Fact]
    public void InstallCopy_ValuePointsPortable_NotificationOnlySave_PreservesValue()
    {
        // 安装版→便携版方向：在安装版里只改通知设置并保存，
        // 指向便携版的启动项必须原样保留
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{PortableExePath}\"";
        var prefs = new PreferencesManager(
            new SettingsStore(dir), new AutoStartManager(runKeys, () => InstallExePath));

        Assert.True(prefs.TrySave(Prefs(notify: true, autoStart: false), out _));
        Assert.Equal($"\"{PortableExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.True(new SettingsStore(dir).Load().NotificationsEnabled); // 通知设置本身已保存
    }

    [Fact]
    public void PortableCopy_ValuePointsInstall_NotificationOnlySave_PreservesValue()
    {
        // 便携版→安装版方向：在便携版里只改通知设置并保存，
        // 指向安装版的启动项必须原样保留
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{InstallExePath}\"";
        var prefs = new PreferencesManager(
            new SettingsStore(dir), new AutoStartManager(runKeys, () => PortableExePath));

        Assert.True(prefs.TrySave(Prefs(notify: true, autoStart: false), out _));
        Assert.Equal($"\"{InstallExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.True(new SettingsStore(dir).Load().NotificationsEnabled);
    }

    [Fact]
    public void ValuePointsElsewhere_ExplicitCheck_SwitchesPointerToThisCopy()
    {
        // 最终规则：明确勾选“开机启动”＝用户显式把启动项切换为当前这份程序
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{PortableExePath}\"";
        var autoStart = new AutoStartManager(runKeys, () => InstallExePath);
        var prefs = new PreferencesManager(new SettingsStore(dir), autoStart);

        Assert.True(prefs.TrySave(Prefs(notify: false, autoStart: true), out _));
        Assert.Equal($"\"{InstallExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.True(autoStart.IsEnabled);
    }

    [Fact]
    public void MovedExeValue_ExplicitCheck_UpdatesPointerToCurrentPath()
    {
        // exe 已移动（旧路径残留）：明确勾选＝用户显式把启动项更新为当前 exe 路径
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        runKeys.Values[AutoStartManager.ValueName] = $"\"{OldExePath}\"";
        var autoStart = new AutoStartManager(runKeys, () => ExePath);
        var prefs = new PreferencesManager(new SettingsStore(dir), autoStart);

        Assert.False(autoStart.IsEnabled); // 旧路径按“当前这份未启用”处理
        Assert.True(prefs.TrySave(Prefs(notify: false, autoStart: true), out _));
        Assert.Equal($"\"{ExePath}\"", runKeys.Values[AutoStartManager.ValueName]);
        Assert.True(autoStart.IsEnabled);
    }

    [Fact]
    public void GetAutoStartPointer_ClassifiesNone_ThisCopy_OtherLocation()
    {
        // 设置窗据此区分“当前这份程序已启用”与“另一位置已有启动项”
        string dir = NewDir();
        var runKeys = new FakeRunKeyStore();
        var prefs = new PreferencesManager(
            new SettingsStore(dir), new AutoStartManager(runKeys, () => ExePath));

        Assert.Equal(AutoStartPointer.None, prefs.GetAutoStartPointer(out string? raw));
        Assert.Null(raw);

        runKeys.Values[AutoStartManager.ValueName] = $"\"{ExePath}\"";
        Assert.Equal(AutoStartPointer.ThisCopy, prefs.GetAutoStartPointer(out raw));
        Assert.Equal($"\"{ExePath}\"", raw);

        runKeys.Values[AutoStartManager.ValueName] = $"\"{PortableExePath}\"";
        Assert.Equal(AutoStartPointer.OtherLocation, prefs.GetAutoStartPointer(out raw));
        Assert.Equal($"\"{PortableExePath}\"", raw);
    }
}
