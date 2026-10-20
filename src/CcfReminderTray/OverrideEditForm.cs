using CcfReminder.Core;
using CcfReminder.Core.Data;
using CcfReminder.Core.Overrides;

namespace CcfReminderTray;

/// <summary>
/// 手动修正 DDL 的编辑窗（PROJECT_BRIEF §4）。
/// 展示节点身份、上游原始值与（若有）当前修正；用户输入“时间＋显式声明的时区＋依据＋来源”，
/// 保存前必须换算成功并经用户确认换算结果。夏令时不存在/歧义等失败只给可理解提示，
/// 绝不猜测、绝不保存。唯一性、必填项与损坏封锁由 <see cref="DeadlineOverrideManager"/> 最终裁决。
/// </summary>
internal sealed class OverrideEditForm : Form
{
    private static readonly string[] TimezoneChoices =
        { UserInputTimeConverter.LocalTimezoneLabel, "AoE", "PT", "PST", "PDT", "UTC+8", "UTC-5", "UTC-12" };

    private readonly DeadlineNode _node;
    private readonly Func<ConferenceDataSnapshot?> _snapshotProvider;
    private readonly DeadlineOverrideManager _manager;
    private readonly TimeZoneInfo _localZone;

    private readonly TextBox _timeInput;
    private readonly ComboBox _timezoneInput;
    private readonly TextBox _reasonInput;
    private readonly TextBox _sourceInput;
    private readonly Label _previewLabel;

    /// <param name="snapshotProvider">
    /// 最新快照提供器：窗口打开期间后台刷新可能已替换数据；
    /// 保存那一刻重新取快照核对唯一性与“修正时上游值”，不以过期快照留证。
    /// </param>
    public OverrideEditForm(
        DeadlineNode node,
        string conferenceLabel,
        Func<ConferenceDataSnapshot?> snapshotProvider,
        DeadlineOverrideManager manager,
        DeadlineOverride? existing,
        TimeZoneInfo localZone)
    {
        _node = node;
        _snapshotProvider = snapshotProvider;
        _manager = manager;
        _localZone = localZone;

        Text = "手动修正截止时间";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 430);

        int y = 12;
        var identity = new Label
        {
            Left = 12, Top = y, Width = 536, Height = 34,
            Text = $"{conferenceLabel} · {CountdownFormatter.NodeTypeLabel(node.NodeType)} · {CountdownFormatter.TrackLabel(node.Track)}",
            Font = new Font(Font, FontStyle.Bold),
        };
        Controls.Add(identity);
        y += 38;

        string upstreamUtc = node.UtcTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无法换算";
        var upstream = new Label
        {
            Left = 12, Top = y, Width = 536, Height = 30,
            Text = $"上游原始：{node.RawTime ?? "—"} · 时区 {node.RawTimezone ?? "—"} · 换算（本地）：{upstreamUtc}",
            ForeColor = Color.DarkSlateBlue,
        };
        Controls.Add(upstream);
        y += 34;

        if (existing is not null)
        {
            var current = new Label
            {
                Left = 12, Top = y, Width = 536, Height = 30,
                ForeColor = Color.DarkGreen,
                Text = $"当前修正：{existing.InputLocalTime}（{existing.InputTimezone}）→ 本地 " +
                    $"{existing.CorrectedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 修正于 {existing.CorrectedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}",
            };
            Controls.Add(current);
            y += 34;
        }

        Controls.Add(new Label { Left = 12, Top = y + 3, Width = 200, Text = "修正后的时间（yyyy-MM-dd HH:mm）" });
        _timeInput = new TextBox { Left = 216, Top = y, Width = 150, Text = existing?.InputLocalTime ?? "" };
        Controls.Add(_timeInput);
        Controls.Add(new Label { Left = 374, Top = y + 3, Width = 40, Text = "时区" });
        _timezoneInput = new ComboBox
        {
            Left = 416, Top = y, Width = 132,
            DropDownStyle = ComboBoxStyle.DropDown, // 可输入列表之外的写法（如 UTC+5:30）
        };
        _timezoneInput.Items.AddRange(TimezoneChoices);
        _timezoneInput.Text = existing?.InputTimezone ?? UserInputTimeConverter.LocalTimezoneLabel;
        Controls.Add(_timezoneInput);
        y += 32;

