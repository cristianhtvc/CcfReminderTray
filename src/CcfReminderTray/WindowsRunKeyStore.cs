using CcfReminder.Core.Reminders;
using Microsoft.Win32;

namespace CcfReminderTray;

/// <summary>
/// HKCU\...\Run 的真实读写。只访问当前用户键（不需要管理员权限），
/// 值的增删由 AutoStartManager 限定为本应用自己的值名。
/// </summary>
internal sealed class WindowsRunKeyStore : IRunKeyStore
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _keyPath = RunKeyPath;

    public WindowsRunKeyStore()
    {
    }

    // 仅供测试指向 HKCU 下本应用自有的隔离测试子键；生产代码一律使用无参构造（真实 Run 键）。
    internal WindowsRunKeyStore(string keyPath)
    {
        _keyPath = keyPath;
    }

    public string? GetValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(name, value);
    }

    public void DeleteValue(string name)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
