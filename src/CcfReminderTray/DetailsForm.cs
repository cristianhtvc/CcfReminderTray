using CcfReminder.Core;
using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;

namespace CcfReminderTray;

/// <summary>
/// 详情窗：展示六个会议的全部已知节点（含 rebuttal/录用通知/特殊轮次）、
/// 上游原始时间/时区、修正后的有效时间、修正状态、来源与核验状态。
/// “有效时间”列与菜单/提醒共用同一套唯一匹配规则：候选不唯一或上游缺失的修正
/// 不应用（该行有效时间仍显示上游值），仅在“修正”列与底部面板醒目标记待核对。
/// 上游已消失的修正以独立分组列在末尾，可查看、恢复或处理，绝不悄悄丢弃。
/// 选中已修正节点时，底部面板给出三方对照（修正时上游值／最新上游值／用户修正值）、
/// 依据、来源与修正时间；修正文件损坏时醒目告警并提供“备份并重新初始化”。
/// 点 X 只隐藏不退出。
/// </summary>
internal sealed class DetailsForm : Form
{
    /// <summary>Node 为空＝上游缺失的孤儿修正行（此时 OverrideKey 必有值）。</summary>
    private sealed record RowRef(DeadlineNode? Node, string ShortName, string? OverrideKey);

    private readonly DeadlineOverrideManager _overrides;
    private readonly Func<ConferenceDataSnapshot?> _snapshotProvider;
    private readonly Label _banner;
    private readonly ListView _list;
    private readonly Button _openSourceButton;
    private readonly Button _editOverrideButton;
    private readonly Button _removeOverrideButton;
    private readonly Button _reinitializeButton;
    private readonly TextBox _detailPanel;
    private bool _allowClose;
    // 可见性意图字段：Control.Visible 读取与父级可见性 AND（窗口未 Show 时恒为 false），
    // 布局/断言须用意图字段（与 SettingsForm 同一模式）
    private bool _reinitializeShouldShow;
    private bool _detailPanelShouldShow;
    private ConferenceDataSnapshot? _snap;
    private string? _lastError;

    public DetailsForm(DeadlineOverrideManager overrides, Func<ConferenceDataSnapshot?> snapshotProvider)
    {
        _overrides = overrides;
        _snapshotProvider = snapshotProvider;
        _overrides.Changed += (_, _) => RefreshContent();

        Text = "会议截稿提醒 — 全部节点";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 620);

        _banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Color.LightSteelBlue,
            ForeColor = Color.Black,
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("会议", 110);
        _list.Columns.Add("节点", 80);
        _list.Columns.Add("轮次", 90);
        _list.Columns.Add("原始时间", 150);
        _list.Columns.Add("原始时区", 80);
        _list.Columns.Add("本地时间", 150);
        _list.Columns.Add("有效时间(本地)", 150);
        _list.Columns.Add("修正", 130);
        _list.Columns.Add("核验", 70);
        _list.Columns.Add("来源", 200);
        _list.SelectedIndexChanged += (_, _) => UpdateSelectionUi();

        // 底部修正详情面板（三方对照/损坏告警），默认隐藏
        _detailPanel = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 128,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Visible = false,
            BackColor = Color.Ivory,
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        _openSourceButton = new Button { Text = "打开来源链接", Enabled = false, Left = 8, Top = 8, Width = 110 };
        _openSourceButton.Click += (_, _) => OpenSelectedSource();
        _editOverrideButton = new Button { Text = "修正此节点…", Enabled = false, Left = 126, Top = 8, Width = 110 };
        _editOverrideButton.Click += (_, _) => EditSelectedOverride();
        _removeOverrideButton = new Button { Text = "恢复上游值", Enabled = false, Left = 244, Top = 8, Width = 110 };
        _removeOverrideButton.Click += (_, _) => RemoveSelectedOverride();
        _reinitializeShouldShow = _overrides.IsCorrupted;
        _reinitializeButton = new Button
        {
            Text = "备份损坏文件并重新初始化",
            Left = 362, Top = 8, Width = 190,
            Visible = _reinitializeShouldShow,
        };
        _reinitializeButton.Click += (_, _) => ReinitializeOverrides();
        bottom.Controls.Add(_openSourceButton);
        bottom.Controls.Add(_editOverrideButton);
        bottom.Controls.Add(_removeOverrideButton);
        bottom.Controls.Add(_reinitializeButton);

