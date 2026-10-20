using CcfReminder.Core;
using Xunit;

namespace CcfReminder.Core.Tests;

public class CountdownFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DaysAndHours()
    {
        var deadline = Now.AddDays(12).AddHours(6).AddMinutes(45); // 分钟应被舍去
        Assert.Equal("12天 6小时", CountdownFormatter.FormatRemaining(Now, deadline));
    }

    [Fact]
    public void HoursAndMinutes_WhenUnderOneDay()
    {
        var deadline = Now.AddHours(6).AddMinutes(30);
        Assert.Equal("6小时 30分", CountdownFormatter.FormatRemaining(Now, deadline));
    }

    [Fact]
    public void UnderOneMinute()
    {
        Assert.Equal("不足1分钟", CountdownFormatter.FormatRemaining(Now, Now.AddSeconds(30)));
    }

    [Theory]
    [InlineData(1, 0, "1分")]
    [InlineData(30, 0, "30分")]
    [InlineData(59, 30, "59分")] // 秒应被舍去，不得落入“不足1分钟”
    public void MinutesOnly_WhenUnderOneHour(int minutes, int seconds, string expected)
    {
        var deadline = Now.AddMinutes(minutes).AddSeconds(seconds);
        Assert.Equal(expected, CountdownFormatter.FormatRemaining(Now, deadline));
    }

    [Fact]
    public void PastDeadline_IsExpired()
    {
        Assert.Equal("已到期", CountdownFormatter.FormatRemaining(Now, Now.AddSeconds(-1)));
        Assert.Equal("已到期", CountdownFormatter.FormatRemaining(Now, Now));
    }

    [Fact]
    public void MenuLine_Upcoming()
    {
        var node = new DeadlineNode
        {
            ConferenceId = "iclr",
            Year = 2027,
            NodeType = DeadlineNodeType.AbstractRegistration,
            Track = TrackKind.MainRegular,
            UtcTime = Now.AddDays(12).AddHours(6),
        };
        var result = new SelectionResult(SelectionKind.Upcoming, node);
        Assert.Equal(
            "ICLR 2027 · 摘要注册 · 12天 6小时",
            CountdownFormatter.FormatMenuLine("ICLR", result, Now));
    }

    [Fact]
    public void MenuLine_SubmissionsClosed()
    {
        var result = new SelectionResult(SelectionKind.SubmissionsClosed, null);
        Assert.Equal(
            "ICML · 本届投稿已截止／下届待公布",
            CountdownFormatter.FormatMenuLine("ICML", result, Now));
    }
}
