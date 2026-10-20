using System.Globalization;
using System.Text.RegularExpressions;

namespace CcfReminder.Core;

/// <summary>
/// 把（原始时间字符串, 原始时区）换算为 UTC 时刻（返回值的 Offset 恒为零）。
/// 支持：AoE（固定 UTC-12）、明确的固定 UTC 偏移（UTC±N / UTC±N:MM）、
/// PT（随日期切换夏令时）、PST（固定 UTC-8）与 PDT（固定 UTC-7）。
/// 缺失时区、无效时间、超界偏移、夏令时歧义/不存在的本地时间一律返回失败原因，不得猜测，
/// 也不得让普通输入使本方法抛异常。
/// </summary>
public static partial class DeadlineTimeConverter
{
    private static readonly string[] TimeFormats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm" };

    private static readonly string[] PacificIds = { "America/Los_Angeles", "Pacific Standard Time" };

    public static ConversionResult Convert(string? rawTime, string? rawTimezone)
    {
        if (string.IsNullOrWhiteSpace(rawTime) ||
            rawTime.Trim().Equals("TBD", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.Failure(ConversionIssue.NotProvided);
        }

        if (!DateTime.TryParseExact(
                rawTime.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime local))
        {
            return ConversionResult.Failure(ConversionIssue.InvalidFormat);
        }

        if (string.IsNullOrWhiteSpace(rawTimezone))
        {
            return ConversionResult.Failure(ConversionIssue.MissingTimezone);
        }

        string tz = rawTimezone.Trim();

        // AoE（Anywhere on Earth）按惯例固定为 UTC-12
        if (tz.Equals("AoE", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.Ok(
                new DateTimeOffset(local, TimeSpan.FromHours(-12)).ToUniversalTime());
        }

        // 固定偏移：UTC+8、UTC-12、UTC+5:30、UTC-12:00 等
        Match m = UtcOffsetPattern().Match(tz);
        if (m.Success)
        {
            int hours = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int minutes = m.Groups[3].Success
                ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
                : 0;
            if (minutes > 59)
            {
                return ConversionResult.Failure(ConversionIssue.InvalidOffset);
            }

            var offset = new TimeSpan(hours, minutes, 0);
            if (m.Groups[1].Value == "-")
            {
                offset = -offset;
            }
            // DateTimeOffset 允许的偏移为 ±14 小时（含）；超界不得抛异常，明确拒绝
            if (offset.Duration() > TimeSpan.FromHours(14))
            {
                return ConversionResult.Failure(ConversionIssue.InvalidOffset);
            }
            return ConversionResult.Ok(new DateTimeOffset(local, offset).ToUniversalTime());
        }

        // 明确写 PST / PDT 的输入：按各自固定含义（UTC-8 / UTC-7），不随日期切换
        if (tz.Equals("PST", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.Ok(
                new DateTimeOffset(local, TimeSpan.FromHours(-8)).ToUniversalTime());
        }
        if (tz.Equals("PDT", StringComparison.OrdinalIgnoreCase))
        {
            return ConversionResult.Ok(
                new DateTimeOffset(local, TimeSpan.FromHours(-7)).ToUniversalTime());
        }

        // PT：随日期切换夏令时的太平洋时间标签
        if (tz.Equals("PT", StringComparison.OrdinalIgnoreCase) ||
            tz.Equals("America/Los_Angeles", StringComparison.OrdinalIgnoreCase) ||
            tz.Equals("Pacific Standard Time", StringComparison.OrdinalIgnoreCase))
        {
            TimeZoneInfo? pacific = ResolvePacific();
            if (pacific is null)
            {
                return ConversionResult.Failure(ConversionIssue.UnknownTimezone);
            }
            if (pacific.IsInvalidTime(local))
            {
                return ConversionResult.Failure(ConversionIssue.NonExistentLocalTime);
            }
            if (pacific.IsAmbiguousTime(local))
            {
                return ConversionResult.Failure(ConversionIssue.AmbiguousLocalTime);
            }
            return ConversionResult.Ok(
                new DateTimeOffset(local, pacific.GetUtcOffset(local)).ToUniversalTime());
        }

        return ConversionResult.Failure(ConversionIssue.UnknownTimezone);
    }

    /// <summary>优先 IANA ID（跨平台），回退 Windows ID。</summary>
    private static TimeZoneInfo? ResolvePacific()
    {
        foreach (string id in PacificIds)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    [GeneratedRegex(@"^UTC\s*([+-])\s*(\d{1,2})(?::?(\d{1,2}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex UtcOffsetPattern();
}

/// <summary>换算结果：Utc 与 Issue 必居其一。</summary>
public sealed record ConversionResult(DateTimeOffset? Utc, ConversionIssue? Issue)
{
    public static ConversionResult Ok(DateTimeOffset utc) => new(utc, null);
    public static ConversionResult Failure(ConversionIssue issue) => new(null, issue);
}
