using CcfReminder.Core.Reminders;
using Xunit;

namespace CcfReminder.Core.Tests;

public class AutoStartManagerTests
{
    /// <summary>内存注册表替身：测试绝不触碰真实 Run 键。</summary>
    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? GetValue(string name) => Values.TryGetValue(name, out string? v) ? v : null;
        public void SetValue(string name, string value) => Values[name] = value;
        public void DeleteValue(string name) => Values.Remove(name);
    }

    private const string ExePath = @"C:\Apps\my app\CcfReminderTray.exe";

    [Fact]
    public void DisabledByDefault_NoValuePresent()
    {
        var mgr = new AutoStartManager(new FakeRunKeyStore(), () => ExePath);
        Assert.False(mgr.IsEnabled); // 默认关闭
    }

    [Fact]
    public void Enable_WritesQuotedAbsoluteExePath_OwnValueOnly()
    {
        var store = new FakeRunKeyStore();
        store.Values["OtherApp"] = @"C:\other\app.exe";
        var mgr = new AutoStartManager(store, () => ExePath);

        mgr.Enable();

        Assert.Equal($"\"{ExePath}\"", store.Values[AutoStartManager.ValueName]);
        Assert.True(mgr.IsEnabled);
        Assert.Equal(@"C:\other\app.exe", store.Values["OtherApp"]); // 不触碰其他启动项
    }

    [Fact]
    public void IsEnabled_FalseWhenValuePointsElsewhere()
    {
        var store = new FakeRunKeyStore
        {
            Values = { [AutoStartManager.ValueName] = @"C:\somewhere\else.exe" },
        };
        var mgr = new AutoStartManager(store, () => ExePath);
        Assert.False(mgr.IsEnabled); // 值被改动/指向别处：按未开启处理
    }

    [Fact]
    public void Disable_DeletesOnlyOwnValue()
    {
        var store = new FakeRunKeyStore();
        store.Values["OtherApp"] = @"C:\other\app.exe";
        var mgr = new AutoStartManager(store, () => ExePath);
        mgr.Enable();
        mgr.Disable();

        Assert.False(store.Values.ContainsKey(AutoStartManager.ValueName));
        Assert.False(mgr.IsEnabled);
        Assert.Equal(@"C:\other\app.exe", store.Values["OtherApp"]);

        mgr.Disable(); // 重复关闭安全（值不存在时不抛错）
        Assert.Single(store.Values);
    }

    [Theory]
    // 带引号与不带引号的精确路径都视为当前副本（与 Inno 卸载脚本判定口径一致）
    [InlineData("\"C:\\Apps\\my app\\CcfReminderTray.exe\"", AutoStartPointer.ThisCopy)]
    [InlineData("C:\\Apps\\my app\\CcfReminderTray.exe", AutoStartPointer.ThisCopy)]
    [InlineData("c:\\apps\\my app\\ccfremindertray.exe", AutoStartPointer.ThisCopy)] // 不区分大小写
    // 附加参数、引号后多出内容、前缀相似的其他路径：一律视为其他位置，不误认
    [InlineData("\"C:\\Apps\\my app\\CcfReminderTray.exe\" --minimized", AutoStartPointer.OtherLocation)]
    [InlineData("C:\\Apps\\my app\\CcfReminderTray.exe -x", AutoStartPointer.OtherLocation)]
    [InlineData("\"C:\\Apps\\my app\\CcfReminderTray.exe\"extra", AutoStartPointer.OtherLocation)]
    [InlineData("C:\\Apps\\my app\\CcfReminderTray.exe.bak", AutoStartPointer.OtherLocation)]
    [InlineData("C:\\Apps\\my app2\\CcfReminderTray.exe", AutoStartPointer.OtherLocation)]
    [InlineData("D:\\portable\\CcfReminderTray.exe", AutoStartPointer.OtherLocation)]
    public void IsCurrentExePath_ExactQuotedOrPlainOnly_ArgsAndOtherPathsRejected(
        string rawValue, AutoStartPointer expected)
    {
        var mgr = new AutoStartManager(new FakeRunKeyStore(), () => ExePath);
        Assert.Equal(expected, mgr.Classify(rawValue));
        Assert.Equal(expected == AutoStartPointer.ThisCopy, mgr.IsCurrentExePath(rawValue));
    }

    [Fact]
    public void IsCurrentExePath_NullIsFalse()
    {
        var mgr = new AutoStartManager(new FakeRunKeyStore(), () => ExePath);
        Assert.False(mgr.IsCurrentExePath(null));
        Assert.Equal(AutoStartPointer.None, mgr.Classify(null));
    }

    [Fact]
    public void IsEnabled_TrueForUnquotedExactPath()
    {
        // 外部（如旧版本或手工）写入不带引号的精确路径：按“当前这份已启用”处理，
        // 此时取消勾选保存才能删除该值
        var store = new FakeRunKeyStore
        {
            Values = { [AutoStartManager.ValueName] = ExePath },
        };
        var mgr = new AutoStartManager(store, () => ExePath);
        Assert.True(mgr.IsEnabled);
    }
}
