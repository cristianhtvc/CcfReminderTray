namespace CcfReminder.Core.Reminders;

/// <summary>注册表 Run 键的读写出口（真实实现／内存替身）。</summary>
public interface IRunKeyStore
{
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
}

/// <summary>
/// 本应用 Run 值的指向分类：安装版与便携版共享同一个值名，
/// 只有明确区分“指向当前这份程序”与“指向其他位置”，才能避免误删另一份副本的启动项。
/// </summary>
public enum AutoStartPointer
{
    /// <summary>无值。</summary>
    None,
    /// <summary>值指向当前运行的这份程序。</summary>
    ThisCopy,
    /// <summary>值指向其他位置（另一份仍可能有效的副本，或 exe 移动后的旧路径）。</summary>
    OtherLocation,
}

/// <summary>
/// 开机启动管理：只读写 HKCU Run 中本应用自己的值（不需要管理员权限），
/// 值为带引号的 exe 绝对路径；关闭时只删除该值，不触碰其他程序的启动项。
/// </summary>
public sealed class AutoStartManager
{
    public const string ValueName = "CcfReminderTray";

    private readonly IRunKeyStore _store;
    private readonly Func<string> _exePath;

    public AutoStartManager(IRunKeyStore store, Func<string> exePath)
    {
        _store = store;
        _exePath = exePath;
    }

    private string ExpectedValue => $"\"{_exePath()}\"";

    /// <summary>
    /// 判定注册表原值是否恰好指向当前 exe：带引号或不带引号的精确路径匹配
    /// （不区分大小写，与 Inno 卸载脚本的判定口径一致）；
    /// 附加参数、引号后多出内容、或只是前缀相同的其他路径一律视为“其他位置”，
    /// 不会误认作当前副本。
    /// </summary>
    public bool IsCurrentExePath(string? rawValue)
    {
        if (rawValue is null)
        {
            return false;
        }
        string v = rawValue.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
        {
            v = v.Substring(1, v.Length - 2);
        }
        return string.Equals(v, _exePath(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>以注册表实际值为准（值被外部改动或 exe 已移动时按未开启处理）。</summary>
    public bool IsEnabled => IsCurrentExePath(_store.GetValue(ValueName));

    /// <summary>本应用的值是否存在（含指向旧 exe 路径的残留值）。</summary>
    public bool HasAnyValue => _store.GetValue(ValueName) is not null;

    /// <summary>把读取到的原值分类为无值／指向当前这份程序／指向其他位置。</summary>
    public AutoStartPointer Classify(string? rawValue) =>
        rawValue is null ? AutoStartPointer.None
        : IsCurrentExePath(rawValue) ? AutoStartPointer.ThisCopy
        : AutoStartPointer.OtherLocation;

    public void Enable() => _store.SetValue(ValueName, ExpectedValue);

    /// <summary>
    /// 删除本应用的值，不碰其他程序的启动项。
    /// 注意：调用方须先经 Classify 确认值指向当前这份程序——
    /// 指向其他位置的值属于另一份副本，只能由用户明确操作来切换或清除。
    /// </summary>
    public void Disable() => _store.DeleteValue(ValueName);

    /// <summary>读取本应用原值，用于设置保存失败时回滚。</summary>
    public string? GetRawValue() => _store.GetValue(ValueName);

    /// <summary>把本应用的值恢复到原样（null＝原本无值则删除），用于回滚。</summary>
    public void RestoreRawValue(string? value)
    {
        if (value is null)
        {
            _store.DeleteValue(ValueName);
        }
        else
        {
            _store.SetValue(ValueName, value);
        }
    }
}
