using System.Text.RegularExpressions;
using CcfReminder.Core.Data;

namespace CcfReminder.Core.Ccfddl;

/// <summary>
/// 把解析后的 allconf 数据映射为六个固定会议的快照。
/// 身份匹配：conference_key 精确匹配（唯一）＋ title/dblp/sub 交叉核对；
/// 不使用简称模糊匹配。任一身份无法可靠确认即整体失败（调用方保留旧缓存）。
/// </summary>
public static class CcfddlAdapter
{
    public const string SourceName = "ccfddl allconf.json";

    /// <summary>目录 Id → (conference_key, title, dblp, sub)，2026-10-13 实测核验。</summary>
    private static readonly IReadOnlyDictionary<string, (string Key, string Title, string Dblp, string Sub)> Expectations =
        new Dictionary<string, (string, string, string, string)>
        {
            ["icml"] = ("AI/icml", "ICML", "icml", "AI"),
            ["iclr"] = ("AI/iclr", "ICLR", "iclr", "AI"),
            ["nips"] = ("AI/nips", "NeurIPS", "nips", "AI"),
            ["aamas"] = ("AI/aamas", "AAMAS", "ifaamas", "AI"),
            ["aaai"] = ("AI/aaai", "AAAI", "aaai", "AI"),
            ["dai"] = ("AI/dai", "DAI", "dai2", "AI"),
        };

    /// <summary>明确的主会表述（不区分大小写，子串匹配）。</summary>
    private static readonly string[] MainPhrases =
        { "main track", "regular track", "main round", "regular round", "main conference" };

    /// <summary>明确的特殊轮次表述（不区分大小写，子串匹配）；另有 NamedTrackRegex。</summary>
    private static readonly string[] SpecialPhrases =
        { "fast track", "workshop", "blue sky", "special track", "journal track" };

    /// <summary>
    /// “专名＋track”的特殊轮次（如 "AAAI track"）：要求 track 前是首字母大写的专名，
    /// 避免把 track 作普通动词/名词的说明文字（如 "authors can track their submission"）误判为特殊。
    /// 专名为 main/regular 时不算特殊线索（已由主会表述覆盖）。
    /// </summary>
    private static readonly Regex NamedTrackRegex =
        new(@"\b(?<name>[A-Z][A-Za-z0-9]*)\s+[Tt]rack\b", RegexOptions.Compiled);

