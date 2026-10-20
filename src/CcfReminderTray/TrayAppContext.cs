using CcfReminder.Core;
using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;
using CcfReminder.Core.Reminders;

namespace CcfReminderTray;

/// <summary>
/// 托盘应用外壳：无常驻主窗口，消息循环由 ApplicationContext 承载。
/// 菜单在每次打开时按当前 UTC 时刻重算；数据刷新全部在后台进行，不阻塞右键。
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReminderCheckInterval = TimeSpan.FromHours(1);

    private readonly SingleInstance _singleInstance;
    private readonly ConferenceDataService _dataService;
    private readonly ReminderService _reminderService;
    private readonly PreferencesManager _preferences;
    private readonly DeadlineOverrideManager _overrideManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly nint _iconHandle;
    private readonly Font _headerFont;
    private readonly Control _marshaller; // 隐藏控件，仅用于把后台事件封送到 UI 线程
    private readonly CancellationTokenSource _appCts = new();
    private readonly System.Threading.Timer _refreshTimer;
    private readonly System.Threading.Timer _reminderTimer;

    private readonly ToolStripLabel _statusLabel;
    private readonly Dictionary<string, ToolStripLabel> _conferenceLabels = new();
    private readonly ToolStripMenuItem _refreshItem;
    private DetailsForm? _detailsForm;

    public TrayAppContext(
        SingleInstance singleInstance,
        ConferenceDataService dataService,
        SettingsStore settingsStore,
        ReminderStateStore reminderStateStore,
        PreferencesManager preferences,
        DeadlineOverrideManager overrideManager)
    {
        _singleInstance = singleInstance;
        _dataService = dataService;
        _preferences = preferences;
        _overrideManager = overrideManager;
        _overrideManager.Changed += OnOverridesChanged;
        _singleInstance.ShowRequested += OnShowRequested;

        (_icon, _iconHandle) = TrayIconFactory.CreateClockIcon();
        _marshaller = new Control();
        _ = _marshaller.Handle; // 强制创建句柄，供 BeginInvoke 使用

        // 订阅完成且 UI 封送设施（_marshaller 句柄）就绪后才开始监听：
        // 任何到达的唤起请求都能立即被封送到 UI 线程，不会因启动时序丢失
        _singleInstance.StartListening();

        // 关闭图片边距：菜单中没有图标，让所有行文字左对齐、易读
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        _headerFont = new Font(menu.Font, FontStyle.Bold);

        _statusLabel = new ToolStripLabel { Font = _headerFont };
        menu.Items.Add(_statusLabel);
        menu.Items.Add(new ToolStripSeparator());

        // 六个会议各一行：只读信息行（ToolStripLabel 无悬停高亮、不可点击）
        foreach (ConferenceInfo info in ConferenceCatalog.All)
        {
            var label = new ToolStripLabel();
            _conferenceLabels[info.Id] = label;
            menu.Items.Add(label);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("查看全部", null, (_, _) => ShowDetails());
        _refreshItem = new ToolStripMenuItem("立即刷新", null, (_, _) => StartRefresh());
        menu.Items.Add(_refreshItem);
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        // 菜单每次打开时按当前时刻重算，不为网络请求阻塞
        menu.Opening += (_, _) => UpdateMenuContent();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "会议截稿提醒",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => ShowDetails();

        // 提醒服务：气球通知经 UI 封送发出；每次检查时从磁盘读设置，保存后立即生效
        _reminderService = new ReminderService(
            settingsStore,
            reminderStateStore,
            new BalloonNotifier(_notifyIcon, _marshaller),
            () => DateTimeOffset.UtcNow);

        UpdateMenuContent();

        // 启动后立即后台刷新一次，之后每 6 小时一次；失败不影响旧缓存
        StartRefresh();
        _refreshTimer = new System.Threading.Timer(
            _ => StartRefresh(),
            null,
            RefreshInterval,
            RefreshInterval);

        // 提醒评估不依赖网络刷新：倒计时按当前时刻每小时重算一次
        _reminderTimer = new System.Threading.Timer(
            _ => CheckReminders(),
            null,
            ReminderCheckInterval,
            ReminderCheckInterval);

        MaybeShowFirstRunGuide();
        WarnIfOverridesCorrupted();
    }

    /// <summary>
    /// 套用用户修正后的有效快照：菜单倒计时、提醒评估统一以此为准；
    /// 原始快照永不被改写（详情窗自行对照展示）。修正文件损坏时内存修正表为空，
    /// 此处自然回退为纯上游视图。
    /// </summary>
    private ConferenceDataSnapshot? EffectiveSnapshot()
    {
        ConferenceDataSnapshot? snap = _dataService.Current;
        if (snap is null || _overrideManager.Overrides.Count == 0)
        {
            return snap;
        }
        return DeadlineOverrideResolver.ApplyToSnapshot(snap, _overrideManager.Overrides);
    }

    /// <summary>修正保存/删除/重新初始化后：菜单立即重算，并按有效时间重估一次提醒。</summary>
    private void OnOverridesChanged(object? sender, EventArgs e)
    {
        MarshalToUi(UpdateMenuContent);
        _ = Task.Run(CheckReminders);
    }

    /// <summary>启动时醒目告警损坏的修正文件（绝不静默当作无修正）。</summary>
    private void WarnIfOverridesCorrupted()
    {
        if (!_overrideManager.IsCorrupted)
        {
            return;
        }
        MessageBox.Show(
            $"修正文件损坏（{_overrideManager.StorePath}）：\n{_overrideManager.CorruptionError}\n\n" +
            "程序已按上游时间运行；你的修正未被覆盖，暂时也无法增删修正。\n" +
            "打开“查看全部”，底部面板可“备份损坏文件并重新初始化”。",
            "修正文件损坏",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>打开详情窗；已存在则带到前台，并刷新内容。</summary>
    public void ShowDetails()
    {
        if (_detailsForm is null || _detailsForm.IsDisposed)
        {
            // 快照提供器：编辑窗保存时按“最新”快照重新核对唯一性与上游值，
            // 避免打开期间后台刷新后以过期快照留证
            _detailsForm = new DetailsForm(_overrideManager, () => _dataService.Current);
        }
        _detailsForm.UpdateContent(_dataService.Current, _dataService.LastError);
        if (!_detailsForm.Visible)
        {
            _detailsForm.Show();
        }
        if (_detailsForm.WindowState == FormWindowState.Minimized)
        {
            _detailsForm.WindowState = FormWindowState.Normal;
        }
        _detailsForm.Activate();
    }

    /// <summary>按当前数据快照与当前 UTC 时刻重算菜单文字（纯内存操作）。</summary>
    private void UpdateMenuContent()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ConferenceDataSnapshot? snap = _dataService.Current;
        ConferenceDataSnapshot? effective = EffectiveSnapshot();

        string refreshing = _dataService.IsRefreshing ? " · 正在刷新…" : "";
        string corrupted = _overrideManager.IsCorrupted ? " · ⚠ 修正文件损坏" : "";
        if (snap is null)
        {
            // 无有效缓存：区分“从未刷新/启动中”与“获取失败”，不提“旧数据”
            _statusLabel.Text =
                (_dataService.LastError is not null ? "获取失败 · 暂无缓存" : "尚未获取数据")
                + refreshing + corrupted;
        }
        else
        {
            // 有有效缓存：失败时明确“已保留旧数据”
            string failed = _dataService.LastError is not null && refreshing.Length == 0
                ? " · 上次刷新失败（已保留旧数据）"
                : "";
            _statusLabel.Text =
                $"更新于 {snap.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}（本地）· 来源 ccfddl，未逐项核验"
                + refreshing + failed + corrupted;
        }

        // “已修正”标记只标实际应用的修正：候选歧义/上游缺失的修正不应用、不标记
        //（它们在详情窗以“待核对/上游缺失”醒目呈现）
        IReadOnlyDictionary<string, DeadlineOverride> applicable =
            snap is null || _overrideManager.Overrides.Count == 0
                ? new Dictionary<string, DeadlineOverride>()
                : DeadlineOverrideResolver.FindApplicable(snap.Conferences, _overrideManager.Overrides);

        foreach (ConferenceInfo info in ConferenceCatalog.All)
        {
            ToolStripLabel label = _conferenceLabels[info.Id];
            if (effective is null)
            {
                label.Text = $"{info.ShortName} · 尚未获取数据";
                continue;
            }
            ConferenceDataset dataset = effective.Conferences.First(c => c.Identity.Id == info.Id);
            SelectionResult result = NextSubmissionSelector.Select(dataset.Nodes, now);
            string line = CountdownFormatter.FormatMenuLine(info.ShortName, result, now);
            string? selectedKey = result.Node is null ? null : OverrideNodeKey.TryBuild(result.Node);
            if (selectedKey is not null && applicable.ContainsKey(selectedKey))
            {
                line += " · 已修正";
            }
            label.Text = line;
        }

        _refreshItem.Enabled = !_dataService.IsRefreshing;
        _refreshItem.Text = _dataService.IsRefreshing ? "正在刷新…" : "立即刷新";
    }

    /// <summary>触发一次后台刷新；进行中/成功/失败状态都反映在菜单状态行。</summary>
    private void StartRefresh()
    {
        if (_dataService.IsRefreshing || _appCts.IsCancellationRequested)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _dataService.RefreshAsync(_appCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 应用退出时取消，无需反馈
            }
            MarshalToUi(() =>
            {
                UpdateMenuContent();
                if (_detailsForm is { IsDisposed: false, Visible: true })
                {
                    _detailsForm.UpdateContent(_dataService.Current, _dataService.LastError);
                }
            });
            // 刷新结束（无论成败）后按最新快照评估一次提醒
            CheckReminders();
        });
    }

    /// <summary>评估提醒：读取当前快照，到期时发一条汇总气球；失败不影响其他功能。</summary>
    private void CheckReminders()
    {
        if (_appCts.IsCancellationRequested)
        {
            return;
        }
        _reminderService.CheckAndNotify(EffectiveSnapshot());
    }

    /// <summary>
    /// 打开设置窗（模态）。保存成功后立即在后台评估一次提醒
    /// （每日上限、每档一次等规则不变）；后台执行避免与提醒定时器在 UI 线程上互等。
    /// </summary>
    private void ShowSettings()
    {
        using var form = new SettingsForm(_reminderService, _preferences);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _ = Task.Run(CheckReminders);
        }
    }

    private void MarshalToUi(Action action)
    {
        if (_marshaller.IsHandleCreated && !_marshaller.IsDisposed)
        {
            try { _marshaller.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowDetails();
        }
    }

    private void OnShowRequested(object? sender, EventArgs e)
    {
        // 来自管道监听线程，封送到 UI 线程执行
        MarshalToUi(ShowDetails);
    }

    private void MaybeShowFirstRunGuide()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CcfReminderTray");
            string marker = Path.Combine(dir, "first-run-guide-shown.txt");
            if (File.Exists(marker))
            {
                return;
            }

            MessageBox.Show(
                "会议截稿提醒已在通知区域（任务栏右下角）运行。\n\n" +
                "如果看不到图标，请点击任务栏右侧的 “^” 展开隐藏的图标；" +
                "希望图标常驻显示时，可在 Windows 设置 → 个性化 → 任务栏 → 其他系统托盘图标 中打开本应用的开关。\n\n" +
                "图标显示或隐藏完全由你控制，本应用不会强制固定。本提示只出现一次。",
                "首次运行提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            Directory.CreateDirectory(dir);
            File.WriteAllText(marker, "shown");
        }
        catch
        {
            // 无法写入标记文件时最多导致提示再次出现，不影响主功能
        }
    }

    private void ExitApplication()
    {
        _appCts.Cancel(); // 取消进行中的后台刷新
        _refreshTimer.Dispose();
        _reminderTimer.Dispose();

        _notifyIcon.Visible = false; // 先移除托盘图标
        _notifyIcon.Dispose();
        TrayIconFactory.Destroy(_icon, _iconHandle);

        if (_detailsForm is { IsDisposed: false })
        {
            _detailsForm.ForceClose();
        }

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _singleInstance.ShowRequested -= OnShowRequested;
            _overrideManager.Changed -= OnOverridesChanged;
            _appCts.Dispose();
            _headerFont.Dispose();
            _marshaller.Dispose();
        }
        base.Dispose(disposing);
    }
}
