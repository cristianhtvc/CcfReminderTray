using CcfReminder.Core;
using Xunit;

namespace CcfReminder.Core.Tests;

public class DeadlineTimeConverterTests
{
    [Fact]
    public void AoE_ConvertsToUtcMinus12()
    {
        var r = DeadlineTimeConverter.Convert("2026-09-25 23:59:59", "AoE");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 11, 59, 59, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void UtcZero_KeepsWallClock()
    {
        var r = DeadlineTimeConverter.Convert("2026-01-29 11:59:59", "UTC+0");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 1, 29, 11, 59, 59, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void PositiveOffset_SubtractsToUtc()
    {
        var r = DeadlineTimeConverter.Convert("2026-06-01 12:00:00", "UTC+8");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 4, 0, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void NegativeOffset_AddsToUtc()
    {
        var r = DeadlineTimeConverter.Convert("2026-08-03 23:59:59", "UTC-12");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 11, 59, 59, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void OffsetWithMinutes_Parsed()
    {
        var r = DeadlineTimeConverter.Convert("2026-06-01 12:00:00", "UTC+5:30");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 6, 30, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void Pacific_SummerTime_UsesUtcMinus7()
    {
        // 2026-05-07 处于 PDT（UTC-7）
        var r = DeadlineTimeConverter.Convert("2026-05-07 11:59:00", "PT");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 5, 7, 18, 59, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void Pacific_WinterTime_UsesUtcMinus8()
    {
        // 2026-01-24 处于 PST（UTC-8）
        var r = DeadlineTimeConverter.Convert("2026-01-24 11:59:59", "PT");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 1, 24, 19, 59, 59, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void Pacific_AmbiguousLocalTime_IsRejected()
    {
        // 2026-11-01 夏令时回拨，01:30 出现两次
        var r = DeadlineTimeConverter.Convert("2026-11-01 01:30:00", "PT");
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.AmbiguousLocalTime, r.Issue);
    }

    [Fact]
    public void Pacific_NonExistentLocalTime_IsRejected()
    {
        // 2026-03-08 夏令时快进，02:30 不存在
        var r = DeadlineTimeConverter.Convert("2026-03-08 02:30:00", "PT");
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.NonExistentLocalTime, r.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTimezone_IsRejected(string? tz)
    {
        var r = DeadlineTimeConverter.Convert("2026-09-25 23:59:59", tz);
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.MissingTimezone, r.Issue);
    }

    [Fact]
    public void UnknownTimezone_IsRejected()
    {
        var r = DeadlineTimeConverter.Convert("2026-09-25 23:59:59", "Mars/Olympus");
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.UnknownTimezone, r.Issue);
    }

    [Fact]
    public void InvalidTimeString_IsRejected()
    {
        var r = DeadlineTimeConverter.Convert("明年春天", "AoE");
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.InvalidFormat, r.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("TBD")]
    [InlineData("tbd")]
    public void TbdOrMissing_IsNotProvided_NotAnError(string? raw)
    {
        var r = DeadlineTimeConverter.Convert(raw, "AoE");
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.NotProvided, r.Issue);
    }

    [Theory]
    [InlineData("2026-09-25 23:59:59", "AoE", 2026, 9, 26, 11, 59, 59)]
    [InlineData("2026-06-01 12:00:00", "UTC+8", 2026, 6, 1, 4, 0, 0)]
    [InlineData("2026-05-07 11:59:00", "PT", 2026, 5, 7, 18, 59, 0)]
    public void Result_AlwaysZeroOffset_UtcInstantUnchanged(
        string raw, string tz, int y, int mo, int d, int h, int mi, int s)
    {
        var r = DeadlineTimeConverter.Convert(raw, tz);
        Assert.Null(r.Issue);
        Assert.Equal(TimeSpan.Zero, r.Utc!.Value.Offset);
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void Offset_ExactlyPlus14_IsValid()
    {
        var r = DeadlineTimeConverter.Convert("2026-06-01 12:00:00", "UTC+14:00");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 22, 0, 0, TimeSpan.Zero), r.Utc);
    }

    [Theory]
    [InlineData("UTC+14:01")]
    [InlineData("UTC+14:30")]
    [InlineData("UTC-14:30")]
    [InlineData("UTC+15")]
    [InlineData("UTC-15")]
    [InlineData("UTC+5:75")]
    public void InvalidOffset_ReturnsIssue_NeverThrows(string tz)
    {
        var r = DeadlineTimeConverter.Convert("2026-06-01 12:00:00", tz);
        Assert.Null(r.Utc);
        Assert.Equal(ConversionIssue.InvalidOffset, r.Issue);
    }

    [Fact]
    public void Pst_IsFixedUtcMinus8_EvenInSummer()
    {
        // 明确写 PST 的夏季输入仍按 UTC-8（PT 才会随季节切换）
        var r = DeadlineTimeConverter.Convert("2026-07-01 12:00:00", "PST");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero), r.Utc);
    }

    [Fact]
    public void Pdt_IsFixedUtcMinus7_EvenInWinter()
    {
        // 明确写 PDT 的冬季输入仍按 UTC-7
        var r = DeadlineTimeConverter.Convert("2026-01-15 12:00:00", "PDT");
        Assert.Null(r.Issue);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 19, 0, 0, TimeSpan.Zero), r.Utc);
    }
}
