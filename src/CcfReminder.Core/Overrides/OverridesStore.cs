using System.Text.Json;
using System.Text.Json.Serialization;
using CcfReminder.Core.Data;

namespace CcfReminder.Core.Overrides;

public enum OverridesLoadStatus
{
    /// <summary>文件存在且解析成功（含“有效的空表”——Items 为 [] 且版本正确）。</summary>
    Loaded,
    /// <summary>文件不存在，且无中断的重新初始化标记（正常初始状态，视为零条修正）。</summary>
    Missing,
    /// <summary>文件存在但无法解析/版本不符/内容不合法，或上次重新初始化中断——绝不当作空表继续。</summary>
    Corrupted,
}

public sealed record OverridesLoadResult(
    OverridesLoadStatus Status,
    IReadOnlyList<DeadlineOverride> Overrides,
    string? Error);

/// <summary>
/// overrides.json 的读写（PROJECT_BRIEF §4）：带版本号的独立修正文件；
/// 写入＝临时文件＋原子替换；读取严格——只有“版本正确且 Items 为 []”才算有效空表；
/// 缺 Items、Items:null、条目为 null、重复节点键、无效节点类型/时间、非零偏移、
/// 键与字段不一致、CorrectedUtc/CorrectedAtUtc 缺失或为 default 等一律返回 Corrupted，
/// 绝不静默载入空表或“最后一条获胜”。
/// 重新初始化采用“先复制备份、再原子替换原件”：提交失败时损坏原件仍在原路径，
/// 重启自然仍判 Corrupted——安全性不依赖故障时还能写入任何标记。
/// （兼容旧版中断遗留的 reinit-pending 标记：主文件缺失而标记存在时同样按 Corrupted 处理。）
/// </summary>
public sealed class OverridesStore
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public OverridesStore(string path) => _path = path;

    public string PathOnDisk => _path;

    /// <summary>旧版重新初始化中断遗留的待处理标记（仅读取/清除；新流程不再写入）。</summary>
    public string PendingMarkerPath => _path + ".reinit-pending";

    // ---- 测试故障注入钩子（生产恒为 null；注入即抛异常）----
    internal Action? BeforeBackupCopyForTest { get; set; }
    internal Action? BeforeSaveCommitForTest { get; set; }

    public OverridesLoadResult Load()
    {
        string json;
        try
        {
            if (!File.Exists(_path))
            {
                // 原路径缺失 + 待处理标记＝上次重新初始化中断：仍是损坏待处理，不是新用户
                if (TryReadPendingMarker(out string? markerText))
                {
                    return new OverridesLoadResult(OverridesLoadStatus.Corrupted,
                        Array.Empty<DeadlineOverride>(),
                        "上次“备份损坏文件并重新初始化”中途失败：空表未能写入。\n" + markerText);
                }
                return new OverridesLoadResult(OverridesLoadStatus.Missing, Array.Empty<DeadlineOverride>(), null);
            }
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new OverridesLoadResult(OverridesLoadStatus.Corrupted, Array.Empty<DeadlineOverride>(),
                $"读取修正文件失败：{ex.Message}");
        }

        try
        {
            OverrideFileDto? dto = JsonSerializer.Deserialize<OverrideFileDto>(json, JsonOptions);
            if (dto is null)
            {
                return Corrupt("修正文件内容为空或不是有效 JSON。");
            }
            if (dto.Version != CurrentVersion)
            {
                return Corrupt($"修正文件版本不受支持（文件版本 {dto.Version}，本程序支持 {CurrentVersion}）。");
            }
            if (dto.Items is null)
            {
                // 只有显式 [] 才是有效空表；缺 Items 或 Items:null 一律视为损坏
                return Corrupt("修正文件缺少 Items 列表（有效的空表应写作 \"Items\": []）。");
            }
            var list = new List<DeadlineOverride>(dto.Items.Count);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (OverrideItemDto? item in dto.Items)
            {
                if (item is null)
                {
                    return Corrupt("修正列表中包含空（null）条目。");
                }
                DeadlineOverride model = item.ToModel();
                ValidateConsistency(model);
                if (!seenKeys.Add(model.NodeKey))
                {
                    return Corrupt($"修正列表中节点键重复：{model.NodeKey}。");
                }
                list.Add(model);
            }
            return new OverridesLoadResult(OverridesLoadStatus.Loaded, list, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return Corrupt($"解析修正文件失败：{ex.Message}");
        }

        OverridesLoadResult Corrupt(string message) =>
            new(OverridesLoadStatus.Corrupted, Array.Empty<DeadlineOverride>(), message);
    }

    /// <summary>字段一致性：节点类型必须开放修正、UTC 偏移必须为零、节点键须与字段吻合。</summary>
    private static void ValidateConsistency(DeadlineOverride o)
    {
        if (o.NodeType is not (DeadlineNodeType.AbstractRegistration or DeadlineNodeType.PaperSubmission))
        {
            throw new InvalidOperationException(
                $"修正条目（{o.NodeKey}）的节点类型 {o.NodeType} 不开放修正，记录不合法。");
        }
        if (o.CorrectedUtc.Offset != TimeSpan.Zero ||
            o.CorrectedAtUtc.Offset != TimeSpan.Zero ||
            (o.UpstreamUtcAtCorrection is { } u && u.Offset != TimeSpan.Zero))
        {
            throw new InvalidOperationException(
                $"修正条目（{o.NodeKey}）包含非零偏移时间；所有 UTC 字段偏移必须为零。");
        }
        string expectedKey = string.Join('|', o.ConferenceId, o.EditionId, o.NodeType, TrackKind.MainRegular);
        if (!string.Equals(o.NodeKey, expectedKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"修正条目的节点键（{o.NodeKey}）与其字段（{expectedKey}）不一致。");
        }
    }

    /// <summary>临时文件＋原子替换写入；IO 失败向调用方传播，磁盘上的旧文件不被替换。</summary>
    public void Save(IReadOnlyList<DeadlineOverride> overrides)
    {
        var dto = new OverrideFileDto
        {
            Version = CurrentVersion,
            Items = overrides.Select(o => (OverrideItemDto?)OverrideItemDto.FromModel(o)).ToList(),
        };
        string json = JsonSerializer.Serialize(dto, JsonOptions);
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, json);
            BeforeSaveCommitForTest?.Invoke(); // 测试：注入“临时文件就绪但最终替换失败”
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (IOException)
            {
                // 残留临时文件不影响正确性，下次写入使用新随机名
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 把损坏文件<strong>复制</strong>（不是移动）为唯一命名的 .corrupt-时间戳.bak 留证；
    /// 原件保留在原路径——即使随后的空表提交失败，重启后读取原件仍判 Corrupted。
    /// </summary>
    public string BackupCorruptFileCopy(DateTime utcNow)
    {
        BeforeBackupCopyForTest?.Invoke(); // 测试：注入“备份复制失败”
        string backup = $"{_path}.corrupt-{utcNow:yyyyMMddHHmmss}.bak";
        if (File.Exists(backup))
        {
            backup = $"{_path}.corrupt-{utcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
        }
        File.Copy(_path, backup);
        return backup;
    }

    /// <summary>重新初始化成功后清除（旧版遗留的）待处理标记。</summary>
    public void TryDeletePendingMarker()
    {
        try
        {
            if (File.Exists(PendingMarkerPath))
            {
                File.Delete(PendingMarkerPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool TryReadPendingMarker(out string markerText)
    {
        markerText = "";
        try
        {
            if (!File.Exists(PendingMarkerPath))
            {
                return false;
            }
            markerText = File.ReadAllText(PendingMarkerPath);
            return true;
        }
        catch (IOException)
        {
            return true; // 标记存在但读不出内容：仍按待处理损坏对待
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class OverrideFileDto
    {
        public int Version { get; set; }
        public List<OverrideItemDto?>? Items { get; set; }
    }

    private sealed class OverrideItemDto
    {
        public string? NodeKey { get; set; }
        public string? ConferenceId { get; set; }
        public string? EditionId { get; set; }
        public DeadlineNodeType NodeType { get; set; }
        /// <summary>可空以区分“字段缺失/null”与“合法值”；缺失绝不允许静默默认为 0001-01-01。</summary>
        public DateTimeOffset? CorrectedUtc { get; set; }
        public string? InputLocalTime { get; set; }
        public string? InputTimezone { get; set; }
        public DateTimeOffset? UpstreamUtcAtCorrection { get; set; }
        public string? UpstreamRawTimeAtCorrection { get; set; }
        public string? UpstreamRawTimezoneAtCorrection { get; set; }
        public string? Reason { get; set; }
        public string? Source { get; set; }
        public DateTimeOffset? CorrectedAtUtc { get; set; }

        public static OverrideItemDto FromModel(DeadlineOverride o) => new()
        {
            NodeKey = o.NodeKey,
            ConferenceId = o.ConferenceId,
            EditionId = o.EditionId,
            NodeType = o.NodeType,
            CorrectedUtc = o.CorrectedUtc,
            InputLocalTime = o.InputLocalTime,
            InputTimezone = o.InputTimezone,
            UpstreamUtcAtCorrection = o.UpstreamUtcAtCorrection,
            UpstreamRawTimeAtCorrection = o.UpstreamRawTimeAtCorrection,
            UpstreamRawTimezoneAtCorrection = o.UpstreamRawTimezoneAtCorrection,
            Reason = o.Reason,
            Source = o.Source,
            CorrectedAtUtc = o.CorrectedAtUtc,
        };

        public DeadlineOverride ToModel()
        {
            if (string.IsNullOrWhiteSpace(NodeKey) || string.IsNullOrWhiteSpace(ConferenceId) ||
                string.IsNullOrWhiteSpace(InputLocalTime) || string.IsNullOrWhiteSpace(InputTimezone) ||
                string.IsNullOrWhiteSpace(Reason) || string.IsNullOrWhiteSpace(Source))
            {
                throw new InvalidOperationException($"修正条目字段不完整（节点键：{NodeKey ?? "（缺失）"}）。");
            }
            if (!Enum.IsDefined(NodeType))
            {
                throw new InvalidOperationException(
                    $"修正条目（{NodeKey}）的节点类型数值 {(int)NodeType} 无效。");
            }
            // CorrectedUtc/CorrectedAtUtc 必须存在、非 default（不限制任何合法历史年份）
            if (CorrectedUtc is not { } correctedUtc || correctedUtc == default)
            {
                throw new InvalidOperationException(
                    $"修正条目（{NodeKey}）缺少有效的 CorrectedUtc（须为存在的非默认 UTC 时间）。");
            }
            if (CorrectedAtUtc is not { } correctedAtUtc || correctedAtUtc == default)
            {
                throw new InvalidOperationException(
                    $"修正条目（{NodeKey}）缺少有效的 CorrectedAtUtc（须为存在的非默认 UTC 时间）。");
            }
            return new DeadlineOverride
            {
                NodeKey = NodeKey,
                ConferenceId = ConferenceId,
                EditionId = EditionId ?? "", // 届次缺失语义上等同空串
                NodeType = NodeType,
                CorrectedUtc = correctedUtc,
                InputLocalTime = InputLocalTime,
                InputTimezone = InputTimezone,
                UpstreamUtcAtCorrection = UpstreamUtcAtCorrection,
                UpstreamRawTimeAtCorrection = UpstreamRawTimeAtCorrection,
                UpstreamRawTimezoneAtCorrection = UpstreamRawTimezoneAtCorrection,
                Reason = Reason,
                Source = Source,
                CorrectedAtUtc = correctedAtUtc,
            };
        }
    }
}
