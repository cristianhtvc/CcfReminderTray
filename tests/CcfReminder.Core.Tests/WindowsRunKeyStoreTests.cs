using CcfReminder.Core.Reminders;
using CcfReminderTray;
using Microsoft.Win32;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>
/// 真实注册表集成测试：只读写 HKCU 下本应用自有的隔离测试子键
/// （HKCU\SOFTWARE\CcfReminderTray\Tests\&lt;guid&gt;\Run），绝不触碰真实 Run 项；
/// 每个用例结束即删除该测试子键。
/// 回归背景：实机复验曾出现“真实 Run 值指向另一副本，设置窗却按无值排版”。
/// 诊断（真实 exe＋真实注册表读取）须覆盖 读取为空／读取到值／分类 全链路，
/// 不能只靠内存替身证明真实 API 行为。
/// </summary>
public class WindowsRunKeyStoreTests : IDisposable
{
    private const string TestKeyRoot = @"SOFTWARE\CcfReminderTray\Tests";
    private readonly string _subKeyName = Guid.NewGuid().ToString("N");
    private string KeyPath => $@"{TestKeyRoot}\{_subKeyName}\Run";

    private const string ThisExe = @"C:\Install\CcfReminderTray\CcfReminderTray.exe";
    private const string OtherExe = @"C:\Portable\CcfReminderTray-portable\CcfReminderTray.exe";

    private WindowsRunKeyStore NewStore() => new(KeyPath);

    [Fact]
    public void MissingValue_GetValue_ReturnsNull_ClassifyNone()
    {
        WindowsRunKeyStore store = NewStore();
        Assert.Null(store.GetValue(AutoStartManager.ValueName)); // 真实读取为空

        var mgr = new AutoStartManager(store, () => ThisExe);
        Assert.Equal(AutoStartPointer.None, mgr.Classify(mgr.GetRawValue()));
        Assert.False(mgr.IsEnabled);
    }

    [Fact]
    public void QuotedOtherPath_RoundTripsVerbatim_ClassifyOtherLocation()
    {
        WindowsRunKeyStore store = NewStore();
        string expected = $"\"{OtherExe}\"";
        store.SetValue(AutoStartManager.ValueName, expected);

        // 真实注册表 API 原样读回（不丢引号、不展开、不改大小写）
        Assert.Equal(expected, store.GetValue(AutoStartManager.ValueName));

        var mgr = new AutoStartManager(store, () => ThisExe);
        Assert.Equal(AutoStartPointer.OtherLocation, mgr.Classify(mgr.GetRawValue()));
        Assert.False(mgr.IsEnabled); // 指向另一位置不算“当前这份程序已启用”
    }

    [Fact]
    public void QuotedThisPath_ClassifyThisCopy_IsEnabledTrue()
    {
        WindowsRunKeyStore store = NewStore();
        store.SetValue(AutoStartManager.ValueName, $"\"{ThisExe}\"");

        var mgr = new AutoStartManager(store, () => ThisExe);
        Assert.Equal(AutoStartPointer.ThisCopy, mgr.Classify(mgr.GetRawValue()));
        Assert.True(mgr.IsEnabled);
    }

    public void Dispose()
    {
        // 清理隔离测试子键；不存在时静默通过。绝不在此处触碰真实 Run 键。
        Registry.CurrentUser.DeleteSubKeyTree($@"{TestKeyRoot}\{_subKeyName}", throwOnMissingSubKey: false);
    }
}
