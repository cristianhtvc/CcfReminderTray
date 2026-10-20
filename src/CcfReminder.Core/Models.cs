namespace CcfReminder.Core;

/// <summary>会议身份。等级标签属于展示层关注点，阶段 2 不建模。</summary>
public sealed record ConferenceInfo(string Id, string ShortName, string FullName);

/// <summary>截止节点类型。摘要注册与全文提交是主菜单唯一关注的两类“投稿节点”。</summary>
public enum DeadlineNodeType
{
    AbstractRegistration,
    PaperSubmission,
    Rebuttal,
    DecisionNotification,
    Other,
}

/// <summary>轮次归属：主会常规论文 / 特殊 track / 身份不明的多轮。</summary>
public enum TrackKind
{
    MainRegular,
    Special,
    Unknown,
}

/// <summary>核验状态：默认未核验，只有人工对照过当届官网才是已核验。</summary>
public enum VerificationStatus
{
    Unverified,
    Verified,
}

/// <summary>UTC 换算失败的原因；为 null 表示换算成功（UtcTime 有值）。</summary>
public enum ConversionIssue
{
    /// <summary>源站未提供时间（null、空串或 TBD）——语义上是“未公布”，不是错误。</summary>
    NotProvided,
    /// <summary>原始时间字符串无法解析。</summary>
    InvalidFormat,
    /// <summary>未提供时区。</summary>
    MissingTimezone,
    /// <summary>无法识别的时区写法。</summary>
    UnknownTimezone,
    /// <summary>夏令时回拨造成的歧义本地时间，不得猜测。</summary>
    AmbiguousLocalTime,
    /// <summary>夏令时快进造成的不存在的本地时间，不得猜测。</summary>
    NonExistentLocalTime,
    /// <summary>固定偏移超出有效范围（绝对值超过 14 小时或分钟超过 59）。</summary>
    InvalidOffset,
}

/// <summary>
/// 一条截止节点记录。UtcTime 允许为空：为空时 Issue 必须说明原因，绝不默默猜测。
/// </summary>
public sealed record DeadlineNode
{
    public required string ConferenceId { get; init; }
    public required int Year { get; init; }
    public required DeadlineNodeType NodeType { get; init; }
    public required TrackKind Track { get; init; }
    /// <summary>原始时间字符串（可能为 null 或 "TBD"），原样保留。</summary>
    public string? RawTime { get; init; }
    /// <summary>原始时区写法（"AoE"、"UTC-12"、"PT" 等），原样保留。</summary>
    public string? RawTimezone { get; init; }
    /// <summary>届次在数据源中的 ID（如 icml26），原样保留。</summary>
    public string? EditionId { get; init; }
    /// <summary>轮次标注（如 "Main track"）；无法识别的 *_deadline 键名也记录在此。</summary>
    public string? RoundLabel { get; init; }
    /// <summary>数据源对该轮次的备注（非轮次区分的说明性文字）。</summary>
    public string? Note { get; init; }
    /// <summary>换算成功时的 UTC 时刻，Offset 必须为零。</summary>
    public DateTimeOffset? UtcTime { get; init; }
    /// <summary>换算失败原因；与 UtcTime 必居其一，不得同时有值或同时为空。</summary>
    public ConversionIssue? Issue { get; init; }

    /// <summary>
    /// 记录一致性：UtcTime 与 Issue 恰有一个有值。
    /// 不一致的记录不得被当作可靠 DDL（选择器会按“待核验”处理）。
    /// </summary>
    public bool IsConsistent => (UtcTime is not null) != (Issue is not null);
    public string? SourceUrl { get; init; }
    public string? DataSource { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public VerificationStatus Verification { get; init; } = VerificationStatus.Unverified;
}
