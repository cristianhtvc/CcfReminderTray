using System.Globalization;

namespace CcfReminder.Core;

/// <summary>
/// 用户手动修正 DDL 的时间输入换算（PROJECT_BRIEF §4）。
/// 与 <see cref="DeadlineTimeConverter"/> 的差别只在一处：用户可以选择“本地时区”，
/// 此时用调用方注入的 <see cref="TimeZoneInfo"/> 换算，并对夏令时
/// “不存在的本地时间／歧义时间”返回明确失败原因，绝不猜测其一。
/// 其余时区写法（AoE、UTC±N、PST/PDT/PT）与上游数据换算完全一致。
/// </summary>
public static class UserInputTimeConverter
{
    public const string LocalTimezoneLabel = "本地时区";

    private static readonly string[] TimeFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };

    public static ConversionResult Convert(string? inputTime, string? inputTimezone, TimeZoneInfo localZone)
    {
        if (string.IsNullOrWhiteSpace(inputTimezone))
        {
            return ConversionResult.Failure(ConversionIssue.MissingTimezone);
        }

        string tz = inputTimezone.Trim();
        if (!IsLocalTimezone(tz))
        {
            return DeadlineTimeConverter.Convert(inputTime, inputTimezone);
        }

        if (string.IsNullOrWhiteSpace(inputTime) ||
            inputTime.Trim().Equals("TBD", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.Failure(ConversionIssue.NotProvided);
        }
        if (!DateTime.TryParseExact(
                inputTime.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime local))
        {
            return ConversionResult.Failure(ConversionIssue.InvalidFormat);
        }
        if (localZone.IsInvalidTime(local))
        {
            return ConversionResult.Failure(ConversionIssue.NonExistentLocalTime);
        }
        if (localZone.IsAmbiguousTime(local))
        {
            return ConversionResult.Failure(ConversionIssue.AmbiguousLocalTime);
        }
        return ConversionResult.Ok(
            new DateTimeOffset(local, localZone.GetUtcOffset(local)).ToUniversalTime());
    }

    public static bool IsLocalTimezone(string timezone) =>
        timezone.Equals(LocalTimezoneLabel, StringComparison.OrdinalIgnoreCase) ||
        timezone.Equals("本地", StringComparison.OrdinalIgnoreCase) ||
        timezone.Equals("local", StringComparison.OrdinalIgnoreCase);

    /// <summary>面向用户的失败原因说明（不猜测、给可操作建议）。</summary>
    public static string DescribeIssue(ConversionIssue issue) => issue switch
    {
        ConversionIssue.NotProvided => "请输入时间，格式：yyyy-MM-dd HH:mm（如 2026-01-22 19:59）。",
        ConversionIssue.InvalidFormat => "时间格式无法识别，请按 yyyy-MM-dd HH:mm 输入（如 2026-01-22 19:59）。",
        ConversionIssue.MissingTimezone => "请选择或输入时区（如 本地时区、AoE、UTC+8、PT）。",
        ConversionIssue.InvalidOffset => "时区偏移超出可接受范围（±14 小时内），请检查写法。",
        ConversionIssue.UnknownTimezone => "无法识别该时区写法；可用：本地时区、AoE、UTC±N、PST、PDT、PT。",
        ConversionIssue.NonExistentLocalTime =>
            "该本地时间因夏令时切换并不存在（时钟被拨快跳过了这一刻）。请核对官网时间；如确为此时刻，请改用 UTC±N 形式明确指定偏移。",
        ConversionIssue.AmbiguousLocalTime =>
            "该本地时间在夏令时回调时会出现两次，无法自动确定是哪一次。请改用 UTC±N 形式明确指定偏移（如 UTC-7 或 UTC-8）。",
        _ => "时间换算失败，请检查输入。",
    };
}
