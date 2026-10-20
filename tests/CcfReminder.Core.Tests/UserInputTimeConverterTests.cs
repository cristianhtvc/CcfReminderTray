using Xunit;

namespace CcfReminder.Core.Tests;

public class UserInputTimeConverterTests
{
    // 确定性“类太平洋”时区：UTC-8，2026 年 3 月第 2 个周日 02:00 拨快、11 月第 1 个周日 02:00 拨回。
    // 自建时区，不依赖系统时区数据库，也不改系统时区。
    private static readonly TimeZoneInfo TestPacific = TimeZoneInfo.CreateCustomTimeZone(
        "TestPacific", TimeSpan.FromHours(-8), "TestPacific", "TestPacific Standard", "TestPacific Daylight",
        new[]
        {
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                    new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
        });

    [Fact]
    public void LocalZone_WinterTime_UsesStandardOffset()
    {
        ConversionResult r = UserInputTimeConverter.Convert(
            "2026-01-15 12:00", "本地时区", TestPacific);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero), r.Utc);
        Assert.Null(r.Issue);
    }

    [Fact]
    public void LocalZone_SummerTime_UsesDaylightOffset()
    {
        ConversionResult r = UserInputTimeConverter.Convert(
            "2026-07-15 12:00", "本地时区", TestPacific);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 19, 0, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void LocalZone_SpringForwardGap_Rejected_NotGuessed()
    {
        // 2026-03-08 02:30 在该时区不存在（02:00 直接跳到 03:00）
        ConversionResult r = UserInputTimeConverter.Convert(
            "2026-03-08 02:30", "本地时区", TestPacific);
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.NonExistentLocalTime, r.Issue);
    }

    [Fact]
    public void LocalZone_FallBackAmbiguous_Rejected_NotGuessed()
    {
        // 2026-11-01 01:30 出现两次（拨回前后各一次）
        ConversionResult r = UserInputTimeConverter.Convert(
            "2026-11-01 01:30", "本地时区", TestPacific);
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.AmbiguousLocalTime, r.Issue);
    }

    [Theory]
    [InlineData("本地")]
    [InlineData("local")]
    [InlineData("本地时区")]
    public void LocalZone_LabelVariants_AllTreatedAsLocal(string label)
    {
        ConversionResult r = UserInputTimeConverter.Convert("2026-01-15 12:00", label, TestPacific);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 20, 0, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void NonLocalTimezone_DelegatesToUpstreamConverter()
    {
        ConversionResult r = UserInputTimeConverter.Convert(
            "2026-01-22 19:59", "AoE", TestPacific);
        Assert.Equal(new DateTimeOffset(2026, 1, 23, 7, 59, 0, TimeSpan.Zero), r.Utc);
    }

    [Theory]
    [InlineData("", ConversionIssue.MissingTimezone)]
    [InlineData("  ", ConversionIssue.MissingTimezone)]
    public void MissingTimezone_Rejected(string tz, ConversionIssue expected)
    {
        ConversionResult r = UserInputTimeConverter.Convert("2026-01-15 12:00", tz, TestPacific);
        Assert.Equal(expected, r.Issue);
    }

    [Fact]
    public void InvalidTimeFormat_Rejected()
    {
        ConversionResult r = UserInputTimeConverter.Convert("下周五", "本地时区", TestPacific);
        Assert.Equal(ConversionIssue.InvalidFormat, r.Issue);
    }

    [Fact]
    public void UnknownTimezone_Rejected()
    {
        ConversionResult r = UserInputTimeConverter.Convert("2026-01-15 12:00", "Mars/Olympus", TestPacific);
        Assert.Equal(ConversionIssue.UnknownTimezone, r.Issue);
    }

    [Theory]
    [InlineData(ConversionIssue.NotProvided)]
    [InlineData(ConversionIssue.InvalidFormat)]
    [InlineData(ConversionIssue.MissingTimezone)]
    [InlineData(ConversionIssue.UnknownTimezone)]
    [InlineData(ConversionIssue.AmbiguousLocalTime)]
    [InlineData(ConversionIssue.NonExistentLocalTime)]
    [InlineData(ConversionIssue.InvalidOffset)]
    public void DescribeIssue_EveryIssue_HasActionableChineseText(ConversionIssue issue)
    {
        string text = UserInputTimeConverter.DescribeIssue(issue);
        Assert.False(string.IsNullOrWhiteSpace(text));
        // 夏令时两类失败必须给出“不猜测、改用明确偏移”的指引
        if (issue is ConversionIssue.AmbiguousLocalTime or ConversionIssue.NonExistentLocalTime)
        {
            Assert.Contains("UTC±N", text);
        }
    }
}