    /// <summary>
    /// 说明性备注例外：仅 ICML 在 2024–2026 经核实的措辞
    /// “Paper Submissions Open on OpenReview …” 仍属主会。
    /// 其他仅提及 OpenReview、又无明确主会身份的备注一律身份不明
    /// （反例实测：“Demo submissions on OpenReview” 不得判主会）。
    /// </summary>
    private static readonly Regex IcmlInformationalRegex =
        new(@"paper submissions open on openreview",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool TryBuildSnapshot(
        IReadOnlyList<ParsedConference> parsed,
        DateTimeOffset fetchedAtUtc,
        out ConferenceDataSnapshot? snapshot,
        out string? error)
    {
        snapshot = null;
        error = null;

        var datasets = new List<ConferenceDataset>();
        var problems = new List<string>();

        foreach (ConferenceInfo info in ConferenceCatalog.All)
        {
            (string key, string title, string dblp, string sub) = Expectations[info.Id];
            var matches = parsed.Where(p => p.ConferenceKey == key).ToList();
            if (matches.Count != 1)
            {
                problems.Add($"{info.ShortName}: conference_key \"{key}\" 匹配到 {matches.Count} 条");
                continue;
            }

            ParsedConference p = matches[0];
            if (p.Title != title || p.Dblp != dblp || p.Sub != sub)
            {
                problems.Add(
                    $"{info.ShortName}: 字段与已核验身份不符 " +
                    $"(title={p.Title ?? "?"}, dblp={p.Dblp ?? "?"}, sub={p.Sub ?? "?"})");
                continue;
            }
            if (p.Warnings.Count > 0)
            {
                // 选中会议的字段级结构问题：不得静默丢弃节点后当作有效数据
                problems.Add($"{info.ShortName}: {string.Join("；", p.Warnings)}");
                continue;
            }

            datasets.Add(new ConferenceDataset(info, p.RankCcf, BuildNodes(info, p, fetchedAtUtc)));
        }

        if (problems.Count > 0)
        {
            error = string.Join("；", problems);
            return false;
        }

        snapshot = new ConferenceDataSnapshot(fetchedAtUtc, SourceName, datasets);
        return true;
    }

    private static IReadOnlyList<DeadlineNode> BuildNodes(
        ConferenceInfo info, ParsedConference conf, DateTimeOffset fetchedAt)
    {
        var nodes = new List<DeadlineNode>();
        foreach (ParsedEdition ed in conf.Editions)
        {
            bool multiRound = ed.Rounds.Count > 1;
            foreach (ParsedRound round in ed.Rounds)
            {
                TrackKind track = ClassifyTrack(info.Id, multiRound, round.Comment);
                AddNode(nodes, info, ed, round, round.AbstractDeadline,
                    DeadlineNodeType.AbstractRegistration, track, fetchedAt);
                AddNode(nodes, info, ed, round, round.Deadline,
                    DeadlineNodeType.PaperSubmission, track, fetchedAt);
                AddNode(nodes, info, ed, round, round.RebuttalDeadline,
                    DeadlineNodeType.Rebuttal, track, fetchedAt);
                AddNode(nodes, info, ed, round, round.DecisionDeadline,
                    DeadlineNodeType.DecisionNotification, track, fetchedAt);
                foreach ((string key, string value) in round.OtherDeadlines)
                {
                    // 不认识的 *_deadline 键：保留记录但标身份不明，只进详情
                    AddNode(nodes, info, ed, round, value,
                        DeadlineNodeType.Other, TrackKind.Unknown, fetchedAt, roundLabel: key);
                }
            }
        }
        return nodes;
    }

    /// <summary>
    /// 轮次归属（保守、可解释的确定性规则）：
    /// 1. 无 comment：单轮即唯一轮次 → 主会常规；同一届多轮中无 comment 的轮次 → 身份不明，不进主菜单；
    /// 2. 主会线索与特殊线索同时出现 → 身份不明（不得因主会词先匹配而进主菜单）；
    /// 3. 仅明确主会表述（main/regular track 等）→ 主会常规；
    /// 4. 仅明确特殊表述（fast track、workshop、Blue Sky Ideas、专名＋track 等）→ 特殊轮次，不进主菜单；
    ///    不仅凭任意位置出现单词 “track” 判特殊；
    /// 5. 单轮的已核实说明性备注（仅 ICML 的 “Paper Submissions Open on OpenReview …” 措辞）→ 主会常规；
    /// 6. 其余一律身份不明（含单轮无法解释的非空备注、多轮无法识别的备注），只在详情显示。
    /// </summary>
    private static TrackKind ClassifyTrack(string conferenceId, bool multiRound, string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return multiRound ? TrackKind.Unknown : TrackKind.MainRegular;
        }

        string c = comment.Trim();
        bool hasMainClue = c.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            MainPhrases.Any(p => c.Contains(p, StringComparison.OrdinalIgnoreCase));
        bool hasSpecialClue =
            SpecialPhrases.Any(p => c.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
            NamedTrackRegex.Matches(c).Any(m =>
                !m.Groups["name"].Value.Equals("main", StringComparison.OrdinalIgnoreCase) &&
                !m.Groups["name"].Value.Equals("regular", StringComparison.OrdinalIgnoreCase));

        if (hasMainClue && hasSpecialClue)
        {
            return TrackKind.Unknown;
        }
        if (hasMainClue)
        {
            return TrackKind.MainRegular;
        }
        if (hasSpecialClue)
        {
            return TrackKind.Special;
        }
        if (!multiRound &&
            conferenceId == "icml" &&
            IcmlInformationalRegex.IsMatch(c))
        {
            return TrackKind.MainRegular;
        }
        return TrackKind.Unknown;
    }

    /// <summary>字段存在（含 "TBD"）才建节点；字段缺失表示该节点不存在，不算“未公布”。</summary>
    private static void AddNode(
        List<DeadlineNode> nodes,
        ConferenceInfo info,
        ParsedEdition ed,
        ParsedRound round,
        string? raw,
        DeadlineNodeType type,
        TrackKind track,
        DateTimeOffset fetchedAt,
        string? roundLabel = null)
    {
        if (raw is null)
        {
            return;
        }

        ConversionResult conv = DeadlineTimeConverter.Convert(raw, ed.Timezone);
        nodes.Add(new DeadlineNode
        {
            ConferenceId = info.Id,
            Year = ed.Year,
            EditionId = ed.EditionId,
            NodeType = type,
            Track = track,
            RoundLabel = roundLabel,
            Note = round.Comment,
            RawTime = raw,
            RawTimezone = ed.Timezone,
            UtcTime = conv.Utc,
            Issue = conv.Issue,
            SourceUrl = ed.Link,
            DataSource = SourceName,
            FetchedAt = fetchedAt,
            Verification = VerificationStatus.Unverified, // 聚合数据一律未核验；已核验只能来自人工对照官网
        });
    }
}
