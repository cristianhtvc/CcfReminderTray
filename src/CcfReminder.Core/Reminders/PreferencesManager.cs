namespace CcfReminder.Core.Reminders;

/// <summary>
/// 设置窗的读写协调：开机启动（注册表）与 settings.json 的事务性保存。
/// 顺序：先改注册表并记住原值 → 再写设置文件；设置文件写失败时把注册表恢复原值。
/// 注意：注册表与 JSON 无法绝对原子提交，这里只保证“错误如实分层报告”——
/// 主保存失败 / 回滚成功 / 回滚失败三种状态在 error 中明确区分，
/// 回滚失败时告知当前可能不一致及可操作的恢复办法。
/// 开机启动的指向规则（安装版与便携版共享同一个 Run 值名）：
/// 勾选＝把启动项写入／切换为当前这份程序（用户明确操作）；取消勾选＝仅当值
/// 恰好指向当前这份程序时才删除；值指向其他位置（另一份仍可能有效的副本、或
/// exe 移动后的旧路径）时，保存任何设置都不改动该值，由用户明确操作来切换或清除。
/// </summary>
public sealed class PreferencesManager
{
    private readonly SettingsStore _settingsStore;
    private readonly AutoStartManager _autoStart;

    /// <summary>
    /// 回滚失败后的恢复说明。如实区分两种明确操作：勾选保存会把值改写为当前程序路径；
    /// 取消保存只在值指向当前程序时才删除——指向其他位置的值不会被取消保存清除。
    /// </summary>
    private static string RecoveryHint =>
        "恢复办法：重新打开设置窗——若希望开机启动指向当前程序，勾选“开机启动”后保存" +
        "（会把该值改写为当前程序路径）；若希望清除该值，先确认它指向当前程序，再取消勾选保存" +
        "（指向其他位置的值不会被取消保存删除）。必要时也可在 " +
        "HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run 中手动核对 " +
        $"{AutoStartManager.ValueName} 值。";

    public PreferencesManager(SettingsStore settingsStore, AutoStartManager autoStart)
    {
        _settingsStore = settingsStore;
        _autoStart = autoStart;
    }

    /// <summary>
    /// 读取当前设置；AutoStartEnabled 以注册表实际值为准。
    /// 注册表读取失败时不抛异常：按“未开启”显示并通过 registryWarning 给出界面可展示的中文提示。
    /// </summary>
    public ReminderSettings Load(out string? registryWarning)
    {
        ReminderSettings s = _settingsStore.Load();
        try
        {
            registryWarning = null;
            return s with { AutoStartEnabled = _autoStart.IsEnabled };
        }
        catch (Exception ex)
        {
            registryWarning =
                $"开机启动状态读取失败：{ex.Message}。\n" +
                "“开机启动”勾选状态暂按“未开启”显示；请重试打开设置窗，稍后再保存。";
            return s with { AutoStartEnabled = false };
        }
    }

    /// <summary>
    /// 读取本应用 Run 值的指向分类与原值，供设置窗区分
    /// “当前这份程序已启用”与“另一位置已有启动项”。注册表读取失败时抛异常，
    /// 调用方应捕获（Load 已就同一失败给出警告，此处不得让异常离开窗口构造流程）。
    /// </summary>
    public AutoStartPointer GetAutoStartPointer(out string? rawValue)
    {
        rawValue = _autoStart.GetRawValue();
        return _autoStart.Classify(rawValue);
    }

    /// <summary>事务性保存。返回 false 时 out error 为可如实展示的中文错误。</summary>
    public bool TrySave(ReminderSettings settings, out string? error)
    {
        string? previousValue;
        try
        {
            previousValue = _autoStart.GetRawValue();
        }
        catch (Exception ex)
        {
            error = $"开机启动状态读取失败：{ex.Message}。未做任何改动。";
            return false;
        }

        bool registryChanged = false;
        try
        {
            if (settings.AutoStartEnabled)
            {
                // 明确勾选：写入当前 exe 路径；旧值指向其他位置时这是用户明确切换指向
                if (!_autoStart.IsEnabled)
                {
                    _autoStart.Enable();
                    registryChanged = true;
                }
            }
            else if (_autoStart.IsEnabled)
            {
                // 明确取消：仅当值指向当前这份程序时才删除
                _autoStart.Disable();
                registryChanged = true;
            }
            // 未勾选且值指向其他位置：不做任何改动（只改通知/阈值不得误删另一份副本的启动项）
        }
        catch (Exception ex)
        {
            // 写入路径抛错无法证明注册表未被改动（值可能已写入后操作才失败）：
            // 尽力恢复事先读取的原值，并按恢复结果如实分层报告，不暗示“完全没有改动”
            try
            {
                _autoStart.RestoreRawValue(previousValue);
                error = $"开机启动设置失败：{ex.Message}。已尽力恢复注册表原值；设置文件未写入。";
            }
            catch (Exception restoreEx)
            {
                error =
                    $"开机启动设置失败：{ex.Message}；恢复注册表原值也失败：{restoreEx.Message}。\n" +
                    "注册表可能已改变，与期望状态不一致。" + RecoveryHint;
            }
            return false;
        }

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception saveEx)
        {
            if (!registryChanged)
            {
                error = $"设置保存失败：{saveEx.Message}。注册表未改动。";
                return false;
            }
            try
            {
                _autoStart.RestoreRawValue(previousValue);
                error = $"设置保存失败：{saveEx.Message}。开机启动项已恢复原值，未保留半套改动。";
                return false;
            }
            catch (Exception rollbackEx)
            {
                error =
                    $"设置保存失败：{saveEx.Message}；并且开机启动项回滚失败：{rollbackEx.Message}。\n" +
                    "当前注册表与设置文件可能不一致。" + RecoveryHint;
                return false;
            }
        }

        error = null;
        return true;
    }
}