        var previewButton = new Button { Left = 216, Top = y, Width = 90, Text = "换算预览" };
        previewButton.Click += (_, _) => ShowPreview();
        Controls.Add(previewButton);
        _previewLabel = new Label
        {
            Left = 12, Top = y + 30, Width = 536, Height = 44,
            ForeColor = Color.DarkSlateBlue,
            Text = "（保存前请先预览，确认换算结果符合预期）",
        };
        Controls.Add(_previewLabel);
        y += 80;

        Controls.Add(new Label { Left = 12, Top = y + 3, Width = 200, Text = "修正依据（必填）" });
        _reasonInput = new TextBox { Left = 216, Top = y, Width = 332, Text = existing?.Reason ?? "" };
        Controls.Add(_reasonInput);
        y += 30;

        Controls.Add(new Label { Left = 12, Top = y + 3, Width = 200, Text = "修正来源（必填，如当届官网链接）" });
        _sourceInput = new TextBox { Left = 216, Top = y, Width = 332, Text = existing?.Source ?? "" };
        Controls.Add(_sourceInput);
        y += 38;

        var saveButton = new Button { Left = 356, Top = y, Width = 92, Text = "保存修正", DialogResult = DialogResult.None };
        saveButton.Click += (_, _) => TrySaveAndClose();
        var cancelButton = new Button { Left = 456, Top = y, Width = 92, Text = "取消", DialogResult = DialogResult.Cancel };
        Controls.Add(saveButton);
        Controls.Add(cancelButton);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    // ---- 测试钩子（不驱动 UI 之外的副作用）----
    internal TextBox TimeInput => _timeInput;
    internal ComboBox TimezoneInput => _timezoneInput;
    internal TextBox ReasonInput => _reasonInput;
    internal TextBox SourceInput => _sourceInput;
    internal string PreviewText => _previewLabel.Text;
    internal void Preview() => ShowPreview();
    internal ConversionResult Convert() =>
        UserInputTimeConverter.Convert(_timeInput.Text, _timezoneInput.Text, _localZone);

    private void ShowPreview()
    {
        ConversionResult result = Convert();
        _previewLabel.ForeColor = result.Utc is null ? Color.DarkRed : Color.DarkGreen;
        _previewLabel.Text = result.Utc is { } utc
            ? $"UTC：{utc:yyyy-MM-dd HH:mm:ss} · 本地：{utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : UserInputTimeConverter.DescribeIssue(result.Issue!.Value);
    }

    /// <summary>
    /// 换算并保存（不含交互确认）：按保存那一刻的最新快照核对唯一性与上游值。
    /// 供“保存”按钮与自动化测试共用；失败只返回原因，不弹窗。
    /// </summary>
    internal bool TrySaveWithLatestSnapshot(out string? error)
    {
        error = null;
        ConversionResult result = Convert();
        if (result.Utc is not { } utc)
        {
            error = UserInputTimeConverter.DescribeIssue(result.Issue!.Value);
            return false;
        }
        ConferenceDataSnapshot? latest = _snapshotProvider();
        if (latest is null)
        {
            error = "当前没有可用的数据快照（可能尚未获取成功），无法核对修正对象，已拒绝保存。";
            return false;
        }
        return _manager.TrySave(latest, _node, utc,
            _timeInput.Text, _timezoneInput.Text, _reasonInput.Text, _sourceInput.Text,
            out error);
    }

    private void TrySaveAndClose()
    {
        ConversionResult result = Convert();
        if (result.Utc is not { } utc)
        {
            MessageBox.Show(this,
                UserInputTimeConverter.DescribeIssue(result.Issue!.Value) + "\n\n未保存任何修改。",
                "无法换算", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult confirm = MessageBox.Show(this,
            $"换算结果：\nUTC：{utc:yyyy-MM-dd HH:mm:ss}\n本地：{utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n\n" +
            "确认以此作为该节点的有效截止时刻？",
            "确认修正", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        if (!TrySaveWithLatestSnapshot(out string? error))
        {
            MessageBox.Show(this, error, "无法保存修正", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
