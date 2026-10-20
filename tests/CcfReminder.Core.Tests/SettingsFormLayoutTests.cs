using CcfReminder.Core.Reminders;
using CcfReminderTray;
using Xunit;

namespace CcfReminder.Core.Tests;

/// <summary>
/// 设置窗布局回归测试：构造真实 SettingsForm（独立 STA 线程，内存注册表替身，
/// 随机临时目录——不触碰真实注册表与真实缓存），验证“另一位置＋首次打开窗体”时
/// 路径与末尾操作说明均完整排布、下方文字与按钮不被遮挡。
/// 回归背景：①WinForms 的 Control.Visible 读取与父控件可见性做 AND，构造期恒为
/// false，曾把提示高度压成 0；②手写 TextRenderer.MeasureText 公式与 Label 实际
/// 绘制口径不一致，曾在实机截断路径与说明（辅助功能树含全文不算通过）。
/// 现实现：提示标签 AutoSize＋宽度上限，由 Label 按自身绘制口径定高；本测试用
/// 控件自身的 GetPreferredSize 核算“完整显示所需高度”，不与实现互抄公式。
/// 这些断言只证明布局预留了完整空间，实机视觉验收（截图核对）仍是最终门槛。
/// </summary>
public class SettingsFormLayoutTests
{
    /// <summary>内存注册表替身：测试绝不触碰真实 Run 键。</summary>
    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? GetValue(string name) => Values.TryGetValue(name, out string? v) ? v : null;
        public void SetValue(string name, string value) => Values[name] = value;
        public void DeleteValue(string name) => Values.Remove(name);
    }

    private sealed class StubNotifier : IReminderNotifier
    {
        public bool Show(string title, string message) => true;
    }

    /// <summary>
    /// 第一次读取成功（返回无值）、第二次读取抛错的替身。
    /// 用于覆盖“Load 读取成功、指向分类读取失败”的分支——Load 失败会弹模态框，
    /// 无法在自动化测试中构造窗体验证，故用第二次失败绕开模态框路径。
    /// </summary>
    private sealed class FailAfterFirstReadStore : IRunKeyStore
    {
        private int _reads;
        public string? GetValue(string name)
        {
            _reads++;
            if (_reads >= 2)
            {
                throw new UnauthorizedAccessException("injected second-read fault");
            }
            return null;
        }
        public void SetValue(string name, string value) { }
        public void DeleteValue(string name) { }
    }

    private const string ThisExe = @"C:\Install\CcfReminderTray\CcfReminderTray.exe";
    // 模拟另一副本的正常长度路径，必须完整可见、不得省略
    private const string StagingExe =
        @"C:\Users\demo\Desktop\CcfReminderTray\artifacts\staging-portable\CcfReminderTray\CcfReminderTray.exe";

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "ccf-reminder-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>WinForms 控件要求 STA：在独立 STA 线程中执行，断言异常原样回传。</summary>
    private static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error is null, error?.ToString());
    }

    private static SettingsForm NewForm(string dir, IRunKeyStore runKeys)
    {
        var settings = new SettingsStore(dir);
        var prefs = new PreferencesManager(settings, new AutoStartManager(runKeys, () => ThisExe));
        var svc = new ReminderService(settings, new ReminderStateStore(dir), new StubNotifier(),
            () => DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        return new SettingsForm(svc, prefs);
    }

    /// <summary>
    /// 用同款 Label 引擎核算完整显示所需高度：复制源标签的字体/宽度/文本到无高度上限的
    /// 探针实例再取 GetPreferredSize（控件自身的 GetPreferredSize 会被 MaximumSize 钳制，
    /// 无法用于判断“是否触发省略”）。借助 Label 自身绘制口径，而非手写 MeasureText 公式。
    /// </summary>
    private static int FullNeededHeight(Label source)
    {
        using var probe = new Label
        {
            AutoSize = true,
            Font = source.Font,
            MaximumSize = new Size(source.MaximumSize.Width, 0),
            Text = source.Text,
        };
        return probe.GetPreferredSize(new Size(source.MaximumSize.Width, 0)).Height;
    }

    [Fact]
    public void OtherLocation_FirstOpen_PathAndHintFullyLaidOut_NothingOverlapped()
    {
        OnStaThread(() =>
        {
            string dir = NewDir();
            var runKeys = new FakeRunKeyStore();
            runKeys.Values[AutoStartManager.ValueName] = $"\"{StagingExe}\"";

            using var form = NewForm(dir, runKeys);

            // 窗体尚未显示（构造期）：提示必须已按“应显示”状态撑开——
            // 旧实现读 Visible（父窗体未显示时恒为 false），此处高度为 0 导致实机提示缺失
            Assert.True(form.AutoStartNoteShouldShow);
            Assert.True(form.AutoStartNoteHintShouldShow);
            Assert.True(form.AutoStartNote.Height > 0,
                $"提示高度必须非零，实际 {form.AutoStartNote.Height}");
            Assert.Contains(StagingExe, form.AutoStartNote.Text);

            // 正常长度的真实路径：完整显示所需高度在上限之内——不会触发省略号
            int noteFull = FullNeededHeight(form.AutoStartNote);
            Assert.True(noteFull <= SettingsForm.MaxPathNoteHeight,
                $"正常路径应完整可见（需要 {noteFull}px），超过上限 {SettingsForm.MaxPathNoteHeight}px 会被省略");

            // 末尾操作说明：高度无上限（结构性保证完整），且已撑开非零
            Assert.Equal(0, form.AutoStartNoteHint.MaximumSize.Height);
            Assert.True(form.AutoStartNoteHint.Height > 0);
            Assert.Contains("不会改动或删除该项", form.AutoStartNoteHint.Text);

            // 悬停提示提供注册表原值全文（路径被省略时仍可核对完整值）
            Assert.Equal($"\"{StagingExe}\"", form.PathToolTipText);

            // 布局无遮挡：说明区在路径区之下，说明文字在说明区之下，按钮在说明文字之下
            Assert.True(form.AutoStartNoteHint.Top >= form.AutoStartNote.Bottom);
            Assert.True(form.NoteLabel.Top >= form.AutoStartNoteHint.Bottom,
                $"说明文字 ({form.NoteLabel.Top}) 与提示 ({form.AutoStartNoteHint.Bottom}) 重叠");
            Assert.True(form.TestButton.Top >= form.NoteLabel.Bottom);
            Assert.True(form.SaveButton.Top >= form.NoteLabel.Bottom);
            Assert.True(form.CancelButtonControl.Top >= form.NoteLabel.Bottom);
            Assert.True(form.ClientSize.Height >= form.CancelButtonControl.Bottom + 12,
                $"窗高 {form.ClientSize.Height} 未容纳按钮 ({form.CancelButtonControl.Bottom})");

            // 实际显示后：提示在真实窗口中可见，而非仅辅助功能树可读
            form.Show();
            Assert.True(form.AutoStartNote.Visible, "显示后提示应实际可见");
            Assert.True(form.AutoStartNoteHint.Visible, "显示后操作说明应实际可见");
            Assert.True(form.TestButton.Visible && form.SaveButton.Visible && form.CancelButtonControl.Visible);
            form.Close();
        });
    }

    [Fact]
    public void OtherLocation_LongPath_PathEllipsizedWithToolTip_HintFullyLaidOut()
    {
        OnStaThread(() =>
        {
            string dir = NewDir();
            // 异常长路径：允许路径区省略（带“…”标示、悬停见全文），但操作说明必须完整
            string longPath = @"C:\Program Files\SomeVeryLongInstallLocationName\" +
                new string('超', 200) + @"\Nested\Deep\Directory\CcfReminderTray.exe";
            var runKeys = new FakeRunKeyStore();
            runKeys.Values[AutoStartManager.ValueName] = $"\"{longPath}\"";

            using var form = NewForm(dir, runKeys);

            Assert.True(form.AutoStartNoteShouldShow);
            Assert.True(form.AutoStartNote.Height > 0);

            // 路径确实超出上限（省略确实发生并被标示，而不是碰巧没超）
            int noteFull = FullNeededHeight(form.AutoStartNote);
            Assert.True(noteFull > SettingsForm.MaxPathNoteHeight,
                $"长路径应超出路径区上限以触发省略（实际需要 {noteFull}px）");
            Assert.True(form.AutoStartNote.Height <= SettingsForm.MaxPathNoteHeight);
            Assert.True(form.AutoStartNote.AutoEllipsis, "超长路径必须以省略号标示截断，不得静默截断");
            Assert.Equal($"\"{longPath}\"", form.PathToolTipText); // 完整值可经悬停查看

            // 操作说明区仍完整（无上限）且布局无遮挡
            Assert.Equal(0, form.AutoStartNoteHint.MaximumSize.Height);
            Assert.True(form.AutoStartNoteHint.Height > 0);
            Assert.Contains("不会改动或删除该项", form.AutoStartNoteHint.Text);
            Assert.True(form.AutoStartNoteHint.Top >= form.AutoStartNote.Bottom);
            Assert.True(form.NoteLabel.Top >= form.AutoStartNoteHint.Bottom);
            Assert.True(form.ClientSize.Height >= form.CancelButtonControl.Bottom + 12);

            form.Show();
            Assert.True(form.AutoStartNote.Visible && form.AutoStartNoteHint.Visible);
            form.Close();
        });
    }

    [Fact]
    public void PointerReadFails_FirstOpen_FailureNoteShown_WindowIntact()
    {
        OnStaThread(() =>
        {
            string dir = NewDir();

            using var form = NewForm(dir, new FailAfterFirstReadStore());

            // 读取失败不得静默伪装成“无启动项”：提示应显示失败说明且布局撑开，
            // 而不是呈现与“无值”相同的紧凑布局
            Assert.True(form.AutoStartNoteShouldShow);
            Assert.False(form.AutoStartNoteHintShouldShow); // 读取失败没有“另一位置”说明段
            Assert.Contains("读取失败", form.AutoStartNote.Text);
            Assert.True(form.AutoStartNote.Height > 0,
                $"失败提示高度必须非零，实际 {form.AutoStartNote.Height}");
            Assert.True(form.ClientSize.Height >= form.CancelButtonControl.Bottom + 12);

            form.Show();
            Assert.True(form.AutoStartNote.Visible);
            form.Close();
        });
    }

    [Fact]
    public void NoRunValue_FirstOpen_CompactLayout_NoteCollapsed()
    {
        OnStaThread(() =>
        {
            string dir = NewDir();
            var runKeys = new FakeRunKeyStore(); // 无值：紧凑布局通过复验的状态不得回归

            using var form = NewForm(dir, runKeys);

            Assert.False(form.AutoStartNoteShouldShow);
            Assert.False(form.AutoStartNoteHintShouldShow);
            // 紧凑：无提示占位空隙（提示段不计入排版），窗高仅容纳既有内容
            Assert.True(form.ClientSize.Height >= form.CancelButtonControl.Bottom + 12);
            Assert.True(form.ClientSize.Height < form.CancelButtonControl.Bottom + 40,
                "无值时出现过大空隙，紧凑布局回归");

            form.Show();
            Assert.False(form.AutoStartNote.Visible);
            Assert.False(form.AutoStartNoteHint.Visible);
            form.Close();
        });
    }
}