        Controls.Add(_list);
        Controls.Add(_detailPanel);
        Controls.Add(_banner);
        Controls.Add(bottom);
    }

    // ---- 测试钩子 ----
    internal Button EditOverrideButton => _editOverrideButton;
    internal Button RemoveOverrideButton => _removeOverrideButton;
    internal Button OpenSourceButton => _openSourceButton;
    internal bool ReinitializeVisible => _reinitializeShouldShow;
    internal string BannerText => _banner.Text;
    internal string DetailPanelText => _detailPanel.Text;
    internal bool DetailPanelVisible => _detailPanelShouldShow;
    internal int RowCount => _list.Items.Count;
    internal int? SelectedIndex => _list.SelectedIndices.Count == 1 ? _list.SelectedIndices[0] : null;
    internal void SelectRowAt(int index)
    {
        // 强制创建 ListView 自身句柄（Form.Handle 不级联创建子控件）：否则自动化测试
        // 不 Show 窗口时 ListView 不保留选中状态，SelectedIndexChanged 也不会触发
        _ = _list.Handle;
        _list.SelectedItems.Clear();
        _list.Items[index].Selected = true;
        UpdateSelectionUi(); // 与事件路径幂等；无消息循环时兜底
    }

    /// <summary>用最新快照重建内容；打开窗口前调用。snap 为原始（未套用修正的）上游快照。</summary>
    public void UpdateContent(ConferenceDataSnapshot? snap, string? lastError)
    {
        _snap = snap;
        _lastError = lastError;
        RefreshContent();
    }

    private void RefreshContent()
    {
        if (IsDisposed)
        {
            return;
        }

        // 重建列表前记住选中行的节点键：保存/恢复修正触发的重建保留选中，
        // 让三方对照立即可见（行消失时自然取消选中）
        string? reselectKey = SelectedRow()?.OverrideKey;

        IReadOnlyList<OverrideNotice> notices =
            OverrideChangeInspector.Inspect(_overrides.Overrides, _snap);

        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Groups.Clear();

        if (_snap is null)
        {
            _banner.Text = _lastError is null
                ? "尚未获取数据。请使用托盘菜单“立即刷新”。"
                : $"数据获取失败（{_lastError}），暂无缓存。可稍后在托盘菜单选择“立即刷新”。";
            _banner.Text += CorruptionSuffix();
            _list.Items.Add(new ListViewItem("暂无数据"));
        }
        else
        {
            _banner.Text =
                $"数据更新于 {_snap.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本地）· " +
                $"来源 {_snap.SourceName} · 全部未逐项核验（核验状态以人工对照当届官网为准）" +
                (_lastError is null ? "" : " · 上次刷新失败（已保留旧数据）") +
                (notices.Count > 0 ? $" · ⚠ {notices.Count} 项修正待核对" : "") +
                CorruptionSuffix();

            // 有效时间列与菜单/提醒同规则：仅候选唯一的修正被应用；
            // 结果与原始快照逐行一一对应（数量与顺序不变）
            IReadOnlyList<ConferenceDataset> effectiveConferences =
                DeadlineOverrideResolver.ApplyEffectiveTimes(_snap.Conferences, _overrides.Overrides);

            for (int d = 0; d < _snap.Conferences.Count; d++)
            {
                ConferenceDataset dataset = _snap.Conferences[d];
                ConferenceDataset effectiveDataset = effectiveConferences[d];
                string rank = dataset.RankCcfUpstream is null
                    ? ""
                    : $" · 上游标签 CCF-{dataset.RankCcfUpstream}（ccfddl）";
                var group = new ListViewGroup(
                    dataset.Identity.Id,
                    $"{dataset.Identity.ShortName} — {dataset.Identity.FullName}{rank}");
                _list.Groups.Add(group);

                // 最近届次排在最前；同届内按时间倒序，无时间的节点靠后
                var ordered = dataset.Nodes
                    .Select((node, index) => (node, index))
                    .OrderByDescending(x => x.node.Year)
                    .ThenByDescending(x => x.node.UtcTime ?? DateTimeOffset.MinValue)
                    .ToList();
                foreach ((DeadlineNode node, int index) in ordered)
                {
                    DeadlineNode effectiveNode = effectiveDataset.Nodes[index];
                    string? key = OverrideNodeKey.TryBuild(node);
                    DeadlineOverride? ov = key is null ? null : _overrides.FindByNodeKey(key);
                    OverrideNotice? notice = key is null
                        ? null
                        : notices.FirstOrDefault(n => n.Override.NodeKey == key);

                    var item = new ListViewItem($"{dataset.Identity.ShortName} {node.Year}")
                    {
                        Group = group,
                        Tag = new RowRef(node, dataset.Identity.ShortName, key),
                    };
                    item.SubItems.Add(CountdownFormatter.NodeTypeLabel(node.NodeType));
                    item.SubItems.Add(TrackText(node));
                    item.SubItems.Add(node.RawTime ?? "—");
                    item.SubItems.Add(node.RawTimezone ?? "—");
                    item.SubItems.Add(node.UtcTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
                    item.SubItems.Add(effectiveNode.UtcTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—");
                    item.SubItems.Add(OverrideStatusText(ov, notice));
                    item.SubItems.Add(node.Verification == VerificationStatus.Verified ? "已核验" : "未核验");
                    item.SubItems.Add(node.SourceUrl ?? "—");
                    _list.Items.Add(item);
                }
            }

            // 上游已找不到对应节点的修正：独立分组列在末尾，可查看/恢复，绝不悄悄丢弃
            var orphanNotices = notices.Where(n => n.Kind == OverrideNoticeKind.UpstreamNodeMissing).ToList();
            if (orphanNotices.Count > 0)
            {
                var group = new ListViewGroup("orphan-overrides", "已修正 · 上游已找不到对应节点");
                _list.Groups.Add(group);
                foreach (OverrideNotice notice in orphanNotices)
                {
                    DeadlineOverride ov = notice.Override;
                    var item = new ListViewItem(ov.ConferenceId)
                    {
                        Group = group,
                        Tag = new RowRef(null, ov.ConferenceId, ov.NodeKey),
                    };
                    item.SubItems.Add(CountdownFormatter.NodeTypeLabel(ov.NodeType));
                    item.SubItems.Add("主会常规");
                    item.SubItems.Add("—");
                    item.SubItems.Add("—");
                    item.SubItems.Add("—");
                    item.SubItems.Add("—"); // 无匹配节点期间不作为有效截止时间
                    item.SubItems.Add("已修正·上游缺失");
                    item.SubItems.Add("—");
                    item.SubItems.Add(ov.Source);
                    _list.Items.Add(item);
                }
            }
        }

        _list.EndUpdate();

        if (reselectKey is not null && _list.IsHandleCreated)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is RowRef row && row.OverrideKey == reselectKey)
                {
                    item.Selected = true; // 触发 SelectedIndexChanged → UpdateSelectionUi
                    break;
                }
            }
        }
        UpdateSelectionUi();
    }

    private string CorruptionSuffix() =>
        _overrides.IsCorrupted ? " · ⚠ 修正文件损坏，已按上游值显示" : "";

    private static string OverrideStatusText(DeadlineOverride? ov, OverrideNotice? notice) =>
        ov is null ? "" : notice?.Kind switch
        {
            OverrideNoticeKind.UpstreamChanged => "已修正·上游已变化",
            OverrideNoticeKind.UpstreamNodeMissing => "已修正·上游缺失",
            OverrideNoticeKind.MatchAmbiguous => "已修正·待核对(未应用)",
            _ => "已修正",
        };

    private static string TrackText(DeadlineNode node)
    {
        string text = CountdownFormatter.TrackLabel(node.Track);
        if (!string.IsNullOrWhiteSpace(node.RoundLabel))
        {
            text += $"（{node.RoundLabel}）";
        }
        else if (node.Track != TrackKind.MainRegular && !string.IsNullOrWhiteSpace(node.Note))
        {
            text += $"（{node.Note}）";
        }
        return text;
    }

    private RowRef? SelectedRow() =>
        _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as RowRef : null;

    private void UpdateSelectionUi()
    {
        RowRef? row = SelectedRow();
        _openSourceButton.Enabled = row?.Node?.SourceUrl is string url && IsSafeUrl(url);

        string? key = row?.OverrideKey;
        DeadlineOverride? ov = key is null ? null : _overrides.FindByNodeKey(key);

        // “修正此节点”仅在节点可修正、候选唯一且修正文件未损坏时可用：
        // 候选歧义时绝不能把修正套到全部候选，损坏时写入被封锁
        bool uniqueCandidate = row?.Node is not null && key is not null && _snap is not null &&
            OverrideNodeKey.FindCandidates(_snap, key).Count == 1;
        _editOverrideButton.Enabled = uniqueCandidate && !_overrides.IsCorrupted;
        _removeOverrideButton.Enabled = ov is not null && !_overrides.IsCorrupted;
        _reinitializeShouldShow = _overrides.IsCorrupted;
        _reinitializeButton.Visible = _reinitializeShouldShow;

        if (ov is not null)
        {
            OverrideNotice? notice = key is null
                ? null
                : OverrideChangeInspector.Inspect(_overrides.Overrides, _snap)
                      .FirstOrDefault(n => n.Override.NodeKey == key);
            _detailPanel.Text = BuildOverrideDetail(row?.Node, ov, notice);
            _detailPanel.BackColor = Color.Ivory;
            _detailPanelShouldShow = true;
        }
        else if (_overrides.IsCorrupted)
        {
            _detailPanel.Text =
                $"⚠ 修正文件损坏（{_overrides.StorePath}）：\r\n{_overrides.CorruptionError}\r\n" +
                "已按上游值显示；你的修正文件未被覆盖、也暂时无法增删修正。" +
                "点击下方“备份损坏文件并重新初始化”可将损坏文件改名留证后重新开始。";
            _detailPanel.BackColor = Color.MistyRose;
            _detailPanelShouldShow = true;
        }
        else
        {
            _detailPanelShouldShow = false;
        }
        _detailPanel.Visible = _detailPanelShouldShow;
    }

    /// <summary>三方对照：修正时上游值／最新上游值／用户修正值，附依据、来源与修正时间。</summary>
    private static string BuildOverrideDetail(DeadlineNode? latest, DeadlineOverride ov, OverrideNotice? notice)
    {
        string atCorrection = ov.UpstreamRawTimeAtCorrection is null
            ? "（当时无上游时间）"
            : $"{ov.UpstreamRawTimeAtCorrection}（{ov.UpstreamRawTimezoneAtCorrection ?? "无时区"}）" +
              (ov.UpstreamUtcAtCorrection is { } u
                  ? $" → 本地 {u.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                  : "（当时无法换算）");
        string latestText = latest is null
            ? "上游数据中已找不到该节点（修正保留，暂不作为有效截止时间）"
            : $"{latest.RawTime ?? "—"}（{latest.RawTimezone ?? "—"}）" +
              (latest.UtcTime is { } lu ? $" → 本地 {lu.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "（无法换算）");
        string header = notice is null ? "" : $"⚠ {notice.Summary}\r\n\r\n";
        return
            header +
            $"已修正节点：{ov.ConferenceId} · {CountdownFormatter.NodeTypeLabel(ov.NodeType)}\r\n" +
            $"修正时上游值：{atCorrection}\r\n" +
            $"最新上游值　：{latestText}\r\n" +
            $"你的修正值　：{ov.InputLocalTime}（{ov.InputTimezone}）→ 本地 {ov.CorrectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\r\n" +
            $"依据：{ov.Reason}\r\n来源：{ov.Source}\r\n" +
            $"修正时间：{ov.CorrectedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本地）";
    }

    private void EditSelectedOverride()
    {
        RowRef? row = SelectedRow();
        if (row?.Node is null || _snap is null)
        {
            return;
        }
        string? key = OverrideNodeKey.TryBuild(row.Node);
        if (key is null || OverrideNodeKey.FindCandidates(_snap, key).Count != 1)
        {
            return; // 按钮已按此禁用；双保险
        }
        DeadlineOverride? existing = _overrides.FindByNodeKey(key);
        string label = $"{row.ShortName} {row.Node.Year}" +
            (row.Node.EditionId is { } e ? $"（届次 {e}）" : "");
        // 传入快照提供器：保存那一刻按最新快照重新核对唯一性与“修正时上游值”，
        // 不使用打开窗口时（可能已被后台刷新替换）的旧快照留证
        using var form = new OverrideEditForm(
            row.Node, label, _snapshotProvider, _overrides, existing, TimeZoneInfo.Local);
        form.ShowDialog(this);
        // 保存成功后 Changed 事件已触发 RefreshContent；取消则界面无变化
    }

    private void RemoveSelectedOverride()
    {
        RowRef? row = SelectedRow();
        if (row?.OverrideKey is not string key)
        {
            return;
        }
        DialogResult confirm = MessageBox.Show(this,
            "恢复上游值将删除这条修正，菜单倒计时、详情与提醒立即按上游时间重算。\n\n确定恢复？",
            "恢复上游值", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        if (!_overrides.TryRemove(key, out string? error))
        {
            MessageBox.Show(this, error, "无法恢复上游值", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReinitializeOverrides()
    {
        DialogResult confirm = MessageBox.Show(this,
            "这会把损坏的修正文件改名备份（不删除），并建立一个空的修正表。\n" +
            "备份文件中保存的修正需要人工查看后重新录入。\n\n确定继续？",
            "备份并重新初始化", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }
        if (!_overrides.TryReinitializeAfterCorruption(out string? backupPath, out string? error))
        {
            MessageBox.Show(this, error, "重新初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(this,
            backupPath is null
                ? "已重新初始化。"
                : $"已重新初始化。损坏文件已备份为：\n{backupPath}",
            "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>来源链接仅允许 http/https。</summary>
    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private void OpenSelectedSource()
    {
        if (SelectedRow()?.Node?.SourceUrl is string url && IsSafeUrl(url))
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    /// <summary>“退出”菜单使用：允许窗口真正关闭。</summary>
    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            // 用户点 X：只隐藏窗口，应用继续驻留托盘
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
