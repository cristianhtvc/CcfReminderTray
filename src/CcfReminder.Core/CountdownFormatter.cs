namespace CcfReminder.Core;

/// <summary>
/// 倒计时与菜单行文案。倒计时一律基于传入的 UTC 当前时刻计算，
/// 由调用方注入“现在”，便于测试且不要求修改系统时区。
/// </summary>
public static class CountdownFormatter
{
    public static string FormatRemaining(DateTimeOffset utcNow, DateTimeOffset deadlineUtc)
    {
        TimeSpan span = deadlineUtc - utcNow;
        if (span <= TimeSpan.Zero)
        {
            return "已到期";
        }
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}天 {span.Hours}小时";
        }
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}小时 {span.Minutes}分";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes}分";
        }
        return "不足1分钟";
    }

    /// <summary>节点类型的中文标注。</summary>
    public static string NodeTypeLabel(DeadlineNodeType type) => type switch
    {
        DeadlineNodeType.AbstractRegistration => "摘要注册",
        DeadlineNodeType.PaperSubmission => "全文提交",
        DeadlineNodeType.Rebuttal => "rebuttal",
        DeadlineNodeType.DecisionNotification => "录用通知",
        _ => "其他",
    };

    /// <summary>轮次归属的中文标注。</summary>
    public static string TrackLabel(TrackKind track) => track switch
    {
        TrackKind.MainRegular => "主会常规",
        TrackKind.Special => "特殊轮次",
        _ => "身份不明",
    };

    /// <summary>主菜单单行文案，如 “ICLR 2027 · 摘要注册 · 12天 6小时”。</summary>
    public static string FormatMenuLine(string shortName, SelectionResult result, DateTimeOffset utcNow) =>
        result.Kind switch
        {
            SelectionKind.Upcoming =>
                $"{shortName} {result.Node!.Year} · {result.Label} · {FormatRemaining(utcNow, result.Node.UtcTime!.Value)}",
            _ => $"{shortName} · {result.Label}",
        };
}
