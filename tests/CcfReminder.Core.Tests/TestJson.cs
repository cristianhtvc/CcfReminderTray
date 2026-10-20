namespace CcfReminder.Core.Tests;

/// <summary>
/// 构造可控的 allconf.json 样例（合成数据，日期均为虚构，仅供测试）。
/// 字段结构对齐 2026-10-13 实测：title/sub/rank/dblp/confs/conference_key。
/// </summary>
internal static class TestJson
{
    public static string Conf(string key, string title, string dblp, string rank, string confs) =>
        $$"""{"title":"{{title}}","description":"d","sub":"AI","rank":{"ccf":"{{rank}}"},"dblp":"{{dblp}}","conference_key":"{{key}}","confs":[{{confs}}]}""";

    public static string Edition(int year, string id, string tz, string timeline, string link = "https://example.org/c") =>
        $$"""{"year":{{year}},"id":"{{id}}","link":"{{link}}","timeline":[{{timeline}}],"timezone":"{{tz}}","date":"x","place":"x"}""";

    /// <summary>六个会议各一届的合成 JSON；可用 overrides 替换某会议的 confs 内容。</summary>
    public static string SixConferences(
        string? icml = null, string? iclr = null, string? nips = null,
        string? aamas = null, string? aaai = null, string? dai = null,
        string extraEntries = "")
    {
        icml ??= Edition(2027, "icml27", "UTC+0", """{"abstract_deadline":"2027-01-20 11:59:59","deadline":"2027-01-27 11:59:59"}""");
        iclr ??= Edition(2027, "iclr27", "AoE", """{"abstract_deadline":"2027-09-18 23:59:59","deadline":"2027-09-25 23:59:59"}""");
        nips ??= Edition(2027, "nips27", "AoE", """{"abstract_deadline":"2027-05-05 11:59:00","deadline":"2027-05-07 11:59:00"}""");
        aamas ??= Edition(2027, "aamas27", "AoE", """{"abstract_deadline":"2027-10-01 23:59:59","deadline":"2027-10-08 23:59:59"}""");
        aaai ??= Edition(2027, "aaai27", "UTC-12", """{"abstract_deadline":"2027-07-21 23:59:59","deadline":"2027-07-28 23:59:59"}""");
        dai ??= Edition(2027, "dai27", "AoE", """{"deadline":"2027-08-03 23:59:59"}""");

        return "[" + string.Join(",", new[]
        {
            Conf("AI/icml", "ICML", "icml", "A", icml),
            Conf("AI/iclr", "ICLR", "iclr", "A", iclr),
            Conf("AI/nips", "NeurIPS", "nips", "A", nips),
            Conf("AI/aamas", "AAMAS", "ifaamas", "B", aamas),
            Conf("AI/aaai", "AAAI", "aaai", "A", aaai),
            Conf("AI/dai", "DAI", "dai2", "C", dai),
        }) + extraEntries + "]";
    }

    /// <summary>缺 DAI 的五会议合成 JSON：结构合法但六会议身份核对必失败。</summary>
    public static string FiveConferencesMissingDai() =>
        "[" + string.Join(",", new[]
        {
            Conf("AI/icml", "ICML", "icml", "A", Edition(2027, "icml27", "UTC+0", """{"deadline":"2027-01-27 11:59:59"}""")),
            Conf("AI/iclr", "ICLR", "iclr", "A", Edition(2027, "iclr27", "AoE", """{"deadline":"2027-09-25 23:59:59"}""")),
            Conf("AI/nips", "NeurIPS", "nips", "A", Edition(2027, "nips27", "AoE", """{"deadline":"2027-05-07 11:59:00"}""")),
            Conf("AI/aamas", "AAMAS", "ifaamas", "B", Edition(2027, "aamas27", "AoE", """{"deadline":"2027-10-08 23:59:59"}""")),
            Conf("AI/aaai", "AAAI", "aaai", "A", Edition(2027, "aaai27", "UTC-12", """{"deadline":"2027-07-28 23:59:59"}""")),
        }) + "]";
}
