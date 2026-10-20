using CcfReminder.Core.Reminders;

namespace CcfReminderTray;

/// <summary>
/// 设置窗：通知开关、五档阈值、开机启动、发送测试通知。
/// 通知与开机启动默认均关闭；保存经 PreferencesManager 事务完成
/// （设置文件写失败时注册表恢复原值，并显示明确错误）。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly CheckBox _enableNotifications;
    private readonly Dictionary<int, CheckBox> _thresholdBoxes = new();
    private readonly CheckBox _enableAutoStart;
    private readonly Label _autoStartNote;
    private readonly Label _autoStartNoteHint;
    private readonly ToolTip _pathTip;
    private readonly Label _noteLabel;
    private readonly Button _testButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly ReminderService _reminderService;
    private readonly PreferencesManager _preferences;
    // 布局依据必须用独立字段，不能读 _autoStartNote.Visible：
    // WinForms 的 Visible 读取是 GetVisibleCore()，会与父控件可见性做 AND——
    // 窗体构造期间（尚未 Show）父窗体不可见，读到恒为 false，会把提示高度压成 0
    private bool _autoStartNoteShouldShow;
    private bool _autoStartNoteHintShouldShow;

    /// <summary>路径区高度上限（约 5 行）：异常长路径超出部分以省略号标示，完整值见悬停提示；操作说明区不受此限。</summary>
    internal const int MaxPathNoteHeight = 96;

    // 布局回归测试的观测点（仅测试程序集可见）
    internal Label AutoStartNote => _autoStartNote;
    internal Label AutoStartNoteHint => _autoStartNoteHint;
    internal Label NoteLabel => _noteLabel;
    internal Button TestButton => _testButton;
    internal Button SaveButton => _saveButton;
    internal Button CancelButtonControl => _cancelButton; // 避免遮蔽基类 Form.CancelButton
    internal bool AutoStartNoteShouldShow => _autoStartNoteShouldShow;
    internal bool AutoStartNoteHintShouldShow => _autoStartNoteHintShouldShow;
    internal string? PathToolTipText => _pathTip.GetToolTip(_autoStartNote);

    public SettingsForm(ReminderService reminderService, PreferencesManager preferences)
    {
        _reminderService = reminderService;
        _preferences = preferences;

        Text = "设置 — 会议截稿提醒";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 330);

        int y = 12;
        _enableNotifications = new CheckBox
        {
            Text = "启用桌面通知（仅主会常规的摘要注册／全文提交；默认关闭）",
            Left = 12, Top = y, Width = 436, AutoSize = false,
        };
        Controls.Add(_enableNotifications);
        y += 30;

        var thresholdGroup = new GroupBox
        {
            Text = "提醒阈值（剩余天数，可多选；同一节点每档至多提醒一次）",
            Left = 12, Top = y, Width = 436, Height = 52,
        };
        int x = 12;
        foreach (int days in ReminderSettings.AllowedThresholds)
        {
            var box = new CheckBox { Text = $"{days} 天", Left = x, Top = 22, Width = 70 };
            _thresholdBoxes[days] = box;
            thresholdGroup.Controls.Add(box);
            x += 82;
        }
        Controls.Add(thresholdGroup);
        y += 62;

        _enableAutoStart = new CheckBox
        {
            Text = "开机启动（写入当前用户注册表 Run 键，不需要管理员权限；默认关闭）",
            Left = 12, Top = y, Width = 436, AutoSize = false,
        };
        Controls.Add(_enableAutoStart);
        y += 26;

        // “另一位置已有启动项”提示分两段：路径区与操作说明区。
        // 两个标签都用 AutoSize＋宽度上限：所需高度由 Label 按自己的绘制口径算出，
        // 不再手写 TextRenderer.MeasureText 公式，也就不存在“测量方式与绘制方式不一致”
        // 的空间。（UseCompatibleTextRendering 保持默认 false，即 GDI TextRenderer 绘制；
        // 微软文档：true 反而是 GDI+ 兼容渲染，与 MeasureText 混用会再次错位。）
        // 路径区高度设上限：异常长路径以省略号标示截断，完整值见悬停提示；
        // 操作说明区不设高度上限，任何情况下完整显示。
        _autoStartNote = new Label
        {
            Left = 24, Top = y, Width = 424,
            ForeColor = Color.DarkSlateBlue,
            Visible = false,
            AutoSize = true,
            MaximumSize = new Size(424, MaxPathNoteHeight),
            AutoEllipsis = true, // 超长路径显示“…”，不做静默截断
        };
        Controls.Add(_autoStartNote);

        _autoStartNoteHint = new Label
        {
            Left = 24, Top = y, Width = 424,
            ForeColor = Color.DarkSlateBlue,
            Visible = false,
            AutoSize = true,
            MaximumSize = new Size(424, 0), // 仅限宽度；高度不限，说明必须完整
        };
        Controls.Add(_autoStartNoteHint);

        _pathTip = new ToolTip();

        _noteLabel = new Label
        {
            Text = "说明：应用未运行时不会主动提醒（无后台守护）；\n" +
                   "通知内容注明“来源 ccfddl，未逐项核验”，每个本地日最多一条汇总。",
            Left = 12, Top = y, Width = 436, Height = 40,
        };
        Controls.Add(_noteLabel);
        y += 48;

        _testButton = new Button { Text = "发送测试通知", Left = 12, Top = y, Width = 130 };
        _testButton.Click += (_, _) => SendTestNotification();
        Controls.Add(_testButton);

        _saveButton = new Button { Text = "保存", Left = 258, Top = y, Width = 90, DialogResult = DialogResult.None };
        _saveButton.Click += (_, _) => SaveAndClose();
        Controls.Add(_saveButton);

        _cancelButton = new Button { Text = "取消", Left = 358, Top = y, Width = 90, DialogResult = DialogResult.Cancel };
        Controls.Add(_cancelButton);
        CancelButton = _cancelButton;

        LoadCurrentValues();
    }

    /// <summary>
    /// 按提示标签的实际高度重排其下方控件并调整窗高。
    /// 提示高度不由本方法测量：两个提示标签均为 AutoSize（宽度受限），高度由 Label
    /// 按自身绘制口径确定并随 DPI 字体缩放自动更新；此处只按“应显示”状态决定
    /// 该段是否计入排版，并维护说明区与后续控件的相对位置。
    /// </summary>
    private void RelayoutForAutoStartNote()
    {
        // 用“应该显示”字段而非 Visible 读取：构造期间父窗体未显示，Visible 恒为 false
        int noteHeight = _autoStartNoteShouldShow && _autoStartNote.Text.Length > 0
            ? _autoStartNote.Height : 0;
        int noteBottom = _autoStartNote.Top + noteHeight;

        int hintHeight = _autoStartNoteHintShouldShow && _autoStartNoteHint.Text.Length > 0
            ? _autoStartNoteHint.Height : 0;
        _autoStartNoteHint.Top = noteBottom + (noteHeight > 0 && hintHeight > 0 ? 2 : 0);

        int contentBottom = Math.Max(noteBottom, _autoStartNoteHint.Top + hintHeight);
        int shift = (contentBottom + 6) - _noteLabel.Top;
        if (shift != 0)
        {
            _noteLabel.Top += shift;
            _testButton.Top += shift;
            _saveButton.Top += shift;
            _cancelButton.Top += shift;
        }
        ClientSize = new Size(ClientSize.Width, _cancelButton.Bottom + 12);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RelayoutForAutoStartNote(); // 缩放变化后 AutoSize 高度随字体更新，这里重排相对位置
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pathTip.Dispose();
        }
        base.Dispose(disposing);
    }

    private void LoadCurrentValues()
    {
        ReminderSettings s = _preferences.Load(out string? registryWarning);
        _enableNotifications.Checked = s.NotificationsEnabled;
        foreach ((int days, CheckBox box) in _thresholdBoxes)
        {
            box.Checked = s.ThresholdDays.Contains(days);
        }
        _enableAutoStart.Checked = s.AutoStartEnabled; // 以注册表实际值为准（仅表示“当前这份程序已启用”）

        // 区分“另一位置已有启动项”：此时勾选框为未选中（不算当前副本已启用），
        // 并用提示说明指向与两种明确操作的后果
        _autoStartNoteShouldShow = false;
        _autoStartNoteHintShouldShow = false;
        try
        {
            AutoStartPointer pointer = _preferences.GetAutoStartPointer(out string? rawValue);
            if (pointer == AutoStartPointer.OtherLocation)
            {
                _autoStartNote.Text = $"检测到另一位置的开机启动项：{rawValue}";
                _autoStartNoteHint.Text =
                    "它不属于当前这份程序。勾选“开机启动”并保存会把启动项改指向当前程序；" +
                    "只改通知或阈值后保存不会改动或删除该项。";
                // 路径区被省略号截断时，悬停提示给出注册表原值全文，方便核对
                _pathTip.SetToolTip(_autoStartNote, rawValue);
                _autoStartNoteShouldShow = true;
                _autoStartNoteHintShouldShow = true;
            }
            else
            {
                _autoStartNote.Text = string.Empty;
                _autoStartNoteHint.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            // 注册表读取失败：不得静默伪装成“无启动项”（紧凑布局＋无提示会让用户
            // 误以为没有另一位置的启动项）。Load 已就同一失败弹出警告时本提示与其并存；
            // 仅本次读取失败时由本提示单独给出准确反馈。异常不离开窗口构造流程。
            _autoStartNote.Text =
                $"开机启动状态读取失败（{ex.Message}），暂无法判断启动项指向；" +
                "请关闭后重试打开设置窗，稍后再保存。";
            _autoStartNoteShouldShow = true;
        }
        _autoStartNote.Visible = _autoStartNoteShouldShow;
        _autoStartNoteHint.Visible = _autoStartNoteHintShouldShow;

        RelayoutForAutoStartNote(); // 按提示是否应显示撑开/折叠布局，窗高随内容调整

        if (registryWarning is not null)
        {
            // 注册表读取失败也要给出清楚的界面反馈，不让异常离开窗口构造流程
            MessageBox.Show(registryWarning, "设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 测试通知的诚实反馈：区分“显示请求已提交给系统”与“横幅一定可见”。
    /// 是否弹出由系统通知开关、请勿打扰等控制，应用无法观测。
    /// </summary>
    private void SendTestNotification()
    {
        bool requested = _reminderService.SendTestNotification();
        if (requested)
        {
            MessageBox.Show(
                "已请求 Windows 显示一条标注“测试通知”的横幅。\n\n" +
                "横幅是否弹出由系统控制。若几秒内未看到，请检查：\n" +
                "· 设置 → 系统 → 通知 已开启；\n" +
                "· “请勿打扰”已关闭；\n" +
                "· 通知中心里是否已有该通知（横幅可能已自动收起）。",
                "测试通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(
                "测试通知未能提交给系统（应用窗口句柄不可用或系统调用失败）。\n" +
                "请重试；若持续失败，请报告此情况。",
                "测试通知", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveAndClose()
    {
        var thresholds = _thresholdBoxes.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToHashSet();
        if (_enableNotifications.Checked && thresholds.Count == 0)
        {
            MessageBox.Show("已启用通知但未勾选任何阈值档位。", "设置",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var settings = new ReminderSettings(
            _enableNotifications.Checked, thresholds, _enableAutoStart.Checked);
        if (!_preferences.TrySave(settings, out string? error))
        {
            // 错误文本由 PreferencesManager 如实分层：主保存失败/回滚成功/回滚失败
            MessageBox.Show(error, "设置保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
