using System.Text.Json;

namespace CcfReminder.Core.Ccfddl;

/// <summary>解析后的一轮投稿时间线（值均为原始字符串，未换算）。</summary>
public sealed record ParsedRound(
    string? AbstractDeadline,
    string? Deadline,
    string? RebuttalDeadline,
    string? DecisionDeadline,
    IReadOnlyDictionary<string, string> OtherDeadlines,
    string? Comment);

/// <summary>解析后的一届会议。</summary>
public sealed record ParsedEdition(
    int Year,
    string? EditionId,
    string? Link,
    string? Timezone,
    IReadOnlyList<ParsedRound> Rounds);

/// <summary>
/// 解析后的一个会议条目。
/// Warnings 记录该会议的字段级结构问题（timeline 类型错误、已知截止字段类型不符、届次缺有效年份）；
/// 适配器据此拒绝受影响的数据，而不是静默丢弃节点。
/// </summary>
public sealed record ParsedConference(
    string ConferenceKey,
    string? Title,
    string? Dblp,
    string? Sub,
    string? RankCcf,
    IReadOnlyList<ParsedEdition> Editions,
    IReadOnlyList<string> Warnings);

/// <summary>
/// allconf.json 的结构化解析（只读、容忍未知字段，但关键结构缺失即整体失败）。
/// 实测结构（2026-10-13）：顶层数组 376 条；条目字段 title/description/sub/rank/dblp/confs/conference_key；
/// confs 元素字段 year/id/link/timeline/timezone/date/place/opening/opening_is_derived；
/// timeline 已知键 abstract_deadline/deadline/rebuttal_deadline/decision_deadline/comment。
/// </summary>
public static class CcfddlJsonParser
{
    private static readonly HashSet<string> KnownTimelineKeys = new(StringComparer.Ordinal)
    {
        "abstract_deadline", "deadline", "rebuttal_deadline", "decision_deadline", "comment",
    };

    public static bool TryParse(
        string json,
        out IReadOnlyList<ParsedConference> conferences,
        out string? error)
    {
        conferences = Array.Empty<ParsedConference>();
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "顶层不是数组";
                return false;
            }

            var list = new List<ParsedConference>();
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = "会议条目不是对象";
                    return false;
                }

                string? key = GetString(item, "conference_key");
                if (key is null)
                {
                    error = "条目缺少 conference_key";
                    return false;
                }
                if (!item.TryGetProperty("confs", out JsonElement confs) ||
                    confs.ValueKind != JsonValueKind.Array)
                {
                    error = $"{key} 缺少 confs 数组";
                    return false;
                }

                string? rankCcf = null;
                if (item.TryGetProperty("rank", out JsonElement rank) &&
                    rank.ValueKind == JsonValueKind.Object)
                {
                    rankCcf = GetString(rank, "ccf");
                }

                var editions = new List<ParsedEdition>();
                var warnings = new List<string>();
                foreach (JsonElement conf in confs.EnumerateArray())
                {
                    if (conf.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add($"{key}: 届次条目不是对象，已跳过");
                        continue;
                    }
                    if (!conf.TryGetProperty("year", out JsonElement yearEl) ||
                        yearEl.ValueKind != JsonValueKind.Number ||
                        !yearEl.TryGetInt32(out int year))
                    {
                        warnings.Add($"{key}: 届次缺少有效 year，已跳过");
                        continue;
                    }

                    var rounds = new List<ParsedRound>();
                    if (conf.TryGetProperty("timeline", out JsonElement timeline))
                    {
                        if (timeline.ValueKind != JsonValueKind.Array)
                        {
                            // 字段存在但类型不符：不得静默当作“没有时间线”
                            warnings.Add($"{key} {year}: timeline 不是数组");
                        }
                        else
                        {
                            // 空数组是合法情形：该届时间尚未公布
                            foreach (JsonElement tl in timeline.EnumerateArray())
                            {
                                if (tl.ValueKind != JsonValueKind.Object)
                                {
                                    warnings.Add($"{key} {year}: timeline 条目不是对象");
                                    continue;
                                }

                                var others = new Dictionary<string, string>(StringComparer.Ordinal);
                                foreach (JsonProperty prop in tl.EnumerateObject())
                                {
                                    if (!KnownTimelineKeys.Contains(prop.Name) &&
                                        prop.Name.EndsWith("_deadline", StringComparison.Ordinal) &&
                                        prop.Value.ValueKind == JsonValueKind.String)
                                    {
                                        others[prop.Name] = prop.Value.GetString()!;
                                    }
                                }

                                rounds.Add(new ParsedRound(
                                    GetDeadlineString(tl, "abstract_deadline", key, year, warnings),
                                    GetDeadlineString(tl, "deadline", key, year, warnings),
                                    GetDeadlineString(tl, "rebuttal_deadline", key, year, warnings),
                                    GetDeadlineString(tl, "decision_deadline", key, year, warnings),
                                    others,
                                    GetString(tl, "comment")));
                            }
                        }
                    }

                    editions.Add(new ParsedEdition(
                        year,
                        GetString(conf, "id"),
                        GetString(conf, "link"),
                        GetString(conf, "timezone"),
                        rounds));
                }

                list.Add(new ParsedConference(
                    key,
                    GetString(item, "title"),
                    GetString(item, "dblp"),
                    GetString(item, "sub"),
                    rankCcf,
                    editions,
                    warnings));
            }

            conferences = list;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>仅当属性存在且为 JSON 字符串时返回其值；缺失或 null 返回 null。</summary>
    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>
    /// 已知截止字段的严格读取：缺失或 null 视为“未提供”（返回 null）；
    /// 存在但不是字符串时记录警告（调用方不得静默丢弃）。
    /// </summary>
    private static string? GetDeadlineString(
        JsonElement round, string name, string key, int year, List<string> warnings)
    {
        if (!round.TryGetProperty(name, out JsonElement el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (el.ValueKind != JsonValueKind.String)
        {
            warnings.Add($"{key} {year}: 字段 {name} 类型不符（期望字符串）");
            return null;
        }
        return el.GetString();
    }
}
